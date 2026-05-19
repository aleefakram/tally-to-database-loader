using System;
using Xunit;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Sync;

namespace TallyDbLoader.Tests
{
    public class SyncOrchestratorTests
    {
        [Fact]
        public void Test_ShouldRunJob_Interval()
        {
            var job = new SyncJob
            {
                SyncIntervalMinutes = 15,
                LastRunTime = DateTime.UtcNow.AddMinutes(-16).ToString("o")
            };
            
            bool shouldRun = SyncOrchestrator.ShouldRun(job, DateTime.UtcNow);
            Assert.True(shouldRun);
        }
        
        [Fact]
        public void Test_ShouldRunJob_TimeOfDay()
        {
            var job = new SyncJob
            {
                DailyTimeLocal = "02:00:00",
                LastRunTime = DateTime.Today.AddDays(-1).AddHours(2).ToString("o")
            };
            
            // Test running at exactly 02:05 AM today
            var now = DateTime.Today.AddHours(2).AddMinutes(5);
            bool shouldRun = SyncOrchestrator.ShouldRun(job, now);
            Assert.True(shouldRun);
        }
    }
}
