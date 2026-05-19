using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Dapper;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
    public class ConfigRepository
    {
        private readonly string _connectionString;

        public ConfigRepository(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public void SaveDatabaseProfile(DatabaseProfile profile)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Execute(@"
                    INSERT OR REPLACE INTO database_profiles (name, technology, server, port, username, password)
                    VALUES (@Name, @Technology, @Server, @Port, @Username, @Password)", profile);
            }
        }

        public DatabaseProfile GetDatabaseProfileByName(string name)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                return conn.QueryFirstOrDefault<DatabaseProfile>(
                    "SELECT * FROM database_profiles WHERE name = @Name", new { Name = name });
            }
        }
    }
}
