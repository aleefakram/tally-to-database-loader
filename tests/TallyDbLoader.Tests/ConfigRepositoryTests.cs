using System.IO;
using System.Linq;
using Xunit;
using Dapper;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Tests
{
    public class ConfigRepositoryTests
    {
        private readonly string _testDbPath = "test_config.db";

        [Fact]
        public void Test_Database_Initialization_And_CRUD()
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);

            DatabaseHelper.InitializeDatabase(_testDbPath);
            var repo = new ConfigRepository(_testDbPath);

            var profile = new DatabaseProfile
            {
                Name = "LocalSQL",
                Technology = "mssql",
                Server = "localhost",
                Port = 1433,
                Username = "sa",
                Password = "encryptedpwd"
            };

            repo.SaveDatabaseProfile(profile);
            var saved = repo.GetDatabaseProfileByName("LocalSQL");

            Assert.NotNull(saved);
            Assert.Equal("mssql", saved.Technology);
            Assert.Equal("localhost", saved.Server);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
        }

        [Fact]
        public void Test_CompanyProfile_CRUD()
        {
            string testDbPath = "test_jobs.db";
            if (System.IO.File.Exists(testDbPath)) System.IO.File.Delete(testDbPath);

            DatabaseHelper.InitializeDatabase(testDbPath);
            var repo = new ConfigRepository(testDbPath);

            var profile = new DatabaseProfile
            {
                Name = "TargetPostgres",
                Technology = "postgres",
                Server = "localhost",
                Port = 5432,
                Username = "postgres",
                Password = "password"
            };
            repo.SaveDatabaseProfile(profile);
            var savedProfile = repo.GetDatabaseProfileByName("TargetPostgres");
            Assert.NotNull(savedProfile);

            var company = new CompanyProfile
            {
                Name = "Yaghma Kababs",
                DbProfileId = savedProfile.Id,
                TargetCatalog = "yaghma_db",
                IntervalMinutes = 15,
                Status = "Idle",
                Enabled = true
            };

            repo.SaveCompanyProfile(company);
            var companies = repo.GetAllCompanyProfiles();

            Assert.Single(companies);
            Assert.Equal("Yaghma Kababs", companies[0].Name);
            Assert.Equal(savedProfile.Id, companies[0].DbProfileId);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(testDbPath)) System.IO.File.Delete(testDbPath);
        }

        [Fact]
        public void Should_Save_And_Retrieve_SyncMode()
        {
            string testDbPath = "test_syncmode.db";
            if (System.IO.File.Exists(testDbPath)) System.IO.File.Delete(testDbPath);

            DatabaseHelper.InitializeDatabase(testDbPath);
            var repo = new ConfigRepository(testDbPath);

            var profile = new DatabaseProfile
            {
                Name = "TestDb",
                Technology = "postgres",
                Server = "localhost"
            };
            repo.SaveDatabaseProfile(profile);
            var savedProfile = repo.GetAllDatabaseProfiles().First();

            var company = new CompanyProfile
            {
                Name = "Company A",
                DbProfileId = savedProfile.Id,
                TargetCatalog = "catalog_a",
                IntervalMinutes = 30,
                Mode = "incremental",
                Enabled = true
            };

            repo.SaveCompanyProfile(company);
            var retrieved = repo.GetAllCompanyProfiles().First();
            Assert.Equal("incremental", retrieved.Mode);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(testDbPath)) System.IO.File.Delete(testDbPath);
        }

        [Fact]
        public void Test_DPAPI_RoundTrip_Encryption()
        {
            string testDbPath = "test_dpapi.db";
            if (File.Exists(testDbPath)) File.Delete(testDbPath);

            DatabaseHelper.InitializeDatabase(testDbPath);
            var repo = new ConfigRepository(testDbPath);

            var profile = new DatabaseProfile
            {
                Name = "LocalSQL",
                Technology = "mssql",
                Server = "localhost",
                Port = 1433,
                Username = "sa",
                Password = "SecretPassword123"
            };

            repo.SaveDatabaseProfile(profile);

            // Directly query database to verify it has the "dpapi:" prefix
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
            {
                var rawPassword = conn.ExecuteScalar<string>("SELECT password FROM database_profiles WHERE name = 'LocalSQL'");
                Assert.NotNull(rawPassword);
                Assert.StartsWith("dpapi:", rawPassword);
            }

            // Retrieve via repository to verify decryption works
            var retrieved = repo.GetDatabaseProfileByName("LocalSQL");
            Assert.NotNull(retrieved);
            Assert.Equal("SecretPassword123", retrieved.Password);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(testDbPath)) File.Delete(testDbPath);
        }

        [Fact]
        public void Test_Legacy_Plaintext_Compatibility_And_Migration()
        {
            string testDbPath = "test_legacy.db";
            if (File.Exists(testDbPath)) File.Delete(testDbPath);

            DatabaseHelper.InitializeDatabase(testDbPath);
            var repo = new ConfigRepository(testDbPath);

            // Bypass SaveDatabaseProfile to insert raw plaintext password
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
            {
                conn.Execute(@"
                    INSERT INTO database_profiles (name, technology, server, port, username, password)
                    VALUES (@Name, @Technology, @Server, @Port, @Username, @Password)",
                    new { Name = "LegacyTarget", Technology = "postgres", Server = "localhost", Port = 5432, Username = "postgres", Password = "raw_plaintext_password" });
            }

            // Verify retrieval returns raw plaintext (compatibility)
            var retrieved = repo.GetDatabaseProfileByName("LegacyTarget");
            Assert.NotNull(retrieved);
            Assert.Equal("raw_plaintext_password", retrieved.Password);

            // Save via repo to trigger migration
            repo.SaveDatabaseProfile(retrieved);

            // Verify database now stores dpapi: prefix (migration)
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
            {
                var rawPassword = conn.ExecuteScalar<string>("SELECT password FROM database_profiles WHERE name = 'LegacyTarget'");
                Assert.NotNull(rawPassword);
                Assert.StartsWith("dpapi:", rawPassword);
            }

            // Verify retrieval still works
            var retrievedMigrated = repo.GetDatabaseProfileByName("LegacyTarget");
            Assert.NotNull(retrievedMigrated);
            Assert.Equal("raw_plaintext_password", retrievedMigrated.Password);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(testDbPath)) File.Delete(testDbPath);
        }

        [Fact]
        public void Test_Malformed_DPAPI_Decryption_Fallback()
        {
            string testDbPath = "test_malformed.db";
            if (File.Exists(testDbPath)) File.Delete(testDbPath);

            DatabaseHelper.InitializeDatabase(testDbPath);
            var repo = new ConfigRepository(testDbPath);

            // Insert a profile with invalid/corrupted dpapi: password
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
            {
                conn.Execute(@"
                    INSERT INTO database_profiles (name, technology, server, port, username, password)
                    VALUES (@Name, @Technology, @Server, @Port, @Username, @Password)",
                    new { Name = "MalformedTarget", Technology = "postgres", Server = "localhost", Port = 5432, Username = "postgres", Password = "dpapi:invalid_corrupted_base64_or_keys" });
            }

            // Verify it logs and returns string.Empty for UI resilience
            var retrieved = repo.GetDatabaseProfileByName("MalformedTarget");
            Assert.NotNull(retrieved);
            Assert.Equal(string.Empty, retrieved.Password);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(testDbPath)) File.Delete(testDbPath);
        }

        [Fact]
        public void Test_FailClosed_Metadata_Updates_Row_Assertion()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_failclosed_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                // 1. MarkCompanyProfileUnknown with missing ID should throw InvalidOperationException
                Assert.Throws<System.InvalidOperationException>(() =>
                    repo.MarkCompanyProfileUnknown(9999, "Testing missing ID", System.DateTime.Now)
                );

                // 2. CompleteCompanyProfileRun with missing ID should throw InvalidOperationException
                Assert.Throws<System.InvalidOperationException>(() =>
                    repo.CompleteCompanyProfileRun(9999, "completed", System.DateTime.Now, 100, 0, false)
                );

                // 3. UpdateSyncRun with missing/invalid run ID should throw InvalidOperationException
                var nonExistentRun = new SyncRun
                {
                    Id = 9999,
                    CompanyId = 1,
                    CompanyName = "Test Company",
                    StartedAt = System.DateTime.Now,
                    Status = "completed"
                };
                Assert.Throws<System.InvalidOperationException>(() =>
                    repo.UpdateSyncRun(nonExistentRun)
                );
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath))
                {
                    try { File.Delete(testDbPath); } catch { }
                }
            }
        }

        [Fact]
        public void SaveTallySettings_WritesAuditRow()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_audit_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                repo.SaveTallySettings(new TallySettings
                {
                    Server = "tallyhost",
                    Port = 9001,
                    AutoStartTally = true,
                    TallyExePath = @"C:\Tally\tally.exe",
                    TallyIniPath = @"C:\Tally\tally.ini"
                });

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    int count = conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM config_audit_log WHERE action = 'update_tally_settings'");
                    Assert.Equal(1, count);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void SaveTallySettings_AuditRow_ContainsOnlyAllowedFields()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_fields_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                repo.SaveTallySettings(new TallySettings
                {
                    Server = "myserver",
                    Port = 9999,
                    AutoStartTally = false,
                    TallyExePath = @"C:\Tally\tally.exe",
                    TallyIniPath = @"C:\Tally\tally.ini"
                });

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    string? beforeJson = conn.ExecuteScalar<string>(
                        "SELECT before_json FROM config_audit_log WHERE action = 'update_tally_settings'");
                    string? afterJson = conn.ExecuteScalar<string>(
                        "SELECT after_json FROM config_audit_log WHERE action = 'update_tally_settings'");

                    Assert.NotNull(beforeJson);
                    Assert.NotNull(afterJson);

                    // Assert exact property set and count for both snapshots.
                    // JsonDocument catches extra fields regardless of casing
                    // (e.g. an accidental whole-object serialization would emit TallyExePath, not tally_exe_path).
                    var allowedProperties = new System.Collections.Generic.HashSet<string>
                    {
                        "server", "port", "auto_start_tally"
                    };

                    using (var beforeDoc = System.Text.Json.JsonDocument.Parse(beforeJson))
                    {
                        var beforeProps = beforeDoc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                        Assert.Equal(3, beforeProps.Count);
                        // SetEquals directly expresses set membership — order of enumeration is irrelevant.
                        Assert.True(allowedProperties.SetEquals(beforeProps));
                    }

                    using (var afterDoc = System.Text.Json.JsonDocument.Parse(afterJson))
                    {
                        var afterProps = afterDoc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                        Assert.Equal(3, afterProps.Count);
                        Assert.True(allowedProperties.SetEquals(afterProps));
                    }

                    // after_json reflects submitted values
                    Assert.Contains("\"myserver\"", afterJson);
                    Assert.Contains("9999", afterJson);

                    // Excluded field names — both snake_case and PascalCase variants — must not appear.
                    // Also check path values, which are distinctive enough to catch value leaks.
                    foreach (var json in new[] { beforeJson, afterJson })
                    {
                        Assert.DoesNotContain("tally_exe_path", json);
                        Assert.DoesNotContain("tally_ini_path", json);
                        Assert.DoesNotContain("TallyExePath", json);
                        Assert.DoesNotContain("TallyIniPath", json);
                        Assert.DoesNotContain("tally.exe", json);
                        Assert.DoesNotContain("tally.ini", json);
                    }
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void SaveTallySettings_AuditRow_BeforeJsonReflectsLoadedRow()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_before_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                // First save: establish known prior state
                repo.SaveTallySettings(new TallySettings { Server = "original", Port = 9000, AutoStartTally = false });

                // Second save: before_json must reflect the first save's values ("original"/9000/false)
                repo.SaveTallySettings(new TallySettings { Server = "updated", Port = 9001, AutoStartTally = true });

                // Query the second audit row (the most recent one) without deleting anything.
                // The audit log is append-only; deleting rows from it in tests sets a bad example.
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    string? beforeJson = conn.ExecuteScalar<string>(
                        "SELECT before_json FROM config_audit_log WHERE action = 'update_tally_settings' ORDER BY id DESC LIMIT 1");

                    Assert.NotNull(beforeJson);
                    Assert.Contains("\"original\"", beforeJson);
                    Assert.Contains("9000", beforeJson);
                    Assert.Contains("false", beforeJson);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void SaveTallySettings_RollsBack_WhenAuditTableMissing()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_rollback_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                // Record original settings before the test
                var original = repo.GetTallySettings();

                // Drop audit table to force rollback
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    conn.Execute("DROP TABLE config_audit_log;");
                }

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    repo.SaveTallySettings(new TallySettings
                    {
                        Server = "should-not-persist",
                        Port = 1234,
                        AutoStartTally = true
                    }));

                Assert.NotNull(ex.InnerException);

                // Settings must be unchanged because the entire transaction rolled back
                var after = repo.GetTallySettings();
                Assert.Equal(original.Server, after.Server);
                Assert.Equal(original.Port, after.Port);
                Assert.Equal(original.AutoStartTally, after.AutoStartTally);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void SaveTallySettings_ThrowsInvalidOperationException_WhenSingletonRowMissing()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_nosingle_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                // Remove the singleton row to simulate corrupt database
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    conn.Execute("DELETE FROM tally_settings WHERE id = 1;");
                }

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    repo.SaveTallySettings(new TallySettings { Server = "x", Port = 9000, AutoStartTally = false }));

                Assert.Contains("tally_settings singleton row (id=1) is missing", ex.Message);

                // Confirm no audit row was written
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log");
                    Assert.Equal(0, count);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void SaveTallySettings_AuditRow_HasExpectedMetadata()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_meta_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                repo.SaveTallySettings(new TallySettings { Server = "localhost", Port = 9000, AutoStartTally = false });

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    string? actor = conn.ExecuteScalar<string?>(
                        "SELECT actor FROM config_audit_log WHERE action = 'update_tally_settings'");
                    string? action = conn.ExecuteScalar<string?>(
                        "SELECT action FROM config_audit_log WHERE action = 'update_tally_settings'");
                    string? entityType = conn.ExecuteScalar<string?>(
                        "SELECT entity_type FROM config_audit_log WHERE action = 'update_tally_settings'");
                    long entityId = conn.ExecuteScalar<long>(
                        "SELECT entity_id FROM config_audit_log WHERE action = 'update_tally_settings'");
                    string? entityName = conn.ExecuteScalar<string?>(
                        "SELECT entity_name FROM config_audit_log WHERE action = 'update_tally_settings'");
                    string? reason = conn.ExecuteScalar<string?>(
                        "SELECT reason FROM config_audit_log WHERE action = 'update_tally_settings'");

                    Assert.NotNull(actor);
                    Assert.NotNull(action);
                    Assert.NotNull(entityType);
                    Assert.NotNull(reason);

                    Assert.Equal("system", actor);
                    Assert.Equal("update_tally_settings", action);
                    Assert.Equal("tally_settings", entityType);
                    Assert.Equal(1L, entityId);
                    Assert.Null(entityName);
                    Assert.Equal("Tally settings updated", reason);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
            }
        }

        // -- CompanyProfile audit -----------------------------------------------

        private static (ConfigRepository repo, int dbProfileId) SetupCompanyProfileDb(string testDbPath)
        {
            DatabaseHelper.InitializeDatabase(testDbPath);
            var repo = new ConfigRepository(testDbPath);
            repo.SaveDatabaseProfile(new DatabaseProfile { Name = "TestDb", Technology = "postgres", Server = "localhost" });
            int dbId;
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                dbId = (int)conn.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'TestDb'");
            return (repo, dbId);
        }

        [Fact]
        public void SaveCompanyProfile_Create_WritesOneAuditRow()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_create_audit_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                repo.SaveCompanyProfile(new CompanyProfile { Name = "Alpha", DbProfileId = dbId, TargetCatalog = "alpha_db" });
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'create_company_profile'");
                Assert.Equal(1, count);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveCompanyProfile_Create_AuditUsesGeneratedIdInEntityIdAndAfterJson()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_create_id_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                repo.SaveCompanyProfile(new CompanyProfile { Name = "Beta", DbProfileId = dbId, TargetCatalog = "beta_db" });
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                long entityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE action = 'create_company_profile'");
                string? afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_company_profile'");
                long rowId = conn.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Beta'");
                Assert.Equal(rowId, entityId);
                Assert.NotNull(afterJson);
                using var doc = System.Text.Json.JsonDocument.Parse(afterJson);
                long idInJson = doc.RootElement.GetProperty("id").GetInt64();
                Assert.Equal(rowId, idInJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveCompanyProfile_Create_BeforeJsonIsEmptyObject()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_create_before_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                repo.SaveCompanyProfile(new CompanyProfile { Name = "Gamma", DbProfileId = dbId, TargetCatalog = "gamma_db" });
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string? beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'create_company_profile'");
                Assert.NotNull(beforeJson);
                Assert.Equal("{}", beforeJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveCompanyProfile_Update_WritesOneAuditRow()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_update_audit_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                var cp = new CompanyProfile { Name = "Delta", DbProfileId = dbId, TargetCatalog = "delta_db" };
                repo.SaveCompanyProfile(cp);
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Delta'");
                cp.Name = "Delta Updated";
                repo.SaveCompanyProfile(cp);
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'update_company_profile'");
                Assert.Equal(1, count);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveCompanyProfile_Update_BeforeJsonReflectsPreMutationState()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_update_before_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                var cp = new CompanyProfile { Name = "Epsilon", DbProfileId = dbId, TargetCatalog = "eps_db", Mode = "full", IntervalMinutes = 30 };
                repo.SaveCompanyProfile(cp);
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Epsilon'");
                cp.Name = "Epsilon V2";
                cp.Mode = "incremental";
                cp.IntervalMinutes = 60;
                repo.SaveCompanyProfile(cp);
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string? beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_company_profile'");
                Assert.NotNull(beforeJson);
                Assert.Contains("\"Epsilon\"", beforeJson);
                Assert.Contains("\"full\"", beforeJson);
                Assert.Contains("30", beforeJson);
                string? afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_company_profile'");
                Assert.NotNull(afterJson);
                Assert.Contains("\"Epsilon V2\"", afterJson);
                Assert.Contains("\"incremental\"", afterJson);
                Assert.Contains("60", afterJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteCompanyProfile_WritesAuditRow_AndRemovesRow()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_delete_audit_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                repo.SaveCompanyProfile(new CompanyProfile { Name = "Zeta", DbProfileId = dbId, TargetCatalog = "zeta_db" });
                int cpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cpId = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Zeta'");
                repo.DeleteCompanyProfile(cpId);
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'delete_company_profile'"));
                Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE id = @Id", new { Id = cpId }));
                string? beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'delete_company_profile'");
                Assert.NotNull(beforeJson);
                Assert.Contains($"{cpId}", beforeJson);
                Assert.Contains("\"Zeta\"", beforeJson);
                Assert.Contains("\"zeta_db\"", beforeJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteCompanyProfile_AfterJsonIsEmptyObject()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_delete_after_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                repo.SaveCompanyProfile(new CompanyProfile { Name = "Eta", DbProfileId = dbId, TargetCatalog = "eta_db" });
                int cpId;
                using (var cId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cpId = (int)cId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Eta'");
                repo.DeleteCompanyProfile(cpId);
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string? afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'delete_company_profile'");
                Assert.NotNull(afterJson);
                Assert.Equal("{}", afterJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveCompanyProfile_Create_RollsBack_WhenAuditTableMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_rb_create_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    conn.Execute("DROP TABLE config_audit_log;");
                Assert.Throws<InvalidOperationException>(() =>
                    repo.SaveCompanyProfile(new CompanyProfile { Name = "ShouldNotExist", DbProfileId = dbId, TargetCatalog = "x" }));
                using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE name = 'ShouldNotExist'"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveCompanyProfile_Update_RollsBack_WhenAuditTableMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_rb_update_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                var cp = new CompanyProfile { Name = "OriginalName", DbProfileId = dbId, TargetCatalog = "orig_db" };
                repo.SaveCompanyProfile(cp);
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'OriginalName'");
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    conn.Execute("DROP TABLE config_audit_log;");
                cp.Name = "ShouldNotUpdate";
                Assert.Throws<InvalidOperationException>(() => repo.SaveCompanyProfile(cp));
                using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE name = 'OriginalName'"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteCompanyProfile_RollsBack_WhenAuditTableMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_rb_delete_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                repo.SaveCompanyProfile(new CompanyProfile { Name = "Theta", DbProfileId = dbId, TargetCatalog = "theta_db" });
                int cpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cpId = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Theta'");
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    conn.Execute("DROP TABLE config_audit_log;");
                Assert.Throws<InvalidOperationException>(() => repo.DeleteCompanyProfile(cpId));
                using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE id = @Id", new { Id = cpId }));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveCompanyProfile_Update_ThrowsInvalidOperationException_WhenProfileMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_miss_upd_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    repo.SaveCompanyProfile(new CompanyProfile { Id = 9999, Name = "Ghost", DbProfileId = 1, TargetCatalog = "x" }));
                Assert.Contains("9999", ex.Message);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteCompanyProfile_ThrowsInvalidOperationException_WhenProfileMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_miss_del_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var ex = Assert.Throws<InvalidOperationException>(() => repo.DeleteCompanyProfile(9999));
                Assert.Contains("9999", ex.Message);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void CompanyProfileAudit_SnapshotContainsExactlyAllowedFields()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_snap_fields_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                var cp = new CompanyProfile
                {
                    Name = "Iota", DbProfileId = dbId, TargetCatalog = "iota_db",
                    TallyGuid = "G1", Consolidated = false, Mode = "full",
                    IntervalMinutes = 15, Schema = "public", TablePrefix = "tally_",
                    Enabled = true, NotifyOnError = true, PauseOnTallyClose = false, EntityFlags = 15
                };
                repo.SaveCompanyProfile(cp);

                int cpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cpId = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Iota'");

                cp.Id = cpId;
                cp.Name = "Iota Updated";
                repo.SaveCompanyProfile(cp);

                repo.DeleteCompanyProfile(cpId);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");

                string? createAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_company_profile'");
                string? updateBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_company_profile'");
                string? updateAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_company_profile'");
                string? deleteBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'delete_company_profile'");

                Assert.NotNull(createAfter);
                Assert.NotNull(updateBefore);
                Assert.NotNull(updateAfter);
                Assert.NotNull(deleteBefore);

                var allowed = new System.Collections.Generic.HashSet<string>
                {
                    "id", "name", "tally_guid", "consolidated", "books_from", "books_to",
                    "db_profile_id", "target_catalog", "schema", "table_prefix", "mode",
                    "interval_minutes", "enabled", "notify_on_error", "pause_on_tally_close", "entity_flags"
                };

                foreach (var json in new[] { createAfter, updateBefore, updateAfter, deleteBefore })
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                    Assert.Equal(16, props.Count);
                    Assert.True(allowed.SetEquals(props), $"JSON properties mismatch: {json}");
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void CompanyProfileAudit_SnapshotExcludesRuntimeFields()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_snap_excl_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);
                repo.SaveCompanyProfile(new CompanyProfile
                {
                    Name = "Kappa", DbProfileId = dbId, TargetCatalog = "kappa_db",
                    Status = "running", LastRunAt = System.DateTime.UtcNow,
                    LastDurationMs = 1234, LastRowsWritten = 99, ErrorCount24h = 3
                });
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string? afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_company_profile'");
                Assert.NotNull(afterJson);

                using var doc = System.Text.Json.JsonDocument.Parse(afterJson);
                var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

                var excludedProperties = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    "status", "last_run_at", "last_duration_ms", "last_rows_written", "error_count_24h", "db"
                };

                foreach (var prop in properties)
                {
                    Assert.False(excludedProperties.Contains(prop), $"Snapshot should not contain property: '{prop}'");
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void CompanyProfileAudit_AuditRows_HaveExpectedMetadata()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cp_meta_expect_{System.Guid.NewGuid()}.db");
            try
            {
                var (repo, dbId) = SetupCompanyProfileDb(path);

                // 1. Create
                var cp = new CompanyProfile { Name = "Mu", DbProfileId = dbId, TargetCatalog = "mu_db" };
                repo.SaveCompanyProfile(cp);

                int cpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    cpId = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Mu'");

                // 2. Update
                cp.Id = cpId;
                cp.Name = "Mu Updated";
                repo.SaveCompanyProfile(cp);

                // 3. Delete
                repo.DeleteCompanyProfile(cpId);

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                {
                    // Assert Create Metadata
                    string? cActor = conn.ExecuteScalar<string?>("SELECT actor FROM config_audit_log WHERE action = 'create_company_profile'");
                    string? cType = conn.ExecuteScalar<string?>("SELECT entity_type FROM config_audit_log WHERE action = 'create_company_profile'");
                    long cEntityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE action = 'create_company_profile'");
                    string? cEntityName = conn.ExecuteScalar<string?>("SELECT entity_name FROM config_audit_log WHERE action = 'create_company_profile'");
                    string? cReason = conn.ExecuteScalar<string?>("SELECT reason FROM config_audit_log WHERE action = 'create_company_profile'");

                    Assert.Equal("system", cActor);
                    Assert.Equal("company_profile", cType);
                    Assert.Equal((long)cpId, cEntityId);
                    Assert.Equal("Mu", cEntityName);
                    Assert.Equal("Company profile created", cReason);

                    // Assert Update Metadata
                    string? uActor = conn.ExecuteScalar<string?>("SELECT actor FROM config_audit_log WHERE action = 'update_company_profile'");
                    string? uType = conn.ExecuteScalar<string?>("SELECT entity_type FROM config_audit_log WHERE action = 'update_company_profile'");
                    long uEntityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE action = 'update_company_profile'");
                    string? uEntityName = conn.ExecuteScalar<string?>("SELECT entity_name FROM config_audit_log WHERE action = 'update_company_profile'");
                    string? uReason = conn.ExecuteScalar<string?>("SELECT reason FROM config_audit_log WHERE action = 'update_company_profile'");

                    Assert.Equal("system", uActor);
                    Assert.Equal("company_profile", uType);
                    Assert.Equal((long)cpId, uEntityId);
                    Assert.Equal("Mu Updated", uEntityName);
                    Assert.Equal("Company profile updated", uReason);

                    // Assert Delete Metadata
                    string? dActor = conn.ExecuteScalar<string?>("SELECT actor FROM config_audit_log WHERE action = 'delete_company_profile'");
                    string? dType = conn.ExecuteScalar<string?>("SELECT entity_type FROM config_audit_log WHERE action = 'delete_company_profile'");
                    long dEntityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE action = 'delete_company_profile'");
                    string? dEntityName = conn.ExecuteScalar<string?>("SELECT entity_name FROM config_audit_log WHERE action = 'delete_company_profile'");
                    string? dReason = conn.ExecuteScalar<string?>("SELECT reason FROM config_audit_log WHERE action = 'delete_company_profile'");

                    Assert.Equal("system", dActor);
                    Assert.Equal("company_profile", dType);
                    Assert.Equal((long)cpId, dEntityId);
                    Assert.Equal("Mu Updated", dEntityName);
                    Assert.Equal("Company profile deleted", dReason);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        // -- DatabaseProfile audit ----------------------------------------------

        [Fact]
        public void SaveDatabaseProfile_Create_WritesOneAuditRow()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_create_audit_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                repo.SaveDatabaseProfile(new DatabaseProfile
                {
                    Name = "PostgresDev",
                    Technology = "postgres",
                    Server = "localhost",
                    Port = 5432,
                    Username = "dev_user"
                });

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'create_database_profile'");
                Assert.Equal(1, count);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_Create_AuditUsesGeneratedIdInEntityIdAndAfterJson()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_create_id_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                repo.SaveDatabaseProfile(new DatabaseProfile
                {
                    Name = "PostgresDev",
                    Technology = "postgres",
                    Server = "localhost",
                    Port = 5432,
                    Username = "dev_user"
                });

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                long entityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE action = 'create_database_profile'");
                string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");
                long rowId = conn.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'PostgresDev'");
                Assert.Equal(rowId, entityId);

                using var doc = System.Text.Json.JsonDocument.Parse(afterJson);
                long idInJson = doc.RootElement.GetProperty("id").GetInt64();
                Assert.Equal(rowId, idInJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_Create_BeforeJsonIsEmptyObject()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_create_before_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                repo.SaveDatabaseProfile(new DatabaseProfile
                {
                    Name = "PostgresDev",
                    Technology = "postgres",
                    Server = "localhost",
                    Port = 5432,
                    Username = "dev_user"
                });

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'create_database_profile'");
                Assert.Equal("{}", beforeJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_Update_WritesOneAuditRow()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_update_audit_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var dp = new DatabaseProfile { Name = "MssqlDev", Technology = "mssql", Server = "127.0.0.1", Port = 1433, Username = "sa" };
                repo.SaveDatabaseProfile(dp);

                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'MssqlDev'");

                dp.Name = "MssqlDev Updated";
                repo.SaveDatabaseProfile(dp);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'update_database_profile'");
                Assert.Equal(1, count);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_Update_BeforeJsonReflectsPreMutationState()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_update_before_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var dp = new DatabaseProfile { Name = "OldName", Technology = "postgres", Server = "localhost", Port = 5432, Username = "old_user" };
                repo.SaveDatabaseProfile(dp);

                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'OldName'");

                dp.Name = "NewName";
                dp.Server = "10.0.0.1";
                dp.Port = 5433;
                repo.SaveDatabaseProfile(dp);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_database_profile'");
                Assert.Contains("\"OldName\"", beforeJson);
                Assert.Contains("\"localhost\"", beforeJson);
                Assert.Contains("5432", beforeJson);

                string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_database_profile'");
                Assert.Contains("\"NewName\"", afterJson);
                Assert.Contains("\"10.0.0.1\"", afterJson);
                Assert.Contains("5433", afterJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_MetadataAssertions()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_metadata_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var dp = new DatabaseProfile { Name = "MetaDb", Technology = "postgres", Server = "localhost", Port = 5432, Username = "dev" };

                // Create path
                repo.SaveDatabaseProfile(dp);

                int dpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'MetaDb'");

                // Update path
                dp.Id = dpId;
                dp.Name = "MetaDb Updated";
                repo.SaveDatabaseProfile(dp);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");

                // Verify Create Row
                var createRow = conn.QuerySingle("SELECT actor, action, entity_type, entity_id, entity_name, reason FROM config_audit_log WHERE action = 'create_database_profile'");
                Assert.Equal("system", createRow.actor);
                Assert.Equal("create_database_profile", createRow.action);
                Assert.Equal("database_profile", createRow.entity_type);
                Assert.Equal((long)dpId, createRow.entity_id);
                Assert.Equal("MetaDb", createRow.entity_name);
                Assert.Equal("Database profile created", createRow.reason);

                // Verify Update Row
                var updateRow = conn.QuerySingle("SELECT actor, action, entity_type, entity_id, entity_name, reason FROM config_audit_log WHERE action = 'update_database_profile'");
                Assert.Equal("system", updateRow.actor);
                Assert.Equal("update_database_profile", updateRow.action);
                Assert.Equal("database_profile", updateRow.entity_type);
                Assert.Equal((long)dpId, updateRow.entity_id);
                Assert.Equal("MetaDb Updated", updateRow.entity_name); // submitted name
                Assert.Equal("Database profile updated", updateRow.reason);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_Update_ThrowsInvalidOperationException_WhenProfileMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_miss_upd_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    repo.SaveDatabaseProfile(new DatabaseProfile { Id = 9999, Name = "Ghost", Server = "x" }));
                Assert.Contains("9999", ex.Message);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_Create_RollsBack_WhenAuditTableMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_rb_create_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    conn.Execute("DROP TABLE config_audit_log;");

                Assert.Throws<InvalidOperationException>(() =>
                    repo.SaveDatabaseProfile(new DatabaseProfile { Name = "ShouldNotExist", Server = "localhost" }));

                using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'ShouldNotExist'"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void SaveDatabaseProfile_Update_RollsBack_WhenAuditTableMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_rb_update_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var dp = new DatabaseProfile { Name = "OriginalName", Server = "localhost" };
                repo.SaveDatabaseProfile(dp);

                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'OriginalName'");

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    conn.Execute("DROP TABLE config_audit_log;");

                dp.Name = "ShouldNotUpdate";
                Assert.Throws<InvalidOperationException>(() => repo.SaveDatabaseProfile(dp));

                using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'OriginalName'"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteDatabaseProfile_WritesAuditRow_AndRemovesRow()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_delete_audit_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "ZetaDb", Server = "localhost" });

                int dpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'ZetaDb'");

                repo.DeleteDatabaseProfile(dpId);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");

                // Verify audit log
                var deleteRow = conn.QuerySingle("SELECT actor, action, entity_type, entity_id, entity_name, reason, before_json, after_json FROM config_audit_log WHERE action = 'delete_database_profile'");
                Assert.Equal("system", deleteRow.actor);
                Assert.Equal("delete_database_profile", deleteRow.action);
                Assert.Equal("database_profile", deleteRow.entity_type);
                Assert.Equal((long)dpId, deleteRow.entity_id);
                Assert.Equal("ZetaDb", deleteRow.entity_name);
                Assert.Equal("Database profile deleted", deleteRow.reason);
                Assert.Contains("\"ZetaDb\"", (string)deleteRow.before_json);
                Assert.Equal("{}", (string)deleteRow.after_json);

                // Verify row is gone
                Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE id = @Id", new { Id = dpId }));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteDatabaseProfile_AfterJsonIsEmptyObject()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_delete_after_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "EtaDb", Server = "localhost" });

                int dpId;
                using (var cId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dpId = (int)cId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'EtaDb'");

                repo.DeleteDatabaseProfile(dpId);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'delete_database_profile'");
                Assert.Equal("{}", afterJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteDatabaseProfile_RollsBack_WhenAuditTableMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_rb_delete_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "ThetaDb", Server = "localhost" });

                int dpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'ThetaDb'");

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    conn.Execute("DROP TABLE config_audit_log;");

                Assert.Throws<InvalidOperationException>(() => repo.DeleteDatabaseProfile(dpId));

                using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE id = @Id", new { Id = dpId }));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DeleteDatabaseProfile_ThrowsInvalidOperationException_WhenProfileMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_miss_del_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var ex = Assert.Throws<InvalidOperationException>(() => repo.DeleteDatabaseProfile(9999));
                Assert.Contains("9999", ex.Message);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DatabaseProfileAudit_SnapshotContainsExactlyAllowedFields()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_snap_fields_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var dp = new DatabaseProfile
                {
                    Name = "IotaDb",
                    Technology = "postgres",
                    Server = "localhost",
                    Port = 5432,
                    Username = "iota_user",
                    Password = "iota_password"
                };
                repo.SaveDatabaseProfile(dp);

                int dpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'IotaDb'");

                dp.Id = dpId;
                dp.Name = "IotaDb Updated";
                repo.SaveDatabaseProfile(dp);

                repo.DeleteDatabaseProfile(dpId);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");

                string createAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");
                string updateBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_database_profile'");
                string updateAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_database_profile'");
                string deleteBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'delete_database_profile'");

                var allowed = new System.Collections.Generic.HashSet<string>
                {
                    "id", "name", "technology", "server", "port", "username", "has_password"
                };

                foreach (var json in new[] { createAfter, updateBefore, updateAfter, deleteBefore })
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                    Assert.Equal(7, props.Count);
                    Assert.True(allowed.SetEquals(props), $"JSON properties mismatch: {json}");
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DatabaseProfileAudit_SnapshotExcludesRuntimeFields()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_snap_excl_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);
                var dp = new DatabaseProfile
                {
                    Name = "KappaDb",
                    Technology = "mssql",
                    Server = "localhost",
                    Port = 1433,
                    Username = "sa",
                    Password = "super_secret_password",
                    LastTestResult = "Success",
                    LastTestedAt = DateTime.UtcNow,
                    UsedByCount = 5
                };
                repo.SaveDatabaseProfile(dp);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");

                using var doc = System.Text.Json.JsonDocument.Parse(afterJson);
                var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                foreach (var excluded in new[] { "password", "last_test_result", "last_tested_at", "used_by_count" })
                {
                    Assert.DoesNotContain(props, p => p.Equals(excluded, StringComparison.OrdinalIgnoreCase));
                }

                // Verify that password contents are completely absent
                Assert.DoesNotContain("super_secret_password", afterJson);
                Assert.DoesNotContain("dpapi:", afterJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DatabaseProfileAudit_HasPasswordTransition()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dp_pwd_trans_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(path);
                var repo = new ConfigRepository(path);

                // 1. Create with password
                var dp = new DatabaseProfile
                {
                    Name = "SecureDb",
                    Technology = "postgres",
                    Server = "localhost",
                    Port = 5432,
                    Username = "postgres",
                    Password = "my_original_password"
                };
                repo.SaveDatabaseProfile(dp);

                int dpId;
                using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                    dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'SecureDb'");

                // 2. Update same profile with empty password
                dp.Id = dpId;
                dp.Password = "";
                repo.SaveDatabaseProfile(dp);

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");

                string createAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");
                string updateBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_database_profile'");
                string updateAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_database_profile'");

                // Assert has_password values
                using (var doc = System.Text.Json.JsonDocument.Parse(createAfter))
                    Assert.True(doc.RootElement.GetProperty("has_password").GetBoolean(), "Expected create after_json has_password == true");

                using (var doc = System.Text.Json.JsonDocument.Parse(updateBefore))
                    Assert.True(doc.RootElement.GetProperty("has_password").GetBoolean(), "Expected update before_json has_password == true");

                using (var doc = System.Text.Json.JsonDocument.Parse(updateAfter))
                    Assert.False(doc.RootElement.GetProperty("has_password").GetBoolean(), "Expected update after_json has_password == false");

                // Verify no plaintext or encrypted string contains secret keywords or signatures
                foreach (var json in new[] { createAfter, updateBefore, updateAfter })
                {
                    Assert.DoesNotContain("my_original_password", json);
                    Assert.DoesNotContain("dpapi:", json);

                    // Verify "password" property itself is excluded, but "has_password" is fine
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                    Assert.DoesNotContain(props, p => p.Equals("password", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void ImportSanitizedConfig_WithInvalidRecord_ThrowsArgumentException()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_import_val_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                var dbImports = new List<ResolvedDatabaseProfileImport>
                {
                    new ResolvedDatabaseProfileImport
                    {
                        SourceId = 1,
                        Action = ImportAction.Overwrite,
                        ExistingLocalId = null, // Invalid: overwrite needs local ID
                        Profile = new DatabaseProfile { Name = "InvalidDB" }
                    }
                };

                Assert.Throws<ArgumentException>(() => repo.ImportSanitizedConfig(
                    dbImports, new List<ResolvedCompanyProfileImport>(), "system", "reason", "{}", "{}"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (File.Exists(testDbPath)) File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void ImportSanitizedConfig_WithMissingPasswordForOverwriteNoPreservation_ThrowsArgumentException()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_import_pw_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                var dbImports = new List<ResolvedDatabaseProfileImport>
                {
                    new ResolvedDatabaseProfileImport
                    {
                        SourceId = 1,
                        Action = ImportAction.Overwrite,
                        ExistingLocalId = 1,
                        Profile = new DatabaseProfile { Name = "ExistingDB" },
                        Password = "", // Empty password is forbidden for overwrite when PreserveExistingPassword is false
                        PreserveExistingPassword = false
                    }
                };

                Assert.Throws<ArgumentException>(() => repo.ImportSanitizedConfig(
                    dbImports, new List<ResolvedCompanyProfileImport>(), "system", "reason", "{}", "{}"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (File.Exists(testDbPath)) File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void ImportSanitizedConfig_WithValidPayload_SavesAndRemapsAndAudits()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_import_run_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                var dbProfile = new DatabaseProfile { Name = "ImportedDB", Technology = "mssql", Server = "127.0.0.1", Port = 1433, Username = "sa" };
                var dbImports = new List<ResolvedDatabaseProfileImport>
                {
                    new ResolvedDatabaseProfileImport
                    {
                        SourceId = 99,
                        Action = ImportAction.Create,
                        Profile = dbProfile,
                        Password = "my_password",
                        PreserveExistingPassword = false
                    }
                };

                var compProfile = new CompanyProfile { Name = "Imported Company", TargetCatalog = "catalog_db" };
                var compImports = new List<ResolvedCompanyProfileImport>
                {
                    new ResolvedCompanyProfileImport
                    {
                        SourceId = 200,
                        Action = ImportAction.Create,
                        SourceDbProfileId = 99,
                        Profile = compProfile
                    }
                };

                repo.ImportSanitizedConfig(dbImports, compImports, "test-user", "Imported config", "{}", "{\"imported\":true}");

                var loadedDbs = repo.GetAllDatabaseProfiles();
                Assert.Single(loadedDbs);
                Assert.Equal("ImportedDB", loadedDbs[0].Name);
                Assert.Equal("my_password", loadedDbs[0].Password);

                var loadedCompanies = repo.GetAllCompanyProfiles();
                Assert.Single(loadedCompanies);
                Assert.Equal("Imported Company", loadedCompanies[0].Name);
                Assert.Equal(loadedDbs[0].Id, loadedCompanies[0].DbProfileId);
                Assert.False(loadedCompanies[0].Enabled);
                Assert.Equal("review_required", loadedCompanies[0].Status);

                int auditCount;
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    auditCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'import_sanitized_config'");
                }
                Assert.Equal(1, auditCount);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (File.Exists(testDbPath)) File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void ImportSanitizedConfig_WithNullArgs_ThrowsArgumentNullException()
        {
            var repo = new ConfigRepository(":memory:");
            Assert.Throws<ArgumentNullException>(() => repo.ImportSanitizedConfig(null!, new List<ResolvedCompanyProfileImport>(), "actor", "reason", "{}", "{}"));
            Assert.Throws<ArgumentNullException>(() => repo.ImportSanitizedConfig(new List<ResolvedDatabaseProfileImport>(), null!, "actor", "reason", "{}", "{}"));
        }

        [Fact]
        public void ImportSanitizedConfig_WhenExceptionOccurs_RollsBackTransaction()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_import_fail_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                var dbProfile = new DatabaseProfile { Name = "ShouldRollbackDB", Technology = "mssql", Server = "127.0.0.1", Port = 1433, Username = "sa" };
                var dbImports = new List<ResolvedDatabaseProfileImport>
                {
                    new ResolvedDatabaseProfileImport
                    {
                        SourceId = 99,
                        Action = ImportAction.Create,
                        Profile = dbProfile,
                        Password = "rollback_password",
                        PreserveExistingPassword = false
                    }
                };

                // Company overwrite with non-existent LocalId to trigger mid-transaction exception
                var compProfile = new CompanyProfile { Name = "Invalid Company", TargetCatalog = "catalog_db" };
                var compImports = new List<ResolvedCompanyProfileImport>
                {
                    new ResolvedCompanyProfileImport
                    {
                        SourceId = 200,
                        Action = ImportAction.Overwrite,
                        ExistingLocalId = 999999, // does not exist in DB!
                        SourceDbProfileId = 99, // references valid source DB profile in payload
                        Profile = compProfile
                    }
                };

                Assert.Throws<InvalidOperationException>(() => repo.ImportSanitizedConfig(dbImports, compImports, "test-user", "Will fail", "{}", "{}"));

                // Verify nothing was committed (db_profiles and company_profiles are empty)
                Assert.Empty(repo.GetAllDatabaseProfiles());
                Assert.Empty(repo.GetAllCompanyProfiles());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (File.Exists(testDbPath)) File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void ImportSanitizedConfig_WithPreservePassword_PreservesPassword()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_import_preserve_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                // Insert existing profile
                var dp = new DatabaseProfile
                {
                    Name = "ExistingDB",
                    Technology = "postgres",
                    Server = "localhost",
                    Port = 5432,
                    Username = "postgres",
                    Password = "original_secret"
                };
                repo.SaveDatabaseProfile(dp);

                var existingDbs = repo.GetAllDatabaseProfiles();
                Assert.Single(existingDbs);
                var existingId = existingDbs[0].Id;

                // Import that overwrites the existing profile but preserves the password
                var updatedProfile = new DatabaseProfile
                {
                    Name = "ExistingDB",
                    Technology = "postgres",
                    Server = "new-host",
                    Port = 5432,
                    Username = "postgres"
                };

                var dbImports = new List<ResolvedDatabaseProfileImport>
                {
                    new ResolvedDatabaseProfileImport
                    {
                        SourceId = 1,
                        ExistingLocalId = existingId,
                        Action = ImportAction.Overwrite,
                        Profile = updatedProfile,
                        PreserveExistingPassword = true
                    }
                };

                repo.ImportSanitizedConfig(dbImports, new List<ResolvedCompanyProfileImport>(), "test-user", "Overwrite config", "{}", "{}");

                var loadedDbs = repo.GetAllDatabaseProfiles();
                Assert.Single(loadedDbs);
                Assert.Equal("new-host", loadedDbs[0].Server);
                Assert.Equal("original_secret", loadedDbs[0].Password); // Password remains original_secret!
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (File.Exists(testDbPath)) File.Delete(testDbPath); } catch { }
            }
        }

        [Fact]
        public void RecordDiagnosticBackupExport_WritesAuditRow_WithCorrectMetadata()
        {
            string testDbPath = Path.Combine(Path.GetTempPath(), $"test_diag_audit_{System.Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(testDbPath);
                var repo = new ConfigRepository(testDbPath);

                long auditId = repo.RecordDiagnosticBackupExport(
                    actor: "support_engineer",
                    reason: "debug connection issues",
                    fileName: "tally_diagnostic_20260615_120000.zip",
                    fileSizeBytes: 204850L,
                    includeRawXml: true,
                    logFileCount: 3,
                    rawXmlFileCount: 5,
                    skippedFileCount: 1,
                    createdAt: new System.DateTime(2026, 6, 15, 12, 0, 0, System.DateTimeKind.Utc)
                );

                Assert.True(auditId > 0);

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                {
                    var row = conn.QuerySingleOrDefault<dynamic>(
                        "SELECT * FROM config_audit_log WHERE id = @Id", new { Id = auditId });

                    Assert.NotNull(row);
                    Assert.Equal("support_engineer", (string)row.actor);
                    Assert.Equal("export_diagnostic_backup", (string)row.action);
                    Assert.Equal("diagnostic_backup", (string)row.entity_type);
                    Assert.Equal(0L, (long)row.entity_id);
                    Assert.Equal("tally_diagnostic_20260615_120000.zip", (string)row.entity_name);
                    Assert.Equal("{}", (string)row.before_json);
                    Assert.Equal("debug connection issues", (string)row.reason);

                    string afterJson = (string)row.after_json;
                    using (var doc = System.Text.Json.JsonDocument.Parse(afterJson))
                    {
                        var root = doc.RootElement;
                        Assert.Equal("tally_diagnostic_20260615_120000.zip", root.GetProperty("file_name").GetString());
                        Assert.Equal(204850L, root.GetProperty("file_size_bytes").GetInt64());
                        Assert.True(root.GetProperty("include_raw_xml").GetBoolean());
                        Assert.Equal(3, root.GetProperty("log_file_count").GetInt32());
                        Assert.Equal(5, root.GetProperty("raw_xml_file_count").GetInt32());
                        Assert.Equal(1, root.GetProperty("skipped_file_count").GetInt32());
                    }
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
            }
        }
    }
}

