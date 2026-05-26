namespace DailyCumulativeLoss
{
    internal readonly struct HistoricalRecoveryResult
    {
        private HistoricalRecoveryResult(
            bool restored,
            double dailyPeakBalance,
            string status,
            int closedPositionCount,
            int replayedPositionCount,
            int replayedHistoryItemCount,
            string lastError)
        {
            Restored = restored;
            DailyPeakBalance = dailyPeakBalance;
            Status = status;
            ClosedPositionCount = closedPositionCount;
            ReplayedPositionCount = replayedPositionCount;
            ReplayedHistoryItemCount = replayedHistoryItemCount;
            LastError = lastError;
        }

        public bool Restored { get; }
        public double DailyPeakBalance { get; }
        public string Status { get; }
        public int ClosedPositionCount { get; }
        public int ReplayedPositionCount { get; }
        public int ReplayedHistoryItemCount { get; }
        public string LastError { get; }

        public static HistoricalRecoveryResult CreateRestored(
            double dailyPeakBalance,
            string status,
            int closedPositionCount,
            int replayedPositionCount,
            int replayedHistoryItemCount,
            string lastError)
        {
            return new HistoricalRecoveryResult(
                true,
                dailyPeakBalance,
                status,
                closedPositionCount,
                replayedPositionCount,
                replayedHistoryItemCount,
                lastError);
        }

        public static HistoricalRecoveryResult NotRestored(string status)
        {
            return new HistoricalRecoveryResult(false, 0, status, 0, 0, 0, status);
        }
    }
}
