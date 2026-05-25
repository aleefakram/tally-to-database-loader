using System;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Sync
{
    public static class SyncOrchestrator
    {
        public static bool ShouldRun(CompanyProfile profile, DateTime now)
        {
            if (!profile.Enabled) return false;
            if (!profile.LastRunAt.HasValue) return true;

            var timeElapsed = now - profile.LastRunAt.Value;
            return timeElapsed.TotalMinutes >= profile.IntervalMinutes;
        }
    }
}
