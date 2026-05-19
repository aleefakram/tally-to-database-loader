using System.IO;
using Xunit;
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
        public void Test_SyncJob_CRUD()
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

            var job = new SyncJob
            {
                CompanyName = "Yaghma Kababs",
                DbProfileId = savedProfile.Id,
                TargetCatalog = "yaghma_db",
                SyncIntervalMinutes = 15,
                DailyTimeLocal = null,
                Status = "Idle"
            };

            repo.SaveSyncJob(job);
            var jobs = repo.GetAllSyncJobs();

            Assert.Single(jobs);
            Assert.Equal("Yaghma Kababs", jobs[0].CompanyName);
            Assert.Equal(savedProfile.Id, jobs[0].DbProfileId);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(testDbPath)) System.IO.File.Delete(testDbPath);
        }
    }
}
