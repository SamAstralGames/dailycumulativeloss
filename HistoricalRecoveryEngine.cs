using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace DailyCumulativeLoss
{
    internal sealed class HistoricalRecoveryEngine
    {
        private const int MaxReplayPositions = 50;
        private const int MaxHistoryItemsPerPosition = 20000;

        public HistoricalRecoveryResult Recover(Account account, DateTime sessionStartUtc, double currentBalance, double currentOpenPnL)
        {
            if (account == null)
                return HistoricalRecoveryResult.NotRestored("no account");

            try
            {
                TradeCandidate[] trades = Core.Instance.ClosedPositions
                    .Where(position => BelongsToAccount(position, account))
                    .Select(CreateTradeCandidate)
                    .Where(trade => trade.CloseTimeUtc >= sessionStartUtc)
                    .OrderBy(trade => trade.CloseTimeUtc)
                    .ToArray();

                if (trades.Length == 0)
                    return HistoricalRecoveryResult.NotRestored("no closed positions");

                double currentEquity = currentBalance + currentOpenPnL;
                double runningBalance = currentBalance - trades.Sum(trade => trade.RealizedPnL);
                double peak = Math.Max(runningBalance, currentEquity);
                int replayedPositions = 0;
                int replayedHistoryItems = 0;
                string lastReplayError = string.Empty;

                foreach (TradeCandidate trade in trades)
                {
                    double balanceBeforeClose = runningBalance;
                    string replayError = string.Empty;

                    if (replayedPositions < MaxReplayPositions &&
                        TryReplayIntratradePeak(trade, balanceBeforeClose, out double intratradePeak, out int historyItems, out replayError))
                    {
                        peak = Math.Max(peak, intratradePeak);
                        replayedPositions++;
                        replayedHistoryItems += historyItems;
                    }
                    else if (!string.IsNullOrWhiteSpace(replayError))
                    {
                        lastReplayError = replayError;
                    }

                    runningBalance += trade.RealizedPnL;
                    peak = Math.Max(peak, runningBalance);
                }

                string status = replayedPositions > 0
                    ? $"history replay {replayedPositions}/{trades.Length}"
                    : "closed positions";

                if (!string.IsNullOrWhiteSpace(lastReplayError))
                    status += $" ({lastReplayError})";

                return HistoricalRecoveryResult.CreateRestored(peak, status, trades.Length, replayedPositions, replayedHistoryItems, lastReplayError);
            }
            catch (Exception ex)
            {
                return HistoricalRecoveryResult.NotRestored($"history error: {ex.Message}");
            }
        }

        private static TradeCandidate CreateTradeCandidate(ClosedPosition position)
        {
            TryGetDateTimeUtc(position, new[] { "OpenTime", "OpenDateTime", "CreationTime", "CreationTimeUtc", "DateTime" }, out DateTime openTimeUtc);
            TryGetDateTimeUtc(position, new[] { "CloseTime", "ClosedTime", "CloseDateTime", "LastUpdateTime", "DateTime" }, out DateTime closeTimeUtc);
            TryGetNumeric(position, new[] { "NetPnL", "NetPnl", "GrossPnL", "GrossPnl" }, out double realizedPnL);
            TryGetNumeric(position, new[] { "EntryPrice", "OpenPrice", "AverageFillPrice", "AverageOpenPrice" }, out double entryPrice);
            TryGetNumeric(position, new[] { "Quantity", "CloseQuantity", "FilledQuantity", "TotalQuantity" }, out double quantity);
            TryGetObject(position, "Symbol", out object symbol);
            bool sideKnown = TryGetLongSide(position, out bool isLong);

            return new TradeCandidate(
                position,
                symbol,
                openTimeUtc,
                closeTimeUtc,
                realizedPnL,
                entryPrice,
                Math.Abs(quantity),
                isLong,
                sideKnown);
        }

        private static bool TryReplayIntratradePeak(
            TradeCandidate trade,
            double balanceBeforeClose,
            out double intratradePeak,
            out int historyItems,
            out string replayError)
        {
            intratradePeak = balanceBeforeClose;
            historyItems = 0;
            replayError = string.Empty;

            if (!trade.HasReplayInputs)
            {
                replayError = "missing trade replay fields";
                return false;
            }

            foreach (string periodName in new[] { "TICK1", "MIN1" })
            {
                if (!TryGetHistory(trade.Symbol, periodName, trade.OpenTimeUtc, trade.CloseTimeUtc, out object history, out replayError))
                    continue;

                double maxFloatingPnl = 0;

                foreach (object historyItem in EnumerateHistory(history))
                {
                    if (historyItems >= MaxHistoryItemsPerPosition)
                    {
                        replayError = "history capped";
                        break;
                    }

                    if (TryGetFavorablePrice(historyItem, trade.IsLong, out double favorablePrice) &&
                        TryCalculatePnl(trade.Symbol, trade.EntryPrice, favorablePrice, trade.Quantity, trade.IsLong, out double floatingPnl))
                    {
                        maxFloatingPnl = Math.Max(maxFloatingPnl, floatingPnl);
                    }

                    historyItems++;
                }

                if (historyItems > 0)
                {
                    intratradePeak = balanceBeforeClose + maxFloatingPnl;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetHistory(object symbol, string periodName, DateTime fromUtc, DateTime toUtc, out object history, out string error)
        {
            history = null;
            error = string.Empty;

            if (symbol == null)
            {
                error = "missing symbol";
                return false;
            }

            if (!TryGetStaticProperty(symbol.GetType().Assembly, "TradingPlatform.BusinessLayer.Period", periodName, out object period))
            {
                error = $"missing period {periodName}";
                return false;
            }

            object historyType = TryGetObject(symbol, "HistoryType", out object symbolHistoryType)
                ? symbolHistoryType
                : null;

            foreach (MethodInfo method in symbol.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(method => method.Name == "GetHistory"))
            {
                if (!TryBuildHistoryArguments(method, period, historyType, fromUtc, toUtc, out object[] args))
                    continue;

                try
                {
                    history = method.Invoke(symbol, args);
                    if (history != null)
                        return true;
                }
                catch (TargetInvocationException ex)
                {
                    error = ex.InnerException?.Message ?? ex.Message;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            if (string.IsNullOrWhiteSpace(error))
                error = $"no GetHistory overload for {periodName}";

            return false;
        }

        private static bool TryBuildHistoryArguments(MethodInfo method, object period, object historyType, DateTime fromUtc, DateTime toUtc, out object[] args)
        {
            ParameterInfo[] parameters = method.GetParameters();
            args = new object[parameters.Length];
            int dateIndex = 0;

            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                Type parameterType = parameter.ParameterType;
                string parameterName = parameter.Name ?? string.Empty;

                if (period != null && parameterType.IsInstanceOfType(period))
                {
                    args[index] = period;
                    continue;
                }

                if (historyType != null && parameterType.IsInstanceOfType(historyType))
                {
                    args[index] = historyType;
                    continue;
                }

                if (parameterType == typeof(DateTime))
                {
                    if (parameterName.IndexOf("to", StringComparison.OrdinalIgnoreCase) >= 0)
                        args[index] = toUtc;
                    else if (parameterName.IndexOf("from", StringComparison.OrdinalIgnoreCase) >= 0)
                        args[index] = fromUtc;
                    else
                        args[index] = dateIndex++ == 0 ? fromUtc : toUtc;

                    continue;
                }

                if (parameter.HasDefaultValue)
                {
                    args[index] = parameter.DefaultValue;
                    continue;
                }

                if (parameterType == typeof(bool))
                {
                    args[index] = false;
                    continue;
                }

                if (parameterType.IsEnum)
                {
                    Array values = Enum.GetValues(parameterType);
                    if (values.Length == 0)
                        return false;

                    args[index] = values.GetValue(0);
                    continue;
                }

                return false;
            }

            return true;
        }

        private static IEnumerable<object> EnumerateHistory(object history)
        {
            if (history is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                    yield return item;

                yield break;
            }

            if (!TryGetNumeric(history, new[] { "Count" }, out double count))
                yield break;

            PropertyInfo indexer = history.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(property => property.GetIndexParameters().Length == 1);

            if (indexer == null)
                yield break;

            for (int index = 0; index < (int)count; index++)
            {
                object item = null;

                try
                {
                    item = indexer.GetValue(history, new object[] { index });
                }
                catch
                {
                }

                if (item != null)
                    yield return item;
            }
        }

        private static bool TryGetFavorablePrice(object historyItem, bool isLong, out double price)
        {
            string[] favorableNames = isLong
                ? new[] { "High", "MaxPrice", "Ask", "Last", "Close", "Price" }
                : new[] { "Low", "MinPrice", "Bid", "Last", "Close", "Price" };

            if (TryGetNumeric(historyItem, favorableNames, out price))
                return true;

            price = 0;
            return false;
        }

        private static bool TryCalculatePnl(object symbol, double entryPrice, double exitPrice, double quantity, bool isLong, out double pnl)
        {
            pnl = 0;

            if (entryPrice <= 0 || exitPrice <= 0 || quantity <= 0)
                return false;

            double priceMove = isLong ? exitPrice - entryPrice : entryPrice - exitPrice;
            if (priceMove <= 0)
                return true;

            if (TryGetNumeric(symbol, new[] { "TickSize", "MinTickSize", "MinimumChange" }, out double tickSize) &&
                TryGetNumeric(symbol, new[] { "TickCost", "TickValue", "PointValue" }, out double tickCost) &&
                tickSize > 0 &&
                tickCost > 0)
            {
                pnl = priceMove / tickSize * tickCost * quantity;
                return true;
            }

            if (TryGetNumeric(symbol, new[] { "ContractMultiplier", "LotSize" }, out double multiplier) && multiplier > 0)
            {
                pnl = priceMove * multiplier * quantity;
                return true;
            }

            pnl = priceMove * quantity;
            return true;
        }

        private static bool TryGetLongSide(object source, out bool isLong)
        {
            isLong = true;

            if (!TryGetObject(source, "Side", out object side) &&
                !TryGetObject(source, "PositionSide", out side) &&
                !TryGetObject(source, "Operation", out side))
                return false;

            string sideText = side.ToString();
            if (sideText.IndexOf("sell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sideText.IndexOf("short", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isLong = false;
                return true;
            }

            if (sideText.IndexOf("buy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sideText.IndexOf("long", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isLong = true;
                return true;
            }

            return false;
        }

        private static bool BelongsToAccount(object tradingObject, Account account)
        {
            if (TryGetObject(tradingObject, "Account", out object objectAccount))
                return Equals(objectAccount, account);

            if (TryGetString(tradingObject, "AccountId", out string accountId))
                return string.Equals(accountId, account.Id, StringComparison.OrdinalIgnoreCase);

            if (TryGetString(tradingObject, "AccountName", out string accountName))
                return string.Equals(accountName, account.Name, StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static bool TryGetDateTimeUtc(object source, string[] propertyNames, out DateTime utcDateTime)
        {
            foreach (string propertyName in propertyNames)
            {
                if (TryGetObject(source, propertyName, out object value) && TryConvertToDateTime(value, out DateTime dateTime))
                {
                    utcDateTime = ToUtc(dateTime);
                    return true;
                }
            }

            utcDateTime = default;
            return false;
        }

        private static bool TryGetNumeric(object source, string[] propertyNames, out double value)
        {
            foreach (string propertyName in propertyNames)
            {
                if (TryGetObject(source, propertyName, out object rawValue) && TryConvertToDouble(rawValue, out value))
                    return true;
            }

            value = 0;
            return false;
        }

        private static bool TryGetString(object source, string propertyName, out string value)
        {
            value = null;

            if (!TryGetObject(source, propertyName, out object rawValue))
                return false;

            value = rawValue?.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetObject(object source, string propertyName, out object value)
        {
            value = null;

            if (source == null)
                return false;

            try
            {
                PropertyInfo property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property == null)
                    return false;

                value = property.GetValue(source);
                return value != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetStaticProperty(Assembly assembly, string typeName, string propertyName, out object value)
        {
            value = null;

            Type type = assembly.GetType(typeName);
            if (type == null)
                return false;

            try
            {
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (property == null)
                    return false;

                value = property.GetValue(null);
                return value != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertToDouble(object value, out double result)
        {
            result = 0;

            if (value == null)
                return false;

            if (value is double doubleValue)
            {
                result = doubleValue;
                return IsFinite(result);
            }

            if (value is decimal decimalValue)
            {
                result = (double)decimalValue;
                return IsFinite(result);
            }

            if (value is IConvertible convertible)
            {
                try
                {
                    result = convertible.ToDouble(CultureInfo.InvariantCulture);
                    return IsFinite(result);
                }
                catch
                {
                }
            }

            string[] nestedProperties = { "Value", "Amount", "AssetValue", "ValueInAccountCurrency" };
            foreach (string propertyName in nestedProperties)
            {
                if (TryGetNumeric(value, new[] { propertyName }, out result))
                    return true;
            }

            return false;
        }

        private static bool TryConvertToDateTime(object value, out DateTime dateTime)
        {
            if (value is DateTime typedDateTime)
            {
                dateTime = typedDateTime;
                return true;
            }

            return DateTime.TryParse(value?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dateTime);
        }

        private static DateTime ToUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
                return dateTime;

            if (dateTime.Kind == DateTimeKind.Local)
                return dateTime.ToUniversalTime();

            return dateTime;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private readonly struct TradeCandidate
        {
            public TradeCandidate(
                ClosedPosition closedPosition,
                object symbol,
                DateTime openTimeUtc,
                DateTime closeTimeUtc,
                double realizedPnl,
                double entryPrice,
                double quantity,
                bool isLong,
                bool sideKnown)
            {
                ClosedPosition = closedPosition;
                Symbol = symbol;
                OpenTimeUtc = openTimeUtc;
                CloseTimeUtc = closeTimeUtc;
                RealizedPnL = realizedPnl;
                EntryPrice = entryPrice;
                Quantity = quantity;
                IsLong = isLong;
                SideKnown = sideKnown;
            }

            public ClosedPosition ClosedPosition { get; }
            public object Symbol { get; }
            public DateTime OpenTimeUtc { get; }
            public DateTime CloseTimeUtc { get; }
            public double RealizedPnL { get; }
            public double EntryPrice { get; }
            public double Quantity { get; }
            public bool IsLong { get; }
            public bool SideKnown { get; }

            public bool HasReplayInputs =>
                Symbol != null &&
                SideKnown &&
                OpenTimeUtc != default &&
                CloseTimeUtc != default &&
                CloseTimeUtc >= OpenTimeUtc &&
                EntryPrice > 0 &&
                Quantity > 0;
        }
    }
}
