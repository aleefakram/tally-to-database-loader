using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;

namespace TallyDbLoader.Core.Data
{
    public static class DatabaseHelper
    {
        public static void InitializeDatabase(string dbPath)
        {
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS database_profiles (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL UNIQUE,
                        technology TEXT NOT NULL,
                        server TEXT NOT NULL,
                        port INTEGER NOT NULL,
                        username TEXT,
                        password TEXT
                    );
                    
                    CREATE TABLE IF NOT EXISTS sync_jobs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        company_name TEXT NOT NULL,
                        db_profile_id INTEGER NOT NULL,
                        target_catalog TEXT NOT NULL,
                        sync_interval_minutes INTEGER,
                        daily_time_local TEXT,
                        last_run_time TEXT,
                        status TEXT NOT NULL DEFAULT 'Idle',
                        FOREIGN KEY (db_profile_id) REFERENCES database_profiles(id)
                    );
                    
                    CREATE TABLE IF NOT EXISTS tally_settings (
                        id INTEGER PRIMARY KEY CHECK (id = 1),
                        server TEXT NOT NULL DEFAULT 'localhost',
                        port INTEGER NOT NULL DEFAULT 9000,
                        tally_exe_path TEXT,
                        tally_ini_path TEXT
                    );
                    
                    INSERT OR IGNORE INTO tally_settings (id, server, port) VALUES (1, 'localhost', 9000);
                ");
            }
        }
    }
}
