using System;

namespace DailyCumulativeLoss
{
    internal sealed class SessionClock
    {
        private readonly TimeSpan resetTime;
        private readonly TimeZoneInfo timeZone;

        public SessionClock(TimeSpan resetTime)
        {
            this.resetTime = resetTime;
            timeZone = ResolveParisTimeZone();
        }

        public DateTime GetSessionStartUtc(DateTime utcNow)
        {
            DateTime localNow = ToLocalTime(utcNow);
            DateTime localSessionStart = localNow.TimeOfDay < resetTime
                ? localNow.Date.AddDays(-1).Add(resetTime)
                : localNow.Date.Add(resetTime);

            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localSessionStart, DateTimeKind.Unspecified), timeZone);
        }

        public DateTime ToLocalTime(DateTime utcDateTime)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc), timeZone);
        }

        public string GetSessionDateKey(DateTime sessionStartUtc)
        {
            return ToLocalTime(sessionStartUtc).ToString("yyyyMMdd");
        }

        private static TimeZoneInfo ResolveParisTimeZone()
        {
            string[] ids = { "Europe/Paris", "Romance Standard Time" };

            foreach (string id in ids)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch
                {
                }
            }

            return TimeZoneInfo.Local;
        }
    }
}
