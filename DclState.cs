using System;

namespace DailyCumulativeLoss
{
    internal sealed class DclState
    {
        public bool HasSnapshot { get; private set; }
        public DclSnapshot Snapshot { get; private set; }

        public void Reset()
        {
            HasSnapshot = false;
            Snapshot = default;
        }

        public void RestoreDailyPeak(double dailyPeakBalance)
        {
            Snapshot = new DclSnapshot(
                DateTime.UtcNow,
                0,
                0,
                dailyPeakBalance,
                dailyPeakBalance,
                0,
                0,
                0);

            HasSnapshot = true;
        }

        public DclSnapshot Update(DateTime timestampUtc, double balance, double openPnL, double maxDailyLoss)
        {
            double currentEquity = balance + openPnL;
            double dailyPeakBalance = HasSnapshot
                ? Math.Max(Snapshot.DailyPeakBalance, currentEquity)
                : currentEquity;

            double dailyCumulativeLoss = dailyPeakBalance - currentEquity;
            double remainingDailyLimit = maxDailyLoss - dailyCumulativeLoss;
            double liquidationThreshold = dailyPeakBalance - maxDailyLoss;

            Snapshot = new DclSnapshot(
                timestampUtc,
                balance,
                openPnL,
                currentEquity,
                dailyPeakBalance,
                dailyCumulativeLoss,
                remainingDailyLimit,
                liquidationThreshold);

            HasSnapshot = true;
            return Snapshot;
        }
    }
}
