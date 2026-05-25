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
    }
}
