using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DailyCumulativeLoss
{
    public class DailyCumulativeLoss : Indicator
    {
        private const int RemainingLineIndex = 0;
        private const int MaxLimitLineIndex = 1;
        private const int LiquidationLineIndex = 2;

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

        [InputParameter("Show diagnostics", 6)]
        public bool ShowDiagnostics = false;

        [InputParameter("Enable historical recovery", 7)]
        public bool EnableHistoricalRecovery = true;

        [InputParameter("Enable platform alerts", 8)]
        public bool EnablePlatformAlerts = true;

        private readonly DclState state = new DclState();
        private SessionClock sessionClock;
        private CsvCacheStore cacheStore;
        private DateTime? currentSessionStartUtc;
        private string currentSessionDateKey;
        private string recoveryStatus = "not checked";
        private bool warningAlertSent;
        private bool criticalAlertSent;
        private bool coreEventsSubscribed;

        public DailyCumulativeLoss()
            : base()
        {
            Name = "Daily Cumulative Loss";
            Description = "Tracks daily cumulative loss from the account equity peak.";

            AddLineSeries("DLL remaining", Color.DodgerBlue, 2, LineStyle.Solid);
            AddLineSeries("Max daily loss", Color.LimeGreen, 2, LineStyle.StepLine);
            AddLineSeries("Liquidation 0", Color.Crimson, 2, LineStyle.Solid);

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

            PlotRelativeRisk(snapshot);
            CheckRiskAlerts(snapshot);

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
            if (args?.Graphics != null && MaxDailyLoss > 0)
                DrawRiskZones(args.Graphics, args.Rectangle);

            base.OnPaintChart(args);

            if (args?.Graphics != null && MaxDailyLoss > 0)
                DrawColoredRemainingLine(args.Graphics, args.Rectangle, args.LeftVisibleBarIndex, args.RightVisibleBarIndex);

            if (!HudEnabled || !state.HasSnapshot || args?.Graphics == null)
                return;

            DrawHud(args.Graphics, args.Rectangle, state.Snapshot);
        }

        protected override bool OnTryGetMinMax(int fromOffset, int toOffset, out double min, out double max)
        {
            if (MaxDailyLoss <= 0)
            {
                min = 0;
                max = 0;
                return false;
            }

            GetRelativeScaleBounds(out min, out max);
            return true;
        }

        private void DrawRiskZones(Graphics graphics, Rectangle panel)
        {
            if (panel.Width <= 0 || panel.Height <= 0)
                return;

            GetRelativeScaleBounds(out double min, out double max);

            FillRiskZone(graphics, panel, min, max, min, MaxDailyLoss * 0.25, Color.FromArgb(26, Color.Crimson));
            FillRiskZone(graphics, panel, min, max, MaxDailyLoss * 0.25, MaxDailyLoss * 0.5, Color.FromArgb(20, Color.Orange));
            FillRiskZone(graphics, panel, min, max, MaxDailyLoss * 0.5, max, Color.FromArgb(16, Color.LimeGreen));
        }

        private static void FillRiskZone(Graphics graphics, Rectangle panel, double min, double max, double valueFrom, double valueTo, Color color)
        {
            int top = ValueToY(panel, min, max, valueTo);
            int bottom = ValueToY(panel, min, max, valueFrom);
            int height = Math.Max(1, bottom - top);

            using SolidBrush brush = new SolidBrush(color);
            graphics.FillRectangle(brush, panel.Left, top, panel.Width, height);
        }

        private static int ValueToY(Rectangle panel, double min, double max, double value)
        {
            if (max <= min)
                return panel.Bottom;

            double clamped = Math.Max(min, Math.Min(max, value));
            double ratio = (clamped - min) / (max - min);
            return panel.Bottom - (int)Math.Round(ratio * panel.Height);
        }

        private void DrawColoredRemainingLine(Graphics graphics, Rectangle panel, int leftVisibleBarIndex, int rightVisibleBarIndex)
        {
            if (Count < 2 || panel.Width <= 0 || panel.Height <= 0)
                return;

            int startIndex = Math.Max(0, leftVisibleBarIndex);
            int endIndex = Math.Min(Count - 1, rightVisibleBarIndex);

            if (endIndex <= startIndex)
                return;

            GetRelativeScaleBounds(out double min, out double max);

            double? previousValue = null;
            Point? previousPoint = null;

            for (int barIndex = startIndex; barIndex <= endIndex; barIndex++)
            {
                int offset = Count - 1 - barIndex;
                double value = GetValue(RemainingLineIndex, offset);

                if (!IsFinite(value))
                {
                    previousValue = null;
                    previousPoint = null;
                    continue;
                }

                int x = panel.Left + (int)Math.Round((barIndex - startIndex) / (double)(endIndex - startIndex) * panel.Width);
                int y = ValueToY(panel, min, max, value);
                Point point = new Point(x, y);

                if (previousPoint.HasValue && previousValue.HasValue)
                    DrawRemainingSegment(graphics, previousPoint.Value, point, previousValue.Value, value);

                previousValue = value;
                previousPoint = point;
            }
        }

        private void DrawRemainingSegment(Graphics graphics, Point from, Point to, double previousValue, double value)
        {
            Color color = value < MaxDailyLoss * 0.25
                ? Color.Crimson
                : value >= previousValue ? Color.LimeGreen : Color.Red;

            using Pen pen = new Pen(color, 2.5f);
            graphics.DrawLine(pen, from, to);
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
            string text = BuildHudText(snapshot);

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
            ResetSessionAlerts();
            RestoreSessionState();
        }

        private void EnsureCurrentSession(DateTime nowUtc)
        {
            DateTime sessionStartUtc = sessionClock.GetSessionStartUtc(nowUtc);
            if (currentSessionStartUtc == sessionStartUtc)
                return;

            currentSessionStartUtc = sessionStartUtc;
            currentSessionDateKey = sessionClock.GetSessionDateKey(sessionStartUtc);
            state.Reset();
            ResetSessionAlerts();
            RestoreSessionState();
        }

        private void RestoreSessionState()
        {
            if (RestorePeakFromCache())
            {
                recoveryStatus = "cache";
                return;
            }

            if (EnableHistoricalRecovery && RestorePeakFromClosedPositions())
            {
                recoveryStatus = "closed positions";
                return;
            }

            recoveryStatus = EnableHistoricalRecovery ? "current equity" : "disabled";
        }

        private bool RestorePeakFromCache()
        {
            if (SelectedAccount == null || cacheStore == null || string.IsNullOrWhiteSpace(currentSessionDateKey))
                return false;

            if (cacheStore.TryReadLastSnapshot(SelectedAccount.Name, currentSessionDateKey, out DclSnapshot snapshot))
            {
                state.RestoreDailyPeak(snapshot.DailyPeakBalance);
                return true;
            }

            return false;
        }

        private bool RestorePeakFromClosedPositions()
        {
            if (SelectedAccount == null || currentSessionStartUtc == null)
                return false;

            double currentEquity = SelectedAccount.Balance + GetOpenProfitLoss(SelectedAccount);
            var sessionPnls = Core.Instance.ClosedPositions
                .Where(position => TradingObjectBelongsToAccount(position, SelectedAccount))
                .Select(position => new
                {
                    CloseTimeUtc = TryGetCloseTimeUtc(position, out DateTime closeTimeUtc) ? closeTimeUtc : DateTime.MinValue,
                    PnL = GetTradingObjectPnL(position)
                })
                .Where(item => item.CloseTimeUtc >= currentSessionStartUtc.Value)
                .OrderBy(item => item.CloseTimeUtc)
                .ToArray();

            if (sessionPnls.Length == 0)
                return false;

            double runningBalance = SelectedAccount.Balance - sessionPnls.Sum(item => item.PnL);
            double peak = Math.Max(runningBalance, currentEquity);

            foreach (var item in sessionPnls)
            {
                runningBalance += item.PnL;
                peak = Math.Max(peak, runningBalance);
            }

            if (peak <= currentEquity)
                return false;

            state.RestoreDailyPeak(peak);
            return true;
        }

        private void PlotRelativeRisk(DclSnapshot snapshot)
        {
            SetValue(snapshot.RemainingDailyLimit, RemainingLineIndex);
            SetValue(MaxDailyLoss, MaxLimitLineIndex);
            SetValue(0, LiquidationLineIndex);
        }

        private string BuildHudText(DclSnapshot snapshot)
        {
            string text =
                $"DLL rem: {FormatCurrency(snapshot.RemainingDailyLimit)}\n" +
                $"DCL: {FormatCurrency(snapshot.DailyCumulativeLoss)}\n" +
                $"Peak: {FormatCurrency(snapshot.DailyPeakBalance)}\n" +
                $"Equity: {FormatCurrency(snapshot.CurrentEquity)}";

            if (!ShowDiagnostics)
                return text;

            string lastWrite = cacheStore?.LastWriteUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "none";
            string cacheStatus = cacheStore?.LastReadStatus ?? "none";
            string cacheError = string.IsNullOrWhiteSpace(cacheStore?.LastError) ? "ok" : Truncate(cacheStore.LastError, 42);

            return text +
                $"\nSession: {currentSessionDateKey}" +
                $"\nRecovery: {recoveryStatus}" +
                $"\nCache: {cacheStatus}" +
                $"\nWrites: {cacheStore?.WriteCount ?? 0} @ {lastWrite}" +
                $"\nI/O: {cacheError}";
        }

        private void GetRelativeScaleBounds(out double min, out double max)
        {
            min = state.HasSnapshot ? Math.Min(0, state.Snapshot.RemainingDailyLimit) : 0;
            max = MaxDailyLoss;

            double padding = Math.Max(MaxDailyLoss * 0.05, 1);
            min -= padding;
            max += padding;
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

            CheckRiskAlerts(snapshot);
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
            return GetTradingObjectPnL(position);
        }

        private static double GetTradingObjectPnL(object tradingObject)
        {
            if (TryReadNumericProperty(tradingObject, "NetPnL", out double netPnL))
                return netPnL;

            if (TryReadNumericProperty(tradingObject, "NetPnl", out netPnL))
                return netPnL;

            if (TryReadNumericProperty(tradingObject, "GrossPnL", out double grossPnL))
                return grossPnL;

            if (TryReadNumericProperty(tradingObject, "GrossPnl", out grossPnL))
                return grossPnL;

            return 0;
        }

        private static bool TryGetCloseTimeUtc(object tradingObject, out DateTime closeTimeUtc)
        {
            string[] propertyNames = { "CloseTime", "ClosedTime", "CloseDateTime", "DateTime", "LastUpdateTime" };

            foreach (string propertyName in propertyNames)
            {
                if (TryReadDateTimeProperty(tradingObject, propertyName, out DateTime value))
                {
                    closeTimeUtc = ToUtc(value);
                    return true;
                }
            }

            closeTimeUtc = default;
            return false;
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

        private static bool TryReadDateTimeProperty(object source, string propertyName, out DateTime value)
        {
            value = default;

            if (!TryReadObjectProperty(source, propertyName, out object rawValue))
                return false;

            if (rawValue is DateTime dateTime)
            {
                value = dateTime;
                return true;
            }

            return DateTime.TryParse(rawValue.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
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

        private void CheckRiskAlerts(DclSnapshot snapshot)
        {
            if (!EnablePlatformAlerts || MaxDailyLoss <= 0)
                return;

            DclRiskLevel riskLevel = GetRiskLevel(snapshot);

            if (riskLevel == DclRiskLevel.Critical && !criticalAlertSent)
            {
                criticalAlertSent = true;
                warningAlertSent = true;
                SendRiskAlert("CRITICAL", snapshot);
                return;
            }

            if (riskLevel == DclRiskLevel.Warning && !warningAlertSent)
            {
                warningAlertSent = true;
                SendRiskAlert("WARNING", snapshot);
            }
        }

        private void SendRiskAlert(string severity, DclSnapshot snapshot)
        {
            try
            {
                string accountName = SelectedAccount?.Name ?? "selected account";
                string message = $"DCL {severity} - {accountName}: {FormatCurrency(snapshot.RemainingDailyLimit)} remaining, {FormatCurrency(snapshot.DailyCumulativeLoss)} used.";
                Core.Instance.Alert(message, string.Empty, string.Empty, null, "Daily Cumulative Loss");
            }
            catch
            {
            }
        }

        private void ResetSessionAlerts()
        {
            warningAlertSent = false;
            criticalAlertSent = false;
        }

        private static DateTime ToUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
                return dateTime;

            if (dateTime.Kind == DateTimeKind.Local)
                return dateTime.ToUniversalTime();

            return dateTime;
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

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            return value.Substring(0, maxLength - 3) + "...";
        }

        private void BreakAllLines()
        {
            SetLineBreak(0, RemainingLineIndex);
            SetLineBreak(0, MaxLimitLineIndex);
            SetLineBreak(0, LiquidationLineIndex);
        }
    }
}
