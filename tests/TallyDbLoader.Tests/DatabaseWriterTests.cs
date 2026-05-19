using Xunit;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using System;

namespace TallyDbLoader.Tests
{
    public class DatabaseWriterTests
    {
        [Fact]
        public void Test_DatabaseWriter_UnsupportedTech_Throws()
        {
            var profile = new DatabaseProfile
            {
                Name = "Invalid",
                Technology = "unsupported_tech",
                Server = "localhost",
                Port = 1234
            };

            Assert.ThrowsAny<Exception>(() => 
                DatabaseWriter.InitializeTargetTables(profile, "test_db")
            );
        }

        [Fact]
        public void Test_DatabaseWriter_InitializeIncrementalSyncSchema_Postgres_ThrowsOnInvalidConn()
        {
            var profile = new DatabaseProfile
            {
                Name = "InvalidPg",
                Technology = "postgres",
                Server = "invalid_server_xyz",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };

            Assert.ThrowsAny<Exception>(() => 
                DatabaseWriter.InitializeIncrementalSyncSchema(profile, "test_db")
            );
        }

        [Fact]
        public void Test_DatabaseWriter_ClearStagingTables_Postgres_ThrowsOnInvalidConn()
        {
            var profile = new DatabaseProfile
            {
                Name = "InvalidPg",
                Technology = "postgres",
                Server = "invalid_server_xyz",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };

            Assert.ThrowsAny<Exception>(() => 
                DatabaseWriter.ClearStagingTables(profile, "test_db")
            );
        }

        [Fact]
        public void Test_DatabaseWriter_GetConfigValue_Postgres_ThrowsOnInvalidConn()
        {
            var profile = new DatabaseProfile
            {
                Name = "InvalidPg",
                Technology = "postgres",
                Server = "invalid_server_xyz",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };

            Assert.ThrowsAny<Exception>(() => 
                DatabaseWriter.GetConfigValue(profile, "test_db", "last_alter_id")
            );
        }

        [Fact]
        public void Test_DatabaseWriter_SetConfigValue_Postgres_ThrowsOnInvalidConn()
        {
            var profile = new DatabaseProfile
            {
                Name = "InvalidPg",
                Technology = "postgres",
                Server = "invalid_server_xyz",
                Port = 5432,
                Username = "postgres",
                Password = "pwd"
            };

            Assert.ThrowsAny<Exception>(() => 
                DatabaseWriter.SetConfigValue(profile, "test_db", "last_alter_id", "123")
            );
        }
    }
}
