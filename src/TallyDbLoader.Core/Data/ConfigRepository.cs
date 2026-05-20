using System;
using System.Text;
using System.Security.Cryptography;
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

        private string EncryptPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            byte[] plainBytes = Encoding.UTF8.GetBytes(password);
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(encryptedBytes);
        }

        private string DecryptPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            if (!password.StartsWith("dpapi:")) return password;

            try
            {
                string base64 = password.Substring(6);
                byte[] encryptedBytes = Convert.FromBase64String(base64);
                byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex)
            {
                TallyDbLoader.Core.Logging.FileLogger.LogMessage($"[DPAPI Error] Decryption failed: {ex.Message}");
                return string.Empty;
            }
        }

        public void SaveDatabaseProfile(DatabaseProfile profile)
        {
            var encryptedPassword = EncryptPassword(profile.Password);
            using (var conn = new SqliteConnection(_connectionString))
            {
                var parameters = new
                {
                    profile.Id,
                    profile.Name,
                    profile.Technology,
                    profile.Server,
                    profile.Port,
                    profile.Username,
                    Password = encryptedPassword
                };

                if (profile.Id == 0)
                {
                    conn.Execute(@"
                        INSERT INTO database_profiles (name, technology, server, port, username, password)
                        VALUES (@Name, @Technology, @Server, @Port, @Username, @Password)", parameters);
                }
                else
                {
                    conn.Execute(@"
                        UPDATE database_profiles 
                        SET name = @Name, 
                            technology = @Technology, 
                            server = @Server, 
                            port = @Port, 
                            username = @Username, 
                            password = @Password 
                        WHERE id = @Id", parameters);
                }
            }
        }

        public DatabaseProfile? GetDatabaseProfileByName(string name)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                var profile = conn.QueryFirstOrDefault<DatabaseProfile>(
                    "SELECT * FROM database_profiles WHERE name = @Name", new { Name = name });
                if (profile != null)
                {
                    profile.Password = DecryptPassword(profile.Password);
                }
                return profile;
            }
        }

        public DatabaseProfile? GetDatabaseProfileById(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                var profile = conn.QueryFirstOrDefault<DatabaseProfile>(
                    "SELECT * FROM database_profiles WHERE id = @Id", new { Id = id });
                if (profile != null)
                {
                    profile.Password = DecryptPassword(profile.Password);
                }
                return profile;
            }
        }

        public List<DatabaseProfile> GetAllDatabaseProfiles()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                var profiles = conn.Query<DatabaseProfile>("SELECT * FROM database_profiles").AsList();
                foreach (var profile in profiles)
                {
                    profile.Password = DecryptPassword(profile.Password);
                }
                return profiles;
            }
        }

        public void SaveSyncJob(SyncJob job)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                if (job.Id == 0)
                {
                    conn.Execute(@"
                        INSERT INTO sync_jobs (company_name, db_profile_id, target_catalog, sync_interval_minutes, daily_time_local, last_run_time, status, sync_mode)
                        VALUES (@CompanyName, @DbProfileId, @TargetCatalog, @SyncIntervalMinutes, @DailyTimeLocal, @LastRunTime, @Status, @SyncMode)", job);
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
                            status = @Status,
                            sync_mode = @SyncMode
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
                           status AS Status,
                           sync_mode AS SyncMode
                    FROM sync_jobs").AsList();
            }
        }

        public TallySettings GetTallySettings()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                var settings = conn.QueryFirstOrDefault<TallySettings>(@"
                    SELECT id AS Id, 
                           server AS Server, 
                           port AS Port, 
                           tally_exe_path AS TallyExePath, 
                           tally_ini_path AS TallyIniPath, 
                           auto_start_tally AS AutoStartTally 
                    FROM tally_settings 
                    WHERE id = 1");
                return settings ?? new TallySettings();
            }
        }

        public void SaveTallySettings(TallySettings settings)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Execute(@"
                    INSERT OR REPLACE INTO tally_settings (id, server, port, tally_exe_path, tally_ini_path, auto_start_tally)
                    VALUES (1, @Server, @Port, @TallyExePath, @TallyIniPath, @AutoStartTally)", settings);
            }
        }

        public void DeleteDatabaseProfile(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Execute("DELETE FROM database_profiles WHERE id = @Id", new { Id = id });
            }
        }

        public void DeleteSyncJob(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Execute("DELETE FROM sync_jobs WHERE id = @Id", new { Id = id });
            }
        }
    }
}
