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
    }
}
