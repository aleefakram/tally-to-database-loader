using System;
using System.IO;
using Xunit;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using Microsoft.Data.Sqlite;
using Dapper;

namespace TallyDbLoader.Tests
{
    public class SyncLifecycleSafetyTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly ConfigRepository _repo;

        public SyncLifecycleSafetyTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"tally_test_{Guid.NewGuid()}.db");
            DatabaseHelper.InitializeDatabase(_dbPath);
            _repo = new ConfigRepository(_dbPath);
        }

        public void Dispose()
        {
            if (File.Exists(_dbPath))
            {
                try { File.Delete(_dbPath); } catch { }
            }
        }

        private CompanyProfile SeedCompany(string status, bool enabled = true)
        {
            var uniqueDbName = "TestDb_" + Guid.NewGuid().ToString("N");
            var dbProfile = new DatabaseProfile { Name = uniqueDbName, Technology = "sqlite" };
            _repo.SaveDatabaseProfile(dbProfile);
            var dbFromDb = _repo.GetDatabaseProfileByName(uniqueDbName);

            var profile = new CompanyProfile
            {
                Name = Guid.NewGuid().ToString(),
                DbProfileId = dbFromDb.Id,
                TargetCatalog = "test",
                Status = status,
                Enabled = enabled
            };
            _repo.SaveCompanyProfile(profile);
            
            // Load back to get auto-generated ID
            var all = _repo.GetAllCompanyProfiles();
            return all.Find(x => x.Name == profile.Name);
        }

        [Fact]
        public void TryStartCompanyProfile_WithIdleStatus_Succeeds()
        {
            var profile = SeedCompany("idle");
            bool started = _repo.TryStartCompanyProfile(profile.Id);
            Assert.True(started);

            var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.Equal("running", updated.Status);
        }

        [Fact]
        public void TryStartCompanyProfile_WithRunningOrBlockedStatus_Fails()
        {
            foreach (var status in new[] { "running", "review_required", "attention_required", "unknown" })
            {
                var profile = SeedCompany(status);
                bool started = _repo.TryStartCompanyProfile(profile.Id);
                Assert.False(started);
            }
        }

        [Fact]
        public void MarkCompanyProfileUnknown_SetsStatusToUnknown()
        {
            var profile = SeedCompany("running");
            _repo.MarkCompanyProfileUnknown(profile.Id, "Metadata failed", DateTime.Now);

            var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.Equal("unknown", updated.Status);
        }

        [Fact]
        public void ReconcileStaleRuns_ReconcilesRunningJobsAndSyncRuns()
        {
            var profile = SeedCompany("running");
            
            var run = new SyncRun
            {
                CompanyId = profile.Id,
                CompanyName = profile.Name,
                StartedAt = DateTime.Now.AddMinutes(-5),
                Mode = "full",
                Status = "running"
            };
            _repo.AddSyncRun(run);

            _repo.ReconcileStaleRuns(DateTime.Now);

            var updatedProfile = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.Equal("unknown", updatedProfile.Status);

            var runs = _repo.GetSyncRunsForCompany(profile.Id);
            Assert.Single(runs);
            Assert.Equal("unknown", runs[0].Status);
            Assert.Contains("Interrupted by application restart", runs[0].ResultSummary);
        }

        [Fact]
        public void AddSyncRun_SetsEndedAtToNullForActiveRuns()
        {
            var profile = SeedCompany("idle");
            var run = new SyncRun
            {
                CompanyId = profile.Id,
                CompanyName = profile.Name,
                StartedAt = DateTime.Now,
                Mode = "full",
                Status = "running"
            };
            _repo.AddSyncRun(run);

            // Query database directly to assert SQLite column is written as NULL
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                var endedAt = conn.QuerySingle<string?>("SELECT ended_at FROM sync_runs WHERE company_id = @CompanyId", new { CompanyId = profile.Id });
                Assert.Null(endedAt);
            }
        }
    }
}
