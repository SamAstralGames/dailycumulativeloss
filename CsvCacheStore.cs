using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DailyCumulativeLoss
{
    internal sealed class CsvCacheStore
    {
        private const string Header = "Timestamp_UTC;Timestamp_Local;Balance;OpenPnL;CurrentEquity;DailyPeakBalance;DailyCumulativeLoss";
        private readonly object fileLock = new object();
        private readonly object queueLock = new object();
        private readonly string cacheDirectory;
        private Task writeQueue = Task.CompletedTask;

        public string LastReadStatus { get; private set; } = "not checked";
        public string LastError { get; private set; } = string.Empty;
        public string LastPath { get; private set; } = string.Empty;
        public int WriteCount { get; private set; }
        public DateTime? LastWriteUtc { get; private set; }

        public CsvCacheStore(string cacheDirectory)
        {
            this.cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DailyCumulativeLoss")
                : cacheDirectory;
        }

        public bool TryReadLastSnapshot(string accountName, string sessionDateKey, out DclSnapshot snapshot)
        {
            snapshot = default;
            string path = GetCachePath(accountName, sessionDateKey);
            LastPath = path;
            LastError = string.Empty;

            if (!File.Exists(path))
            {
                LastReadStatus = "cache missing";
                return false;
            }

            try
            {
                foreach (string line in File.ReadLines(path).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Timestamp_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryParseSnapshot(line, out snapshot))
                    {
                        LastReadStatus = "cache restored";
                        return true;
                    }
                }

                LastReadStatus = "cache invalid";
                return false;
            }
            catch (Exception ex)
            {
                LastReadStatus = "cache read error";
                LastError = ex.Message;
                return false;
            }
        }

        public void AppendAsync(string accountName, string sessionDateKey, DclSnapshot snapshot, DateTime localTimestamp)
        {
            string path = GetCachePath(accountName, sessionDateKey);
            string line = FormatLine(snapshot, localTimestamp);

            lock (queueLock)
                writeQueue = writeQueue.ContinueWith(_ => Append(path, line), TaskScheduler.Default);
        }

        private void Append(string path, string line)
        {
            try
            {
                lock (fileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

                    using (StreamWriter writer = new StreamWriter(path, append: true, Encoding.UTF8))
                    {
                        if (writeHeader)
                            writer.WriteLine(Header);

                        writer.WriteLine(line);
                    }

                    WriteCount++;
                    LastWriteUtc = DateTime.UtcNow;
                    LastPath = path;
                    LastError = string.Empty;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private string GetCachePath(string accountName, string sessionDateKey)
        {
            string safeAccountName = SanitizeFileName(accountName);
            return Path.Combine(cacheDirectory, $"DailyCumulativeLoss_Cache_{safeAccountName}_{sessionDateKey}.csv");
        }

        private static string FormatLine(DclSnapshot snapshot, DateTime localTimestamp)
        {
            return string.Join(";",
                snapshot.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                localTimestamp.ToString("O", CultureInfo.InvariantCulture),
                FormatDouble(snapshot.Balance),
                FormatDouble(snapshot.OpenPnL),
                FormatDouble(snapshot.CurrentEquity),
                FormatDouble(snapshot.DailyPeakBalance),
                FormatDouble(snapshot.DailyCumulativeLoss));
        }

        private static bool TryParseSnapshot(string line, out DclSnapshot snapshot)
        {
            snapshot = default;
            string[] parts = line.Split(';');

            if (parts.Length < 7)
                return false;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime timestampUtc))
                return false;

            if (!TryParseDouble(parts[2], out double balance) ||
                !TryParseDouble(parts[3], out double openPnL) ||
                !TryParseDouble(parts[4], out double currentEquity) ||
                !TryParseDouble(parts[5], out double dailyPeakBalance) ||
                !TryParseDouble(parts[6], out double dailyCumulativeLoss))
                return false;

            snapshot = new DclSnapshot(
                timestampUtc,
                balance,
                openPnL,
                currentEquity,
                dailyPeakBalance,
                dailyCumulativeLoss,
                0,
                0);

            return true;
        }

        private static bool TryParseDouble(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "UnknownAccount";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] sanitized = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
            return new string(sanitized);
        }
    }
}
