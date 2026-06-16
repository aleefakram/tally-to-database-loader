using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Wpf;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class MainViewModelTests
    {
        private class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _sender;

            public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> sender)
            {
                _sender = sender;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                return Task.FromResult(_sender(request));
            }
        }

        [Fact]
        public async Task Test_DetectActiveCompanies_SingleCompany_SelectsAutomatically()
        {
            string dbPath = "vm_test_single.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            var vm = new MainViewModel(dbPath);
            vm.DisableDispatcher = true;
            vm.TallyServer = "localhost";
            vm.TallyPort = 9000;

            var xmlResponse = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <NAME>Single Company Ltd</NAME>
        <ISGROUP>No</ISGROUP>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler((req) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xmlResponse, System.Text.Encoding.UTF8, "text/xml")
            });
            var httpClient = new HttpClient(mockHandler);

            vm.TallyClientFactory = (server, port) => new TallyClient(httpClient, server, port);

            string? messageShown = null;
            vm.MessageBoxShowHandler = (msg, caption, btn, icon) =>
            {
                messageShown = msg;
            };

            await vm.DetectActiveCompaniesAsync();

            Assert.Equal("Single Company Ltd", vm.JobCompany);
            Assert.Contains("Selected Company: Single Company Ltd", messageShown);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        [Fact]
        public async Task Test_DetectActiveCompanies_MultipleCompanies_UserSelectsOne()
        {
            string dbPath = "vm_test_multi.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            var vm = new MainViewModel(dbPath);
            vm.DisableDispatcher = true;
            vm.TallyServer = "localhost";
            vm.TallyPort = 9000;

            var xmlResponse = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <NAME>Company A</NAME>
        <ISGROUP>No</ISGROUP>
      </ROW>
      <ROW>
        <NAME>Group Company B</NAME>
        <ISGROUP>Yes</ISGROUP>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler((req) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xmlResponse, System.Text.Encoding.UTF8, "text/xml")
            });
            var httpClient = new HttpClient(mockHandler);

            vm.TallyClientFactory = (server, port) => new TallyClient(httpClient, server, port);

            vm.CompanySelector = (companies) =>
            {
                Assert.Equal(2, companies.Count);
                return companies[1]; // Select Group Company B
            };

            string? messageShown = null;
            vm.MessageBoxShowHandler = (msg, caption, btn, icon) =>
            {
                messageShown = msg;
            };

            await vm.DetectActiveCompaniesAsync();

            Assert.Equal("Group Company B", vm.JobCompany);
            Assert.Contains("Selected Company: Group Company B", messageShown);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        [Fact]
        public async Task Test_DetectActiveCompanies_MultipleCompanies_UserCancels()
        {
            string dbPath = "vm_test_cancel.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            var vm = new MainViewModel(dbPath);
            vm.DisableDispatcher = true;
            vm.TallyServer = "localhost";
            vm.TallyPort = 9000;
            vm.JobCompany = "Initial Company";

            var xmlResponse = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <NAME>Company A</NAME>
        <ISGROUP>No</ISGROUP>
      </ROW>
      <ROW>
        <NAME>Group Company B</NAME>
        <ISGROUP>Yes</ISGROUP>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler((req) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xmlResponse, System.Text.Encoding.UTF8, "text/xml")
            });
            var httpClient = new HttpClient(mockHandler);

            vm.TallyClientFactory = (server, port) => new TallyClient(httpClient, server, port);

            vm.CompanySelector = (companies) => null; // Cancel selection

            bool messageBoxCalled = false;
            vm.MessageBoxShowHandler = (msg, caption, btn, icon) =>
            {
                messageBoxCalled = true;
            };

            await vm.DetectActiveCompaniesAsync();

            Assert.Equal("Initial Company", vm.JobCompany);
            Assert.False(messageBoxCalled);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        [Fact]
        public async Task Test_Mutation_Guards_And_SyncRunning_Properties()
        {
            string dbPath = "vm_test_guards.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            var vm = new MainViewModel(dbPath);
            vm.DisableDispatcher = true;

            // Verify initial state
            Assert.False(vm.IsSyncRunning);
            Assert.True(vm.IsSyncNotRunning);

            // Hook up PropertyChanged tracker
            var changedProperties = new List<string>();
            vm.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName != null) changedProperties.Add(e.PropertyName);
            };

            // Start Sync
            vm.StartSyncEngine();
            Assert.True(vm.IsSyncRunning);
            Assert.False(vm.IsSyncNotRunning);
            Assert.Contains(nameof(vm.IsSyncRunning), changedProperties);
            Assert.Contains(nameof(vm.IsSyncNotRunning), changedProperties);

            changedProperties.Clear();

            // Attempt configuration mutations when sync is running and verify they early-return / guard
            vm.TallyServer = "new_server";
            vm.SaveTallySettings();
            
            // Verify Tally settings did not save to database (should still be default or empty)
            var repo = new ConfigRepository(dbPath);
            var savedSettings = repo.GetTallySettings();
            Assert.NotEqual("new_server", savedSettings.Server);

            // Attempt save db profile
            vm.DbName = "GuardedDb";
            vm.SaveDatabaseProfile();
            Assert.Null(repo.GetDatabaseProfileByName("GuardedDb"));

            // Attempt detect companies
            vm.JobCompany = "Initial Company";
            await vm.DetectActiveCompaniesAsync();
            Assert.Equal("Initial Company", vm.JobCompany);

            // Stop Sync
            vm.StopSyncEngine();
            Assert.False(vm.IsSyncRunning);
            Assert.True(vm.IsSyncNotRunning);
            Assert.Contains(nameof(vm.IsSyncRunning), changedProperties);
            Assert.Contains(nameof(vm.IsSyncNotRunning), changedProperties);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        [Fact]
        public void Test_ResolveSafetyBlockCommand_Cancelled()
        {
            string dbPath = $"vm_test_resolve_cancel_{Guid.NewGuid():N}.db";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);

                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                // Seed blocked company
                var repo = new ConfigRepository(dbPath);
                var dbProfile = new DatabaseProfile { Name = "TestDb", Technology = "sqlite" };
                repo.SaveDatabaseProfile(dbProfile);
                var dbFromDb = repo.GetDatabaseProfileByName("TestDb");
                Assert.NotNull(dbFromDb);

                var company = new CompanyProfile
                {
                    Name = "BlockedCo",
                    DbProfileId = dbFromDb.Id,
                    TargetCatalog = "test",
                    Status = "attention_required"
                };
                repo.SaveCompanyProfile(company);
                vm.LoadConfiguration();

                // Select the company
                var selected = vm.Companies.First(c => c.Name == "BlockedCo");
                vm.SelectedCompany = selected;
                Assert.True(vm.CanResolveSelectedCompanySafetyBlock);

                // Cancel dialog callback
                vm.SafetyResolveReasonPrompter = (name) => null;

                // Execute
                vm.ResolveSafetyBlockCommand.Execute(vm.SelectedCompany);

                // Assert status remains blocked
                Assert.Equal("attention_required", vm.SelectedCompany.Status);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch { }
                }
            }
        }

        [Fact]
        public void Test_ResolveSafetyBlockCommand_Success()
        {
            string dbPath = $"vm_test_resolve_ok_{Guid.NewGuid():N}.db";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);

                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                var repo = new ConfigRepository(dbPath);
                var dbProfile = new DatabaseProfile { Name = "TestDb", Technology = "sqlite" };
                repo.SaveDatabaseProfile(dbProfile);
                var dbFromDb = repo.GetDatabaseProfileByName("TestDb");
                Assert.NotNull(dbFromDb);

                var company = new CompanyProfile
                {
                    Name = "BlockedCo",
                    DbProfileId = dbFromDb.Id,
                    TargetCatalog = "test",
                    Status = "unknown"
                };
                repo.SaveCompanyProfile(company);
                vm.LoadConfiguration();

                var selected = vm.Companies.First(c => c.Name == "BlockedCo");
                vm.SelectedCompany = selected;

                // Reason mock
                vm.SafetyResolveReasonPrompter = (name) => "operator manual override reason";

                // Execute
                vm.ResolveSafetyBlockCommand.Execute(vm.SelectedCompany);

                // Assert status is now idle and command is disabled
                Assert.Equal("idle", vm.SelectedCompany.Status);
                Assert.False(vm.CanResolveSelectedCompanySafetyBlock);

                // Verify audit trail exists
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    var auditCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'resolve_safety_state'");
                    Assert.Equal(1, auditCount);

                    var reason = conn.ExecuteScalar<string>("SELECT reason FROM config_audit_log WHERE entity_id = @Id AND action = 'resolve_safety_state'", new { Id = selected.Id });
                    var action = conn.ExecuteScalar<string>("SELECT action FROM config_audit_log WHERE entity_id = @Id AND action = 'resolve_safety_state'", new { Id = selected.Id });
                    Assert.Equal("operator manual override reason", reason);
                    Assert.Equal("resolve_safety_state", action);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch { }
                }
            }
        }

        [Fact]
        public void Test_ExportSanitizedConfig_Success()
        {
            string dbPath = $"vm_test_export_ok_{Guid.NewGuid():N}.db";
            string exportPath = $"vm_test_export_out_{Guid.NewGuid():N}.json";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                // Mock save file dialog
                vm.SaveFileDialogHandler = (defaultName, filter) => exportPath;

                // Run config export
                vm.ExportSanitizedConfigCommand.Execute(null);

                // Assert file exists and contains expected metadata
                Assert.True(File.Exists(exportPath));
                string content = File.ReadAllText(exportPath);
                Assert.Contains("tally-db-loader.config-export", content);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (File.Exists(exportPath)) try { File.Delete(exportPath); } catch { }
            }
        }

        [Fact]
        public void Test_ExportSanitizedConfig_Cancelled()
        {
            string dbPath = $"vm_test_export_cancel_{Guid.NewGuid():N}.db";
            string exportPath = $"vm_test_export_cancel_out_{Guid.NewGuid():N}.json";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                // Mock user cancelled
                vm.SaveFileDialogHandler = (defaultName, filter) => null;

                vm.ExportSanitizedConfigCommand.Execute(null);

                // Assert no file created and no toasts registered
                Assert.False(File.Exists(exportPath));
                Assert.Empty(vm.Toasts);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public void Test_ExportSanitizedConfig_Failure()
        {
            string dbPath = $"vm_test_export_fail_{Guid.NewGuid():N}.db";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                // Provide an invalid path that will cause write failure
                vm.SaveFileDialogHandler = (defaultName, filter) => "invalid:\\path/to/nonexistent/file.json";

                vm.ExportSanitizedConfigCommand.Execute(null);

                // Toast collection should contain failure message
                Assert.Contains(vm.Toasts, t => t.Kind == "err");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task Test_CreateDiagnosticBackup_Success()
        {
            string dbPath = $"vm_test_diag_ok_{Guid.NewGuid():N}.db";
            string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_out_{Guid.NewGuid():N}");
            string tempBaseDir = Path.Combine(Path.GetTempPath(), $"vm_diag_base_ok_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(tempBaseDir);
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;
                vm.DiagnosticsBaseDirectory = tempBaseDir;

                vm.FolderBrowserDialogHandler = () => outputDir;
                vm.ConfirmationPromptHandler = (msg, title) => false; // No XML

                await vm.CreateDiagnosticBackupAsync();

                var files = Directory.GetFiles(outputDir, "*.zip");
                Assert.Single(files);
                Assert.Contains(vm.Toasts, t => t.Kind == "ok");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (Directory.Exists(outputDir)) try { Directory.Delete(outputDir, true); } catch { }
                if (Directory.Exists(tempBaseDir)) try { Directory.Delete(tempBaseDir, true); } catch { }
            }
        }

        [Fact]
        public void Test_CreateDiagnosticBackup_Cancelled()
        {
            string dbPath = $"vm_test_diag_cancel_{Guid.NewGuid():N}.db";
            string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_cancel_out_{Guid.NewGuid():N}");
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                bool confirmationCalled = false;
                vm.FolderBrowserDialogHandler = () => null;
                vm.ConfirmationPromptHandler = (msg, title) => { confirmationCalled = true; return false; };

                vm.CreateDiagnosticBackupCommand.Execute(null);

                // Assert cancellation doesn't trigger prompts or output files or toasts
                Assert.False(confirmationCalled);
                Assert.Empty(vm.Toasts);
                if (Directory.Exists(outputDir))
                {
                    Assert.Empty(Directory.GetFiles(outputDir, "*.zip"));
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task Test_CreateDiagnosticBackup_HandlerException()
        {
            string dbPath = $"vm_test_diag_exc_{Guid.NewGuid():N}.db";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                // Mock handler that throws an exception
                vm.FolderBrowserDialogHandler = () => throw new InvalidOperationException("Simulated dialog failure");

                await vm.CreateDiagnosticBackupAsync();

                // Assert failure toast was posted
                Assert.Contains(vm.Toasts, t => t.Kind == "err" && t.Body.Contains("Simulated dialog failure"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task Test_CreateDiagnosticBackup_MissingXmlDirectory()
        {
            string dbPath = $"vm_test_diag_xml_missing_{Guid.NewGuid():N}.db";
            string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_xml_out_{Guid.NewGuid():N}");
            string tempBaseDir = Path.Combine(Path.GetTempPath(), $"vm_diag_base_xml_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(tempBaseDir); // Kept empty so raw_xml won't exist
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;
                vm.DiagnosticsBaseDirectory = tempBaseDir;

                vm.FolderBrowserDialogHandler = () => outputDir;
                vm.ConfirmationPromptHandler = (msg, title) => true; // User asks for XML

                await vm.CreateDiagnosticBackupAsync();

                // Toast collection should contain warning message about folder missing
                Assert.Contains(vm.Toasts, t => t.Kind == "warn" && t.Title.Contains("Folder Missing"));

                // Verify ZIP manifest.json states include_raw_xml = false
                var files = Directory.GetFiles(outputDir, "*.zip");
                Assert.Single(files);
                using (var archive = System.IO.Compression.ZipFile.OpenRead(files[0]))
                {
                    var manifestEntry = archive.GetEntry("manifest.json");
                    Assert.NotNull(manifestEntry);
                    using (var reader = new StreamReader(manifestEntry.Open()))
                    {
                        string json = reader.ReadToEnd();
                        Assert.Contains("\"include_raw_xml\": false", json);
                    }
                    Assert.Empty(archive.Entries.Where(e => e.FullName.StartsWith("raw_xml/")));
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (Directory.Exists(outputDir)) try { Directory.Delete(outputDir, true); } catch { }
                if (Directory.Exists(tempBaseDir)) try { Directory.Delete(tempBaseDir, true); } catch { }
            }
        }

        [Fact]
        public async Task Test_ExportAndBackup_AllowedWhileEngineRunning()
        {
            string dbPath = $"vm_test_engine_run_{Guid.NewGuid():N}.db";
            string exportPath = $"vm_test_engine_run_out_{Guid.NewGuid():N}.json";
            string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_engine_run_out_{Guid.NewGuid():N}");
            string tempBaseDir = Path.Combine(Path.GetTempPath(), $"vm_diag_base_run_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(tempBaseDir);
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;
                vm.DiagnosticsBaseDirectory = tempBaseDir;

                // Force State to Running directly without starting background threads
                vm.State = EngineState.Running;
                Assert.True(vm.IsSyncRunning);

                // 1. Verify Config Export is allowed
                vm.SaveFileDialogHandler = (defaultName, filter) => exportPath;
                vm.ExportSanitizedConfigCommand.Execute(null);
                Assert.True(File.Exists(exportPath));
                Assert.Contains(vm.Toasts, t => t.Kind == "ok" && t.Title.Contains("Export Succeeded"));

                // 2. Verify Diagnostic Backup is allowed
                vm.FolderBrowserDialogHandler = () => outputDir;
                vm.ConfirmationPromptHandler = (msg, title) => false;
                await vm.CreateDiagnosticBackupAsync();

                var files = Directory.GetFiles(outputDir, "*.zip");
                Assert.Single(files);
                Assert.Contains(vm.Toasts, t => t.Kind == "ok" && t.Title.Contains("Backup Created"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (File.Exists(exportPath)) try { File.Delete(exportPath); } catch { }
                if (Directory.Exists(outputDir)) try { Directory.Delete(outputDir, true); } catch { }
                if (Directory.Exists(tempBaseDir)) try { Directory.Delete(tempBaseDir, true); } catch { }
            }
        }
    }
}
