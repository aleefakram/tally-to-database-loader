using System;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Dapper;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
    public class ConfigRepository : IConfigRepository
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
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        var parameters = new
                        {
                            profile.Id,
                            profile.Name,
                            profile.Technology,
                            profile.Server,
                            profile.Port,
                            profile.Username,
                            Password = encryptedPassword,
                            profile.LastTestResult,
                            LastTestedAt = profile.LastTestedAt?.ToString("o")
                        };

                        if (profile.Id == 0)
                        {
                            conn.Execute(@"
                                INSERT INTO database_profiles (name, technology, server, port, username, password, last_test_result, last_tested_at)
                                VALUES (@Name, @Technology, @Server, @Port, @Username, @Password, @LastTestResult, @LastTestedAt)", parameters, transaction);
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
                                    password = @Password,
                                    last_test_result = @LastTestResult,
                                    last_tested_at = @LastTestedAt
                                WHERE id = @Id", parameters, transaction);
                        }
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public DatabaseProfile? GetDatabaseProfileByName(string name)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                var profile = conn.QueryFirstOrDefault<DatabaseProfile>(
                    "SELECT id, name, technology, server, port, username, password, last_test_result AS LastTestResult, last_tested_at AS LastTestedAt FROM database_profiles WHERE name = @Name", new { Name = name });
                if (profile != null)
                {
                    profile.Password = DecryptPassword(profile.Password);
                    profile.UsedByCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE db_profile_id = @Id", new { Id = profile.Id });
                }
                return profile;
            }
        }

        public DatabaseProfile? GetDatabaseProfileById(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                var profile = conn.QueryFirstOrDefault<DatabaseProfile>(
                    "SELECT id, name, technology, server, port, username, password, last_test_result AS LastTestResult, last_tested_at AS LastTestedAt FROM database_profiles WHERE id = @Id", new { Id = id });
                if (profile != null)
                {
                    profile.Password = DecryptPassword(profile.Password);
                    profile.UsedByCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE db_profile_id = @Id", new { Id = profile.Id });
                }
                return profile;
            }
        }

        public List<DatabaseProfile> GetAllDatabaseProfiles()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                var profiles = conn.Query<DatabaseProfile>("SELECT id, name, technology, server, port, username, password, last_test_result AS LastTestResult, last_tested_at AS LastTestedAt FROM database_profiles").AsList();
                
                var countMap = conn.Query<(int DbProfileId, int Cnt)>(@"
                    SELECT db_profile_id AS DbProfileId, COUNT(*) AS Cnt 
                    FROM company_profiles 
                    GROUP BY db_profile_id").ToDictionary(x => x.DbProfileId, x => x.Cnt);

                foreach (var profile in profiles)
                {
                    profile.Password = DecryptPassword(profile.Password);
                    profile.UsedByCount = countMap.TryGetValue(profile.Id, out int count) ? count : 0;
                }
                return profiles;
            }
        }

        public void SaveCompanyProfile(CompanyProfile company)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        var status = string.IsNullOrWhiteSpace(company.Status) ? "idle" : company.Status.Trim().ToLowerInvariant();
                        var parameters = new
                        {
                            company.Id,
                            company.Name,
                            company.TallyGuid,
                            company.Consolidated,
                            BooksFrom = company.BooksFrom?.ToString("o"),
                            BooksTo = company.BooksTo?.ToString("o"),
                            company.DbProfileId,
                            company.TargetCatalog,
                            company.Schema,
                            company.TablePrefix,
                            company.Mode,
                            company.IntervalMinutes,
                            company.Enabled,
                            company.NotifyOnError,
                            company.PauseOnTallyClose,
                            company.EntityFlags,
                            Status = status,
                            LastRunAt = company.LastRunAt?.ToString("o"),
                            company.LastDurationMs,
                            company.LastRowsWritten,
                            company.ErrorCount24h
                        };

                        if (company.Id == 0)
                        {
                            conn.Execute(@"
                                INSERT INTO company_profiles (name, tally_guid, consolidated, books_from, books_to, db_profile_id, target_catalog, schema, table_prefix, mode, interval_minutes, enabled, notify_on_error, pause_on_tally_close, entity_flags, status, last_run_at, last_duration_ms, last_rows_written, error_count_24h)
                                VALUES (@Name, @TallyGuid, @Consolidated, @BooksFrom, @BooksTo, @DbProfileId, @TargetCatalog, @Schema, @TablePrefix, @Mode, @IntervalMinutes, @Enabled, @NotifyOnError, @PauseOnTallyClose, @EntityFlags, @Status, @LastRunAt, @LastDurationMs, @LastRowsWritten, @ErrorCount24h)", parameters, transaction);
                        }
                        else
                        {
                            conn.Execute(@"
                                UPDATE company_profiles 
                                SET name = @Name,
                                    tally_guid = @TallyGuid,
                                    consolidated = @Consolidated,
                                    books_from = @BooksFrom,
                                    books_to = @BooksTo,
                                    db_profile_id = @DbProfileId,
                                    target_catalog = @TargetCatalog,
                                    schema = @Schema,
                                    table_prefix = @TablePrefix,
                                    mode = @Mode,
                                    interval_minutes = @IntervalMinutes,
                                    enabled = @Enabled,
                                    notify_on_error = @NotifyOnError,
                                    pause_on_tally_close = @PauseOnTallyClose,
                                    entity_flags = @EntityFlags,
                                    status = @Status,
                                    last_run_at = @LastRunAt,
                                    last_duration_ms = @LastDurationMs,
                                    last_rows_written = @LastRowsWritten,
                                    error_count_24h = @ErrorCount24h
                                WHERE id = @Id", parameters, transaction);
                        }
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public List<CompanyProfile> GetAllCompanyProfiles()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                var sql = @"
                    SELECT c.id AS Id, 
                           c.name AS Name, 
                           c.tally_guid AS TallyGuid,
                           c.consolidated AS Consolidated,
                           c.books_from AS BooksFrom,
                           c.books_to AS BooksTo,
                           c.db_profile_id AS DbProfileId, 
                           c.target_catalog AS TargetCatalog,
                           c.schema AS Schema,
                           c.table_prefix AS TablePrefix,
                           c.mode AS Mode, 
                           c.interval_minutes AS IntervalMinutes, 
                           c.enabled AS Enabled,
                           c.notify_on_error AS NotifyOnError,
                           c.pause_on_tally_close AS PauseOnTallyClose,
                           c.entity_flags AS EntityFlags,
                           c.status AS Status,
                           c.last_run_at AS LastRunAt,
                           c.last_duration_ms AS LastDurationMs,
                           c.last_rows_written AS LastRowsWritten,
                           c.error_count_24h AS ErrorCount24h,
                           d.id AS DbId,
                           d.name AS Name,
                           d.technology AS Technology,
                           d.server AS Server,
                           d.port AS Port,
                           d.username AS Username,
                           d.password AS Password,
                           d.last_test_result AS LastTestResult,
                           d.last_tested_at AS LastTestedAt
                    FROM company_profiles c
                    LEFT JOIN database_profiles d ON c.db_profile_id = d.id";

                var companies = conn.Query<CompanyProfile, DatabaseProfile, CompanyProfile>(
                    sql,
                    (c, d) =>
                    {
                        if (d != null)
                        {
                            d.Password = DecryptPassword(d.Password);
                        }
                        c.Db = d;
                        return c;
                    },
                    splitOn: "DbId"
                ).AsList();

                return companies;
            }
        }

        public void DeleteCompanyProfile(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        conn.Execute("DELETE FROM company_profiles WHERE id = @Id", new { Id = id }, transaction);
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public TallySettings GetTallySettings()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
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
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        conn.Execute(@"
                            INSERT OR REPLACE INTO tally_settings (id, server, port, tally_exe_path, tally_ini_path, auto_start_tally)
                            VALUES (1, @Server, @Port, @TallyExePath, @TallyIniPath, @AutoStartTally)", settings, transaction);
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public void DeleteDatabaseProfile(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        conn.Execute("DELETE FROM database_profiles WHERE id = @Id", new { Id = id }, transaction);
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public long AddSyncRun(SyncRun run)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        string? endedAtStr = (run.Status == "running" || run.EndedAt == default(System.DateTime)) 
                            ? null 
                            : run.EndedAt.ToString("o");

                        conn.Execute(@"
                            INSERT INTO sync_runs (company_id, started_at, ended_at, mode, status, retries, rows_in, rows_written, by_entity_json, result_summary, log_excerpt)
                            VALUES (@CompanyId, @StartedAt, @EndedAt, @Mode, @Status, @Retries, @RowsIn, @RowsWritten, @ByEntityJson, @ResultSummary, @LogExcerpt)",
                            new
                            {
                                run.CompanyId,
                                StartedAt = run.StartedAt.ToString("o"),
                                EndedAt = endedAtStr,
                                run.Mode,
                                run.Status,
                                run.Retries,
                                run.RowsIn,
                                run.RowsWritten,
                                run.ByEntityJson,
                                run.ResultSummary,
                                run.LogExcerpt
                            }, transaction);
                        
                        long id = conn.QuerySingle<long>("SELECT last_insert_rowid();", null, transaction);
                        transaction.Commit();
                        run.Id = id;
                        return id;
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public bool TryStartCompanyProfile(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        int affected = conn.Execute(@"
                            UPDATE company_profiles
                            SET status = 'running'
                            WHERE id = @Id
                              AND enabled = 1
                              AND status IN ('idle', 'completed', 'failed');", new { Id = id }, transaction);
                        transaction.Commit();
                        return affected > 0;
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public void MarkCompanyProfileUnknown(int id, string reason, System.DateTime now)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        conn.Execute(@"
                            UPDATE company_profiles
                            SET status = 'unknown',
                                last_run_at = @Now
                            WHERE id = @Id;", new { Id = id, Now = now.ToString("o") }, transaction);
                        transaction.Commit();

                        TallyDbLoader.Core.Logging.FileLogger.LogMessage($"[Safety] Company profile {id} marked unknown. Reason: {reason}");
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public void CompleteCompanyProfileRun(
            int id,
            string finalStatus,
            System.DateTime endedAt,
            int durationMs,
            long rowsWritten,
            bool incrementErrorCount)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        conn.Execute(@"
                            UPDATE company_profiles
                            SET status = @FinalStatus,
                                last_run_at = @EndedAt,
                                last_duration_ms = @DurationMs,
                                last_rows_written = @RowsWritten,
                                error_count_24h = CASE WHEN @IncrementErrorCount = 1 THEN error_count_24h + 1 ELSE 0 END
                            WHERE id = @Id;",
                            new
                            {
                                Id = id,
                                FinalStatus = finalStatus?.Trim().ToLowerInvariant() ?? "idle",
                                EndedAt = endedAt.ToString("o"),
                                DurationMs = durationMs,
                                RowsWritten = rowsWritten,
                                IncrementErrorCount = incrementErrorCount ? 1 : 0
                            }, transaction);
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public void UpdateSyncRun(SyncRun run)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        string? endedAtStr = (run.EndedAt == default(System.DateTime)) ? null : run.EndedAt.ToString("o");

                        conn.Execute(@"
                            UPDATE sync_runs
                            SET ended_at = @EndedAt,
                                status = @Status,
                                retries = @Retries,
                                rows_in = @RowsIn,
                                rows_written = @RowsWritten,
                                by_entity_json = @ByEntityJson,
                                result_summary = @ResultSummary,
                                log_excerpt = @LogExcerpt
                            WHERE id = @Id;",
                            new
                            {
                                Id = run.Id,
                                EndedAt = endedAtStr,
                                run.Status,
                                run.Retries,
                                run.RowsIn,
                                run.RowsWritten,
                                run.ByEntityJson,
                                run.ResultSummary,
                                run.LogExcerpt
                            }, transaction);
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public void ReconcileStaleRuns(System.DateTime now)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        conn.Execute(@"
                            UPDATE sync_runs
                            SET status = 'unknown',
                                ended_at = @Now,
                                result_summary = 'Interrupted by application restart before completion',
                                log_excerpt = 'Startup reconciliation found stale running state.'
                            WHERE status = 'running';", new { Now = now.ToString("o") }, transaction);

                        conn.Execute(@"
                            UPDATE company_profiles
                            SET status = 'unknown'
                            WHERE status = 'running';", null, transaction);

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        public List<SyncRun> GetRecentSyncRuns(int limit = 50)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                return conn.Query<SyncRun>(@"
                    SELECT r.id AS Id,
                           r.company_id AS CompanyId,
                           c.name AS CompanyName,
                           r.started_at AS StartedAt,
                           r.ended_at AS EndedAt,
                           r.mode AS Mode,
                           r.status AS Status,
                           r.retries AS Retries,
                           r.rows_in AS RowsIn,
                           r.rows_written AS RowsWritten,
                           r.by_entity_json AS ByEntityJson,
                           r.result_summary AS ResultSummary,
                           r.log_excerpt AS LogExcerpt
                    FROM sync_runs r
                    JOIN company_profiles c ON r.company_id = c.id
                    ORDER BY r.started_at DESC
                    LIMIT @Limit", new { Limit = limit }).AsList();
            }
        }

        public List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute("PRAGMA foreign_keys = ON;");
                return conn.Query<SyncRun>(@"
                    SELECT r.id AS Id,
                           r.company_id AS CompanyId,
                           c.name AS CompanyName,
                           r.started_at AS StartedAt,
                           r.ended_at AS EndedAt,
                           r.mode AS Mode,
                           r.status AS Status,
                           r.retries AS Retries,
                           r.rows_in AS RowsIn,
                           r.rows_written AS RowsWritten,
                           r.by_entity_json AS ByEntityJson,
                           r.result_summary AS ResultSummary,
                           r.log_excerpt AS LogExcerpt
                    FROM sync_runs r
                    JOIN company_profiles c ON r.company_id = c.id
                    WHERE r.company_id = @CompanyId
                    ORDER BY r.started_at DESC
                    LIMIT @Limit", new { CompanyId = companyId, Limit = limit }).AsList();
            }
        }
    }
}
