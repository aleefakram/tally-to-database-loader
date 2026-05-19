using System;
using System.Globalization;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Sync
{
    public static class SyncOrchestrator
    {
        public static bool ShouldRun(SyncJob job, DateTime now)
        {
            // 1. Interval-based triggering (e.g. every 15 minutes)
            if (job.SyncIntervalMinutes.HasValue && job.SyncIntervalMinutes.Value > 0)
            {
                if (string.IsNullOrEmpty(job.LastRunTime)) return true;
                if (DateTime.TryParse(job.LastRunTime, null, DateTimeStyles.RoundtripKind, out var lastRun))
                {
                    return (now - lastRun.ToUniversalTime()).TotalMinutes >= job.SyncIntervalMinutes.Value;
                }
                return true;
            }
            
            // 2. Daily time-of-day triggering (e.g. 2:00 AM local time)
            if (!string.IsNullOrEmpty(job.DailyTimeLocal))
            {
                if (TimeSpan.TryParse(job.DailyTimeLocal, out var targetTime))
                {
                    var targetToday = now.Date.Add(targetTime);
                    
                    // If target time is past in the current day, check if we already ran it today
                    if (now >= targetToday)
                    {
                        if (string.IsNullOrEmpty(job.LastRunTime)) return true;
                        if (DateTime.TryParse(job.LastRunTime, null, DateTimeStyles.RoundtripKind, out var lastRun))
                        {
                            // Compare using the local timezone representation of the run date
                            return lastRun.ToLocalTime().Date < now.Date;
                        }
                        return true;
                    }
                }
            }
            
            return false;
        }
    }
}
