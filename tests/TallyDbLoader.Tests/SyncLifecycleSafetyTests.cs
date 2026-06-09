using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.DatabaseLoaders;
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
            Assert.NotNull(dbFromDb);

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
            var found = all.Find(x => x.Name == profile.Name);
            Assert.NotNull(found);
            return found;
        }

        [Fact]
        public void TryStartCompanyProfile_WithIdleStatus_Succeeds()
        {
            var profile = SeedCompany("idle");
            bool started = _repo.TryStartCompanyProfile(profile.Id);
            Assert.True(started);

            var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.NotNull(updated);
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
            Assert.NotNull(updated);
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
            Assert.NotNull(updatedProfile);
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

        [Fact]
        public void TryRequestManualSync_Accepts_EligibleJob()
        {
            var profile = SeedCompany("idle");
            using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
            {
                worker.Start(startScheduler: false); // Starts in preflight-only mock mode without background thread loop
                var result = worker.TryRequestManualSync(profile.Id);
                Assert.True(result.Accepted);
                Assert.Equal("PendingDispatch", result.ReasonCode);
            }
        }

        [Fact]
        public void TryRequestManualSync_Rejects_DisabledJob()
        {
            var profile = SeedCompany("idle", enabled: false);
            using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
            {
                worker.Start(startScheduler: false);
                var result = worker.TryRequestManualSync(profile.Id);
                Assert.False(result.Accepted);
                Assert.Equal("Disabled", result.ReasonCode);
            }
        }

        [Fact]
        public void TryRequestManualSync_Rejects_SafetyBlockedJob()
        {
            foreach (var status in new[] { "review_required", "attention_required", "unknown" })
            {
                var profile = SeedCompany(status);
                using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
                {
                    worker.Start(startScheduler: false);
                    var result = worker.TryRequestManualSync(profile.Id);
                    Assert.False(result.Accepted);
                    Assert.Equal("SafetyBlocked", result.ReasonCode);
                }
            }
        }

        [Fact]
        public void TryRequestManualSync_Rejects_AlreadyRunningJob()
        {
            using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
            {
                worker.Start(startScheduler: false);
                var profile = SeedCompany("running");
                var result = worker.TryRequestManualSync(profile.Id);
                Assert.False(result.Accepted);
                Assert.Equal("AlreadyRunning", result.ReasonCode);
            }
        }

        [Fact]
        public void TryRequestManualSyncAll_Accepts_EligibleWorker()
        {
            using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
            {
                worker.Start(startScheduler: false);
                var result = worker.TryRequestManualSyncAll();
                Assert.True(result.Accepted);
                Assert.Equal("PendingDispatch", result.ReasonCode);
            }
        }

        [Fact]
        public void TryRequestManualSyncAll_Rejects_WorkerBusy()
        {
            using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
            {
                worker.Start(startScheduler: false);
                var result1 = worker.TryRequestManualSyncAll();
                Assert.True(result1.Accepted);

                var result2 = worker.TryRequestManualSyncAll();
                Assert.False(result2.Accepted);
                Assert.Equal("WorkerBusy", result2.ReasonCode);
            }
        }

        [Fact]
        public async Task IncrementalSync_DoesNotAdvanceWatermark_OnFailure()
        {
            // 1. Initialize temporary test database
            string dbFile = Path.Combine(Path.GetTempPath(), $"tally_watermark_test_{Guid.NewGuid()}.db");
            var connStr = $"Data Source={dbFile}";
            
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                await conn.OpenAsync();
                
                // Initialize watermark schema by creating config table directly
                await conn.ExecuteAsync("CREATE TABLE config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024))");
                
                var watermarkRepo = new WatermarkRepository(conn);
                
                // Seed initial watermarks
                await watermarkRepo.WriteAsync(100, 200);
                
                // 2. Instantiate loader and inject a failing run simulation
                var client = new SafetyFakeTallyClient();
                var dbLoader = new FakeFailingDatabaseLoader();
                var runner = new IncrementalSyncRunner(client, dbLoader);

                // Configure config with some master/txn tables so it does work and triggers the loader
                var config = new TallyExportConfig
                {
                    Master = new List<TableConfig>
                    {
                        new TableConfig
                        {
                            Name = "mst_group",
                            Collection = "Group",
                            Nature = "Primary",
                            Fields = new List<FieldConfig> { new() { Name = "guid", Field = "Guid", Type = "text" } }
                        }
                    },
                    Transaction = new List<TableConfig>()
                };
                
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await runner.RunAsync(config, "TestCompany", DateTime.Today, DateTime.Today, conn, 100, 200);
                });
                
                // 3. Confirm watermarks remain unchanged
                var (master, txn) = await watermarkRepo.ReadAsync();
                Assert.Equal(100, master);
                Assert.Equal(200, txn);
            }
            
            if (File.Exists(dbFile))
            {
                try { File.Delete(dbFile); } catch { }
            }
        }

        [Fact]
        public void ResolveCompanyProfileSafetyState_Success_UpdatesStatusAndLogsAudit()
        {
            var profile = SeedCompany("attention_required");
            DateTime resolvedAt = DateTime.Now;

            long auditId = _repo.ResolveCompanyProfileSafetyState(profile.Id, "OperatorName", "Resolved network issue", resolvedAt);
            Assert.True(auditId > 0);

            // Assert status was updated to idle
            var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.NotNull(updated);
            Assert.Equal("idle", updated.Status);

            // Assert audit log entry exists and contains correct information
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                var actor = conn.ExecuteScalar<string>("SELECT actor FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var action = conn.ExecuteScalar<string>("SELECT action FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var entityType = conn.ExecuteScalar<string>("SELECT entity_type FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var entityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var entityName = conn.ExecuteScalar<string>("SELECT entity_name FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var reason = conn.ExecuteScalar<string>("SELECT reason FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var createdAt = conn.ExecuteScalar<string>("SELECT created_at FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE id = @Id", new { Id = auditId });
                var afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE id = @Id", new { Id = auditId });

                Assert.Equal("OperatorName", actor);
                Assert.Equal("resolve_safety_state", action);
                Assert.Equal("company_profile", entityType);
                Assert.Equal((long)profile.Id, entityId);
                Assert.Equal(profile.Name, entityName);
                Assert.Equal("Resolved network issue", reason);
                Assert.Equal(resolvedAt.ToString("o"), createdAt);
                Assert.NotNull(beforeJson);
                Assert.Contains("\"status\":\"attention_required\"", beforeJson);
                Assert.NotNull(afterJson);
                Assert.Contains("\"status\":\"idle\"", afterJson);
            }
        }

        [Fact]
        public void ResolveCompanyProfileSafetyState_EmptyInputs_ThrowsArgumentException()
        {
            var profile = SeedCompany("attention_required");

            Assert.Throws<ArgumentException>(() => 
                _repo.ResolveCompanyProfileSafetyState(profile.Id, "   ", "Reason", DateTime.Now));

            Assert.Throws<ArgumentException>(() => 
                _repo.ResolveCompanyProfileSafetyState(profile.Id, "Operator", "", DateTime.Now));
        }

        [Fact]
        public void ResolveCompanyProfileSafetyState_Success_ReviewRequired_UpdatesStatusAndLogsAudit()
        {
            var profile = SeedCompany("review_required");
            DateTime resolvedAt = DateTime.Now;

            long auditId = _repo.ResolveCompanyProfileSafetyState(profile.Id, "OperatorName", "Resolved schema issue", resolvedAt);
            Assert.True(auditId > 0);

            var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.NotNull(updated);
            Assert.Equal("idle", updated.Status);
        }

        [Fact]
        public void ResolveCompanyProfileSafetyState_Success_Unknown_UpdatesStatusAndLogsAudit()
        {
            var profile = SeedCompany("unknown");
            DateTime resolvedAt = DateTime.Now;

            long auditId = _repo.ResolveCompanyProfileSafetyState(profile.Id, "OperatorName", "Resolved unknown issue", resolvedAt);
            Assert.True(auditId > 0);

            var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.NotNull(updated);
            Assert.Equal("idle", updated.Status);
        }

        [Fact]
        public void ResolveCompanyProfileSafetyState_InvalidStatus_RejectsCompletedFailedRunning()
        {
            foreach (var status in new[] { "idle", "completed", "failed", "running" })
            {
                var profile = SeedCompany(status);

                Assert.Throws<InvalidOperationException>(() => 
                    _repo.ResolveCompanyProfileSafetyState(profile.Id, "Operator", "Reason", DateTime.Now));
            }
        }

        [Fact]
        public void ResolveCompanyProfileSafetyState_MissingProfile_ThrowsKeyNotFoundException()
        {
            Assert.Throws<KeyNotFoundException>(() => 
                _repo.ResolveCompanyProfileSafetyState(999999, "Operator", "Reason", DateTime.Now));
        }

        [Fact]
        public void ResolveCompanyProfileSafetyState_AuditInsertFailure_RollsBackTransaction()
        {
            var profile = SeedCompany("review_required");

            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                // Drop config_audit_log table temporarily inside the DB connection
                conn.Execute("DROP TABLE config_audit_log;");
            }

            // The insert will fail because table is dropped, throwing InvalidOperationException
            var ex = Assert.Throws<InvalidOperationException>(() => 
                _repo.ResolveCompanyProfileSafetyState(profile.Id, "Operator", "Reason", DateTime.Now));
            
            Assert.NotNull(ex.InnerException);

            // Assert company profile status remains review_required (rolled back)
            var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
            Assert.NotNull(updated);
            Assert.Equal("review_required", updated.Status);
        }
    }

    public class SafetyFakeTallyClient : ITallyClient
    {
        public Task<string> PostXMLAsync(string xmlRequest) => Task.FromResult("");
        public Task<string> FetchLedgersXmlAsync(string companyName) => Task.FromResult("");
        public Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync() => Task.FromResult(new List<TallyCompanyInfo>());
        public Task<List<string>> FetchActiveCompaniesAsync() => Task.FromResult(new List<string> { "TestCompany" });
        public Task<TallyCompanyInfo?> FetchCompanyInfoAsync(string companyName) => Task.FromResult<TallyCompanyInfo?>(new TallyCompanyInfo
        {
            Name = "TestCompany",
            Guid = "guid",
            AltMstId = 999, // New alter ID
            AltVchId = 999  // New alter ID
        });
    }

    public class FakeFailingDatabaseLoader : IDatabaseLoader
    {
        public Task LoadBulkDataAsync(System.Data.DataTable data, string tableName) => Task.CompletedTask;
        public string TruncateSql(string tableName) => throw new InvalidOperationException("Simulated db write failure");
        public string CascadeUpdateSql(string primaryTable, string childTable, string field) => "";
        public string VoucherNumberUpdateSql() => "";
        public string CountAutoNumberVoucherTypesSql() => "";
    }
}
