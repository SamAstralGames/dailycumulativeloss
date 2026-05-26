using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DailyCumulativeLoss
{
    public class DailyCumulativeLoss : Indicator
    {
        [InputParameter("Account", 0)]
        public Account SelectedAccount;

        [InputParameter("Max daily loss", 1, 0, 1000000, 1, 2)]
        public double MaxDailyLoss = 2500;

        [InputParameter("Reset hour Paris", 2, 0, 23, 1, 0)]
        public int ResetHourParis = 23;

        [InputParameter("Cache directory", 3)]
        public string CacheDirectory = string.Empty;

        private readonly DclState state = new DclState();
        private SessionClock sessionClock;
        private CsvCacheStore cacheStore;
        private DateTime? currentSessionStartUtc;
        private string currentSessionDateKey;

        public DailyCumulativeLoss()
            : base()
        {
            Name = "Daily Cumulative Loss";
            Description = "Tracks daily cumulative loss from the account equity peak.";

            AddLineSeries("Equity", Color.DodgerBlue, 2, LineStyle.Solid);
            AddLineSeries("Daily peak", Color.LimeGreen, 2, LineStyle.StepLine);
            AddLineSeries("Liquidation", Color.Crimson, 2, LineStyle.Solid);

            SeparateWindow = true;
        }

        protected override void OnInit()
        {
            SelectedAccount ??= Core.Instance.Accounts.FirstOrDefault();
            ResetStateForCurrentSession();
        }

        protected override void OnSettingsUpdated()
        {
            base.OnSettingsUpdated();
            ResetStateForCurrentSession();
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (SelectedAccount == null || MaxDailyLoss <= 0)
            {
                BreakAllLines();
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            EnsureCurrentSession(nowUtc);

            double balance = SelectedAccount.Balance;
            double openPnL = GetOpenProfitLoss(SelectedAccount);
            bool hadSnapshot = state.HasSnapshot;
            double previousPeak = hadSnapshot ? state.Snapshot.DailyPeakBalance : 0;
            DclSnapshot snapshot = state.Update(nowUtc, balance, openPnL, MaxDailyLoss);

            SetValue(snapshot.CurrentEquity, 0);
            SetValue(snapshot.DailyPeakBalance, 1);
            SetValue(snapshot.LiquidationThreshold, 2);

            if (!hadSnapshot || snapshot.DailyPeakBalance > previousPeak)
                cacheStore.AppendAsync(SelectedAccount.Name, currentSessionDateKey, snapshot, sessionClock.ToLocalTime(nowUtc));
        }

        private void ResetStateForCurrentSession()
        {
            sessionClock = new SessionClock(TimeSpan.FromHours(ResetHourParis));
            currentSessionStartUtc = sessionClock.GetSessionStartUtc(DateTime.UtcNow);
            currentSessionDateKey = sessionClock.GetSessionDateKey(currentSessionStartUtc.Value);
            cacheStore = new CsvCacheStore(CacheDirectory);
            state.Reset();
            RestorePeakFromCache();
        }

        private void EnsureCurrentSession(DateTime nowUtc)
        {
            DateTime sessionStartUtc = sessionClock.GetSessionStartUtc(nowUtc);
            if (currentSessionStartUtc == sessionStartUtc)
                return;

            currentSessionStartUtc = sessionStartUtc;
            currentSessionDateKey = sessionClock.GetSessionDateKey(sessionStartUtc);
            state.Reset();
            RestorePeakFromCache();
        }

        private void RestorePeakFromCache()
        {
            if (SelectedAccount == null || cacheStore == null || string.IsNullOrWhiteSpace(currentSessionDateKey))
                return;

            if (cacheStore.TryReadLastSnapshot(SelectedAccount.Name, currentSessionDateKey, out DclSnapshot snapshot))
                state.RestoreDailyPeak(snapshot.DailyPeakBalance);
        }

        private double GetOpenProfitLoss(Account account)
        {
            if (TryReadNumericProperty(account, "OpenProfitLoss", out double accountOpenPnL))
                return accountOpenPnL;

            double openPnL = 0;
            foreach (Position position in Core.Instance.Positions)
            {
                if (!PositionBelongsToAccount(position, account))
                    continue;

                openPnL += GetPositionPnL(position);
            }

            return openPnL;
        }

        private static bool PositionBelongsToAccount(Position position, Account account)
        {
            if (TryReadObjectProperty(position, "Account", out object positionAccount))
                return Equals(positionAccount, account);

            if (TryReadStringProperty(position, "AccountId", out string accountId))
                return string.Equals(accountId, account.Id, StringComparison.OrdinalIgnoreCase);

            if (TryReadStringProperty(position, "AccountName", out string accountName))
                return string.Equals(accountName, account.Name, StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static double GetPositionPnL(Position position)
        {
            if (TryReadNumericProperty(position, "NetPnL", out double netPnL))
                return netPnL;

            if (TryReadNumericProperty(position, "GrossPnL", out double grossPnL))
                return grossPnL;

            return 0;
        }

        private static bool TryReadNumericProperty(object source, string propertyName, out double value)
        {
            value = 0;

            if (!TryReadObjectProperty(source, propertyName, out object rawValue))
                return false;

            return TryConvertToDouble(rawValue, out value);
        }

        private static bool TryReadStringProperty(object source, string propertyName, out string value)
        {
            value = null;

            if (!TryReadObjectProperty(source, propertyName, out object rawValue))
                return false;

            value = rawValue?.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryReadObjectProperty(object source, string propertyName, out object value)
        {
            value = null;

            if (source == null)
                return false;

            try
            {
                System.Reflection.PropertyInfo property = source.GetType().GetProperty(propertyName);
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
                if (TryReadNumericProperty(value, propertyName, out result))
                    return true;
            }

            return false;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private void BreakAllLines()
        {
            SetLineBreak(0, 0);
            SetLineBreak(0, 1);
            SetLineBreak(0, 2);
        }
    }
}
