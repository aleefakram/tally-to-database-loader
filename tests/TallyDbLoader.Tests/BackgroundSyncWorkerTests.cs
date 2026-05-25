using Xunit;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Tally;
using System.Collections.Generic;

namespace TallyDbLoader.Tests
{
    public class BackgroundSyncWorkerTests
    {
        private class MockHttpMessageHandler : HttpMessageHandler
        {
            public string CompanyInfoResponse { get; set; } = "\"guid-123\",\"TestCompany\",\"20260401\",\"20260519\",\"0\",\"0\",\"†\"";
            public string TableDataResponse { get; set; } = "<ENVELOPE><BODY><DATA><ROW><F01>guid-1</F01><F02>Ledger A</F02></ROW></DATA></BODY></ENVELOPE>";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var contentStr = "";
                // Inspect request XML content to decide response
                var requestContent = request.Content?.ReadAsStringAsync(cancellationToken).Result ?? "";
                if (requestContent.Contains("TallyDatabaseLoaderReport"))
                {
                    contentStr = CompanyInfoResponse;
                }
                else if (requestContent.Contains("MyReportLedgerTable"))
                {
                    contentStr = "<ENVELOPE><BODY><DATA><ROW><NAME>TestCompany</NAME><ISGROUP>false</ISGROUP></ROW></DATA></BODY></ENVELOPE>";
                }
                else
                {
                    contentStr = TableDataResponse;
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(contentStr, System.Text.Encoding.UTF8, "text/xml")
                };
                return Task.FromResult(response);
            }
        }

        [Fact]
        public async Task Test_BackgroundSyncWorker_Orchestration()
        {
            // Setup SQLite db
            var dbPath = "sync_test.db";
            var targetDbPath = "test_catalog.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
            DatabaseHelper.InitializeDatabase(dbPath);
            var repo = new ConfigRepository(dbPath);

            // Add db profile
            var profile = new DatabaseProfile
            {
                Name = "LocalPg",
                Technology = "sqlite",
                Server = "localhost",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };
            repo.SaveDatabaseProfile(profile);
            var savedProfile = repo.GetDatabaseProfileByName("LocalPg");

            // Add sync profile
            var job = new CompanyProfile
            {
                Name = "TestCompany",
                DbProfileId = savedProfile.Id,
                TargetCatalog = "test_catalog",
                IntervalMinutes = 1,
                Status = "Idle",
                Enabled = true
            };
            repo.SaveCompanyProfile(job);

            // Write temporary yaml config to execution directory
            var yamlContent = @"
master:
  - name: mst_test
    fields:
      - name: guid
        field: Guid
        type: text
      - name: name
        field: Name
        type: text
transaction: []
";
            var yamlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tally-export-config.yaml");
            File.WriteAllText(yamlPath, yamlContent);

            // Setup Mock Client
            var mockHandler = new MockHttpMessageHandler();
            var httpClient = new HttpClient(mockHandler);
            var tallyClient = new TallyDbLoader.Core.Tally.TallyClient(httpClient, "localhost", 9000);

            // Create worker and list logs
            var worker = new BackgroundSyncWorker(repo, "localhost", 9000);
            worker.SetTallyClientForTest(tallyClient);
            var logs = new List<string>();
            worker.OnLogMessage += (msg) => logs.Add(msg);

            // Run a mock sync loop
            worker.Start();
            
            // Wait up to 2 seconds for worker to process the job
            await Task.Delay(2000);
            worker.Stop();

            // Check if sync loop processed the job
            Assert.Contains(logs, l => l.Contains("Background Sync Engine started."));
            Assert.Contains(logs, l => l.Contains("Starting sync for company 'TestCompany'"));
            Assert.Contains(logs, l => l.Contains("sync finished. Wrote"));

            // Cleanup
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(yamlPath)) File.Delete(yamlPath);
            if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
        }

        [Fact]
        public async Task Test_BackgroundSyncWorker_IncrementalOrchestration()
        {
            // Setup SQLite db
            var dbPath = "sync_incremental_test.db";
            var targetDbPath = "test_catalog_inc.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
            DatabaseHelper.InitializeDatabase(dbPath);
            var repo = new ConfigRepository(dbPath);

            // Add db profile
            var profile = new DatabaseProfile
            {
                Name = "LocalPg",
                Technology = "sqlite",
                Server = "localhost",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };
            repo.SaveDatabaseProfile(profile);
            var savedProfile = repo.GetDatabaseProfileByName("LocalPg");

            // Add sync profile with incremental mode
            var job = new CompanyProfile
            {
                Name = "TestCompany",
                DbProfileId = savedProfile.Id,
                TargetCatalog = "test_catalog_inc",
                IntervalMinutes = 1,
                Status = "Idle",
                Mode = "incremental",
                Enabled = true
            };
            repo.SaveCompanyProfile(job);

            // Pre-initialize the target table so deletes don't fail
            var tableConfig = new TableConfig
            {
                Name = "mst_test",
                Collection = "Ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "name", Field = "Name", Type = "text" },
                    new FieldConfig { Name = "alterid", Field = "AlterId", Type = "number" }
                }
            };
            DatabaseWriter.InitializeTargetTableDynamic(profile, "test_catalog_inc", tableConfig);

            // Write temporary yaml config
            var yamlContent = @"
master:
  - name: mst_test
    nature: Primary
    collection: Ledger
    fields:
      - name: guid
        field: Guid
        type: text
      - name: name
        field: Name
        type: text
      - name: alterid
        field: AlterId
        type: number
transaction: []
";
            var yamlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tally-export-config.yaml");
            File.WriteAllText(yamlPath, yamlContent);

            // Setup Mock Client
            var mockHandler = new MockHttpMessageHandler();
            // Provide a mock company info response with AlterIDs
            mockHandler.CompanyInfoResponse = "\"guid-123\",\"TestCompany\",\"20260401\",\"20260519\",\"100\",\"200\",\"†\"";
            mockHandler.TableDataResponse = "<ENVELOPE><BODY><DATA><ROW><F01>guid-1</F01><F02>Ledger A</F02></ROW></DATA></BODY></ENVELOPE>";
            var httpClient = new HttpClient(mockHandler);
            var tallyClient = new TallyDbLoader.Core.Tally.TallyClient(httpClient, "localhost", 9000);

            // Create worker
            var worker = new BackgroundSyncWorker(repo, "localhost", 9000);
            worker.SetTallyClientForTest(tallyClient);
            var logs = new List<string>();
            worker.OnLogMessage += (msg) => logs.Add(msg);

            worker.Start();
            
            // Wait up to 2 seconds for worker to process
            await Task.Delay(2000);
            worker.Stop();

            // Check if sync log calls were made
            Assert.Contains(logs, l => l.Contains("Background Sync Engine started."));
            Assert.Contains(logs, l => l.Contains("Starting sync for company 'TestCompany'"));
            Assert.Contains(logs, l => l.Contains("sync finished. Wrote"));
            Assert.DoesNotContain(logs, l => l.Contains("failed"));

            // Cleanup
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(yamlPath)) File.Delete(yamlPath);
            if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
        }

        [Fact]
        public async Task Test_BackgroundSyncWorker_ManualSync_WakesUpImmediately()
        {
            var dbPath = "manual_sync_test.db";
            var targetDbPath = "test_manual_catalog.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
            DatabaseHelper.InitializeDatabase(dbPath);
            var repo = new ConfigRepository(dbPath);

            var profile = new DatabaseProfile
            {
                Name = "LocalPg",
                Technology = "sqlite",
                Server = "localhost",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };
            repo.SaveDatabaseProfile(profile);
            var savedProfile = repo.GetDatabaseProfileByName("LocalPg");

            var job = new CompanyProfile
            {
                Name = "TestCompany",
                DbProfileId = savedProfile.Id,
                TargetCatalog = "test_manual_catalog",
                IntervalMinutes = 60, // Set high so it doesn't trigger automatically on interval
                Status = "Idle",
                Enabled = true
            };
            repo.SaveCompanyProfile(job);

            var yamlContent = @"
master:
  - name: mst_test
    fields:
      - name: guid
        field: Guid
        type: text
transaction: []
";
            var yamlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tally-export-config.yaml");
            File.WriteAllText(yamlPath, yamlContent);

            var mockHandler = new MockHttpMessageHandler();
            var httpClient = new HttpClient(mockHandler);
            var tallyClient = new TallyDbLoader.Core.Tally.TallyClient(httpClient, "localhost", 9000);

            var logs = new List<string>();
            using (var worker = new BackgroundSyncWorker(repo, "localhost", 9000))
            {
                worker.SetTallyClientForTest(tallyClient);
                worker.OnLogMessage += (msg) => logs.Add(msg);
                int runCount = 0;
                worker.OnSyncCompleted += () => { runCount++; };

                // Verify calling TriggerManualSync on inactive worker does not throw/fails gracefully
                worker.TriggerManualSync();
                Assert.Equal(0, runCount);

                worker.Start();
                
                // Wait for first initial sync to run and complete (fires twice: starting and completing)
                await Task.Delay(1000);
                if (runCount != 2)
                {
                    Assert.Fail($"Expected runCount to be 2, but was {runCount}. Logs:\n" + string.Join("\n", logs));
                }

                // Trigger manual sync
                worker.TriggerManualSync();

                // Wait a short delay and check that it ran a second time immediately (fires twice: starting and completing)
                await Task.Delay(1000);
                if (runCount != 4)
                {
                    Assert.Fail($"Expected runCount to be 4, but was {runCount}. Logs:\n" + string.Join("\n", logs));
                }

                worker.Stop();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(yamlPath)) File.Delete(yamlPath);
            if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
        }
    }
}
