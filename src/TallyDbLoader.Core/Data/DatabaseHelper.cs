using System;
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
                conn.Execute("PRAGMA foreign_keys = ON;");

                // Get current database version
                int version = conn.ExecuteScalar<int>("PRAGMA user_version;");

                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        // Version 0 or fresh setup
                        conn.Execute(@"
                            CREATE TABLE IF NOT EXISTS database_profiles (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                name TEXT NOT NULL UNIQUE,
                                technology TEXT NOT NULL,
                                server TEXT NOT NULL,
                                port INTEGER NOT NULL,
                                username TEXT,
                                password TEXT,
                                last_test_result TEXT NOT NULL DEFAULT 'Untested',
                                last_tested_at TEXT
                            );", null, transaction);

                        conn.Execute(@"
                            CREATE TABLE IF NOT EXISTS tally_settings (
                                id INTEGER PRIMARY KEY CHECK (id = 1),
                                server TEXT NOT NULL DEFAULT 'localhost',
                                port INTEGER NOT NULL DEFAULT 9000,
                                tally_exe_path TEXT,
                                tally_ini_path TEXT,
                                auto_start_tally INTEGER NOT NULL DEFAULT 0
                            );", null, transaction);

                        conn.Execute(@"
                            INSERT OR IGNORE INTO tally_settings (id, server, port, auto_start_tally) 
                            VALUES (1, 'localhost', 9000, 0);", null, transaction);

                        if (version < 2)
                        {
                            // We need to perform the migration from v1 (SyncJobs table) to v2 (CompanyProfiles table)
                            bool syncJobsExists = conn.ExecuteScalar<int>(
                                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sync_jobs';", null, transaction) > 0;

                            if (syncJobsExists)
                            {
                                // 1. Safely add columns if they do not exist (snake_case to match ConfigRepository queries)
                                AddColumnIfNotExists(conn, "sync_jobs", "tally_guid", "TEXT NULL", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "consolidated", "INTEGER NOT NULL DEFAULT 0", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "books_from", "TEXT NULL", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "books_to", "TEXT NULL", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "schema", "TEXT NOT NULL DEFAULT 'public'", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "table_prefix", "TEXT NOT NULL DEFAULT 'tally_'", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "enabled", "INTEGER NOT NULL DEFAULT 1", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "notify_on_error", "INTEGER NOT NULL DEFAULT 1", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "pause_on_tally_close", "INTEGER NOT NULL DEFAULT 0", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "entity_flags", "INTEGER NOT NULL DEFAULT 15", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "last_run_at", "TEXT NULL", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "last_duration_ms", "INTEGER NULL", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "last_rows_written", "INTEGER NULL", transaction);
                                AddColumnIfNotExists(conn, "sync_jobs", "error_count_24h", "INTEGER NOT NULL DEFAULT 0", transaction);

                                // 2. De-duplicate prior to adding UNIQUE constraint
                                conn.Execute(@"
                                    DELETE FROM sync_jobs
                                    WHERE rowid NOT IN (
                                        SELECT MIN(rowid)
                                        FROM sync_jobs
                                        GROUP BY company_name
                                    );", null, transaction);

                                conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_jobs_company_name ON sync_jobs(company_name);", null, transaction);

                                // 3. Rename table & columns
                                conn.Execute("ALTER TABLE sync_jobs RENAME TO company_profiles;", null, transaction);
                                conn.Execute("ALTER TABLE company_profiles RENAME COLUMN company_name TO name;", null, transaction);
                                conn.Execute("ALTER TABLE company_profiles RENAME COLUMN sync_mode TO mode;", null, transaction);
                                conn.Execute("ALTER TABLE company_profiles RENAME COLUMN sync_interval_minutes TO interval_minutes;", null, transaction);
                            }
                            else
                            {
                                // Fresh v2 install or no existing sync_jobs
                                conn.Execute(@"
                                    CREATE TABLE IF NOT EXISTS company_profiles (
                                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                                        name TEXT NOT NULL UNIQUE,
                                        tally_guid TEXT,
                                        consolidated INTEGER NOT NULL DEFAULT 0,
                                        books_from TEXT,
                                        books_to TEXT,
                                        db_profile_id INTEGER NOT NULL,
                                        target_catalog TEXT NOT NULL,
                                        schema TEXT NOT NULL DEFAULT 'public',
                                        table_prefix TEXT NOT NULL DEFAULT 'tally_',
                                        mode TEXT NOT NULL DEFAULT 'full',
                                        interval_minutes INTEGER NOT NULL DEFAULT 15,
                                        enabled INTEGER NOT NULL DEFAULT 1,
                                        notify_on_error INTEGER NOT NULL DEFAULT 1,
                                        pause_on_tally_close INTEGER NOT NULL DEFAULT 0,
                                        entity_flags INTEGER NOT NULL DEFAULT 15,
                                        status TEXT NOT NULL DEFAULT 'idle',
                                        last_run_at TEXT,
                                        last_duration_ms INTEGER,
                                        last_rows_written INTEGER,
                                        error_count_24h INTEGER NOT NULL DEFAULT 0,
                                        FOREIGN KEY (db_profile_id) REFERENCES database_profiles(id) ON DELETE CASCADE
                                    );", null, transaction);
                            }

                            // Ensure database_profiles has the test columns
                            AddColumnIfNotExists(conn, "database_profiles", "last_test_result", "TEXT NOT NULL DEFAULT 'Untested'", transaction);
                            AddColumnIfNotExists(conn, "database_profiles", "last_tested_at", "TEXT", transaction);

                            // 4. Create SyncRuns table
                            conn.Execute(@"
                                CREATE TABLE IF NOT EXISTS sync_runs (
                                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    company_id INTEGER NOT NULL REFERENCES company_profiles(id) ON DELETE CASCADE,
                                    started_at TEXT NOT NULL,
                                    ended_at TEXT,
                                    mode TEXT NOT NULL,
                                    status TEXT NOT NULL,
                                    retries INTEGER NOT NULL DEFAULT 0,
                                    rows_in INTEGER NOT NULL DEFAULT 0,
                                    rows_written INTEGER NOT NULL DEFAULT 0,
                                    by_entity_json TEXT NOT NULL DEFAULT '{}',
                                    result_summary TEXT NULL,
                                    log_excerpt TEXT NULL
                                );", null, transaction);

                            conn.Execute("CREATE INDEX IF NOT EXISTS ix_sync_runs_company_id_started_at ON sync_runs(company_id, started_at DESC);", null, transaction);

                            // Set version to 2
                            conn.Execute("PRAGMA user_version = 2;", null, transaction);
                        }

                        if (version < 3)
                        {
                            conn.Execute("UPDATE company_profiles SET status = 'idle' WHERE status IS NULL OR TRIM(status) = '';", null, transaction);

                            // Migrate sync_runs to make ended_at nullable in existing user databases (v2 -> v3)
                            bool syncRunsExists = conn.ExecuteScalar<int>(
                                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sync_runs';", null, transaction) > 0;
                            if (syncRunsExists)
                            {
                                // Check if ended_at is not null
                                var columns = conn.Query("PRAGMA table_info(sync_runs);", null, transaction);
                                bool endedAtNotNull = false;
                                foreach (var col in columns)
                                {
                                    var colName = ((dynamic)col).name as string;
                                    var notnull = (long)((dynamic)col).notnull;
                                    if (string.Equals(colName, "ended_at", System.StringComparison.OrdinalIgnoreCase) && notnull == 1)
                                    {
                                        endedAtNotNull = true;
                                        break;
                                    }
                                }

                                if (endedAtNotNull)
                                {
                                    conn.Execute(@"
                                        CREATE TABLE sync_runs_new (
                                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                                            company_id INTEGER NOT NULL REFERENCES company_profiles(id) ON DELETE CASCADE,
                                            started_at TEXT NOT NULL,
                                            ended_at TEXT,
                                            mode TEXT NOT NULL,
                                            status TEXT NOT NULL,
                                            retries INTEGER NOT NULL DEFAULT 0,
                                            rows_in INTEGER NOT NULL DEFAULT 0,
                                            rows_written INTEGER NOT NULL DEFAULT 0,
                                            by_entity_json TEXT NOT NULL DEFAULT '{}',
                                            result_summary TEXT NULL,
                                            log_excerpt TEXT NULL
                                        );", null, transaction);

                                    conn.Execute(@"
                                        INSERT INTO sync_runs_new (id, company_id, started_at, ended_at, mode, status, retries, rows_in, rows_written, by_entity_json, result_summary, log_excerpt)
                                        SELECT id, company_id, started_at, ended_at, mode, status, retries, rows_in, rows_written, by_entity_json, result_summary, log_excerpt FROM sync_runs;", null, transaction);

                                    conn.Execute("DROP TABLE sync_runs;", null, transaction);
                                    conn.Execute("ALTER TABLE sync_runs_new RENAME TO sync_runs;", null, transaction);
                                    conn.Execute("CREATE INDEX IF NOT EXISTS ix_sync_runs_company_id_started_at ON sync_runs(company_id, started_at DESC);", null, transaction);
                                }
                            }

                            conn.Execute("PRAGMA user_version = 3;", null, transaction);
                        }

                        if (version < 4)
                        {
                            conn.Execute(@"
                                CREATE TABLE IF NOT EXISTS config_audit_log (
                                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    created_at TEXT NOT NULL,
                                    actor TEXT NOT NULL,
                                    action TEXT NOT NULL,
                                    entity_type TEXT NOT NULL,
                                    entity_id INTEGER NOT NULL,
                                    entity_name TEXT NULL,
                                    before_json TEXT NOT NULL,
                                    after_json TEXT NOT NULL,
                                    reason TEXT NOT NULL
                                );", null, transaction);

                            conn.Execute("CREATE INDEX IF NOT EXISTS ix_config_audit_log_created_at ON config_audit_log(created_at DESC);", null, transaction);
                            conn.Execute("CREATE INDEX IF NOT EXISTS ix_config_audit_log_entity ON config_audit_log(entity_type, entity_id, created_at DESC);", null, transaction);

                            conn.Execute("PRAGMA user_version = 4;", null, transaction);
                        }

                        transaction.Commit();
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        private static void AddColumnIfNotExists(SqliteConnection conn, string tableName, string columnName, string columnType, SqliteTransaction transaction)
        {
            // SQL Injection protection validation
            if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                throw new ArgumentException("Invalid table name.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(columnName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                throw new ArgumentException("Invalid column name.");

            var columns = conn.Query($"PRAGMA table_info(\"{tableName}\");", null, transaction);
            foreach (var col in columns)
            {
                var name = ((dynamic)col).name as string;
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Column already exists
                }
            }
            conn.Execute($"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType};", null, transaction);
        }
    }
}
