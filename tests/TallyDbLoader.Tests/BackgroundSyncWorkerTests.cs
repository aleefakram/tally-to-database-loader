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
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);
            var repo = new ConfigRepository(dbPath);

            // Add db profile
            var profile = new DatabaseProfile
            {
                Name = "LocalPg",
                Technology = "postgres",
                Server = "localhost",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };
            repo.SaveDatabaseProfile(profile);
            var savedProfile = repo.GetDatabaseProfileByName("LocalPg");

            // Add sync job
            var job = new SyncJob
            {
                CompanyName = "TestCompany",
                DbProfileId = savedProfile.Id,
                TargetCatalog = "test_catalog",
                SyncIntervalMinutes = 1,
                DailyTimeLocal = null,
                Status = "Idle"
            };
            repo.SaveSyncJob(job);

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

            // Run a mock sync loop by manually calling the worker's inner loop (using reflection or invoking it)
            // Or since WorkerLoop is private, we can use start/stop and sleep for a second.
            worker.Start();
            
            // Wait up to 2 seconds for worker to process the job
            await Task.Delay(2000);
            worker.Stop();

            // Check if YAML was parsed and job orchestration started
            Assert.Contains(logs, l => l.Contains("Loading Tally definition file"));
            Assert.Contains(logs, l => l.Contains("Parsed YAML configuration"));
            Assert.Contains(logs, l => l.Contains("Target database technology: postgres"));

            // Cleanup
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(yamlPath)) File.Delete(yamlPath);
        }
    }
}
