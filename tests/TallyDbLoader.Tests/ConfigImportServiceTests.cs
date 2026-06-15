using System;
using System.Collections.Generic;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class ConfigImportServiceTests
    {
        private class FakeConfigRepository : IConfigRepository
        {
            public List<DatabaseProfile> DatabaseProfiles { get; set; } = new List<DatabaseProfile>();
            public List<CompanyProfile> CompanyProfiles { get; set; } = new List<CompanyProfile>();

            public List<ResolvedDatabaseProfileImport>? LastDatabaseImports { get; set; }
            public List<ResolvedCompanyProfileImport>? LastCompanyImports { get; set; }
            public string? LastActor { get; set; }
            public string? LastReason { get; set; }
            public string? LastBeforeJson { get; set; }
            public string? LastAfterJson { get; set; }

            public List<DatabaseProfile> GetAllDatabaseProfiles() => DatabaseProfiles;
            public List<CompanyProfile> GetAllCompanyProfiles() => CompanyProfiles;

            public void ImportSanitizedConfig(
                List<ResolvedDatabaseProfileImport> databaseProfiles,
                List<ResolvedCompanyProfileImport> companyProfiles,
                string actor,
                string reason,
                string beforeJson,
                string afterJson)
            {
                LastDatabaseImports = databaseProfiles;
                LastCompanyImports = companyProfiles;
                LastActor = actor;
                LastReason = reason;
                LastBeforeJson = beforeJson;
                LastAfterJson = afterJson;
            }

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
        }

        [Fact]
        public void Constructor_Throws_WhenRepositoryIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new ConfigImportService(null!));
        }

        [Fact]
        public void ImportJson_WithNullOrEmptyJson_ThrowsArgumentException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            Assert.Throws<ArgumentException>(() => service.ImportJson(null!, new ImportDecision(), "actor", "reason"));
            Assert.Throws<ArgumentException>(() => service.ImportJson("", new ImportDecision(), "actor", "reason"));
            Assert.Throws<ArgumentException>(() => service.ImportJson("   ", new ImportDecision(), "actor", "reason"));
        }

        [Fact]
        public void ImportJson_WithNullActorOrReason_ThrowsArgumentException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            Assert.Throws<ArgumentException>(() => service.ImportJson("{}", new ImportDecision(), null!, "reason"));
            Assert.Throws<ArgumentException>(() => service.ImportJson("{}", new ImportDecision(), "", "reason"));
            Assert.Throws<ArgumentException>(() => service.ImportJson("{}", new ImportDecision(), "actor", null!));
            Assert.Throws<ArgumentException>(() => service.ImportJson("{}", new ImportDecision(), "actor", ""));
        }

        [Fact]
        public void ImportJson_WithInvalidJsonFormat_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            var ex = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson("invalid json", new ImportDecision(), "actor", "reason"));
            
            Assert.Single(ex.Errors);
            Assert.Contains("Invalid JSON content", ex.Errors[0]);
        }

        [Fact]
        public void ImportJson_WithUnsupportedFormatOrSchemaOrAppVersion_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            // Invalid format
            string json1 = @"{
                ""format"": ""invalid-format"",
                ""schema_version"": 1,
                ""application_version"": ""1.0.0"",
                ""payload"": { ""database_profiles"": [], ""company_profiles"": [] }
            }";
            var ex1 = Assert.Throws<ConfigImportValidationException>(() => service.ImportJson(json1, new ImportDecision(), "actor", "reason"));
            Assert.Contains("Unsupported or invalid format string.", ex1.Errors);

            // Invalid schema version
            string json2 = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 2,
                ""application_version"": ""1.0.0"",
                ""payload"": { ""database_profiles"": [], ""company_profiles"": [] }
            }";
            var ex2 = Assert.Throws<ConfigImportValidationException>(() => service.ImportJson(json2, new ImportDecision(), "actor", "reason"));
            Assert.Contains("Unsupported schema version. Only version 1 is supported.", ex2.Errors);

            // Missing app version
            string json3 = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": """",
                ""payload"": { ""database_profiles"": [], ""company_profiles"": [] }
            }";
            var ex3 = Assert.Throws<ConfigImportValidationException>(() => service.ImportJson(json3, new ImportDecision(), "actor", "reason"));
            Assert.Contains("Application version must be a non-empty string.", ex3.Errors);
        }

        [Fact]
        public void ImportJson_WithMissingPayload_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""1.0.0"",
                ""payload"": null
            }";

            var ex = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, new ImportDecision(), "actor", "reason"));

            Assert.Contains("Configuration payload is missing or empty.", ex.Errors);
        }
    }
}
