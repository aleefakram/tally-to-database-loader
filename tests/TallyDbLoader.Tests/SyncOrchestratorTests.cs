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
            var profile = new CompanyProfile
            {
                Enabled = true,
                IntervalMinutes = 15,
                LastRunAt = DateTime.UtcNow.AddMinutes(-16)
            };
            
            bool shouldRun = SyncOrchestrator.ShouldRun(profile, DateTime.UtcNow);
            Assert.True(shouldRun);
        }

        [Fact]
        public void Test_ShouldNotRun_IntervalNotElapsed()
        {
            var profile = new CompanyProfile
            {
                Enabled = true,
                IntervalMinutes = 15,
                LastRunAt = DateTime.UtcNow.AddMinutes(-10)
            };
            
            bool shouldRun = SyncOrchestrator.ShouldRun(profile, DateTime.UtcNow);
            Assert.False(shouldRun);
        }

        [Fact]
        public void Test_ShouldNotRun_Disabled()
        {
            var profile = new CompanyProfile
            {
                Enabled = false,
                IntervalMinutes = 15,
                LastRunAt = DateTime.UtcNow.AddMinutes(-20)
            };
            
            bool shouldRun = SyncOrchestrator.ShouldRun(profile, DateTime.UtcNow);
            Assert.False(shouldRun);
        }
    }
}
