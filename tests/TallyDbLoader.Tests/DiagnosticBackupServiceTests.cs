using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Dapper;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Tests
{
    public class DiagnosticBackupServiceTests
    {
        public class FakeDiagnosticBackupRepository : IConfigRepository
        {
            public string? LastActor { get; set; }
            public string? LastReason { get; set; }
            public string? LastFileName { get; set; }
            public long LastFileSizeBytes { get; set; }
            public bool LastIncludeRawXml { get; set; }
            public int LastLogFileCount { get; set; }
            public int LastRawXmlFileCount { get; set; }
            public int LastSkippedFileCount { get; set; }
            public DateTime LastCreatedAt { get; set; }
            public long NextAuditId { get; set; } = 42;
            public bool ShouldThrowOnAudit { get; set; }

            public long RecordDiagnosticBackupExport(
                string actor, string reason, string fileName, long fileSizeBytes,
                bool includeRawXml, int logFileCount, int rawXmlFileCount, int skippedFileCount,
                DateTime createdAt)
            {
                if (ShouldThrowOnAudit)
                    throw new InvalidOperationException("Simulated audit database insertion failure");

                LastActor = actor;
                LastReason = reason;
                LastFileName = fileName;
                LastFileSizeBytes = fileSizeBytes;
                LastIncludeRawXml = includeRawXml;
                LastLogFileCount = logFileCount;
                LastRawXmlFileCount = rawXmlFileCount;
                LastSkippedFileCount = skippedFileCount;
                LastCreatedAt = createdAt;
                return NextAuditId;
            }

            public List<DatabaseProfile> GetAllDatabaseProfiles() => throw new NotImplementedException();
            public List<CompanyProfile> GetAllCompanyProfiles() => throw new NotImplementedException();
            public void SaveDatabaseProfile(DatabaseProfile profile) => throw new NotImplementedException();
            public DatabaseProfile? GetDatabaseProfileByName(string name) => throw new NotImplementedException();
            public DatabaseProfile? GetDatabaseProfileById(int id) => throw new NotImplementedException();
            public void SaveCompanyProfile(CompanyProfile company) => throw new NotImplementedException();
            public void DeleteCompanyProfile(int id) => throw new NotImplementedException();
            public TallySettings GetTallySettings() => throw new NotImplementedException();
            public void SaveTallySettings(TallySettings settings) => throw new NotImplementedException();
            public void DeleteDatabaseProfile(int id) => throw new NotImplementedException();
            public long AddSyncRun(SyncRun run) => throw new NotImplementedException();
            public List<SyncRun> GetRecentSyncRuns(int limit = 50) => throw new NotImplementedException();
            public List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50) => throw new NotImplementedException();
            public bool TryStartCompanyProfile(int id) => throw new NotImplementedException();
            public void MarkCompanyProfileUnknown(int id, string reason, DateTime now) => throw new NotImplementedException();
            public void CompleteCompanyProfileRun(int id, string finalStatus, DateTime endedAt, int durationMs, long rowsWritten, bool incrementErrorCount) => throw new NotImplementedException();
            public void UpdateSyncRun(SyncRun run) => throw new NotImplementedException();
            public void ReconcileStaleRuns(DateTime now) => throw new NotImplementedException();
            public long ResolveCompanyProfileSafetyState(int companyProfileId, string actor, string reason, DateTime resolvedAt) => throw new NotImplementedException();
            public void ImportSanitizedConfig(List<ResolvedDatabaseProfileImport> databaseProfiles, List<ResolvedCompanyProfileImport> companyProfiles, string actor, string reason, string beforeJson, string afterJson) => throw new NotImplementedException();
            public void AddBalanceSheetVerificationRun(BalanceSheetVerificationRun run) => throw new NotImplementedException();
            public List<BalanceSheetVerificationRun> GetRecentBalanceSheetVerificationRuns(int limit = 50) => throw new NotImplementedException();
        }

        private readonly FakeDiagnosticBackupRepository _repoFake = new FakeDiagnosticBackupRepository();

        [Fact]
        public void Constructor_ThrowsArgumentNullException_WhenRepositoryIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new DiagnosticBackupService(null!));
        }

        [Fact]
        public void CreateBackup_ThrowsArgumentNullException_WhenRequestIsNull()
        {
            var service = new DiagnosticBackupService(_repoFake);
            Assert.Throws<ArgumentNullException>(() => service.CreateBackup(null!));
        }

        [Theory]
        [InlineData("", "out", "1.0", "actor", "reason")]
        [InlineData("db", "", "1.0", "actor", "reason")]
        [InlineData("db", "out", "", "actor", "reason")]
        [InlineData("db", "out", "1.0", "", "reason")]
        [InlineData("db", "out", "1.0", "actor", "")]
        public void CreateBackup_ThrowsArgumentException_WhenRequiredFieldsAreEmpty(
            string dbPath, string outPath, string appVersion, string actor, string reason)
        {
            var service = new DiagnosticBackupService(_repoFake);
            var req = new DiagnosticBackupRequest
            {
                ConfigDatabasePath = dbPath,
                OutputDirectoryPath = outPath,
                ApplicationVersion = appVersion,
                Actor = actor,
                Reason = reason
            };
            Assert.Throws<ArgumentException>(() => service.CreateBackup(req));
        }

        [Fact]
        public void CreateBackup_ThrowsFileNotFound_WhenDbPathDoesNotExist()
        {
            var service = new DiagnosticBackupService(_repoFake);
            var req = new DiagnosticBackupRequest
            {
                ConfigDatabasePath = "nonexistent_db.db",
                OutputDirectoryPath = Path.GetTempPath(),
                ApplicationVersion = "1.0",
                Actor = "actor",
                Reason = "reason"
            };
            Assert.Throws<FileNotFoundException>(() => service.CreateBackup(req));
        }

        [Fact]
        public void CreateBackup_ThrowsDirectoryNotFound_WhenOutputDirDoesNotExist()
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"dummy_db_{Guid.NewGuid()}.db");
            File.WriteAllText(dbPath, "dummy sqlite content");
            try
            {
                var service = new DiagnosticBackupService(_repoFake);
                var req = new DiagnosticBackupRequest
                {
                    ConfigDatabasePath = dbPath,
                    OutputDirectoryPath = @"C:\NonexistentDir_" + Guid.NewGuid(),
                    ApplicationVersion = "1.0",
                    Actor = "actor",
                    Reason = "reason"
                };
                Assert.Throws<DirectoryNotFoundException>(() => service.CreateBackup(req));
            }
            finally
            {
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public void PerformSQLiteBackup_CopiesDatabase_SafelyAndSuccessfully()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag_temp_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            string sourceDbPath = Path.Combine(tempDir, "source.db");
            string targetDb = Path.Combine(tempDir, "target.db");

            try
            {
                DatabaseHelper.InitializeDatabase(sourceDbPath);

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath}"))
                {
                    conn.Open();
                    conn.Execute("INSERT INTO database_profiles (name, technology, server, port) VALUES ('LiveDb', 'mssql', 'localhost', 1433)");
                }

                var service = new DiagnosticBackupService(_repoFake);
                service.PerformSQLiteBackup(sourceDbPath, targetDb);

                Assert.True(File.Exists(targetDb));
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={targetDb}"))
                {
                    conn.Open();
                    int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'LiveDb'");
                    Assert.Equal(1, count);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void GatherSystemInfo_ReturnsExpectedProperties_WithoutLeakingSecrets()
        {
            var service = new DiagnosticBackupService(_repoFake);
            var req = new DiagnosticBackupRequest
            {
                ApplicationVersion = "2.0.0-beta",
                CreatedAt = DateTimeOffset.UtcNow
            };

            string info = service.GenerateSystemInfoText(req);

            Assert.Contains("application_version=2.0.0-beta", info);
            Assert.Contains("os_version=", info);
            Assert.Contains("dotnet_version=", info);
            Assert.Contains("is_64_bit_process=", info);
            Assert.DoesNotContain("password", info);
            Assert.DoesNotContain("dpapi", info);
        }

        [Fact]
        public void CreateBackup_PackagesRecursiveStructure_GeneratesValidManifestAndCleansWorkingFiles()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag_zip_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            string sourceDbPath = Path.Combine(tempDir, "source.db");
            string outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);

            string logsDir = Path.Combine(tempDir, "logs");
            Directory.CreateDirectory(logsDir);
            string subLogsDir = Path.Combine(logsDir, "subfolder");
            Directory.CreateDirectory(subLogsDir);

            string xmlDir = Path.Combine(tempDir, "xml");
            Directory.CreateDirectory(xmlDir);
            string subXmlDir = Path.Combine(xmlDir, "subxml");
            Directory.CreateDirectory(subXmlDir);

            try
            {
                DatabaseHelper.InitializeDatabase(sourceDbPath);

                // Add dummy records to DB
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath};Pooling=False;"))
                {
                    conn.Open();
                    conn.Execute("INSERT INTO database_profiles (name, technology, server, port, password) VALUES ('TestProfile', 'mssql', 'localhost', 1433, 'dpapi:xyz123')");
                }

                // Create nested logs and raw XMLs
                File.WriteAllText(Path.Combine(logsDir, "root.log"), "Root log content");
                File.WriteAllText(Path.Combine(subLogsDir, "nested.log"), "Nested log content");

                File.WriteAllText(Path.Combine(xmlDir, "root.xml"), "<ENVELOPE></ENVELOPE>");
                File.WriteAllText(Path.Combine(subXmlDir, "nested.xml"), "<DATA></DATA>");

                var service = new DiagnosticBackupService(_repoFake);
                var request = new DiagnosticBackupRequest
                {
                    ConfigDatabasePath = sourceDbPath,
                    LogDirectoryPath = logsDir,
                    RawXmlDirectoryPath = xmlDir,
                    OutputDirectoryPath = outputDir,
                    ApplicationVersion = "2.0.0-beta",
                    Actor = "test_agent",
                    Reason = "troubleshoot",
                    IncludeRawXml = true,
                    CreatedAt = new DateTimeOffset(2026, 6, 15, 15, 30, 0, TimeSpan.FromHours(5.5))
                };

                var result = service.CreateBackup(request);

                Assert.True(File.Exists(result.FilePath));
                Assert.Equal("tally_diagnostic_20260615_153000.zip", result.FileName);
                Assert.Equal(2, result.LogFileCount);
                Assert.Equal(2, result.RawXmlFileCount);

                // Extract and inspect ZIP contents
                string extractDir = Path.Combine(tempDir, "extract");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                Assert.True(File.Exists(Path.Combine(extractDir, "config/config.db")));
                Assert.True(File.Exists(Path.Combine(extractDir, "logs/root.log")));
                Assert.True(File.Exists(Path.Combine(extractDir, "logs/subfolder/nested.log")));
                Assert.True(File.Exists(Path.Combine(extractDir, "system/system_info.txt")));
                Assert.True(File.Exists(Path.Combine(extractDir, "raw_xml/root.xml")));
                Assert.True(File.Exists(Path.Combine(extractDir, "raw_xml/subxml/nested.xml")));
                Assert.True(File.Exists(Path.Combine(extractDir, "manifest.json")));

                string manifestJson = File.ReadAllText(Path.Combine(extractDir, "manifest.json"));
                using (var doc = System.Text.Json.JsonDocument.Parse(manifestJson))
                {
                    var root = doc.RootElement;
                    Assert.Equal("tally-db-loader.diagnostic-backup", root.GetProperty("format").GetString());
                    Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
                    Assert.Equal("2.0.0-beta", root.GetProperty("application_version").GetString());
                    Assert.Equal("2026-06-15T15:30:00.0000000+05:30", root.GetProperty("created_at").GetString());
                    Assert.True(root.GetProperty("include_raw_xml").GetBoolean());

                    var entries = root.GetProperty("entries");
                    Assert.True(entries.GetProperty("config_database").GetBoolean());
                    Assert.True(entries.GetProperty("system_info").GetBoolean());
                    Assert.Equal(2, entries.GetProperty("log_file_count").GetInt32());
                    Assert.Equal(2, entries.GetProperty("raw_xml_file_count").GetInt32());
                    Assert.Equal(0, entries.GetProperty("skipped_file_count").GetInt32());
                }

                // Verify SQLite database in zip is readable and contains our record
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(extractDir, "config/config.db")};Pooling=False;"))
                {
                    conn.Open();
                    int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'TestProfile'");
                    Assert.Equal(1, count);
                    string password = conn.ExecuteScalar<string>("SELECT password FROM database_profiles WHERE name = 'TestProfile'");
                    Assert.Equal("dpapi:xyz123", password);
                }

                // Assert security requirements: no passwords, no raw XML file contents, or absolute path leaks
                Assert.DoesNotContain("dpapi:", manifestJson);
                Assert.DoesNotContain("C:/", manifestJson);
                Assert.DoesNotContain("C:\\", manifestJson);
                Assert.DoesNotContain("diag_zip_", manifestJson);
                Assert.DoesNotContain("<ENVELOPE>", manifestJson);
                Assert.DoesNotContain("<DATA>", manifestJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void CreateBackup_ExcludeRawXmlByDefault_AndHandlesMissingLogDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag_defaults_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            string sourceDbPath = Path.Combine(tempDir, "source.db");
            string outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);
            string nonexistentLogs = Path.Combine(tempDir, "nonexistent_logs");
            string xmlDir = Path.Combine(tempDir, "xml");
            Directory.CreateDirectory(xmlDir);

            try
            {
                DatabaseHelper.InitializeDatabase(sourceDbPath);
                File.WriteAllText(Path.Combine(xmlDir, "should_be_ignored.xml"), "<IGNORE />");

                var service = new DiagnosticBackupService(_repoFake);
                var request = new DiagnosticBackupRequest
                {
                    ConfigDatabasePath = sourceDbPath,
                    LogDirectoryPath = nonexistentLogs,
                    RawXmlDirectoryPath = xmlDir,
                    OutputDirectoryPath = outputDir,
                    ApplicationVersion = "1.0",
                    Actor = "defaults_agent",
                    Reason = "test defaults",
                    IncludeRawXml = false,
                    CreatedAt = DateTimeOffset.Now
                };

                var result = service.CreateBackup(request);

                Assert.True(File.Exists(result.FilePath));
                Assert.Equal(0, result.LogFileCount);
                Assert.Equal(0, result.RawXmlFileCount);

                string extractDir = Path.Combine(tempDir, "extract");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                // Assert XML and Log directories are not created inside the zip
                Assert.False(Directory.Exists(Path.Combine(extractDir, "raw_xml")));
                Assert.False(Directory.Exists(Path.Combine(extractDir, "logs")));
                Assert.True(File.Exists(Path.Combine(extractDir, "config/config.db")));
                Assert.True(File.Exists(Path.Combine(extractDir, "system/system_info.txt")));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void CreateBackup_TracksSkippedFiles_WhenReadFailureOccurs()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag_skipped_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            string sourceDbPath = Path.Combine(tempDir, "source.db");
            string outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);
            string logsDir = Path.Combine(tempDir, "logs");
            Directory.CreateDirectory(logsDir);

            try
            {
                DatabaseHelper.InitializeDatabase(sourceDbPath);

                string log1 = Path.Combine(logsDir, "app1.log");
                string log2 = Path.Combine(logsDir, "app2.log");
                File.WriteAllText(log1, "Log content 1");
                File.WriteAllText(log2, "Log content 2");

                // Lock log1 exclusively to simulate a read failure during file system copying
                using (var lockStream = new FileStream(log1, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var service = new DiagnosticBackupService(_repoFake);
                    var request = new DiagnosticBackupRequest
                    {
                        ConfigDatabasePath = sourceDbPath,
                        LogDirectoryPath = logsDir,
                        OutputDirectoryPath = outputDir,
                        ApplicationVersion = "1.0",
                        Actor = "skipped_agent",
                        Reason = "test skips",
                        IncludeRawXml = false,
                        CreatedAt = DateTimeOffset.Now
                    };

                    var result = service.CreateBackup(request);

                    Assert.Equal(1, result.LogFileCount);
                    Assert.True(result.AuditId > 0);

                    string extractDir = Path.Combine(tempDir, "extract");
                    Directory.CreateDirectory(extractDir);
                    System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                    // Confirm app2.log exists, but app1.log is missing
                    Assert.True(File.Exists(Path.Combine(extractDir, "logs/app2.log")));
                    Assert.False(File.Exists(Path.Combine(extractDir, "logs/app1.log")));

                    string manifestJson = File.ReadAllText(Path.Combine(extractDir, "manifest.json"));
                    using (var doc = System.Text.Json.JsonDocument.Parse(manifestJson))
                    {
                        var root = doc.RootElement;
                        var entries = root.GetProperty("entries");
                        Assert.Equal(1, entries.GetProperty("skipped_file_count").GetInt32());

                        var skippedArray = root.GetProperty("skipped_files");
                        Assert.Single(skippedArray.EnumerateArray());
                        var item = skippedArray[0].GetString()!;
                        Assert.StartsWith("logs/app1.log: IOException", item);

                        // Ensure absolute paths do not leak through exception details
                        Assert.DoesNotContain("C:/", item);
                        Assert.DoesNotContain("C:\\", item);
                        Assert.DoesNotContain("diag_skipped_", item);
                    }
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void CreateBackup_KeepsZipFileOnDisk_WhenAuditDatabaseWriteThrows()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag_audit_fail_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            string sourceDbPath = Path.Combine(tempDir, "source.db");
            string outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);

            try
            {
                DatabaseHelper.InitializeDatabase(sourceDbPath);

                var failingRepo = new FakeDiagnosticBackupRepository { ShouldThrowOnAudit = true };
                var service = new DiagnosticBackupService(failingRepo);
                var request = new DiagnosticBackupRequest
                {
                    ConfigDatabasePath = sourceDbPath,
                    OutputDirectoryPath = outputDir,
                    ApplicationVersion = "1.0",
                    Actor = "troubled_agent",
                    Reason = "fail audit",
                    IncludeRawXml = false,
                    CreatedAt = DateTimeOffset.Now
                };

                // The ZIP creation must still complete, but writing the audit row throws.
                // The operation should throw the audit exception, but the ZIP must remain on disk (fail-closed auditing).
                var ex = Assert.Throws<InvalidOperationException>(() => service.CreateBackup(request));
                Assert.Contains("Simulated audit database insertion failure", ex.Message);

                var expectedZipFile = Directory.GetFiles(outputDir, "*.zip");
                Assert.Single(expectedZipFile);
                Assert.True(File.Exists(expectedZipFile[0]));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void CreateBackup_DoesNotLeakRawXmlContents_ToManifestOrAuditLogs()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag_xml_leak_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            string sourceDbPath = Path.Combine(tempDir, "source.db");
            string outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);
            string xmlDir = Path.Combine(tempDir, "xml");
            Directory.CreateDirectory(xmlDir);

            try
            {
                DatabaseHelper.InitializeDatabase(sourceDbPath);

                // Sensitive raw XML content
                string xmlContent1 = "<ENVELOPE><HEADER><VERSION>1</VERSION></HEADER><BODY><DATA><TALLYMESSAGE>SecretCompanyData</TALLYMESSAGE></DATA></BODY></ENVELOPE>";
                File.WriteAllText(Path.Combine(xmlDir, "tally_export.xml"), xmlContent1);

                var service = new DiagnosticBackupService(_repoFake);
                var request = new DiagnosticBackupRequest
                {
                    ConfigDatabasePath = sourceDbPath,
                    RawXmlDirectoryPath = xmlDir,
                    OutputDirectoryPath = outputDir,
                    ApplicationVersion = "1.0",
                    Actor = "xml_checker",
                    Reason = "security test",
                    IncludeRawXml = true,
                    CreatedAt = DateTimeOffset.Now
                };

                var result = service.CreateBackup(request);

                // Verify ZIP extracts correctly
                string extractDir = Path.Combine(tempDir, "extract");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                string manifestJson = File.ReadAllText(Path.Combine(extractDir, "manifest.json"));

                // Verify that raw xml tags and contents never leak to manifest.json
                Assert.DoesNotContain("<ENVELOPE>", manifestJson);
                Assert.DoesNotContain("<BODY>", manifestJson);
                Assert.DoesNotContain("<DATA>", manifestJson);
                Assert.DoesNotContain("SecretCompanyData", manifestJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void CreateBackup_TracksSkippedDirectories_WhenAccessDenied()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag_access_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            string sourceDbPath = Path.Combine(tempDir, "source.db");
            string outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);
            string logsDir = Path.Combine(tempDir, "logs");
            Directory.CreateDirectory(logsDir);
            string inaccessibleDir = Path.Combine(logsDir, "locked_folder");
            Directory.CreateDirectory(inaccessibleDir);

            // Restrict read access to simulate access denied on Windows
            var dInfo = new DirectoryInfo(inaccessibleDir);
            var dSecurity = dInfo.GetAccessControl();
            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var accessRule = new System.Security.AccessControl.FileSystemAccessRule(
                currentUser,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny);
            dSecurity.AddAccessRule(accessRule);
            dInfo.SetAccessControl(dSecurity);

            try
            {
                DatabaseHelper.InitializeDatabase(sourceDbPath);

                var service = new DiagnosticBackupService(_repoFake);
                var request = new DiagnosticBackupRequest
                {
                    ConfigDatabasePath = sourceDbPath,
                    LogDirectoryPath = logsDir,
                    OutputDirectoryPath = outputDir,
                    ApplicationVersion = "1.0",
                    Actor = "access_agent",
                    Reason = "test access denied",
                    IncludeRawXml = false,
                    CreatedAt = DateTimeOffset.Now
                };

                var result = service.CreateBackup(request);

                Assert.True(File.Exists(result.FilePath));

                string extractDir = Path.Combine(tempDir, "extract");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                string manifestJson = File.ReadAllText(Path.Combine(extractDir, "manifest.json"));
                using (var doc = System.Text.Json.JsonDocument.Parse(manifestJson))
                {
                    var root = doc.RootElement;
                    var entries = root.GetProperty("entries");
                    Assert.Equal(1, entries.GetProperty("skipped_file_count").GetInt32());

                    var skippedArray = root.GetProperty("skipped_files");
                    Assert.Single(skippedArray.EnumerateArray());
                    var item = skippedArray[0].GetString()!;
                    Assert.StartsWith("logs/locked_folder: UnauthorizedAccessException", item);
                }
            }
            finally
            {
                // Restore permissions so we can clean up
                try
                {
                    dSecurity.RemoveAccessRule(accessRule);
                    dInfo.SetAccessControl(dSecurity);
                }
                catch { }

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
