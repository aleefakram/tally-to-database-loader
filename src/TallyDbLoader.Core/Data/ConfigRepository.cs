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

        public DatabaseProfile? GetDatabaseProfileByName(string name)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                return conn.QueryFirstOrDefault<DatabaseProfile>(
                    "SELECT * FROM database_profiles WHERE name = @Name", new { Name = name });
            }
        }

        public DatabaseProfile? GetDatabaseProfileById(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                return conn.QueryFirstOrDefault<DatabaseProfile>(
                    "SELECT * FROM database_profiles WHERE id = @Id", new { Id = id });
            }
        }

        public void SaveSyncJob(SyncJob job)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                if (job.Id == 0)
                {
                    conn.Execute(@"
                        INSERT INTO sync_jobs (company_name, db_profile_id, target_catalog, sync_interval_minutes, daily_time_local, last_run_time, status)
                        VALUES (@CompanyName, @DbProfileId, @TargetCatalog, @SyncIntervalMinutes, @DailyTimeLocal, @LastRunTime, @Status)", job);
                }
                else
                {
                    conn.Execute(@"
                        UPDATE sync_jobs 
                        SET company_name = @CompanyName, 
                            db_profile_id = @DbProfileId, 
                            target_catalog = @TargetCatalog, 
                            sync_interval_minutes = @SyncIntervalMinutes, 
                            daily_time_local = @DailyTimeLocal, 
                            last_run_time = @LastRunTime, 
                            status = @Status
                        WHERE id = @Id", job);
                }
            }
        }

        public List<SyncJob> GetAllSyncJobs()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                return conn.Query<SyncJob>(@"
                    SELECT id AS Id, 
                           company_name AS CompanyName, 
                           db_profile_id AS DbProfileId, 
                           target_catalog AS TargetCatalog, 
                           sync_interval_minutes AS SyncIntervalMinutes, 
                           daily_time_local AS DailyTimeLocal, 
                           last_run_time AS LastRunTime, 
                           status AS Status 
                    FROM sync_jobs").AsList();
            }
        }
    }
}
