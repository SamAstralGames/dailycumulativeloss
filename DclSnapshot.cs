using System;

namespace DailyCumulativeLoss
{
    internal readonly struct DclSnapshot
    {
        public DclSnapshot(
            DateTime timestampUtc,
            double balance,
            double openPnL,
            double currentEquity,
            double dailyPeakBalance,
            double dailyCumulativeLoss,
            double remainingDailyLimit,
            double liquidationThreshold)
        {
            TimestampUtc = timestampUtc;
            Balance = balance;
            OpenPnL = openPnL;
            CurrentEquity = currentEquity;
            DailyPeakBalance = dailyPeakBalance;
            DailyCumulativeLoss = dailyCumulativeLoss;
            RemainingDailyLimit = remainingDailyLimit;
            LiquidationThreshold = liquidationThreshold;
        }

        public DateTime TimestampUtc { get; }
        public double Balance { get; }
        public double OpenPnL { get; }
        public double CurrentEquity { get; }
        public double DailyPeakBalance { get; }
        public double DailyCumulativeLoss { get; }
        public double RemainingDailyLimit { get; }
        public double LiquidationThreshold { get; }
    }
}
