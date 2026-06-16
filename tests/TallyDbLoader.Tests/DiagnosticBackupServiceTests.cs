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
    }
}
