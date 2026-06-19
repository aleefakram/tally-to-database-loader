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
            public CompanyProfile? GetCompanyProfileById(int id) => CompanyProfiles.FirstOrDefault(c => c.Id == id);

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
            public long RecordDiagnosticBackupExport(
                string actor,
                string reason,
                string fileName,
                long fileSizeBytes,
                bool includeRawXml,
                int logFileCount,
                int rawXmlFileCount,
                int skippedFileCount,
                DateTime createdAt) => throw new NotImplementedException();

            public void AddBalanceSheetVerificationRun(BalanceSheetVerificationRun run) => throw new NotImplementedException();
            public List<BalanceSheetVerificationRun> GetRecentBalanceSheetVerificationRuns(int limit = 50) => throw new NotImplementedException();
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

        [Fact]
        public void ImportJson_WithUnresolvedConflicts_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            fake.DatabaseProfiles.Add(new DatabaseProfile { Id = 1, Name = "ExistingDB" });
            var service = new ConfigImportService(fake);

            // Payload contains DB profile with the same name, creating a conflict
            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""ExistingDB"",
                            ""technology"": ""postgres"",
                            ""server"": ""localhost"",
                            ""has_password"": false
                        }
                    ],
                    ""company_profiles"": []
                }
            }";

            var ex = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, new ImportDecision(), "system", "reason"));

            Assert.Contains("Conflict detected for database profile 'ExistingDB'", ex.Errors[0]);
        }

        [Fact]
        public void ImportJson_WithMissingRequiredFields_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": """",
                            ""technology"": ""postgres"",
                            ""server"": """"
                        }
                    ],
                    ""company_profiles"": [
                        {
                            ""id"": 2,
                            ""name"": ""MyCompany"",
                            ""db_profile_id"": 1,
                            ""target_catalog"": """"
                        }
                    ]
                }
            }";

            var ex = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, new ImportDecision(), "system", "reason"));

            Assert.Contains("is missing a name.", ex.Errors[0]);
            Assert.Contains("is missing target_catalog.", ex.Errors[ex.Errors.Count - 1]);
        }

        [Fact]
        public void ImportJson_WithInvalidDateFormat_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""MyDB"",
                            ""technology"": ""postgres"",
                            ""server"": ""localhost"",
                            ""has_password"": false
                        }
                    ],
                    ""company_profiles"": [
                        {
                            ""id"": 2,
                            ""name"": ""MyCompany"",
                            ""db_profile_id"": 1,
                            ""books_from"": ""invalid-date-format"",
                            ""target_catalog"": ""catalog""
                        }
                    ]
                }
            }";

            var ex = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, new ImportDecision(), "system", "reason"));

            Assert.Contains("has an invalid books_from date format", ex.Errors[0]);
        }


        [Fact]
        public void ImportJson_WithValidPayloadAndConflictStrategy_ImportsSuccessfully()
        {
            var fake = new FakeConfigRepository();
            fake.DatabaseProfiles.Add(new DatabaseProfile { Id = 10, Name = "TargetDB" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""TargetDB"",
                            ""technology"": ""mssql"",
                            ""server"": ""localhost"",
                            ""port"": 1433,
                            ""username"": ""sa"",
                            ""has_password"": true
                        }
                    ],
                    ""company_profiles"": []
                }
            }";

            var decision = new ImportDecision();
            decision.DatabaseConflicts[1] = ConflictResolutionStrategy.Overwrite;
            decision.DatabasePasswords[1] = "new-pass";

            service.ImportJson(json, decision, "system", "reason");

            // Verify it invoked repository import with correct mapped arguments
            Assert.NotNull(fake.LastDatabaseImports);
            Assert.Single(fake.LastDatabaseImports);
            var mappedDb = fake.LastDatabaseImports[0];
            Assert.Equal(1, mappedDb.SourceId);
            Assert.Equal(10, mappedDb.ExistingLocalId);
            Assert.Equal(ImportAction.Overwrite, mappedDb.Action);
            Assert.Equal("new-pass", mappedDb.Password);
            Assert.False(mappedDb.PreserveExistingPassword);

            // Verify audit before/after JSON does not contain any passwords/secrets
            Assert.NotNull(fake.LastBeforeJson);
            Assert.NotNull(fake.LastAfterJson);
            Assert.DoesNotContain("new-pass", fake.LastBeforeJson);
            Assert.DoesNotContain("new-pass", fake.LastAfterJson);
            Assert.DoesNotContain("password", fake.LastBeforeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", fake.LastAfterJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dpapi:", fake.LastBeforeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dpapi:", fake.LastAfterJson, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ImportJson_AuditExcludesSecrets()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""MyDB"",
                            ""technology"": ""postgres"",
                            ""server"": ""localhost"",
                            ""has_password"": true
                        }
                    ],
                    ""company_profiles"": []
                }
            }";

            var decision = new ImportDecision();
            decision.DatabasePasswords[1] = "secret-import-password-12345";

            service.ImportJson(json, decision, "system", "reason");

            Assert.NotNull(fake.LastBeforeJson);
            Assert.NotNull(fake.LastAfterJson);
            Assert.DoesNotContain("secret-import-password-12345", fake.LastBeforeJson);
            Assert.DoesNotContain("secret-import-password-12345", fake.LastAfterJson);
            Assert.DoesNotContain("password", fake.LastBeforeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", fake.LastAfterJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dpapi:", fake.LastBeforeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dpapi:", fake.LastAfterJson, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ImportJson_WithAmbiguousCompanyMatches_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            // Match guid with one company profile, name with another
            fake.CompanyProfiles.Add(new CompanyProfile { Id = 101, Name = "CompanyOne", TallyGuid = "guid-111" });
            fake.CompanyProfiles.Add(new CompanyProfile { Id = 102, Name = "CompanyTwo", TallyGuid = "guid-222" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""MyDB"",
                            ""technology"": ""postgres"",
                            ""server"": ""localhost"",
                            ""has_password"": false
                        }
                    ],
                    ""company_profiles"": [
                        {
                            ""id"": 5,
                            ""name"": ""CompanyOne"",
                            ""tally_guid"": ""guid-222"",
                            ""db_profile_id"": 1,
                            ""target_catalog"": ""catalog""
                        }
                    ]
                }
            }";

            var decision = new ImportDecision();
            decision.CompanyConflicts[5] = ConflictResolutionStrategy.Overwrite;

            var ex = Assert.Throws<ConfigImportValidationException>(() => service.ImportJson(json, decision, "system", "reason"));
            Assert.Contains("Ambiguous conflict for company profile 'CompanyOne'", ex.Errors[0]);
        }

        [Fact]
        public void ImportJson_WithSkippedDbReference_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            fake.DatabaseProfiles.Add(new DatabaseProfile { Id = 10, Name = "MyDB" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""MyDB"",
                            ""technology"": ""postgres"",
                            ""server"": ""localhost"",
                            ""has_password"": false
                        }
                    ],
                    ""company_profiles"": [
                        {
                            ""id"": 5,
                            ""name"": ""MyCompany"",
                            ""db_profile_id"": 1,
                            ""target_catalog"": ""catalog""
                        }
                    ]
                }
            }";

            var decision = new ImportDecision();
            decision.DatabaseConflicts[1] = ConflictResolutionStrategy.Skip;
            decision.CompanyConflicts[5] = ConflictResolutionStrategy.Overwrite; // Overwrite company but DB is skipped -> invalid!

            var ex = Assert.Throws<ConfigImportValidationException>(() => service.ImportJson(json, decision, "system", "reason"));
            Assert.Contains("is skipped, but the company profile is not marked to skip", ex.Errors[0]);
        }

        [Fact]
        public void ImportJson_WithMissingTechnology_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""MyDB"",
                            ""server"": ""localhost"",
                            ""has_password"": false
                        }
                    ],
                    ""company_profiles"": []
                }
            }";

            var ex = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, new ImportDecision(), "system", "reason"));

            Assert.Contains("is missing technology.", ex.Errors[0]);
        }

        [Fact]
        public void ImportJson_WithMissingHasPassword_ThrowsConfigImportValidationException()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        {
                            ""id"": 1,
                            ""name"": ""MyDB"",
                            ""technology"": ""postgres"",
                            ""server"": ""localhost""
                        }
                    ],
                    ""company_profiles"": []
                }
            }";

            var ex = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, new ImportDecision(), "system", "reason"));

            Assert.Contains("is missing has_password flag.", ex.Errors[0]);
        }

        [Fact]
        public void PreviewJson_WithInvalidJson_ReturnsValidationErrors()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            var preview = service.PreviewJson("invalid json");
            Assert.False(preview.IsValid);
            Assert.Contains("Invalid JSON content", preview.ValidationErrors[0]);
        }

        [Fact]
        public void PreviewJson_WithValidPayload_ReturnsProfiles()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 2, ""name"": ""NewDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": false }
                    ],
                    ""company_profiles"": [
                        { ""id"": 11, ""name"": ""NewComp"", ""db_profile_id"": 2, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.False(preview.HasConflicts);

            var db = Assert.Single(preview.DatabaseProfiles);
            Assert.Equal(2, db.SourceId);
            Assert.Equal("NewDb", db.Name);
            Assert.False(db.HasConflict);
            Assert.False(db.HasPassword);

            var comp = Assert.Single(preview.CompanyProfiles);
            Assert.Equal(11, comp.SourceId);
            Assert.Equal("NewComp", comp.Name);
            Assert.False(comp.HasConflict);
        }

        [Fact]
        public void PreviewJson_WithDbConflict_SetsHasConflicts()
        {
            var fake = new FakeConfigRepository();
            fake.DatabaseProfiles.Add(new DatabaseProfile { Name = "ConflictingDb" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""ConflictingDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": true }
                    ],
                    ""company_profiles"": []
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.True(preview.HasConflicts);
            Assert.True(preview.DatabaseProfiles[0].HasConflict);
        }

        [Fact]
        public void PreviewJson_WithCompanyConflict_SetsHasConflicts()
        {
            var fake = new FakeConfigRepository();
            fake.CompanyProfiles.Add(new CompanyProfile { Name = "ConflictingComp" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""MyDB"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": false }
                    ],
                    ""company_profiles"": [
                        { ""id"": 10, ""name"": ""ConflictingComp"", ""db_profile_id"": 1, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.True(preview.HasConflicts);
            Assert.True(preview.CompanyProfiles[0].HasConflict);
        }

        [Fact]
        public void PreviewJson_WithMissingHasPassword_ReturnsValidationError()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""MyDB"", ""technology"": ""postgres"", ""server"": ""localhost"" }
                    ],
                    ""company_profiles"": []
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.False(preview.IsValid);
            Assert.Contains("missing has_password flag", preview.ValidationErrors[0]);
        }

        [Fact]
        public void PreviewJson_WithBrokenDbProfileIdReference_ReturnsValidationError()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [],
                    ""company_profiles"": [
                        { ""id"": 10, ""name"": ""OrphanComp"", ""db_profile_id"": 99, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.False(preview.IsValid);
            Assert.Contains("references database profile ID 99 which is not present in the import payload", preview.ValidationErrors[0]);
        }

        [Fact]
        public void ImportAndPreview_HaveParity_ForConflicts()
        {
            var fake = new FakeConfigRepository();
            fake.DatabaseProfiles.Add(new DatabaseProfile { Name = "ConflictingDb" });
            fake.CompanyProfiles.Add(new CompanyProfile { Name = "ConflictingComp" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""ConflictingDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": false }
                    ],
                    ""company_profiles"": [
                        { ""id"": 10, ""name"": ""ConflictingComp"", ""db_profile_id"": 1, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            // 1. Verify Preview detects conflict
            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.True(preview.HasConflicts);
            Assert.True(preview.DatabaseProfiles[0].HasConflict);
            Assert.True(preview.CompanyProfiles[0].HasConflict);

            // 2. Verify ImportJson throws validation exception with matching error message
            var decision = new ImportDecision(); // No strategy given
            var importEx = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, decision, "system", "reason"));

            Assert.Contains("Conflict detected for database profile 'ConflictingDb'", importEx.Errors[0]);
        }
    }
}
