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

        [InputParameter("HUD enabled", 4)]
        public bool HudEnabled = true;

        [InputParameter("Flashing alert", 5)]
        public bool FlashingAlertEnabled = true;

        private readonly DclState state = new DclState();
        private SessionClock sessionClock;
        private CsvCacheStore cacheStore;
        private DateTime? currentSessionStartUtc;
        private string currentSessionDateKey;
        private bool coreEventsSubscribed;

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
            SubscribeCoreEvents();
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
                state.Reset();
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

        protected override void OnClear()
        {
            UnsubscribeCoreEvents();
            base.OnClear();
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            if (!HudEnabled || !state.HasSnapshot || args?.Graphics == null)
                return;

            DrawHud(args.Graphics, args.Rectangle, state.Snapshot);
        }

        private void DrawHud(Graphics graphics, Rectangle panel, DclSnapshot snapshot)
        {
            if (panel.Width < 180 || panel.Height < 80)
                return;

            DclRiskLevel riskLevel = GetRiskLevel(snapshot);
            bool flashOff = riskLevel == DclRiskLevel.Critical &&
                FlashingAlertEnabled &&
                DateTime.UtcNow.Second % 2 == 0;

            Color accent = flashOff ? Color.FromArgb(90, 90, 90) : GetRiskColor(riskLevel);
            string text =
                $"DLL rem: {FormatCurrency(snapshot.RemainingDailyLimit)}\n" +
                $"DCL: {FormatCurrency(snapshot.DailyCumulativeLoss)}\n" +
                $"Peak: {FormatCurrency(snapshot.DailyPeakBalance)}\n" +
                $"Equity: {FormatCurrency(snapshot.CurrentEquity)}";

            using Font font = new Font("Segoe UI", 10, FontStyle.Bold);
            using StringFormat format = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near
            };

            SizeF textSize = graphics.MeasureString(text, font);
            int padding = 10;
            int width = Math.Min(panel.Width - 16, (int)Math.Ceiling(textSize.Width) + padding * 2);
            int height = Math.Min(panel.Height - 16, (int)Math.Ceiling(textSize.Height) + padding * 2);
            Rectangle hudRect = new Rectangle(panel.Right - width - 8, panel.Top + 8, width, height);

            using SolidBrush background = new SolidBrush(Color.FromArgb(185, 18, 22, 28));
            using Pen border = new Pen(accent, 2);
            using SolidBrush foreground = new SolidBrush(accent);

            graphics.FillRectangle(background, hudRect);
            graphics.DrawRectangle(border, hudRect);
            graphics.DrawString(text, font, foreground, hudRect.Left + padding, hudRect.Top + padding, format);
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

        private void SubscribeCoreEvents()
        {
            if (coreEventsSubscribed)
                return;

            Core.Instance.ClosedPositionAdded += OnClosedPositionAdded;
            coreEventsSubscribed = true;
        }

        private void UnsubscribeCoreEvents()
        {
            if (!coreEventsSubscribed)
                return;

            Core.Instance.ClosedPositionAdded -= OnClosedPositionAdded;
            coreEventsSubscribed = false;
        }

        private void OnClosedPositionAdded(ClosedPosition closedPosition)
        {
            if (SelectedAccount == null ||
                MaxDailyLoss <= 0 ||
                !TradingObjectBelongsToAccount(closedPosition, SelectedAccount))
                return;

            DateTime nowUtc = DateTime.UtcNow;
            EnsureCurrentSession(nowUtc);

            double balance = SelectedAccount.Balance;
            double openPnL = GetOpenProfitLoss(SelectedAccount);
            DclSnapshot snapshot = state.Update(nowUtc, balance, openPnL, MaxDailyLoss);

            cacheStore.AppendAsync(SelectedAccount.Name, currentSessionDateKey, snapshot, sessionClock.ToLocalTime(nowUtc));
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
            return TradingObjectBelongsToAccount(position, account);
        }

        private static bool TradingObjectBelongsToAccount(object tradingObject, Account account)
        {
            if (TryReadObjectProperty(tradingObject, "Account", out object positionAccount))
                return Equals(positionAccount, account);

            if (TryReadStringProperty(tradingObject, "AccountId", out string accountId))
                return string.Equals(accountId, account.Id, StringComparison.OrdinalIgnoreCase);

            if (TryReadStringProperty(tradingObject, "AccountName", out string accountName))
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

        private DclRiskLevel GetRiskLevel(DclSnapshot snapshot)
        {
            double ratio = snapshot.RemainingDailyLimit / MaxDailyLoss;

            if (ratio < 0.25)
                return DclRiskLevel.Critical;

            if (ratio <= 0.5)
                return DclRiskLevel.Warning;

            return DclRiskLevel.Safe;
        }

        private static Color GetRiskColor(DclRiskLevel riskLevel)
        {
            switch (riskLevel)
            {
                case DclRiskLevel.Critical:
                    return Color.Crimson;
                case DclRiskLevel.Warning:
                    return Color.Orange;
                default:
                    return Color.LimeGreen;
            }
        }

        private static string FormatCurrency(double value)
        {
            return value.ToString("C2", CultureInfo.CurrentCulture);
        }

        private void BreakAllLines()
        {
            SetLineBreak(0, 0);
            SetLineBreak(0, 1);
            SetLineBreak(0, 2);
        }
    }
}
