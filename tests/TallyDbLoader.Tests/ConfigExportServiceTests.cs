using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dapper;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class ConfigExportServiceTests
    {
        private class FakeConfigRepository : IConfigRepository
        {
            public List<DatabaseProfile> DatabaseProfiles { get; set; } = new List<DatabaseProfile>();
            public List<CompanyProfile> CompanyProfiles { get; set; } = new List<CompanyProfile>();

            public List<DatabaseProfile> GetAllDatabaseProfiles() => DatabaseProfiles;
            public List<CompanyProfile> GetAllCompanyProfiles() => CompanyProfiles;

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
        public void Constructor_Throws_WhenParametersAreInvalid()
        {
            var fakeRepo = new FakeConfigRepository();

            Assert.Throws<ArgumentNullException>(() => new ConfigExportService(null!, "1.0.0"));
            Assert.Throws<ArgumentException>(() => new ConfigExportService(fakeRepo, null!));
            Assert.Throws<ArgumentException>(() => new ConfigExportService(fakeRepo, ""));
            Assert.Throws<ArgumentException>(() => new ConfigExportService(fakeRepo, "   "));
        }

        [Fact]
        public void ExportJson_WithEmptyRepository_ReturnsValidEmptyEnvelope()
        {
            var fakeRepo = new FakeConfigRepository();
            var service = new ConfigExportService(fakeRepo, "2.0.0-beta");
            var exportedAt = new DateTimeOffset(2026, 6, 12, 10, 15, 30, TimeSpan.FromHours(5.5));

            string json = service.ExportJson(exportedAt);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("tally-db-loader.config-export", root.GetProperty("format").GetString());
            Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
            Assert.Equal("2.0.0-beta", root.GetProperty("application_version").GetString());
            Assert.Equal("2026-06-12T10:15:30.0000000+05:30", root.GetProperty("exported_at").GetString());

            var payload = root.GetProperty("payload");
            Assert.Empty(payload.GetProperty("database_profiles").EnumerateArray());
            Assert.Empty(payload.GetProperty("company_profiles").EnumerateArray());
        }

        [Fact]
        public void ExportJson_SanitizesSecrets_AndOmittedFields()
        {
            var fakeRepo = new FakeConfigRepository();
            fakeRepo.DatabaseProfiles.Add(new DatabaseProfile
            {
                Id = 42,
                Name = "SecretDB",
                Technology = "mssql",
                Server = "secret-server",
                Port = 1433,
                Username = "sa",
                Password = "SuperSecretPassword123",
                LastTestResult = "Passed",
                LastTestedAt = DateTime.UtcNow,
                UsedByCount = 5
            });

            var service = new ConfigExportService(fakeRepo, "1.0.0");
            string json = service.ExportJson(DateTimeOffset.Now);

            // Assert secrets are absolutely absent
            Assert.DoesNotContain("SuperSecretPassword123", json);
            Assert.DoesNotContain("dpapi:", json);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var dbProfiles = root.GetProperty("payload").GetProperty("database_profiles");
            
            var element = dbProfiles[0];
            Assert.Equal(42, element.GetProperty("id").GetInt32());
            Assert.Equal("SecretDB", element.GetProperty("name").GetString());
            Assert.Equal("mssql", element.GetProperty("technology").GetString());
            Assert.Equal("secret-server", element.GetProperty("server").GetString());
            Assert.Equal(1433, element.GetProperty("port").GetInt32());
            Assert.Equal("sa", element.GetProperty("username").GetString());
            Assert.True(element.GetProperty("has_password").GetBoolean());

            // Enforce exact payload shape
            var allowedProperties = new System.Collections.Generic.HashSet<string>
            {
                "id", "name", "technology", "server", "port", "username", "has_password"
            };
            var actualProperties = new System.Collections.Generic.HashSet<string>();
            foreach (var prop in element.EnumerateObject())
            {
                actualProperties.Add(prop.Name);
            }
            Assert.True(allowedProperties.SetEquals(actualProperties), "Database profile keys mismatch");
        }

        [Fact]
        public void ExportJson_ProjectsCompanyProfilesCorrectly()
        {
            var fakeRepo = new FakeConfigRepository();
            fakeRepo.CompanyProfiles.Add(new CompanyProfile
            {
                Id = 101,
                Name = "Acme Corp",
                TallyGuid = "guid-123",
                Consolidated = true,
                BooksFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Unspecified),
                BooksTo = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Unspecified),
                DbProfileId = 42,
                TargetCatalog = "acme_db",
                Schema = "custom_schema",
                TablePrefix = "custom_",
                Mode = "incremental",
                IntervalMinutes = 10,
                Enabled = true,
                NotifyOnError = false,
                PauseOnTallyClose = true,
                EntityFlags = 7,
                Status = "ok",
                LastRunAt = DateTime.UtcNow,
                LastDurationMs = 200,
                LastRowsWritten = 1000,
                ErrorCount24h = 0
            });

            var service = new ConfigExportService(fakeRepo, "1.0.0");
            string json = service.ExportJson(DateTimeOffset.Now);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var companyProfiles = root.GetProperty("payload").GetProperty("company_profiles");

            var element = companyProfiles[0];
            Assert.Equal(101, element.GetProperty("id").GetInt32());
            Assert.Equal("Acme Corp", element.GetProperty("name").GetString());
            Assert.Equal("guid-123", element.GetProperty("tally_guid").GetString());
            Assert.True(element.GetProperty("consolidated").GetBoolean());
            Assert.Equal("2026-04-01T00:00:00.0000000", element.GetProperty("books_from").GetString());
            Assert.Equal("2026-06-30T00:00:00.0000000", element.GetProperty("books_to").GetString());
            Assert.Equal(42, element.GetProperty("db_profile_id").GetInt32());
            Assert.Equal("acme_db", element.GetProperty("target_catalog").GetString());
            Assert.Equal("custom_schema", element.GetProperty("schema").GetString());
            Assert.Equal("custom_", element.GetProperty("table_prefix").GetString());
            Assert.Equal("incremental", element.GetProperty("mode").GetString());
            Assert.Equal(10, element.GetProperty("interval_minutes").GetInt32());
            Assert.True(element.GetProperty("enabled").GetBoolean());
            Assert.False(element.GetProperty("notify_on_error").GetBoolean());
            Assert.True(element.GetProperty("pause_on_tally_close").GetBoolean());
            Assert.Equal(7, element.GetProperty("entity_flags").GetInt32());

            // Enforce exact payload shape
            var allowedProperties = new System.Collections.Generic.HashSet<string>
            {
                "id", "name", "tally_guid", "consolidated", "books_from", "books_to",
                "db_profile_id", "target_catalog", "schema", "table_prefix", "mode",
                "interval_minutes", "enabled", "notify_on_error", "pause_on_tally_close",
                "entity_flags"
            };
            var actualProperties = new System.Collections.Generic.HashSet<string>();
            foreach (var prop in element.EnumerateObject())
            {
                actualProperties.Add(prop.Name);
            }
            Assert.True(allowedProperties.SetEquals(actualProperties), "Company profile keys mismatch");
        }
    }
}
