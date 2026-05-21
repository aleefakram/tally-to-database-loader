# V2 UI/UX Design Migration Implementation Plan

> **For executing agents:** This plan is 100% self-contained. Implement the files exactly as specified. Do not use placeholders or reference previous plans.

---

## Phase 1: Models & SQLite Database Migration

### Task 1.1: Update C# Models

**Files:**
- Modify: `src/TallyDbLoader.Core/Models/Models.cs`

- [ ] **Step 1: Replace model definitions**
  Replace `src/TallyDbLoader.Core/Models/Models.cs` with this exact implementation.

```csharp
using System;
using System.Collections.Generic;

namespace TallyDbLoader.Core.Models
{
    [Flags]
    public enum EntityFlags
    {
        None = 0,
        Vouchers = 1 << 0,      // 1
        Ledgers = 1 << 1,       // 2
        StockItems = 1 << 2,    // 4
        Groups = 1 << 3,        // 8
        CostCentres = 1 << 4,   // 16
        Currencies = 1 << 5,    // 32
        All = Vouchers | Ledgers | StockItems | Groups | CostCentres | Currencies
    }

    public class DatabaseProfile
    {
        public int Id { get; set; }
        public int DbId { get => Id; set => Id = value; } // For Dapper split mapping alias
        public string Name { get; set; } = string.Empty;
        public string Technology { get; set; } = "postgres"; // "postgres" | "mssql"
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 5432;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty; // DPAPI encrypted format "dpapi:..." in database
        public string LastTestResult { get; set; } = "Untested";
        public DateTime? LastTestedAt { get; set; }
        public int UsedByCount { get; set; }
    }

    public class TallySettings
    {
        public int Id { get; set; } = 1;
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 9000;
        public string? TallyExePath { get; set; }
        public string? TallyIniPath { get; set; }
        public int AutoStartTally { get; set; }
    }

    public class CompanyProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? TallyGuid { get; set; }
        public int Consolidated { get; set; } = 0;
        public DateTime? BooksFrom { get; set; }
        public DateTime? BooksTo { get; set; }

        public int DbProfileId { get; set; }
        public DatabaseProfile? Db { get; set; } // Populated on load

        public string TargetCatalog { get; set; } = string.Empty;
        public string Schema { get; set; } = "public";
        public string TablePrefix { get; set; } = "tally_";

        public string Mode { get; set; } = "full"; // "full" | "incremental"
        public int IntervalMinutes { get; set; } = 15;
        public int Enabled { get; set; } = 1;
        public int NotifyOnError { get; set; } = 1;
        public int PauseOnTallyClose { get; set; } = 0;

        // Default: 15 (Vouchers=1 | Ledgers=2 | StockItems=4 | Groups=8)
        public int EntityFlags { get; set; } = 15; 

        public string Status { get; set; } = "idle"; // "ok" | "warn" | "err" | "idle"
        public DateTime? LastRunAt { get; set; }
        public int? LastDurationMs { get; set; }
        public long? LastRowsWritten { get; set; }
        public int ErrorCount24h { get; set; }
    }

    public class SyncRun
    {
        public long Id { get; set; }
        public int CompanyId { get; set; }
        public string CompanyName { get; set; } = string.Empty; // Populated via join query
        public DateTime StartedAt { get; set; }
        public DateTime EndedAt { get; set; }
        public TimeSpan Duration => EndedAt - StartedAt;
        public string Mode { get; set; } = "full"; // "full" | "incremental"
        public string Status { get; set; } = "ok"; // "ok" | "warn" | "err"
        public int Retries { get; set; } = 0;
        public long RowsIn { get; set; } = 0;
        public long RowsWritten { get; set; } = 0;
        public string ByEntityJson { get; set; } = "{}"; // JSON stats mapping: {"Vouchers": 12, "Ledgers": 4}
        public string? ResultSummary { get; set; }
        public string? LogExcerpt { get; set; }
    }

    public class Ledger
    {
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Parent { get; set; } = string.Empty;
        public decimal OpeningBalance { get; set; }
        public decimal ClosingBalance { get; set; }
    }

    public class Voucher
    {
        public string Guid { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string VoucherNumber { get; set; } = string.Empty;
        public string VoucherType { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    // NOTE: TallyCompanyInfo already exists at Core.Tally.TallyCompanyInfo.
    // Extend the existing class (in src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs) with these fields:
    //   public string? Guid { get; set; }
    //   public DateTime? BooksFrom { get; set; }
    //   public DateTime? BooksTo { get; set; }
    // Keep existing IsGroup as bool, and map to CompanyProfile.Consolidated (as int) only when saving.
}
```

---

### Task 1.1b: Extend Existing TallyCompanyInfo

**Files:**
- Modify: `src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs`

- [ ] **Step 2: Add missing fields to existing TallyCompanyInfo**
  Replace `src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs` with this implementation. Do NOT create a duplicate class in Models.cs.

```csharp
using System;

namespace TallyDbLoader.Core.Tally
{
    public class TallyCompanyInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? Guid { get; set; }
        public bool IsGroup { get; set; }
        public DateTime? BooksFrom { get; set; }
        public DateTime? BooksTo { get; set; }

        public override string ToString()
        {
            return IsGroup ? $"{Name} (Consolidated)" : Name;
        }
    }
}
```

---

### Task 1.2: Implement SQLite Database Migration

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`

- [ ] **Step 1: Rewrite DatabaseHelper to run migrations atomically**
  Replace `src/TallyDbLoader.Core/Data/DatabaseHelper.cs` with the following implementation. It manages database schemas idempotently and safely renames and alters the tables inside a single transaction.

```csharp
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
                                    ended_at TEXT NOT NULL,
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
            var columns = conn.Query($"PRAGMA table_info({tableName});", null, transaction);
            foreach (var col in columns)
            {
                var name = ((dynamic)col).name as string;
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Column already exists
                }
            }
            conn.Execute($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType};", null, transaction);
        }
    }
}
```

---

## Phase 2: Config Repository & Sync Engine

### Task 2.1: Add NuGet Package & Implement Config Repository

**Files:**
- Modify: `src/TallyDbLoader.Core/TallyDbLoader.Core.csproj`
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`

- [ ] **Step 1: Reference Cryptography and Verify ADO.NET Drivers**
  Ensure the NuGet reference is added to `src/TallyDbLoader.Core/TallyDbLoader.Core.csproj` inside an `<ItemGroup>` block:
  ```xml
  <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
  ```
  *(Note: ADO.NET provider packages `Npgsql` and `Microsoft.Data.SqlClient` are already present in the CSPROJ file to support PostgreSQL and MS SQL Server connections).*


- [ ] **Step 2: Rewrite ConfigRepository implementation**
  Replace `src/TallyDbLoader.Core/Data/ConfigRepository.cs` with the following code. It optimizes queries by fetching companies and their associated database profile via a single JOIN query, preventing N+1 connection overheads.

```csharp
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
                conn.Open();
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
                var profiles = conn.Query<DatabaseProfile>("SELECT id, name, technology, server, port, username, password, last_test_result AS LastTestResult, last_tested_at AS LastTestedAt FROM database_profiles").AsList();
                foreach (var profile in profiles)
                {
                    profile.Password = DecryptPassword(profile.Password);
                    profile.UsedByCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE db_profile_id = @Id", new { Id = profile.Id });
                }
                return profiles;
            }
        }

        public void SaveCompanyProfile(CompanyProfile company)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
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
                            company.Status,
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

        public void AddSyncRun(SyncRun run)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        conn.Execute(@"
                            INSERT INTO sync_runs (company_id, started_at, ended_at, mode, status, retries, rows_in, rows_written, by_entity_json, result_summary, log_excerpt)
                            VALUES (@CompanyId, @StartedAt, @EndedAt, @Mode, @Status, @Retries, @RowsIn, @RowsWritten, @ByEntityJson, @ResultSummary, @LogExcerpt)",
                            new
                            {
                                run.CompanyId,
                                StartedAt = run.StartedAt.ToString("o"),
                                EndedAt = run.EndedAt.ToString("o"),
                                run.Mode,
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

        public List<SyncRun> GetRecentSyncRuns(int limit = 50)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
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
```

---

### Task 2.2: Implement Sync Orchestrator Scheduler

**Files:**
- Create/Overwrite: `src/TallyDbLoader.Core/Sync/SyncOrchestrator.cs`

- [ ] **Step 1: Write SyncOrchestrator code**
  Implement the timing checker that evaluates whether a CompanyProfile has surpassed its target interval.

```csharp
using System;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Sync
{
    public static class SyncOrchestrator
    {
        public static bool ShouldRun(CompanyProfile profile, DateTime now)
        {
            if (profile.Enabled == 0) return false;
            if (!profile.LastRunAt.HasValue) return true;

            var timeElapsed = now - profile.LastRunAt.Value;
            return timeElapsed.TotalMinutes >= profile.IntervalMinutes;
        }
    }
}
```

---

### Task 2.3: Update Background Sync Engine

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`

- [ ] **Step 1: Rewrite BackgroundSyncWorker**
  Overwrite `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` with the full implementation support for Pause, Resume, manual single-company schedules, and bitmask `EntityFlags` extraction.

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Core.DatabaseLoaders;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Sync
{
    public class BackgroundSyncWorker : IDisposable
    {
        private readonly ConfigRepository _repo;
        private readonly string _tallyServer;
        private readonly int _tallyPort;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private TallyClient? _tallyClient;

        private readonly object _syncLock = new object();
        private bool _forceSyncOnce = false;
        private int? _manualSyncCompanyId = null;
        private CancellationTokenSource _wakeUpCts = new CancellationTokenSource();
        private bool _disposed = false;
        private bool _isPaused = false;

        public event Action<string>? OnLogMessage;
        public event Action? OnSyncCompleted;

        public bool IsRunning => _cts != null;
        public bool IsPaused => _isPaused;

        public BackgroundSyncWorker(ConfigRepository repo, string tallyServer, int tallyPort)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _tallyServer = tallyServer;
            _tallyPort = tallyPort <= 0 ? 9000 : tallyPort;
        }

        public void SetTallyClientForTest(TallyClient client)
        {
            _tallyClient = client;
        }

        private void Log(string message)
        {
            OnLogMessage?.Invoke(message);
            TallyDbLoader.Core.Logging.FileLogger.LogMessage(message);
        }

        public void Start()
        {
            lock (_syncLock)
            {
                if (IsRunning) return;
                _isPaused = false;
                _cts = new CancellationTokenSource();
                _runTask = Task.Run(() => WorkerLoop(_cts.Token));
                Log("Background Sync Engine started.");
            }
        }

        public void Pause()
        {
            lock (_syncLock)
            {
                if (!IsRunning) return;
                _isPaused = true;
                Log($"[Engine] Paused at {DateTime.Now:HH:mm:ss}");
            }
        }

        public void Resume()
        {
            lock (_syncLock)
            {
                if (!IsRunning) return;
                _isPaused = false;
                Log($"[Engine] Resumed at {DateTime.Now:HH:mm:ss}");
                TriggerWakeUp();
            }
        }

        public void Stop()
        {
            CancellationTokenSource? localCts = null;
            Task? localTask = null;

            lock (_syncLock)
            {
                if (!IsRunning) return;
                localCts = _cts;
                localTask = _runTask;
                _cts = null;
                _runTask = null;
                _isPaused = false;
            }

            localCts?.Cancel();
            try
            {
                localTask?.Wait();
            }
            catch { }
            localCts?.Dispose();
            Log("Background Sync Engine stopped.");
        }

        public void TriggerManualSync(int? companyId = null)
        {
            lock (_syncLock)
            {
                if (_disposed || !IsRunning)
                {
                    Log("[Sync warning] Sync engine is not running or disposed.");
                    return;
                }
                _forceSyncOnce = true;
                _manualSyncCompanyId = companyId;
                TriggerWakeUp();
            }
        }

        private void TriggerWakeUp()
        {
            var oldCts = _wakeUpCts;
            _wakeUpCts = new CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
            _wakeUpCts.Dispose();
        }

        private async Task WorkerLoop(CancellationToken token)
        {
            var client = _tallyClient ?? new TallyClient(_tallyServer, _tallyPort);

            while (!token.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    bool hasManualRun = false;
                    lock (_syncLock)
                    {
                        hasManualRun = _forceSyncOnce;
                    }
                    if (!hasManualRun)
                    {
                        try
                        {
                            await Task.Delay(1000, _wakeUpCts.Token);
                        }
                        catch { }
                        continue;
                    }
                }

                try
                {
                    var settings = _repo.GetTallySettings();
                    if (settings.AutoStartTally == 1 && !string.IsNullOrEmpty(settings.TallyExePath))
                    {
                        if (!TallyLauncher.IsTallyRunning())
                        {
                            Log("[Engine] Auto-start Tally: Tally is not running. Launching...");
                            try
                            {
                                TallyLauncher.LaunchTally(settings.TallyExePath);
                                Log("[Engine] Tally launched successfully.");
                                await Task.Delay(TimeSpan.FromSeconds(5), token);
                            }
                            catch (Exception ex)
                            {
                                Log($"[Engine ERROR] Auto-start Tally failed: {ex.Message}");
                            }
                        }
                    }

                    bool runManualSync;
                    int? manualCompanyId;
                    lock (_syncLock)
                    {
                        runManualSync = _forceSyncOnce;
                        manualCompanyId = _manualSyncCompanyId;
                        _forceSyncOnce = false;
                        _manualSyncCompanyId = null;
                    }

                    var companies = _repo.GetAllCompanyProfiles();
                    foreach (var company in companies)
                    {
                        if (token.IsCancellationRequested) break;

                        bool shouldSync = false;
                        if (runManualSync)
                        {
                            shouldSync = !manualCompanyId.HasValue || manualCompanyId.Value == company.Id;
                        }
                        else
                        {
                            shouldSync = SyncOrchestrator.ShouldRun(company, DateTime.Now);
                        }

                        if (shouldSync)
                        {
                            await SyncCompany(company, client, token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Engine Error] Cycle execution error: {ex.Message}");
                }

                try
                {
                    // Sleep for 60 seconds unless woken up earlier by manual trigger
                    await Task.Delay(TimeSpan.FromSeconds(60), _wakeUpCts.Token);
                }
                catch (TaskCanceledException)
                {
                    // Woken up
                }
            }
        }

        private async Task SyncCompany(CompanyProfile company, TallyClient client, CancellationToken token)
        {
            Log($"[Sync] Starting sync for company '{company.Name}' (Target: '{company.TargetCatalog}')...");

            if (string.IsNullOrWhiteSpace(company.TargetCatalog))
            {
                company.Status = "err";
                _repo.SaveCompanyProfile(company);
                Log($"[Sync ERROR] Company '{company.Name}' failed: Target database name is empty.");
                OnSyncCompleted?.Invoke();
                return;
            }

            company.Status = "running";
            _repo.SaveCompanyProfile(company);
            OnSyncCompleted?.Invoke();

            var run = new SyncRun
            {
                CompanyId = company.Id,
                CompanyName = company.Name,
                StartedAt = DateTime.Now,
                Mode = company.Mode,
                Status = "ok"
            };

            try
            {
                var dbProfile = company.Db ?? _repo.GetDatabaseProfileById(company.DbProfileId);
                if (dbProfile == null)
                {
                    throw new Exception("Target database profile not found.");
                }

                // Verify company in Tally
                var activeCompanies = await client.GetActiveCompaniesAsync();
                if (!activeCompanies.Contains(company.Name))
                {
                    throw new Exception("Company is not open in Tally Prime.");
                }

                IDatabaseLoader dbLoader;
                string connStr;
                var tech = dbProfile.Technology.ToLower();
                if (tech.Contains("postgres"))
                {
                    string sslParam = "";
                    if (!dbProfile.Server.Equals("localhost", System.StringComparison.OrdinalIgnoreCase) && 
                        !dbProfile.Server.Equals("127.0.0.1", System.StringComparison.OrdinalIgnoreCase))
                    {
                        sslParam = "SslMode=Require;TrustServerCertificate=True;";
                    }
                    connStr = $"Host={dbProfile.Server};Port={dbProfile.Port};Username={dbProfile.Username};Password={dbProfile.Password};Database={company.TargetCatalog};{sslParam}";
                    dbLoader = new PostgreSqlLoader(connStr);
                }
                else
                {
                    connStr = $"Server={dbProfile.Server},{dbProfile.Port};User Id={dbProfile.Username};Password={dbProfile.Password};Database={company.TargetCatalog};TrustServerCertificate=True;";
                    dbLoader = new MSSqlLoader(connStr);
                }

                // Use existing dynamic table pipeline (YAML config → TDL XML → DataTable → Bulk Load)
                var yamlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tally-export-config.yaml");
                if (!System.IO.File.Exists(yamlPath))
                {
                    yamlPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "tally-export-config.yaml");
                }

                if (!System.IO.File.Exists(yamlPath))
                {
                    throw new System.IO.FileNotFoundException($"Tally definition file '{yamlPath}' not found.");
                }

                var yamlContent = System.IO.File.ReadAllText(yamlPath);
                var config = YamlConfigParser.Parse(yamlContent);

                var dates = await GetCompanyDatesAsync(client, company.Name);
                var tablesToSync = new System.Collections.Generic.List<TableConfig>();
                tablesToSync.AddRange(config.Master);
                tablesToSync.AddRange(config.Transaction);

                // Filter tables by entity flags bitmask
                EntityFlags flags = (EntityFlags)company.EntityFlags;
                var filteredTables = tablesToSync.Where(t => ShouldSyncTable(t, flags)).ToList();

                long totalRows = 0;

                foreach (var table in filteredTables)
                {
                    if (token.IsCancellationRequested) break;

                    Log($"[Sync] Extracting '{table.Name}' for '{company.Name}'...");
                    var xmlQuery = DynamicTdlXmlGenerator.GenerateXml(table, company.Name, dates.fromDate, dates.toDate);
                    var responseXml = await client.PostXMLAsync(xmlQuery);
                    var dataTable = DynamicXmlParser.ParseXml(responseXml, table);

                    if (dataTable.Rows.Count > 0)
                    {
                        await dbLoader.LoadBulkDataAsync(dataTable, table.Name);
                        totalRows += dataTable.Rows.Count;
                    }
                }

                // Complete SyncRun
                run.EndedAt = DateTime.Now;
                run.RowsIn = totalRows;
                run.RowsWritten = totalRows;
                run.Status = "ok";
                run.ResultSummary = $"Sync completed successfully. Wrote {totalRows} records.";
                _repo.AddSyncRun(run);

                // Update Profile
                company.Status = "ok";
                company.LastRunAt = DateTime.Now;
                company.LastDurationMs = (int)(run.EndedAt - run.StartedAt).TotalMilliseconds;
                company.LastRowsWritten = totalRows;
                company.ErrorCount24h = 0;
                _repo.SaveCompanyProfile(company);

                Log($"[Sync SUCCESS] Company '{company.Name}' sync finished. Wrote {totalRows} rows.");
            }
            catch (Exception ex)
            {
                run.EndedAt = DateTime.Now;
                run.Status = "err";
                run.ResultSummary = ex.Message;
                run.LogExcerpt = ex.StackTrace;
                _repo.AddSyncRun(run);

                company.Status = "err";
                company.ErrorCount24h++;
                _repo.SaveCompanyProfile(company);

                Log($"[Sync ERROR] Sync failed for '{company.Name}': {ex.Message}");
            }
            finally
            {
                OnSyncCompleted?.Invoke();
            }
        }

        private bool ShouldSyncTable(TableConfig table, EntityFlags flags)
        {
            // Map table names to entity flags
            var name = table.Name.ToLowerInvariant();
            if (name.Contains("group")) return flags.HasFlag(EntityFlags.Groups);
            if (name.Contains("ledger")) return flags.HasFlag(EntityFlags.Ledgers);
            if (name.Contains("voucher") || name.Contains("sales") || name.Contains("purchase") || name.Contains("receipt") || name.Contains("payment") || name.Contains("journal") || name.Contains("contra")) return flags.HasFlag(EntityFlags.Vouchers);
            if (name.Contains("stock") || name.Contains("item")) return flags.HasFlag(EntityFlags.StockItems);
            return true; // Default: sync if flag unknown
        }

        private async Task<(DateTime fromDate, DateTime toDate)> GetCompanyDatesAsync(TallyClient client, string companyName)
        {
            var companies = await client.GetCompaniesAsync();
            var info = companies.FirstOrDefault(c => c.Name.Equals(companyName, StringComparison.OrdinalIgnoreCase));
            if (info == null) return (DateTime.MinValue, DateTime.MaxValue);
            return (info.BooksFrom ?? DateTime.MinValue, info.BooksTo ?? DateTime.MaxValue);
        }
    }
}
```

---

## Phase 3: Fluent Themes & Design Tokens

### Task 3.1: Write Token and Style Dictionaries

**Files:**
- Create: `src/TallyDbLoader.Wpf/Themes/Tokens.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/Icons.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/Typography.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/Buttons.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/TextBoxes.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/Card.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/Pill.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/CommandBar.xaml`
- Create: `src/TallyDbLoader.Wpf/Themes/NavigationView.xaml`
- Modify: `src/TallyDbLoader.Wpf/App.xaml`

- [ ] **Step 1: Write Icons.xaml**
  Create `src/TallyDbLoader.Wpf/Themes/Icons.xaml` with SVG geometries for rail and toolbar navigation items.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- SVG Path Geometries for Icons -->
    <Geometry x:Key="IconDashboard">M19,5V19H5V5H19M19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M9,17H7V10H9V17M13,17H11V7H13V17M17,17H15V12H17V17Z</Geometry>
    <Geometry x:Key="IconCompanies">M12,5.5A3.5,3.5 0 0,1 15.5,9A3.5,3.5 0 0,1 12,12.5A3.5,3.5 0 0,1 8.5,9A3.5,3.5 0 0,1 12,5.5M5,8C5.56,8 6.08,8.15 6.53,8.42C5.9,9.74 5.9,11.26 6.53,12.58C6.08,12.85 5.56,13 5,13A3,3 0 0,1 2,10A3,3 0 0,1 5,8M19,8A3,3 0 0,1 22,10A3,3 0 0,1 19,13C18.44,13 17.92,12.85 17.47,12.58C18.1,11.26 18.1,9.74 17.47,8.42C17.92,8.15 18.44,8 19,8M5.5,18.25C5.5,16 8,14.5 12,14.5C16,14.5 18.5,16 18.5,18.25V20H5.5V18.25M0,20V18.5C0,17.11 1.89,15.94 4.47,15.57C3.55,16.74 3.55,18.26 4.47,19.43C4.24,19.47 4,19.5 3.75,19.5H0V20M24,20H20.25C20,19.5 19.76,19.47 19.53,19.43C20.45,18.26 20.45,16.74 19.53,15.57C22.11,15.94 24,17.11 24,18.5V20Z</Geometry>
    <Geometry x:Key="IconDatabases">M12,3C7.58,3 4,4.79 4,7C4,9.21 7.58,11 12,11C16.42,11 20,9.21 20,7C20,4.79 16.42,3 12,3M4,9V12C4,14.21 7.58,16 12,16C16.42,16 20,14.21 20,12V9C20,11.21 16.42,13 12,13C7.58,13 4,11.21 4,9M4,14V17C4,19.21 7.58,21 12,21C16.42,21 20,19.21 20,17V14C20,16.21 16.42,18 12,18C7.58,18 4,16.21 4,14Z</Geometry>
    <Geometry x:Key="IconLog">M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M8,12H16V10H8V12M8,16H16V14H8V16M8,18H13V17H8V18Z</Geometry>
    <Geometry x:Key="IconHistory">M13.5,8H12V13L16.28,15.54L17,14.22L13.5,12.11V8M13,3A9,9 0 0,0 4,12H1L4.89,15.89L5,16L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,20 10.5,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3Z</Geometry>
    <Geometry x:Key="IconSettings">M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.47,5.34 14.85,5.08L14.47,2.42C14.43,2.18 14.22,2 13.97,2H9.97C9.72,2 9.51,2.18 9.47,2.42L9.09,5.08C8.47,5.34 7.9,5.66 7.38,6.05L4.89,5.05C4.67,4.96 4.4,5.05 4.27,5.27L2.27,8.73C2.15,8.95 2.2,9.22 2.39,9.37L4.5,11C4.46,11.34 4.43,11.67 4.43,12C4.43,12.33 4.46,12.65 4.5,12.97L2.39,14.63C2.2,14.78 2.15,15.05 2.27,15.27L4.27,18.73C4.4,18.95 4.67,19.04 4.89,18.95L7.38,17.95C7.9,18.34 8.47,18.66 9.09,18.92L9.47,21.58C9.51,21.82 9.72,22 9.97,22H13.97C14.22,22 14.43,21.82 14.47,21.58L14.85,18.92C15.47,18.66 16.04,18.34 16.56,17.95L19.05,18.95C19.27,19.04 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z</Geometry>
    <Geometry x:Key="IconWarning">M12,2L1,21H23L12,2M12,6L19.8,20H4.2L12,6M11,10V14H13V10H11M11,16V18H13V16H11Z</Geometry>
</ResourceDictionary>
```

- [ ] **Step 2: Write Tokens.xaml**

  Create `src/TallyDbLoader.Wpf/Themes/Tokens.xaml` mapping the Light theme (default) and Dark theme token configurations.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Light Theme Colors -->
    <Color x:Key="BgColor">#F3F3F3</Color>
    <Color x:Key="LayerColor">#FBFBFB</Color>
    <Color x:Key="Layer2Color">#F6F6F6</Color>
    <Color x:Key="DrawerBackgroundColor">#EFEFEF</Color>
    <Color x:Key="BorderColor">#E0E0E0</Color>
    <Color x:Key="PrimaryTextColor">#1A1A1A</Color>
    <Color x:Key="MutedTextColor">#666666</Color>
    <Color x:Key="SubtleTextColor">#888888</Color>
    <Color x:Key="AccentColor">#0067C0</Color>
    <Color x:Key="AccentSoftColor">#1A0067C0</Color>
    <Color x:Key="StatusOkColor">#16A34A</Color>
    <Color x:Key="StatusWarnColor">#D97706</Color>
    <Color x:Key="StatusErrColor">#DC2626</Color>

    <!-- Brush definitions mapping Colors to Keys -->
    <SolidColorBrush x:Key="BackgroundBrush" Color="{StaticResource BgColor}"/>
    <SolidColorBrush x:Key="LayerBrush" Color="{StaticResource LayerColor}"/>
    <SolidColorBrush x:Key="Layer2Brush" Color="{StaticResource Layer2Color}"/>
    <SolidColorBrush x:Key="DrawerBackgroundBrush" Color="{StaticResource DrawerBackgroundColor}"/>
    <SolidColorBrush x:Key="BorderBrush" Color="{StaticResource BorderColor}"/>
    <SolidColorBrush x:Key="ForegroundBrush" Color="{StaticResource PrimaryTextColor}"/>
    <SolidColorBrush x:Key="MutedTextBrush" Color="{StaticResource MutedTextColor}"/>
    <SolidColorBrush x:Key="SubtleTextBrush" Color="{StaticResource SubtleTextColor}"/>
    <SolidColorBrush x:Key="PrimaryBrush" Color="{StaticResource AccentColor}"/>
    <SolidColorBrush x:Key="AccentSoftBrush" Color="{StaticResource AccentSoftColor}"/>
    
    <SolidColorBrush x:Key="StatusOkBrush" Color="{StaticResource StatusOkColor}"/>
    <SolidColorBrush x:Key="StatusWarnBrush" Color="{StaticResource StatusWarnColor}"/>
    <SolidColorBrush x:Key="StatusErrBrush" Color="{StaticResource StatusErrColor}"/>

    <Thickness x:Key="CardPadding">16</Thickness>
    <CornerRadius x:Key="CardCornerRadius">8</CornerRadius>
    <CornerRadius x:Key="ControlCornerRadius">4</CornerRadius>
</ResourceDictionary>
```

- [ ] **Step 3: Write Typography.xaml**
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Font Tokens -->
    <FontFamily x:Key="SegoeUI">Segoe UI Variable Text, Segoe UI, Arial</FontFamily>
    <FontFamily x:Key="SegoeUIDisplay">Segoe UI Variable Display, Segoe UI, Arial</FontFamily>
    <FontFamily x:Key="CascadiaMono">Cascadia Mono, Consolas, Courier New</FontFamily>

    <!-- Styles -->
    <Style x:Key="DisplayTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource SegoeUIDisplay}"/>
        <Setter Property="FontSize" Value="22"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
    </Style>

    <Style x:Key="SubtitleTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource SegoeUI}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
    </Style>

    <Style x:Key="BodyTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource SegoeUI}"/>
        <Setter Property="FontSize" Value="12.5"/>
        <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
    </Style>

    <Style x:Key="BodyStrongTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource SegoeUI}"/>
        <Setter Property="FontSize" Value="12.5"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
    </Style>

    <Style x:Key="CaptionTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource SegoeUI}"/>
        <Setter Property="FontSize" Value="11.5"/>
        <Setter Property="Foreground" Value="{DynamicResource MutedTextBrush}"/>
    </Style>

    <Style x:Key="CaptionMuteTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource SegoeUI}"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Foreground" Value="{DynamicResource SubtleTextBrush}"/>
    </Style>

    <Style x:Key="MonoTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource CascadiaMono}"/>
        <Setter Property="FontSize" Value="11.5"/>
        <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 5: Write Buttons.xaml**
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="HyperlinkButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderBrush" Value="Transparent"/>
        <Setter Property="Foreground" Value="{DynamicResource PrimaryBrush}"/>
        <Setter Property="Padding" Value="0"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <TextBlock Text="{TemplateBinding Content}" TextDecorations="Underline"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="StandardButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="#FFFFFF"/>
        <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="12,6"/>
        <Setter Property="Height" Value="32"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="HorizontalContentAlignment" Value="Center"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{DynamicResource ControlCornerRadius}">
                        <ContentPresenter Padding="{TemplateBinding Padding}"
                                          HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="PrimaryButtonStyle" TargetType="Button" BasedOn="{StaticResource StandardButtonStyle}">
        <Setter Property="Background" Value="{DynamicResource PrimaryBrush}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource PrimaryBrush}"/>
        <Setter Property="Foreground" Value="#FFFFFF"/>
    </Style>

    <Style x:Key="DangerButtonStyle" TargetType="Button" BasedOn="{StaticResource StandardButtonStyle}">
        <Setter Property="Background" Value="{DynamicResource StatusErrBrush}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource StatusErrBrush}"/>
        <Setter Property="Foreground" Value="#FFFFFF"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 6: Write TextBoxes.xaml**
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="AccentTextBoxStyle" TargetType="TextBox">
        <Setter Property="Height" Value="30"/>
        <Setter Property="Background" Value="#FFFFFF"/>
        <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
        <Setter Property="BorderThickness" Value="0,0,0,1"/>
        <Setter Property="Padding" Value="8,4"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="2">
                        <ScrollViewer x:Name="PART_ContentHost" Margin="0"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsFocused" Value="True">
                            <Setter Property="BorderBrush" Value="{DynamicResource PrimaryBrush}"/>
                            <Setter Property="BorderThickness" Value="0,0,0,2"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 7: Write Card.xaml**
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="FluentCardStyle" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource LayerBrush}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="{DynamicResource CardCornerRadius}"/>
        <Setter Property="Padding" Value="{DynamicResource CardPadding}"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 8: Write Pill.xaml**
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="StatusPillStyle" TargetType="Border">
        <Setter Property="CornerRadius" Value="10"/>
        <Setter Property="Padding" Value="8,2"/>
        <Setter Property="Background" Value="{DynamicResource AccentSoftBrush}"/>
        <Setter Property="HorizontalAlignment" Value="Left"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 9: Write CommandBar.xaml**
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="CommandBarPanelStyle" TargetType="StackPanel">
        <Setter Property="Height" Value="54"/>
        <Setter Property="Background" Value="{DynamicResource Layer2Brush}"/>
        <Setter Property="Orientation" Value="Horizontal"/>
        <Setter Property="Margin" Value="0"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 10: Write NavigationView.xaml**
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="NavButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderBrush" Value="Transparent"/>
        <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
        <Setter Property="Padding" Value="16,8"/>
        <Setter Property="HorizontalContentAlignment" Value="Left"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}" CornerRadius="4" Margin="2">
                        <ContentPresenter Padding="{TemplateBinding Padding}" HorizontalAlignment="Left"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Background" Value="{DynamicResource AccentSoftBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 11: Write App.xaml**
  Replace `src/TallyDbLoader.Wpf/App.xaml` with the full Application resource dictionaries and converters mapping:
  ```xml
  <Application x:Class="TallyDbLoader.Wpf.App"
               xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               xmlns:local="clr-namespace:TallyDbLoader.Wpf"
               xmlns:converters="clr-namespace:TallyDbLoader.Wpf.Converters"
               StartupUri="MainWindow.xaml">
      <Application.Resources>
          <ResourceDictionary>
              <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="Themes/Tokens.xaml"/>
                  <ResourceDictionary Source="Themes/Icons.xaml"/>
                  <ResourceDictionary Source="Themes/Typography.xaml"/>
                  <ResourceDictionary Source="Themes/Buttons.xaml"/>
                  <ResourceDictionary Source="Themes/TextBoxes.xaml"/>
                  <ResourceDictionary Source="Themes/Card.xaml"/>
                  <ResourceDictionary Source="Themes/Pill.xaml"/>
                  <ResourceDictionary Source="Themes/CommandBar.xaml"/>
                  <ResourceDictionary Source="Themes/NavigationView.xaml"/>
              </ResourceDictionary.MergedDictionaries>

              <!-- Value Converters -->
              <converters:StatusToToneConverter x:Key="StatusToToneConverter"/>
              <converters:EngineStateToColorConverter x:Key="EngineStateToColorConverter"/>
              <converters:RelativeTimeConverter x:Key="RelativeTimeConverter"/>
              <converters:NumberConverter x:Key="NumberConverter"/>
              <converters:NextRunConverter x:Key="NextRunConverter"/>
              <converters:NullToBoolConverter x:Key="NullToBoolConverter"/>
              <converters:CountToVisibilityConverter x:Key="CountToVisibilityConverter"/>
              <converters:NullToVisibilityConverter x:Key="NullToVisibilityConverter"/>
              <converters:AddOneConverter x:Key="AddOneConverter"/>
              <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter"/>
          </ResourceDictionary>
      </Application.Resources>
  </Application>
  ```

---

## Phase 4: Base ViewModels & Routing

### Task 4.1: Base Classes and Navigation Implementation

**Files:**
- Create: `src/TallyDbLoader.Wpf/ViewModels/BaseViewModel.cs`
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs`

- [ ] **Step 1: Write BaseViewModel, RelayCommand and RelayCommand<T>**
```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TallyDbLoader.Wpf.ViewModels
{
    public class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute();
        public void Execute(object? parameter) => _execute();
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter)
        {
            if (_canExecute == null) return true;
            if (parameter == null && typeof(T).IsValueType) return _canExecute(default);
            return _canExecute((T?)parameter);
        }

        public void Execute(object? parameter)
        {
            if (parameter == null && typeof(T).IsValueType) _execute(default);
            else _execute((T?)parameter);
        }
    }
}
```

- [ ] **Step 2: Rewrite MainViewModel.cs with Navigation and Engine State**
  Replace `src/TallyDbLoader.Wpf/MainViewModel.cs` with the full view model implementation, supporting engine state changes, toast queues, connection testing, and wizard navigations.

```csharp
using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Windows.Input;
using System.Windows.Threading;
using TallyDbLoader.Wpf.ViewModels;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Wpf
{
    public enum RouteScreen
    {
        Dashboard,
        Companies,
        CompanyProfile,
        Databases,
        Log,
        History,
        Settings,
        Wizard
    }

    public class NavigationRoute
    {
        public RouteScreen Screen { get; set; }
        public int? ParameterId { get; set; }
    }

    public class ToastModel : BaseViewModel
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Kind { get; set; } = "info"; // "info" | "warn" | "err" | "ok"
    }

    public enum EngineState
    {
        Idle,
        Running,
        Paused
    }

    public class MainViewModel : BaseViewModel, IDisposable
    {
        private readonly ConfigRepository _repo;
        private BackgroundSyncWorker? _worker;
        private readonly DispatcherTimer _logBatchTimer;
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();

        // Navigation callback for opening Dialog from View Model
        public Func<List<TallyCompanyInfo>, TallyCompanyInfo?>? CompanySelector { get; set; }

        // Navigation properties
        private NavigationRoute _currentRoute;
        public NavigationRoute CurrentRoute
        {
            get => _currentRoute;
            set { _currentRoute = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGoBack)); }
        }
        public Stack<NavigationRoute> RouteStack { get; } = new Stack<NavigationRoute>();
        public bool CanGoBack => RouteStack.Count > 1;

        // Collections
        public ObservableCollection<DatabaseProfile> DatabaseProfiles { get; } = new ObservableCollection<DatabaseProfile>();
        public ObservableCollection<CompanyProfile> Companies { get; } = new ObservableCollection<CompanyProfile>();
        public ObservableCollection<SyncRun> RunHistory { get; } = new ObservableCollection<SyncRun>();
        public ObservableCollection<SyncRun> SelectedCompanyRecentRuns { get; } = new ObservableCollection<SyncRun>();
        public ObservableCollection<TallyCompanyInfo> UnlinkedTallyCompanies { get; } = new ObservableCollection<TallyCompanyInfo>();
        public ObservableCollection<ToastModel> Toasts { get; } = new ObservableCollection<ToastModel>();
        public ObservableCollection<CompanyProfile> CompaniesUsingSelectedDb { get; } = new ObservableCollection<CompanyProfile>();

        // Selected items
        private CompanyProfile? _selectedCompany;
        public CompanyProfile? SelectedCompany
        {
            get => _selectedCompany;
            set
            {
                if (_selectedCompany == value) return;
                _selectedCompany = value;
                OnPropertyChanged();
            }
        }

        private DatabaseProfile? _selectedDatabaseProfile;
        public DatabaseProfile? SelectedDatabaseProfile
        {
            get => _selectedDatabaseProfile;
            set
            {
                if (_selectedDatabaseProfile == value) return;
                _selectedDatabaseProfile = value;
                OnPropertyChanged();
                CompaniesUsingSelectedDb.Clear();
                if (value != null)
                {
                    foreach (var c in Companies.Where(cp => cp.DbProfileId == value.Id))
                    {
                        CompaniesUsingSelectedDb.Add(c);
                    }
                    StartEditingDbProfile(value);
                }
            }
        }

        private SyncRun? _selectedRun;
        public SyncRun? SelectedRun
        {
            get => _selectedRun;
            set { _selectedRun = value; OnPropertyChanged(); }
        }

        // Global Settings Properties
        private string _tallyServer = "localhost";
        public string TallyServer
        {
            get => _tallyServer;
            set { _tallyServer = value; OnPropertyChanged(); }
        }

        private int _tallyPort = 9000;
        public int TallyPort
        {
            get => _tallyPort;
            set { _tallyPort = value; OnPropertyChanged(); }
        }

        private string _tallyExePath = string.Empty;
        public string TallyExePath
        {
            get => _tallyExePath;
            set { _tallyExePath = value; OnPropertyChanged(); }
        }

        private string _tallyIniPath = string.Empty;
        public string TallyIniPath
        {
            get => _tallyIniPath;
            set { _tallyIniPath = value; OnPropertyChanged(); }
        }

        private bool _autoStartTally;
        public bool AutoStartTally
        {
            get => _autoStartTally;
            set { _autoStartTally = value; OnPropertyChanged(); }
        }

        // Database Profile Editor Scratch Properties
        private string _dbName = string.Empty;
        public string DbName
        {
            get => _dbName;
            set { _dbName = value; OnPropertyChanged(); }
        }

        private string _dbTech = "postgres";
        public string DbTech
        {
            get => _dbTech;
            set { _dbTech = value; OnPropertyChanged(); }
        }

        private string _dbServer = "localhost";
        public string DbServer
        {
            get => _dbServer;
            set { _dbServer = value; OnPropertyChanged(); }
        }

        private int _dbPort = 5432;
        public int DbPort
        {
            get => _dbPort;
            set { _dbPort = value; OnPropertyChanged(); }
        }

        private string _dbUsername = string.Empty;
        public string DbUsername
        {
            get => _dbUsername;
            set { _dbUsername = value; OnPropertyChanged(); }
        }

        private string _dbPassword = string.Empty;
        public string DbPassword
        {
            get => _dbPassword;
            set { _dbPassword = value; OnPropertyChanged(); }
        }

        private string _dbFormHeader = "New Database Connection";
        public string DbFormHeader
        {
            get => _dbFormHeader;
            set { _dbFormHeader = value; OnPropertyChanged(); }
        }

        private string _dbSaveButtonText = "Save profile";
        public string DbSaveButtonText
        {
            get => _dbSaveButtonText;
            set { _dbSaveButtonText = value; OnPropertyChanged(); }
        }

        private System.Windows.Visibility _isEditingDbProfileVisibility = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility IsEditingDbProfileVisibility
        {
            get => _isEditingDbProfileVisibility;
            set { _isEditingDbProfileVisibility = value; OnPropertyChanged(); }
        }

        // Company Profile / Sync Job Editor Scratch Properties
        private string _jobCompany = string.Empty;
        public string JobCompany
        {
            get => _jobCompany;
            set { _jobCompany = value; OnPropertyChanged(); }
        }

        private DatabaseProfile? _jobSelectedProfile;
        public DatabaseProfile? JobSelectedProfile
        {
            get => _jobSelectedProfile;
            set { _jobSelectedProfile = value; OnPropertyChanged(); }
        }

        private string _jobTargetCatalog = string.Empty;
        public string JobTargetCatalog
        {
            get => _jobTargetCatalog;
            set { _jobTargetCatalog = value; OnPropertyChanged(); }
        }

        private string _jobSchema = "public";
        public string JobSchema
        {
            get => _jobSchema;
            set { _jobSchema = value; OnPropertyChanged(); }
        }

        private string _jobTablePrefix = "tally_";
        public string JobTablePrefix
        {
            get => _jobTablePrefix;
            set { _jobTablePrefix = value; OnPropertyChanged(); }
        }

        private string _jobSyncMode = "full";
        public string JobSyncMode
        {
            get => _jobSyncMode;
            set { _jobSyncMode = value; OnPropertyChanged(); }
        }

        private int _jobInterval = 15;
        public int JobInterval
        {
            get => _jobInterval;
            set { _jobInterval = value; OnPropertyChanged(); }
        }

        private bool _jobEnabled = true;
        public bool JobEnabled
        {
            get => _jobEnabled;
            set { _jobEnabled = value; OnPropertyChanged(); }
        }

        private bool _jobNotifyOnError = true;
        public bool JobNotifyOnError
        {
            get => _jobNotifyOnError;
            set { _jobNotifyOnError = value; OnPropertyChanged(); }
        }

        private bool _jobPauseOnTallyClose = false;
        public bool JobPauseOnTallyClose
        {
            get => _jobPauseOnTallyClose;
            set { _jobPauseOnTallyClose = value; OnPropertyChanged(); }
        }

        private string _jobFormHeader = "New Sync Profile";
        public string JobFormHeader
        {
            get => _jobFormHeader;
            set { _jobFormHeader = value; OnPropertyChanged(); }
        }

        private string _jobSaveButtonText = "Save profile";
        public string JobSaveButtonText
        {
            get => _jobSaveButtonText;
            set { _jobSaveButtonText = value; OnPropertyChanged(); }
        }

        private System.Windows.Visibility _isEditingJobVisibility = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility IsEditingJobVisibility
        {
            get => _isEditingJobVisibility;
            set { _isEditingJobVisibility = value; OnPropertyChanged(); }
        }

        // Entity check scratch variables
        private bool _jobSyncVouchers = true;
        public bool JobSyncVouchers
        {
            get => _jobSyncVouchers;
            set { _jobSyncVouchers = value; OnPropertyChanged(); }
        }

        private bool _jobSyncLedgers = true;
        public bool JobSyncLedgers
        {
            get => _jobSyncLedgers;
            set { _jobSyncLedgers = value; OnPropertyChanged(); }
        }

        private bool _jobSyncStockItems = true;
        public bool JobSyncStockItems
        {
            get => _jobSyncStockItems;
            set { _jobSyncStockItems = value; OnPropertyChanged(); }
        }

        private bool _jobSyncGroups = true;
        public bool JobSyncGroups
        {
            get => _jobSyncGroups;
            set { _jobSyncGroups = value; OnPropertyChanged(); }
        }

        private bool _jobSyncCostCentres = false;
        public bool JobSyncCostCentres
        {
            get => _jobSyncCostCentres;
            set { _jobSyncCostCentres = value; OnPropertyChanged(); }
        }

        private bool _jobSyncCurrencies = false;
        public bool JobSyncCurrencies
        {
            get => _jobSyncCurrencies;
            set { _jobSyncCurrencies = value; OnPropertyChanged(); }
        }

        // Connection string paste properties
        private string _connectionStringPasteText = string.Empty;
        public string ConnectionStringPasteText
        {
            get => _connectionStringPasteText;
            set
            {
                _connectionStringPasteText = value;
                OnPropertyChanged();
                if (!string.IsNullOrEmpty(value))
                {
                    TryParseConnectionString(value);
                }
            }
        }

        // Wizard Properties
        private int _wizardStepIndex = 0;
        public int WizardStepIndex
        {
            get => _wizardStepIndex;
            set { _wizardStepIndex = value; OnPropertyChanged(); }
        }



        // Engine State
        private EngineState _state = EngineState.Idle;
        public EngineState State
        {
            get => _state;
            set 
            { 
                _state = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsSyncRunning));
                OnPropertyChanged(nameof(IsSyncNotRunning));
                OnPropertyChanged(nameof(StateText));
            }
        }

        public bool IsSyncRunning => State == EngineState.Running;
        public bool IsSyncNotRunning => !IsSyncRunning;
        public string StateText => State.ToString();

        private string _logOutput = string.Empty;
        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        // Commands
        public ICommand NavigateCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand StartSyncEngineCommand { get; }
        public ICommand PauseSyncEngineCommand { get; }
        public ICommand ResumeSyncEngineCommand { get; }
        public ICommand StopSyncEngineCommand { get; }
        public ICommand RunCompanyCommand { get; }
        public ICommand SaveTallySettingsCommand { get; }
        public ICommand OpenCompanyPickerCommand { get; }
        public ICommand StartEditingCompanyCommand { get; }
        public ICommand SaveCompanyProfileCommand { get; }
        public ICommand DeleteCompanyProfileCommand { get; }
        public ICommand StartEditingDbProfileCommand { get; }
        public ICommand SaveDatabaseProfileCommand { get; }
        public ICommand DeleteDatabaseProfileCommand { get; }
        public ICommand TestDatabaseConnectionCommand { get; }
        public ICommand TestTallyConnectionCommand { get; }
        public ICommand DetectActiveCompaniesCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ExportLogCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand CancelDbEditCommand { get; }
        public ICommand CancelJobEditCommand { get; }

        public MainViewModel(string dbPath)
        {
            _repo = new ConfigRepository(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            // Initialize Routing
            _currentRoute = new NavigationRoute { Screen = RouteScreen.Dashboard };
            RouteStack.Push(_currentRoute);

            // Command bindings
            NavigateCommand = new RelayCommand<object?>(ExecuteNavigate);
            BackCommand = new RelayCommand(GoBack);
            StartSyncEngineCommand = new RelayCommand(StartEngine);
            PauseSyncEngineCommand = new RelayCommand(PauseEngine);
            ResumeSyncEngineCommand = new RelayCommand(ResumeEngine);
            StopSyncEngineCommand = new RelayCommand(StopEngine);
            RunCompanyCommand = new RelayCommand<object?>(RunCompany);
            SaveTallySettingsCommand = new RelayCommand(SaveTallySettings);
            OpenCompanyPickerCommand = new RelayCommand(DetectActiveCompanies);
            StartEditingCompanyCommand = new RelayCommand<object?>(StartEditingCompany);
            SaveCompanyProfileCommand = new RelayCommand(SaveCompanyProfile);
            DeleteCompanyProfileCommand = new RelayCommand<object?>(DeleteCompanyProfile);
            StartEditingDbProfileCommand = new RelayCommand<object?>(StartEditingDbProfile);
            SaveDatabaseProfileCommand = new RelayCommand(SaveDatabaseProfile);
            DeleteDatabaseProfileCommand = new RelayCommand<object?>(DeleteDatabaseProfile);
            TestDatabaseConnectionCommand = new RelayCommand(TestDatabaseConnection);
            TestTallyConnectionCommand = new RelayCommand(TestTallyConnection);
            DetectActiveCompaniesCommand = new RelayCommand(DetectActiveCompanies);
            RefreshCommand = new RelayCommand(LoadConfiguration);
            ExportLogCommand = new RelayCommand(ExportLog);
            ClearLogCommand = new RelayCommand(ClearLog);
            CancelDbEditCommand = new RelayCommand(() => StartEditingDbProfile(null));
            CancelJobEditCommand = new RelayCommand(() => GoBack());

            LoadConfiguration();

            // Set up log batching timer
            _logBatchTimer = new DispatcherTimer();
            _logBatchTimer.Interval = TimeSpan.FromMilliseconds(100);
            _logBatchTimer.Tick += FlushLogs;
            _logBatchTimer.Start();
        }

        public void ShowToast(string title, string body, string kind = "info")
        {
            var toast = new ToastModel { Title = title, Body = body, Kind = kind };
            Toasts.Add(toast);
            if (Toasts.Count > 5)
            {
                Toasts.RemoveAt(0);
            }

            var dismissTimer = new DispatcherTimer();
            dismissTimer.Interval = TimeSpan.FromMilliseconds(4500);
            dismissTimer.Tick += (s, e) =>
            {
                Toasts.Remove(toast);
                dismissTimer.Stop();
            };
            dismissTimer.Start();
        }

        private void StartEngine()
        {
            if (State == EngineState.Running) return;
            if (_worker == null)
            {
                _worker = new BackgroundSyncWorker(_repo, TallyServer, TallyPort);
                _worker.OnLogMessage += message => _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
                _worker.OnSyncCompleted += () => System.Windows.Application.Current.Dispatcher.Invoke(LoadConfiguration);
            }
            _worker.Start();
            State = EngineState.Running;
            ShowToast("Engine started", "Background worker spinning up...", "info");
        }

        private void PauseEngine()
        {
            if (_worker != null && State == EngineState.Running)
            {
                _worker.Pause();
                State = EngineState.Paused;
                ShowToast("Engine paused", "", "warn");
            }
        }

        private void ResumeEngine()
        {
            if (_worker != null && State == EngineState.Paused)
            {
                _worker.Resume();
                State = EngineState.Running;
                ShowToast("Engine resumed", "", "ok");
            }
        }

        private void StopEngine()
        {
            if (_worker != null)
            {
                _worker.Stop();
                _worker.Dispose();
                _worker = null;
                State = EngineState.Idle;
                ShowToast("Engine stopped", "Background worker disposed.", "warn");
            }
        }

        public void TriggerManualSync()
        {
            if (State == EngineState.Idle)
            {
                ShowToast("Engine is idle", "Start the engine to sync.", "warn");
                return;
            }
            if (_worker != null)
            {
                _worker.TriggerManualSync(null);
                ShowToast("Sync queued", "Schedule tick requested.", "info");
            }
        }

        public void Dispose()
        {
            StopEngine();
            _logBatchTimer?.Stop();
        }

        private void RunCompany(object? parameter)
        {
            if (State == EngineState.Idle)
            {
                ShowToast("Engine is idle", "Start the engine to sync.", "warn");
                return;
            }
            if (_worker != null)
            {
                int? companyId = parameter as int?;
                _worker.TriggerManualSync(companyId);
                ShowToast("Sync queued", "Schedule tick requested.", "info");
            }
        }

        private void ExecuteNavigate(object? parameter)
        {
            if (parameter is RouteScreen screen)
            {
                Navigate(screen, resetStack: (screen == RouteScreen.Dashboard || screen == RouteScreen.Companies || screen == RouteScreen.Databases || screen == RouteScreen.Log || screen == RouteScreen.History || screen == RouteScreen.Settings));
            }
            else if (parameter is string actionStr)
            {
                if (actionStr == "WizardNext")
                {
                    if (WizardStepIndex < 5)
                    {
                        WizardStepIndex++;
                    }
                    else
                    {
                        // Save everything!
                        SaveTallySettings();
                        
                        if (!string.IsNullOrEmpty(DbName))
                        {
                            var db = new DatabaseProfile
                            {
                                Name = DbName,
                                Technology = DbTech,
                                Server = DbServer,
                                Port = DbPort,
                                Username = DbUsername,
                                Password = DbPassword
                            };
                            _repo.SaveDatabaseProfile(db);
                            
                            var dbs = _repo.GetAllDatabaseProfiles();
                            var savedDb = dbs.Find(d => d.Name == DbName);
                            
                            if (savedDb != null && !string.IsNullOrEmpty(JobCompany))
                            {
                                int flags = 0;
                                if (JobSyncVouchers) flags |= 1;
                                if (JobSyncLedgers) flags |= 2;
                                if (JobSyncStockItems) flags |= 4;
                                if (JobSyncGroups) flags |= 8;
                                if (JobSyncCostCentres) flags |= 16;
                                if (JobSyncCurrencies) flags |= 32;

                                var job = new CompanyProfile
                                {
                                    Name = JobCompany,
                                    DbProfileId = savedDb.Id,
                                    TargetCatalog = JobTargetCatalog,
                                    Schema = JobSchema,
                                    TablePrefix = JobTablePrefix,
                                    Mode = JobSyncMode,
                                    IntervalMinutes = JobInterval,
                                    Enabled = JobEnabled ? 1 : 0,
                                    NotifyOnError = JobNotifyOnError ? 1 : 0,
                                    PauseOnTallyClose = JobPauseOnTallyClose ? 1 : 0,
                                    EntityFlags = flags,
                                    Status = "idle"
                                };
                                _repo.SaveCompanyProfile(job);
                            }
                        }
                        
                        LoadConfiguration();
                        WizardStepIndex = 0;
                        Navigate(RouteScreen.Dashboard, resetStack: true);
                        ShowToast("Setup Complete", "Initial sync profile created.", "ok");
                    }
                }
                else if (actionStr == "WizardBack")
                {
                    if (WizardStepIndex > 0)
                    {
                        WizardStepIndex--;
                    }
                }
                else if (actionStr == "Dashboard")
                {
                    Navigate(RouteScreen.Dashboard, resetStack: true);
                }
                else if (Enum.TryParse<RouteScreen>(actionStr, ignoreCase: true, out var parsedScreen))
                {
                    Navigate(parsedScreen, resetStack: (parsedScreen == RouteScreen.Dashboard || parsedScreen == RouteScreen.Companies || parsedScreen == RouteScreen.Databases || parsedScreen == RouteScreen.Log || parsedScreen == RouteScreen.History || parsedScreen == RouteScreen.Settings));
                }
            }
        }

        public void Navigate(RouteScreen screen, int? parameterId = null, bool resetStack = false)
        {
            var route = new NavigationRoute { Screen = screen, ParameterId = parameterId };
            if (resetStack)
            {
                RouteStack.Clear();
            }
            RouteStack.Push(route);
            CurrentRoute = route;

            if (screen == RouteScreen.CompanyProfile)
            {
                int id = parameterId ?? 0;
                var profile = Companies.FirstOrDefault(c => c.Id == id);
                if (profile == null)
                {
                    SelectedCompany = new CompanyProfile();
                    JobCompany = string.Empty;
                    JobSelectedProfile = DatabaseProfiles.FirstOrDefault();
                    JobTargetCatalog = string.Empty;
                    JobSchema = "public";
                    JobTablePrefix = "tally_";
                    JobSyncMode = "full";
                    JobInterval = 15;
                    JobEnabled = true;
                    JobNotifyOnError = true;
                    JobPauseOnTallyClose = false;

                    JobSyncVouchers = true;
                    JobSyncLedgers = true;
                    JobSyncStockItems = true;
                    JobSyncGroups = true;
                    JobSyncCostCentres = false;
                    JobSyncCurrencies = false;

                    JobFormHeader = "New Sync Profile";
                    JobSaveButtonText = "Save profile";
                    IsEditingJobVisibility = System.Windows.Visibility.Collapsed;

                    SelectedCompanyRecentRuns.Clear();
                }
                else
                {
                    SelectedCompany = profile;
                    JobCompany = profile.Name;
                    JobSelectedProfile = DatabaseProfiles.FirstOrDefault(d => d.Id == profile.DbProfileId);
                    JobTargetCatalog = profile.TargetCatalog;
                    JobSchema = profile.Schema;
                    JobTablePrefix = profile.TablePrefix;
                    JobSyncMode = profile.Mode;
                    JobInterval = profile.IntervalMinutes;
                    JobEnabled = profile.Enabled == 1;
                    JobNotifyOnError = profile.NotifyOnError == 1;
                    JobPauseOnTallyClose = profile.PauseOnTallyClose == 1;

                    EntityFlags flags = (EntityFlags)profile.EntityFlags;
                    JobSyncVouchers = flags.HasFlag(EntityFlags.Vouchers);
                    JobSyncLedgers = flags.HasFlag(EntityFlags.Ledgers);
                    JobSyncStockItems = flags.HasFlag(EntityFlags.StockItems);
                    JobSyncGroups = flags.HasFlag(EntityFlags.Groups);
                    JobSyncCostCentres = flags.HasFlag(EntityFlags.CostCentres);
                    JobSyncCurrencies = flags.HasFlag(EntityFlags.Currencies);

                    JobFormHeader = $"Edit Profile - {profile.Name}";
                    JobSaveButtonText = "Update profile";
                    IsEditingJobVisibility = System.Windows.Visibility.Visible;

                    var runs = _repo.GetSyncRunsForCompany(id, 6);
                    SelectedCompanyRecentRuns.Clear();
                    foreach (var r in runs) SelectedCompanyRecentRuns.Add(r);
                }
            }
        }

        public void GoBack()
        {
            if (RouteStack.Count > 1)
            {
                RouteStack.Pop();
                CurrentRoute = RouteStack.Peek();
            }
        }

        public void LoadConfiguration()
        {
            DatabaseProfiles.Clear();
            Companies.Clear();
            RunHistory.Clear();

            var settings = _repo.GetTallySettings();
            TallyServer = settings.Server;
            TallyPort = settings.Port;
            TallyExePath = settings.TallyExePath ?? string.Empty;
            TallyIniPath = settings.TallyIniPath ?? string.Empty;
            AutoStartTally = settings.AutoStartTally == 1;

            var profiles = _repo.GetAllDatabaseProfiles();
            foreach (var profile in profiles) DatabaseProfiles.Add(profile);

            var companyProfiles = _repo.GetAllCompanyProfiles();
            foreach (var company in companyProfiles) Companies.Add(company);

            var runs = _repo.GetRecentSyncRuns(50);
            foreach (var run in runs) RunHistory.Add(run);
        }

        private bool GuardEngineRunning(string operation)
        {
            if (IsSyncRunning)
            {
                _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss} [guard] {operation} skipped — engine running{Environment.NewLine}");
                ShowToast("Engine is running", "Stop the engine to save changes.", "warn");
                return true;
            }
            return false;
        }

        public void SaveTallySettings()
        {
            if (GuardEngineRunning("SaveTallySettings")) return;
            var settings = new TallySettings
            {
                Server = TallyServer,
                Port = TallyPort,
                TallyExePath = TallyExePath,
                TallyIniPath = TallyIniPath,
                AutoStartTally = AutoStartTally ? 1 : 0
            };
            _repo.SaveTallySettings(settings);
            ShowToast("Saved", "Tally connection settings updated.", "ok");
        }

        private void StartEditingCompany(object? parameter)
        {
            int id = 0;
            if (parameter is int intId) id = intId;
            else if (parameter is string strId && int.TryParse(strId, out var parsedId)) id = parsedId;
            
            Navigate(RouteScreen.CompanyProfile, id == 0 ? null : id);
        }

        private void SaveCompanyProfile()
        {
            if (GuardEngineRunning("SaveCompanyProfile")) return;
            if (string.IsNullOrWhiteSpace(JobCompany))
            {
                ShowToast("Validation Error", "Company name is required.", "err");
                return;
            }
            if (JobSelectedProfile == null)
            {
                ShowToast("Validation Error", "Database profile is required.", "err");
                return;
            }
            if (string.IsNullOrWhiteSpace(JobTargetCatalog))
            {
                ShowToast("Validation Error", "Target catalog name is required.", "err");
                return;
            }
            
            var profile = SelectedCompany ?? new CompanyProfile();
            profile.Name = JobCompany;
            profile.DbProfileId = JobSelectedProfile.Id;
            profile.TargetCatalog = JobTargetCatalog;
            profile.Schema = JobSchema;
            profile.TablePrefix = JobTablePrefix;
            profile.Mode = JobSyncMode;
            profile.IntervalMinutes = JobInterval;
            profile.Enabled = JobEnabled ? 1 : 0;
            profile.NotifyOnError = JobNotifyOnError ? 1 : 0;
            profile.PauseOnTallyClose = JobPauseOnTallyClose ? 1 : 0;
            
            int entityFlags = 0;
            if (JobSyncVouchers) entityFlags |= (int)EntityFlags.Vouchers;
            if (JobSyncLedgers) entityFlags |= (int)EntityFlags.Ledgers;
            if (JobSyncStockItems) entityFlags |= (int)EntityFlags.StockItems;
            if (JobSyncGroups) entityFlags |= (int)EntityFlags.Groups;
            if (JobSyncCostCentres) entityFlags |= (int)EntityFlags.CostCentres;
            if (JobSyncCurrencies) entityFlags |= (int)EntityFlags.Currencies;
            profile.EntityFlags = entityFlags;
            
            _repo.SaveCompanyProfile(profile);
            LoadConfiguration();
            ShowToast("Profile Saved", $"{profile.Name} profile settings updated.", "ok");
            Navigate(RouteScreen.Companies);
        }

        private void DeleteCompanyProfile(object? parameter)
        {
            if (GuardEngineRunning("DeleteCompanyProfile")) return;
            int id = 0;
            if (parameter is int intId) id = intId;
            if (id > 0)
            {
                _repo.DeleteCompanyProfile(id);
                LoadConfiguration();
                ShowToast("Profile Deleted", "Company profile removed successfully.", "ok");
                Navigate(RouteScreen.Companies);
            }
        }

        private void StartEditingDbProfile(object? parameter)
        {
            int id = 0;
            if (parameter is int intId) id = intId;
            else if (parameter is DatabaseProfile dp) id = dp.Id;
            
            var profile = DatabaseProfiles.FirstOrDefault(d => d.Id == id);
            if (profile == null)
            {
                SelectedDatabaseProfile = null;
                DbName = string.Empty;
                DbTech = "postgres";
                DbServer = "localhost";
                DbPort = 5432;
                DbUsername = string.Empty;
                DbPassword = string.Empty;
                
                DbFormHeader = "New Database Connection";
                DbSaveButtonText = "Save profile";
                IsEditingDbProfileVisibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                SelectedDatabaseProfile = profile;
                DbName = profile.Name;
                DbTech = profile.Technology;
                DbServer = profile.Server;
                DbPort = profile.Port;
                DbUsername = profile.Username;
                DbPassword = profile.Password;
                
                DbFormHeader = $"Edit Connection - {profile.Name}";
                DbSaveButtonText = "Update profile";
                IsEditingDbProfileVisibility = System.Windows.Visibility.Visible;
            }
        }

        private void SaveDatabaseProfile()
        {
            if (GuardEngineRunning("SaveDatabaseProfile")) return;
            if (string.IsNullOrWhiteSpace(DbName))
            {
                ShowToast("Validation Error", "Profile name is required.", "err");
                return;
            }
            if (string.IsNullOrWhiteSpace(DbServer))
            {
                ShowToast("Validation Error", "Server address is required.", "err");
                return;
            }
            if (string.IsNullOrWhiteSpace(DbUsername))
            {
                ShowToast("Validation Error", "Username is required.", "err");
                return;
            }
            
            var profile = SelectedDatabaseProfile ?? new DatabaseProfile();
            profile.Name = DbName;
            profile.Technology = DbTech;
            profile.Server = DbServer;
            profile.Port = DbPort;
            profile.Username = DbUsername;
            profile.Password = DbPassword;
            
            _repo.SaveDatabaseProfile(profile);
            LoadConfiguration();
            ShowToast("Profile Saved", $"Database profile '{profile.Name}' updated.", "ok");
            StartEditingDbProfile(null);
        }

        private void DeleteDatabaseProfile(object? parameter)
        {
            if (GuardEngineRunning("DeleteDatabaseProfile")) return;
            int id = 0;
            if (parameter is int intId) id = intId;
            else if (SelectedDatabaseProfile != null) id = SelectedDatabaseProfile.Id;
            
            if (id > 0)
            {
                var profile = DatabaseProfiles.FirstOrDefault(d => d.Id == id);
                if (profile != null && profile.UsedByCount > 0)
                {
                    ShowToast("Cannot Delete", $"This connection is currently used by {profile.UsedByCount} companies.", "err");
                    return;
                }
                _repo.DeleteDatabaseProfile(id);
                LoadConfiguration();
                ShowToast("Profile Deleted", "Database profile removed successfully.", "ok");
                StartEditingDbProfile(null);
            }
        }

        private void TestDatabaseConnection()
        {
            if (GuardEngineRunning("TestDatabaseConnection")) return;
            
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var start = DateTime.Now;
                    bool success = false;
                    if (DbTech.ToLower().Contains("postgres"))
                    {
                        using (var conn = new Npgsql.NpgsqlConnection($"Host={DbServer};Port={DbPort};Username={DbUsername};Password={DbPassword};Database=postgres;Timeout=5"))
                        {
                            conn.Open();
                            success = true;
                        }
                    }
                    else
                    {
                        using (var conn = new Microsoft.Data.SqlClient.SqlConnection($"Server={DbServer},{DbPort};User Id={DbUsername};Password={DbPassword};Database=master;Connect Timeout=5"))
                        {
                            conn.Open();
                            success = true;
                        }
                    }
                    var ms = (int)(DateTime.Now - start).TotalMilliseconds;
                    
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (success)
                            ShowToast("Connection OK", $"{DbName} responded in {ms}ms.", "ok");
                        else
                            ShowToast("Connection failed", "Unknown connection failure.", "err");
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowToast("Connection failed", $"{ex.Message.Substring(0, Math.Min(120, ex.Message.Length))}", "err");
                    });
                }
            });
        }

        private void TestTallyConnection()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var client = new TallyClient(TallyServer, TallyPort);
                    var companies = await client.GetActiveCompaniesAsync();
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowToast("Tally Reachable", $"Active Companies: {companies.Count}", "ok");
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowToast("Tally Unreachable", ex.Message, "err");
                    });
                }
            });
        }

        private void DetectActiveCompanies()
        {
            if (GuardEngineRunning("DetectActiveCompanies")) return;
            
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var client = new TallyClient(TallyServer, TallyPort);
                    var details = await client.FetchActiveCompaniesDetailedAsync();
                    
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        UnlinkedTallyCompanies.Clear();
                        foreach (var info in details)
                        {
                            if (!Companies.Any(c => c.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                UnlinkedTallyCompanies.Add(info);
                            }
                        }
                        
                        if (UnlinkedTallyCompanies.Count == 0)
                        {
                            ShowToast("No active companies", "Open a company in Tally Prime, then try again.", "warn");
                        }
                        else if (UnlinkedTallyCompanies.Count == 1)
                        {
                            var single = UnlinkedTallyCompanies[0];
                            SelectedCompany = new CompanyProfile { Name = single.Name, TallyGuid = single.Guid, BooksFrom = single.BooksFrom, BooksTo = single.BooksTo };
                            JobCompany = single.Name;
                            JobSelectedProfile = DatabaseProfiles.FirstOrDefault();
                            JobTargetCatalog = string.Empty;
                            JobSchema = "public";
                            JobTablePrefix = "tally_";
                            JobSyncMode = "full";
                            JobInterval = 15;
                            JobEnabled = true;
                            
                            JobSyncVouchers = true;
                            JobSyncLedgers = true;
                            JobSyncStockItems = true;
                            JobSyncGroups = true;
                            JobSyncCostCentres = false;
                            JobSyncCurrencies = false;
                            
                            JobFormHeader = "New Sync Profile (Auto-detected)";
                            JobSaveButtonText = "Save profile";
                            IsEditingJobVisibility = System.Windows.Visibility.Collapsed;
                            
                            Navigate(RouteScreen.CompanyProfile);
                            ShowToast("Company linked", $"{single.Name} is now linked.", "ok");
                        }
                        else
                        {
                            if (CompanySelector != null)
                            {
                                var selected = CompanySelector(UnlinkedTallyCompanies.ToList());
                                if (selected != null)
                                {
                                    SelectedCompany = new CompanyProfile { Name = selected.Name, TallyGuid = selected.Guid, BooksFrom = selected.BooksFrom, BooksTo = selected.BooksTo, Consolidated = selected.IsGroup ? 1 : 0 };
                                    JobCompany = selected.Name;
                                    JobSelectedProfile = DatabaseProfiles.FirstOrDefault();
                                    JobTargetCatalog = string.Empty;
                                    JobSchema = "public";
                                    JobTablePrefix = "tally_";
                                    JobSyncMode = "full";
                                    JobInterval = 15;
                                    JobEnabled = true;
                                    
                                    JobSyncVouchers = true;
                                    JobSyncLedgers = true;
                                    JobSyncStockItems = true;
                                    JobSyncGroups = true;
                                    JobSyncCostCentres = false;
                                    JobSyncCurrencies = false;
                                    
                                    JobFormHeader = "New Sync Profile (Auto-detected)";
                                    JobSaveButtonText = "Save profile";
                                    IsEditingJobVisibility = System.Windows.Visibility.Collapsed;
                                    
                                    Navigate(RouteScreen.CompanyProfile);
                                    ShowToast("Company linked", $"{selected.Name} is now linked.", "ok");
                                }
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowToast("Detection Failed", ex.Message, "err");
                    });
                }
            });
        }

        private void TryParseConnectionString(string input)
        {
            try
            {
                if (input.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(input);
                    DbTech = "postgres";
                    DbServer = uri.Host;
                    DbPort = uri.Port > 0 ? uri.Port : 5432;
                    var userInfo = uri.UserInfo.Split(':');
                    if (userInfo.Length > 0) DbUsername = userInfo[0];
                    if (userInfo.Length > 1) DbPassword = userInfo[1];
                    if (uri.AbsolutePath.Length > 1) JobTargetCatalog = uri.AbsolutePath.TrimStart('/');
                    ShowToast("Connection string detected", "Filled 5 fields.", "info");
                }
                else if (input.Contains("Server=") || input.Contains("Host=") || input.Contains("Database=") || input.Contains("Initial Catalog="))
                {
                    var builder = new System.Data.Common.DbConnectionStringBuilder();
                    builder.ConnectionString = input;
                    
                    if (builder.ContainsKey("Server") || builder.ContainsKey("Host") || builder.ContainsKey("Data Source"))
                    {
                        var srv = (builder.ContainsKey("Server") ? builder["Server"] : (builder.ContainsKey("Host") ? builder["Host"] : builder["Data Source"])).ToString() ?? string.Empty;
                        var parts = srv.Split(',');
                        DbServer = parts[0];
                        if (parts.Length > 1 && int.TryParse(parts[1], out var p)) DbPort = p;
                    }
                    if (builder.ContainsKey("Database") || builder.ContainsKey("Initial Catalog"))
                    {
                        JobTargetCatalog = (builder.ContainsKey("Database") ? builder["Database"] : builder["Initial Catalog"]).ToString() ?? string.Empty;
                    }
                    if (builder.ContainsKey("User Id") || builder.ContainsKey("User ID") || builder.ContainsKey("Username") || builder.ContainsKey("Uid"))
                    {
                        DbUsername = (builder.ContainsKey("User Id") ? builder["User Id"] : (builder.ContainsKey("User ID") ? builder["User ID"] : (builder.ContainsKey("Username") ? builder["Username"] : builder["Uid"]))).ToString() ?? string.Empty;
                    }
                    if (builder.ContainsKey("Password") || builder.ContainsKey("Pwd"))
                    {
                        DbPassword = (builder.ContainsKey("Password") ? builder["Password"] : builder["Pwd"]).ToString() ?? string.Empty;
                    }
                    
                    DbTech = input.Contains("postgres", StringComparison.OrdinalIgnoreCase) ? "postgres" : "mssql";
                    ShowToast("Connection string detected", "Filled connection parameters.", "info");
                }
            }
            catch
            {
                ShowToast("Auto-parse Failed", "Could not parse connection string format.", "warn");
            }
        }

        private void ExportLog()
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Log Files (*.log)|*.log|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    FileName = "tally_sync_log.log"
                };
                if (dialog.ShowDialog() == true)
                {
                    File.WriteAllText(dialog.FileName, LogOutput);
                    ShowToast("Export Succeeded", "Log file saved successfully.", "ok");
                }
            }
            catch (Exception ex)
            {
                ShowToast("Export Failed", ex.Message, "err");
            }
        }

        private void ClearLog()
        {
            LogOutput = string.Empty;
            ShowToast("Log Cleared", "Console output buffer cleared.", "info");
        }

        private void FlushLogs(object? sender, EventArgs e)
        {
            if (_logQueue.IsEmpty) return;

            var sb = new StringBuilder();
            while (_logQueue.TryDequeue(out var line))
            {
                sb.Append(line);
            }

            var textToAppend = sb.ToString();
            var newLog = LogOutput + textToAppend;
            var lines = newLog.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 2000)
            {
                newLog = string.Join(Environment.NewLine, lines.Skip(lines.Length - 2000)) + Environment.NewLine;
            }
            LogOutput = newLog;
        }
    }
}
```

---

## Phase 5: Value Converters & Binding Helpers

### Task 5.1: Value Converters Implementation

**Files:**
- Create: `src/TallyDbLoader.Wpf/Converters/RelativeTimeConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/StatusToToneConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/NumberConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/NextRunConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/EngineStateToColorConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/NullToBoolConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/NullToVisibilityConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/CountToVisibilityConverter.cs`
- Create: `src/TallyDbLoader.Wpf/Converters/AddOneConverter.cs`

- [ ] **Step 1: Write RelativeTimeConverter.cs**
```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class RelativeTimeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DateTime dt)
            {
                var span = DateTime.Now - dt;
                if (span.TotalSeconds < 60) return "just now";
                if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
                if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
                if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
                return dt.ToString("yyyy-MM-dd HH:mm");
            }
            return "never";
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Write StatusToToneConverter.cs**
  Return the exact hexadecimal status colors specified in `02-design-tokens.md` using a ConcurrentDictionary cache and freezing the SolidColorBrushes to prevent GC pressure:
```csharp
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TallyDbLoader.Wpf.Converters
{
    public class StatusToToneConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, SolidColorBrush> _brushCache = new ConcurrentDictionary<string, SolidColorBrush>();

        private static SolidColorBrush GetFrozenBrush(string hexColor)
        {
            return _brushCache.GetOrAdd(hexColor, hex =>
            {
                var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
                brush.Freeze();
                return brush;
            });
        }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string status = (value?.ToString() ?? "idle").ToLower();
            if (status == "ok" || status == "success" || status == "healthy" || status == "running")
                return GetFrozenBrush("#16a34a"); // status-ok/running: green
            if (status == "warn" || status == "warning" || status == "paused" || status == "stale")
                return GetFrozenBrush("#d97706"); // status-warn/paused: amber
            if (status == "err" || status == "error" || status == "failed")
                return GetFrozenBrush("#dc2626"); // status-err: red
            return GetFrozenBrush("#888888"); // status-idle: gray
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 3: Write NumberConverter.cs**
```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NumberConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null) return "0";
            if (long.TryParse(value.ToString(), out long val))
            {
                return val.ToString("N0", culture);
            }
            return value.ToString() ?? "0";
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 4: Write NextRunConverter.cs**
```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NextRunConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 3 && values[0] is DateTime lastRun && values[1] is int interval && values[2] is int enabled)
            {
                if (enabled == 0) return "Disabled";
                var next = lastRun.AddMinutes(interval);
                if (next < DateTime.Now) return "Pending";
                return next.ToString("HH:mm:ss");
            }
            return "Pending";
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 5: Write EngineStateToColorConverter.cs**
  Maps `EngineState` to the correct status color for the engine pulse dot. Running = green, Paused = amber, Idle = muted gray.
```csharp
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TallyDbLoader.Wpf.Converters
{
    public class EngineStateToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _runningBrush = new SolidColorBrush(Color.FromRgb(22, 163, 74));
        private static readonly SolidColorBrush _pausedBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6));
        private static readonly SolidColorBrush _idleBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        static EngineStateToColorConverter()
        {
            _runningBrush.Freeze();
            _pausedBrush.Freeze();
            _idleBrush.Freeze();
        }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TallyDbLoader.Wpf.EngineState state)
            {
                return state switch
                {
                    TallyDbLoader.Wpf.EngineState.Running => _runningBrush,
                    TallyDbLoader.Wpf.EngineState.Paused => _pausedBrush,
                    _ => _idleBrush
                };
            }
            return _idleBrush;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 6: Write NullToBoolConverter.cs**
```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString()?.ToLower() == "invert";
            return invert ? value == null : value != null;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 7: Write NullToVisibilityConverter.cs**
```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString() == "invert";
            bool isNull = value == null;
            if (invert)
                return isNull ? Visibility.Visible : Visibility.Collapsed;
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 8: Write CountToVisibilityConverter.cs**
```csharp
using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int count = 0;
            if (value is int c) count = c;
            else if (value is ICollection coll) count = coll.Count;
            else if (value != null && int.TryParse(value.ToString(), out int parsed)) count = parsed;

            bool invert = parameter?.ToString() == "invert";
            if (invert)
                return count > 0 ? Visibility.Collapsed : Visibility.Visible;
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 9: Write AddOneConverter.cs**
```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class AddOneConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int val) return val + 1;
            return 1;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
```

---

### Task 5.2: Binding Helpers

**Files:**
- Create: `src/TallyDbLoader.Wpf/Helpers/PasswordBoxHelper.cs`
- Create: `src/TallyDbLoader.Wpf/Helpers/RichTextBoxHelper.cs`

- [ ] **Step 1: Write PasswordBoxHelper.cs**
  Securely bind `PasswordBox.Password` with safe object comparisons to prevent NullReferenceException.

```csharp
using System.Windows;
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Helpers
{
    public static class PasswordBoxHelper
    {
        public static readonly DependencyProperty BoundPasswordProperty =
            DependencyProperty.RegisterAttached("BoundPassword", typeof(string), typeof(PasswordBoxHelper),
                new FrameworkPropertyMetadata(string.Empty, OnBoundPasswordChanged));

        public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);
        public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

        public static readonly DependencyProperty BindBehaviorProperty =
            DependencyProperty.RegisterAttached("BindBehavior", typeof(bool), typeof(PasswordBoxHelper),
                new PropertyMetadata(false, OnBindBehaviorChanged));

        public static bool GetBindBehavior(DependencyObject d) => (bool)d.GetValue(BindBehaviorProperty);
        public static void SetBindBehavior(DependencyObject d, bool value) => d.SetValue(BindBehaviorProperty, value);

        private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PasswordBox box)
            {
                box.PasswordChanged -= HandlePasswordChanged;
                if (e.NewValue as string != box.Password)
                {
                    box.Password = (e.NewValue as string) ?? string.Empty;
                }
                box.PasswordChanged += HandlePasswordChanged;
            }
        }

        private static void OnBindBehaviorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PasswordBox box)
            {
                if ((bool)e.NewValue)
                {
                    box.PasswordChanged += HandlePasswordChanged;
                }
                else
                {
                    box.PasswordChanged -= HandlePasswordChanged;
                }
            }
        }

        private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box)
            {
                SetBoundPassword(box, box.Password);
            }
        }
    }
}
```

- [ ] **Step 2: Write RichTextBoxHelper.cs**
  Avoid recreating FlowDocuments on every keystroke by updating block contents incrementally.

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TallyDbLoader.Wpf.Helpers
{
    public static class RichTextBoxHelper
    {
        public static readonly DependencyProperty LogTextProperty =
            DependencyProperty.RegisterAttached("LogText", typeof(string), typeof(RichTextBoxHelper),
                new FrameworkPropertyMetadata(string.Empty, OnLogTextChanged));

        public static string GetLogText(DependencyObject d) => (string)d.GetValue(LogTextProperty);
        public static void SetLogText(DependencyObject d, string value) => d.SetValue(LogTextProperty, value);

        private static readonly DependencyProperty LastTextLengthProperty =
            DependencyProperty.RegisterAttached("LastTextLength", typeof(int), typeof(RichTextBoxHelper),
                new PropertyMetadata(0));

        public static int GetLastTextLength(DependencyObject d) => (int)d.GetValue(LastTextLengthProperty);
        public static void SetLastTextLength(DependencyObject d, int value) => d.SetValue(LastTextLengthProperty, value);

        private static void OnLogTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RichTextBox box)
            {
                var text = e.NewValue as string ?? string.Empty;
                if (box.Document == null)
                {
                    box.Document = new FlowDocument();
                }

                int lastLength = GetLastTextLength(box);

                // If text was cleared or is shorter, clear document and start fresh
                if (text.Length < lastLength || lastLength == 0)
                {
                    box.Document.Blocks.Clear();
                    lastLength = 0;
                }

                if (text.Length > lastLength)
                {
                    string newText = text.Substring(lastLength);
                    var lines = newText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    
                    Paragraph? p = box.Document.Blocks.LastBlock as Paragraph;
                    if (p == null)
                    {
                        p = new Paragraph();
                        box.Document.Blocks.Add(p);
                    }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (i > 0)
                        {
                            p.Inlines.Add(new LineBreak());
                        }

                        if (string.IsNullOrEmpty(line))
                            continue;

                        var run = new Run(line);
                        if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
                            run.Foreground = Brushes.Crimson;
                        else if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
                            run.Foreground = Brushes.Goldenrod;
                        else
                            run.Foreground = Brushes.LightGray;

                        p.Inlines.Add(run);
                    }

                    SetLastTextLength(box, text.Length);
                    box.ScrollToEnd();
                }
            }
        }
    }
}
```

---

## Phase 6: Shell & MainWindow Redesign

### Task 6.1: Rewrite MainWindow Layout

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml`
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml.cs`

- [ ] **Step 1: Rewrite MainWindow.xaml shell structure**
  Implement the standard WinUI-style layout featuring z-order layered Toast popups.

```xml
<Window x:Class="TallyDbLoader.Wpf.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:TallyDbLoader.Wpf"
        Title="Tally to Database Loader" Height="700" Width="1100"
        Background="{DynamicResource BackgroundBrush}"
        Foreground="{DynamicResource ForegroundBrush}">
    
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition>
                <ColumnDefinition.Style>
                    <Style TargetType="ColumnDefinition">
                        <Setter Property="Width" Value="240"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding CurrentRoute.Screen}" Value="{x:Static local:RouteScreen.Wizard}">
                                <Setter Property="Width" Value="0"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </ColumnDefinition.Style>
            </ColumnDefinition>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- Navigation Drawer Rail -->
        <Border Grid.Column="0" Background="{DynamicResource DrawerBackgroundBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,1,0">
            <Border.Style>
                <Style TargetType="Border">
                    <Setter Property="Visibility" Value="Visible"/>
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding CurrentRoute.Screen}" Value="{x:Static local:RouteScreen.Wizard}">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Border.Style>
            <Grid Margin="10,20,10,20">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <TextBlock Text="Tally Db Loader" Style="{DynamicResource DisplayTextStyle}" FontSize="20" Margin="10,0,0,20"/>

                <StackPanel Grid.Row="1">
                    <Button Content="Dashboard" Style="{StaticResource NavButtonStyle}" Command="{Binding NavigateCommand}" CommandParameter="Dashboard"/>
                    <Button Content="Companies" Style="{StaticResource NavButtonStyle}" Command="{Binding NavigateCommand}" CommandParameter="Companies"/>
                    <Button Content="Database Profiles" Style="{StaticResource NavButtonStyle}" Command="{Binding NavigateCommand}" CommandParameter="Databases"/>
                    <Button Content="Execution Log" Style="{StaticResource NavButtonStyle}" Command="{Binding NavigateCommand}" CommandParameter="Log"/>
                    <Button Content="Run History" Style="{StaticResource NavButtonStyle}" Command="{Binding NavigateCommand}" CommandParameter="History"/>
                </StackPanel>

                <!-- Engine Pulse Dot inside Drawer Footer -->
                <StackPanel Grid.Row="2" Margin="10">
                    <Border CornerRadius="8" Background="{DynamicResource LayerBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" Padding="10">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>
                            <Ellipse Width="10" Height="10" Fill="{Binding State, Converter={StaticResource EngineStateToColorConverter}}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <TextBlock Grid.Column="1" Text="{Binding StateText}" Style="{DynamicResource BodyStrongTextStyle}" VerticalAlignment="Center"/>
                        </Grid>
                    </Border>
                    <Button Content="Settings" Style="{StaticResource NavButtonStyle}" Command="{Binding NavigateCommand}" CommandParameter="Settings" Margin="0,10,0,0"/>
                </StackPanel>
            </Grid>
        </Border>

        <!-- Frame view container -->
        <Frame x:Name="NavigationFrame" Grid.Column="1" JournalOwnership="OwnsJournal" NavigationUIVisibility="Hidden"/>

        <!-- Toast Notification Overlay Stack -->
        <ItemsControl Grid.Column="1" ItemsSource="{Binding Toasts}" VerticalAlignment="Bottom" HorizontalAlignment="Right" Margin="0,0,18,32" Panel.ZIndex="99">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Border Background="{DynamicResource LayerBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="6" Width="280" Margin="0,8,0,0" Padding="12">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="6"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Background="{Binding Kind, Converter={StaticResource StatusToToneConverter}}" CornerRadius="3" Width="4"/>
                            <StackPanel Grid.Column="1" Margin="8,0,0,0">
                                <TextBlock Text="{Binding Title}" Style="{DynamicResource SubtitleTextStyle}"/>
                                <TextBlock Text="{Binding Body}" Style="{DynamicResource CaptionTextStyle}" TextWrapping="Wrap" Margin="0,2,0,0"/>
                            </StackPanel>
                        </Grid>
                    </Border>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </Grid>
</Window>
```

- [ ] **Step 2: Update MainWindow.xaml.cs route handling**
  Handle routing transitions when `CurrentRoute` changes in the view model, and explicitly inject the view model DataContext.

```csharp
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TallyDbLoader.Wpf.Views;

namespace TallyDbLoader.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private TrayController? _trayController;
        private bool _isExiting = false;

        public MainWindow()
        {
            InitializeComponent();

            // Instantiate VM directly — do NOT rely on DataContext being set by XAML
            _vm = new MainViewModel("config.db");
            DataContext = _vm;

            _vm.PropertyChanged += OnVmPropertyChanged;

            // Setup tray controller and company picker callback
            _trayController = new TrayController(this);
            _vm.CompanySelector = (companies) =>
            {
                var dialog = new CompanySelectionWindow(companies);
                dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    return dialog.SelectedCompany;
                }
                return null;
            };

            // Session ending handler
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.SessionEnding += App_SessionEnding;
            }

            NavigateToRoute(_vm.CurrentRoute);
        }

        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            _isExiting = true;
            _vm.Dispose();
            _trayController?.Dispose();
        }

        public void ExitApplication()
        {
            _isExiting = true;
            _vm.Dispose();
            _trayController?.Dispose();
            System.Windows.Application.Current?.Shutdown();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
            base.OnStateChanged(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
                _trayController?.ShowNotification("Minimized", "The Tally loader utility is running in the background.");
            }
            base.OnClosing(e);
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_vm.CurrentRoute))
            {
                NavigateToRoute(_vm.CurrentRoute);
            }
        }

        private void NavigateToRoute(NavigationRoute route)
        {
            var frame = (Frame)FindName("NavigationFrame");
            if (frame == null) return;

            Page? page = null;
            switch (route.Screen)
            {
                case RouteScreen.Dashboard:
                    page = new DashboardPage();
                    break;
                case RouteScreen.Companies:
                    page = new CompaniesPage();
                    break;
                case RouteScreen.CompanyProfile:
                    page = new CompanyProfilePage(route.ParameterId ?? 0);
                    break;
                case RouteScreen.Databases:
                    page = new DatabasesPage();
                    break;
                case RouteScreen.Log:
                    page = new LogPage();
                    break;
                case RouteScreen.History:
                    page = new HistoryPage();
                    break;
                case RouteScreen.Settings:
                    page = new SettingsPage();
                    break;
                case RouteScreen.Wizard:
                    page = new SetupWizardPage();
                    break;
            }

            if (page != null)
            {
                page.DataContext = _vm;
                frame.Navigate(page);
            }
        }
    }
}
```

---

## Phase 7: Page View Layouts

### Task 7.1: Implement WinUI-Styled Page Templates

**Files:**
- Create: `src/TallyDbLoader.Wpf/Views/DashboardPage.xaml` / `.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/Views/CompaniesPage.xaml` / `.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/Views/CompanyProfilePage.xaml` / `.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/Views/DatabasesPage.xaml` / `.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/Views/LogPage.xaml` / `.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/Views/HistoryPage.xaml` / `.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/Views/SettingsPage.xaml` / `.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/Views/SetupWizardPage.xaml` / `.xaml.cs`

- [ ] **Step 1: Write DashboardPage.xaml UI**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.DashboardPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      DataContext="{Binding}"
      Title="Dashboard">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Page Header -->
        <StackPanel Grid.Row="0" Margin="0,0,0,16">
            <TextBlock Text="Dashboard" Style="{StaticResource DisplayTextStyle}"/>
            <TextBlock Text="Monitor synchronization profiles and active sync jobs." Style="{StaticResource CaptionTextStyle}"/>
        </StackPanel>

        <!-- CommandBar -->
        <Border Grid.Row="1" Background="{DynamicResource Layer2Brush}" Height="54" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4" Margin="0,0,0,16" Padding="8,4">
            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                <Button Content="Start All" Command="{Binding StartSyncEngineCommand}" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"/>
                <Button Content="Stop Engine" Command="{Binding StopSyncEngineCommand}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0"/>
                <Button Content="Pause" Command="{Binding PauseSyncEngineCommand}" Style="{StaticResource StandardButtonStyle}"/>
            </StackPanel>
        </Border>

        <!-- Cards Grid list -->
        <ListView Grid.Row="2" ItemsSource="{Binding Companies}" BorderThickness="0" Background="Transparent"
                  Visibility="{Binding Companies.Count, Converter={StaticResource CountToVisibilityConverter}}">
            <ListView.ItemTemplate>
                <DataTemplate>
                    <Border Style="{StaticResource FluentCardStyle}" Margin="0,4,0,4">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <StackPanel>
                                <TextBlock Text="{Binding Name}" Style="{StaticResource SubtitleTextStyle}"/>
                                <TextBlock Text="{Binding TargetCatalog}" Style="{StaticResource CaptionTextStyle}"/>
                                <TextBlock Text="{Binding Status}" Foreground="{Binding Status, Converter={StaticResource StatusToToneConverter}}" Style="{StaticResource BodyStrongTextStyle}" Margin="0,4,0,0"/>
                            </StackPanel>
                            <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                                <Button Content="Run Now" Command="{Binding DataContext.RunCompanyCommand, RelativeSource={RelativeSource AncestorType=Page}}" CommandParameter="{Binding Id}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0"/>
                                <Button Content="Edit" Command="{Binding DataContext.StartEditingCompanyCommand, RelativeSource={RelativeSource AncestorType=Page}}" CommandParameter="{Binding Id}" Style="{StaticResource StandardButtonStyle}"/>
                            </StackPanel>
                        </Grid>
                    </Border>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>

        <!-- Empty State (shown when no companies exist) -->
        <Border Grid.Row="2" Style="{StaticResource FluentCardStyle}" HorizontalAlignment="Center" VerticalAlignment="Center" Width="400"
                Visibility="{Binding Companies.Count, Converter={StaticResource CountToVisibilityConverter}, ConverterParameter='invert'}">
            <StackPanel HorizontalAlignment="Center" Margin="32">
                <TextBlock Text="No companies linked yet" Style="{StaticResource SubtitleTextStyle}" FontSize="18" HorizontalAlignment="Center" Margin="0,0,0,8"/>
                <TextBlock Text="Open a company in Tally Prime, then click Detect to link it and start syncing." Style="{StaticResource CaptionTextStyle}" TextAlignment="Center" TextWrapping="Wrap" Margin="0,0,0,20"/>
                <Button Content="Detect from Tally" Command="{Binding DetectActiveCompaniesCommand}" Style="{StaticResource PrimaryButtonStyle}" HorizontalAlignment="Center" Width="200"/>
            </StackPanel>
        </Border>
    </Grid>
</Page>
```
Code-behind for `DashboardPage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class DashboardPage : Page
    {
        public DashboardPage()
        {
            InitializeComponent();
        }
    }
}
```

- [ ] **Step 2: Write CompaniesPage.xaml UI & Code-Behind**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.CompaniesPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      DataContext="{Binding}"
      Title="Companies Sync Profiles">

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Page Header -->
        <Grid Grid.Row="0" Margin="0,0,0,16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel>
                <TextBlock Text="Companies Sync Schedules" Style="{StaticResource DisplayTextStyle}"/>
                <TextBlock Text="Each company has exactly one synchronization profile." Style="{StaticResource CaptionTextStyle}"/>
            </StackPanel>
            <Button Grid.Column="1" Content="Detect Active Companies" Command="{Binding DetectActiveCompaniesCommand}" Style="{StaticResource PrimaryButtonStyle}" VerticalAlignment="Center"/>
        </Grid>

        <!-- CommandBar -->
        <Border Grid.Row="1" Background="{DynamicResource Layer2Brush}" Height="54" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4" Margin="0,0,0,16" Padding="8,4">
            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                <Button Content="New Company Profile" Command="{Binding StartEditingCompanyCommand}" CommandParameter="0" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"/>
                <Button Content="Configure Selected" Command="{Binding StartEditingCompanyCommand}" CommandParameter="{Binding SelectedCompany.Id}" IsEnabled="{Binding SelectedCompany, Converter={StaticResource NullToBoolConverter}}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0"/>
                <Button Content="Delete Selected" Command="{Binding DeleteCompanyProfileCommand}" CommandParameter="{Binding SelectedCompany.Id}" IsEnabled="{Binding SelectedCompany, Converter={StaticResource NullToBoolConverter}}" Style="{StaticResource StandardButtonStyle}" Foreground="#EF4444"/>
            </StackPanel>
        </Border>

        <!-- Main Profiles Grid -->
        <DataGrid Grid.Row="2" x:Name="CompaniesGrid" ItemsSource="{Binding Companies}" SelectedItem="{Binding SelectedCompany, Mode=TwoWay}"
                  AutoGenerateColumns="False" IsReadOnly="True" GridLinesVisibility="None" BorderThickness="0" Background="Transparent">
            <DataGrid.Resources>
                <Style TargetType="DataGridRow">
                    <EventSetter Event="MouseDoubleClick" Handler="Row_DoubleClick"/>
                </Style>
            </DataGrid.Resources>
            <DataGrid.Columns>
                <DataGridTemplateColumn Header="Company Profile" Width="2*">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <StackPanel Margin="4">
                                <TextBlock Text="{Binding Name}" FontWeight="Bold" FontSize="13"/>
                                <TextBlock Text="{Binding TargetCatalog, StringFormat='Catalog: {0}'}" FontSize="10" FontFamily="Cascadia Mono" Foreground="{DynamicResource MutedTextBrush}"/>
                            </StackPanel>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>

                <DataGridTextColumn Header="DB Target" Binding="{Binding Db.Name}" Width="1.2*"/>
                <DataGridTextColumn Header="Sync Mode" Binding="{Binding Mode}" Width="0.7*"/>
                <DataGridTextColumn Header="Interval" Binding="{Binding IntervalMinutes, StringFormat='{}{0} min'}" Width="0.7*"/>
                
                <DataGridTemplateColumn Header="Status" Width="0.9*">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <Border Background="{Binding Status, Converter={StaticResource StatusToToneConverter}}" CornerRadius="4" Padding="6,2" HorizontalAlignment="Left">
                                <TextBlock Text="{Binding Status}" Foreground="White" FontSize="10" FontWeight="SemiBold" TextTransform="Uppercase"/>
                            </Border>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
            </DataGrid.Columns>
        </DataGrid>

        <!-- Unlinked Tally Companies Hint Card -->
        <Border Grid.Row="3" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="6" Background="{DynamicResource LayerBrush}" Padding="16" Margin="0,16,0,0"
                Visibility="{Binding UnlinkedTallyCompanies.Count, Converter={StaticResource CountToVisibilityConverter}, ConverterParameter='invert'}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <StackPanel>
                    <TextBlock Text="Unlinked Active Companies Detected in Tally" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,4"/>
                    <TextBlock Text="The following companies are open in Tally Prime but do not have database sync profiles configured:" Style="{StaticResource CaptionTextStyle}"/>
                    
                    <ItemsControl ItemsSource="{Binding UnlinkedTallyCompanies}" Margin="0,8,0,0">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Name, StringFormat='• {0}'}" Style="{StaticResource BodyTextStyle}" FontSize="12" Margin="0,2,0,2"/>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>
                <Button Grid.Column="1" Content="Configure them now →" Command="{Binding DetectActiveCompaniesCommand}" Style="{StaticResource PrimaryButtonStyle}" VerticalAlignment="Center"/>
            </Grid>
        </Border>
    </Grid>
</Page>
```
Code-behind for `CompaniesPage.xaml.cs`:
```csharp
using System.Windows.Controls;
using System.Windows.Input;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Wpf.Views
{
    public partial class CompaniesPage : Page
    {
        public CompaniesPage() => InitializeComponent();

        private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is CompanyProfile profile)
            {
                var vm = (MainViewModel)this.DataContext;
                vm.StartEditingCompanyCommand.Execute(profile.Id);
            }
        }
    }
}
```

- [ ] **Step 3: Write CompanyProfilePage.xaml UI & Code-Behind**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.CompanyProfilePage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      DataContext="{Binding}"
      Title="Company Profile Details">

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/> <!-- Breadcrumb -->
            <RowDefinition Height="Auto"/> <!-- Page Header -->
            <RowDefinition Height="*"/>    <!-- Split Content -->
            <RowDefinition Height="Auto"/> <!-- Action Footer -->
        </Grid.RowDefinitions>

        <!-- Breadcrumbs -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,8">
            <Button Content="Dashboard" Command="{Binding NavigateCommand}" CommandParameter="Dashboard" Style="{StaticResource HyperlinkButtonStyle}"/>
            <TextBlock Text="  /  " Style="{StaticResource CaptionMuteTextStyle}" VerticalAlignment="Center"/>
            <Button Content="Companies" Command="{Binding NavigateCommand}" CommandParameter="Companies" Style="{StaticResource HyperlinkButtonStyle}"/>
            <TextBlock Text="  /  " Style="{StaticResource CaptionMuteTextStyle}" VerticalAlignment="Center"/>
            <TextBlock Text="Profile Details" Style="{StaticResource CaptionMuteTextStyle}" VerticalAlignment="Center"/>
        </StackPanel>

        <!-- Page Header -->
        <Grid Grid.Row="1" Margin="0,0,0,20">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel Grid.Column="0">
                <TextBlock Text="{Binding JobCompany, FallbackValue='Edit Sync Profile'}" Style="{StaticResource DisplayTextStyle}"/>
                <TextBlock Text="Configure extraction boundaries, database mappings, and tables to sync." Style="{StaticResource CaptionTextStyle}"/>
            </StackPanel>
            
            <!-- Edits Locked Pill -->
            <Border Grid.Column="1" CornerRadius="12" Background="#FFFBEB" BorderBrush="#FCD34D" BorderThickness="1" Padding="8,4" VerticalAlignment="Center" Margin="8,0,0,0" Visibility="{Binding IsSyncRunning, Converter={StaticResource BooleanToVisibilityConverter}}">
                <TextBlock Text="Engine running — edits locked" Foreground="#B45309" FontSize="11" FontWeight="SemiBold" VerticalAlignment="Center"/>
            </Border>
        </Grid>

        <!-- Content Area -->
        <Grid Grid.Row="2">
            <Grid.Style>
                <Style TargetType="Grid">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                            <Setter Property="Opacity" Value="0.94"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Grid.Style>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- Left Column: Source and Target Configuration -->
            <ScrollViewer Grid.Column="0" VerticalScrollBarVisibility="Auto" Margin="0,0,12,0">
                <StackPanel>
                    <!-- Source Details Card -->
                    <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,16">
                        <StackPanel>
                            <TextBlock Text="Source Details (Tally Prime)" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>
                            
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>

                                <StackPanel Grid.Row="0" Grid.Column="0" Margin="0,0,0,10">
                                    <TextBlock Text="Company Name" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding JobCompany}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>
                                
                                <StackPanel Grid.Row="0" Grid.Column="1" Margin="0,0,0,10">
                                    <TextBlock Text="Type" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="Consolidated / Group" Style="{StaticResource BodyStrongTextStyle}" Visibility="{Binding SelectedCompany.Consolidated, Converter={StaticResource CountToVisibilityConverter}}"/>
                                    <TextBlock Text="Single Company" Style="{StaticResource BodyStrongTextStyle}" Visibility="{Binding SelectedCompany.Consolidated, Converter={StaticResource CountToVisibilityConverter}, ConverterParameter='invert'}"/>
                                </StackPanel>

                                <StackPanel Grid.Row="1" Grid.Column="0" Margin="0,0,0,10">
                                    <TextBlock Text="Books From" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedCompany.BooksFrom, StringFormat='{}{0:dd-MMM-yyyy}', FallbackValue='N/A'}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>

                                <StackPanel Grid.Row="1" Grid.Column="1" Margin="0,0,0,10">
                                    <TextBlock Text="Books To" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedCompany.BooksTo, StringFormat='{}{0:dd-MMM-yyyy}', FallbackValue='N/A'}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>

                                <StackPanel Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="2">
                                    <TextBlock Text="Tally GUID" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedCompany.TallyGuid, FallbackValue='N/A'}" Style="{StaticResource BodyStrongTextStyle}" FontFamily="Cascadia Mono" FontSize="11"/>
                                </StackPanel>
                            </Grid>
                        </StackPanel>
                    </Border>

                    <!-- Target Configuration Card -->
                    <Border Style="{StaticResource FluentCardStyle}">
                        <StackPanel>
                            <TextBlock Text="Destination Details (SQL Database)" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>

                            <TextBlock Text="Database Target Connection Profile" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                            <ComboBox ItemsSource="{Binding DatabaseProfiles}" SelectedItem="{Binding JobSelectedProfile, Mode=TwoWay}" DisplayMemberPath="Name" 
                                      Margin="0,0,0,12"/>

                            <TextBlock Text="Target Catalog (Database Name)" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                            <TextBox Text="{Binding JobTargetCatalog, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,12"/>

                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <StackPanel Margin="0,0,8,0">
                                    <TextBlock Text="Schema Name" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                    <TextBox Text="{Binding JobSchema, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}"/>
                                </StackPanel>
                                <StackPanel Margin="8,0,0,0">
                                    <TextBlock Text="Table Prefix" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                    <TextBox Text="{Binding JobTablePrefix, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}"/>
                                </StackPanel>
                            </Grid>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </ScrollViewer>

            <!-- Right Column: Schedule and Entities -->
            <ScrollViewer Grid.Column="1" VerticalScrollBarVisibility="Auto" Margin="12,0,0,0">
                <StackPanel>
                    <!-- Schedule Mappings Card -->
                    <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,16">
                        <StackPanel>
                            <TextBlock Text="Sync Schedule Configuration" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>

                            <TextBlock Text="Sync Extraction Mode" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                            <ComboBox SelectedValue="{Binding JobSyncMode, Mode=TwoWay}" SelectedValuePath="Tag" Margin="0,0,0,12">
                                <ComboBoxItem Content="Full Extraction (All rows)" Tag="full"/>
                                <ComboBoxItem Content="Incremental (Based on AlterID)" Tag="incremental"/>
                            </ComboBox>

                            <TextBlock Text="Sync Interval (Minutes)" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                            <TextBox Text="{Binding JobInterval, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,16"/>

                            <CheckBox Content="Synchronize Automatically (Schedule Enabled)" IsChecked="{Binding JobEnabled, Mode=TwoWay}" Margin="0,0,0,8"/>
                            <CheckBox Content="Notify on Errors via Toast" IsChecked="{Binding JobNotifyOnError, Mode=TwoWay}" Margin="0,0,0,8"/>
                            <CheckBox Content="Pause Synchronization when Tally Prime closes" IsChecked="{Binding JobPauseOnTallyClose, Mode=TwoWay}"/>
                        </StackPanel>
                    </Border>

                    <!-- Entities Synced Card -->
                    <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,16">
                        <StackPanel>
                            <TextBlock Text="Select Tables to Sync" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>
                            
                            <UniformGrid Columns="2">
                                <CheckBox Content="Vouchers" IsChecked="{Binding JobSyncVouchers, Mode=TwoWay}" Margin="0,0,0,10"/>
                                <CheckBox Content="Ledgers" IsChecked="{Binding JobSyncLedgers, Mode=TwoWay}" Margin="0,0,0,10"/>
                                <CheckBox Content="Stock Items" IsChecked="{Binding JobSyncStockItems, Mode=TwoWay}" Margin="0,0,0,10"/>
                                <CheckBox Content="Groups" IsChecked="{Binding JobSyncGroups, Mode=TwoWay}" Margin="0,0,0,10"/>
                                <CheckBox Content="Cost Centres" IsChecked="{Binding JobSyncCostCentres, Mode=TwoWay}"/>
                                <CheckBox Content="Currencies" IsChecked="{Binding JobSyncCurrencies, Mode=TwoWay}"/>
                            </UniformGrid>
                        </StackPanel>
                    </Border>

                    <!-- Recent Runs Card -->
                    <Border Style="{StaticResource FluentCardStyle}">
                        <StackPanel>
                            <TextBlock Text="Recent Runs History" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,8"/>
                            <ItemsControl ItemsSource="{Binding SelectedCompanyRecentRuns}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Border BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1" Padding="0,6" Margin="0,2">
                                            <Grid>
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="Auto"/>
                                                    <ColumnDefinition Width="*"/>
                                                    <ColumnDefinition Width="Auto"/>
                                                </Grid.ColumnDefinitions>
                                                <Border Grid.Column="0" Background="{Binding Status, Converter={StaticResource StatusToToneConverter}}" CornerRadius="3" Padding="4,1" Margin="0,0,8,0" VerticalAlignment="Center">
                                                    <TextBlock Text="{Binding Status}" Foreground="White" FontSize="9" FontWeight="Bold" TextTransform="Uppercase"/>
                                                </Border>
                                                <StackPanel Grid.Column="1">
                                                    <TextBlock Text="{Binding StartedAt, StringFormat='{}{0:dd-MMM HH:mm}'}" Style="{StaticResource BodyTextStyle}" FontSize="12"/>
                                                    <TextBlock Text="{Binding ResultSummary}" Style="{StaticResource CaptionMuteTextStyle}" FontSize="10"/>
                                                </StackPanel>
                                                <TextBlock Grid.Column="2" Text="{Binding Duration, StringFormat='{}{0:mm\\:ss}'}" FontFamily="Cascadia Mono" FontSize="11" VerticalAlignment="Center"/>
                                            </Grid>
                                        </Border>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </ScrollViewer>
        </Grid>

        <!-- Action Footer -->
        <Border Grid.Row="3" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0" Padding="0,16,0,0" Margin="0,16,0,0">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                
                <Button Grid.Column="0" Content="Delete Profile" Command="{Binding DeleteCompanyProfileCommand}" CommandParameter="{Binding SelectedCompany.Id}" Foreground="#EF4444">
                    <Button.Style>
                        <Style TargetType="Button" BasedOn="{StaticResource StandardButtonStyle}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                                    <Setter Property="ToolTip" Value="Stop the engine to save changes."/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Button.Style>
                </Button>
                <StackPanel Grid.Column="2" Orientation="Horizontal">
                    <Button Content="Cancel" Command="{Binding CancelJobEditCommand}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0"/>
                    <Button Content="Save Sync Profile" Command="{Binding SaveCompanyProfileCommand}">
                        <Button.Style>
                            <Style TargetType="Button" BasedOn="{StaticResource PrimaryButtonStyle}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                                        <Setter Property="ToolTip" Value="Stop the engine to save changes."/>
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Button.Style>
                    </Button>
                </StackPanel>
            </Grid>
        </Border>
    </Grid>
</Page>
```
Code-behind for `CompanyProfilePage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class CompanyProfilePage : Page
    {
        public CompanyProfilePage(int companyId)
        {
            InitializeComponent();
        }
    }
}
```

- [ ] **Step 4: Write DatabasesPage.xaml UI & Code-Behind**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.DatabasesPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:helpers="clr-namespace:TallyDbLoader.Wpf.Helpers"
      DataContext="{Binding}"
      Title="Database Connections">

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Grid Grid.Row="0" Margin="0,0,0,16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel Grid.Column="0">
                <TextBlock Text="Database Connections" Style="{StaticResource DisplayTextStyle}"/>
                <TextBlock Text="Manage destinations where synchronized financial data is populated." Style="{StaticResource CaptionTextStyle}"/>
            </StackPanel>
            
            <!-- Edits Locked Pill -->
            <Border Grid.Column="1" CornerRadius="12" Background="#FFFBEB" BorderBrush="#FCD34D" BorderThickness="1" Padding="8,4" VerticalAlignment="Center" Margin="0,0,8,0" Visibility="{Binding IsSyncRunning, Converter={StaticResource BooleanToVisibilityConverter}}">
                <TextBlock Text="Engine running — edits locked" Foreground="#B45309" FontSize="11" FontWeight="SemiBold" VerticalAlignment="Center"/>
            </Border>
            <Button Grid.Column="2" Content="Add New Connection" Command="{Binding StartEditingDbProfileCommand}" CommandParameter="0" Style="{StaticResource PrimaryButtonStyle}" VerticalAlignment="Center"/>
        </Grid>

        <!-- Master-Detail 2-Pane Content -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="320"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- Connection List (Left - Master) -->
            <Border Grid.Column="0" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,1,0" Padding="0,0,16,0" Margin="0,0,16,0">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    
                    <TextBlock Grid.Row="0" Text="Saved Profiles" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,8"/>
                    
                    <ListBox Grid.Row="1" ItemsSource="{Binding DatabaseProfiles}" SelectedItem="{Binding SelectedDatabaseProfile, Mode=TwoWay}"
                             Background="Transparent" BorderThickness="0">
                        <ListBox.ItemTemplate>
                            <DataTemplate>
                                <Border Padding="8" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1" Margin="0,2">
                                    <StackPanel>
                                        <TextBlock Text="{Binding Name}" FontWeight="Bold" FontSize="13"/>
                                        <TextBlock Text="{Binding Server, StringFormat='Host: {0}'}" FontSize="11" Foreground="{DynamicResource MutedTextBrush}"/>
                                        <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                                            <Border Background="{DynamicResource Layer2Brush}" CornerRadius="3" Padding="4,1" Margin="0,0,6,0">
                                                <TextBlock Text="{Binding Technology}" FontSize="9" TextTransform="Uppercase"/>
                                            </Border>
                                            <TextBlock Text="{Binding UsedByCount, StringFormat='{}{0} linked syncs'}" FontSize="10" Foreground="{DynamicResource MutedTextBrush}" VerticalAlignment="Center"/>
                                        </StackPanel>
                                    </StackPanel>
                                </Border>
                            </DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
                </Grid>
            </Border>

            <!-- Editor Pane (Right - Detail) -->
            <Grid Grid.Column="1">
                <Grid.Style>
                    <Style TargetType="Grid">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                                <Setter Property="Opacity" Value="0.94"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Grid.Style>
                <Grid.RowDefinitions>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto">
                    <StackPanel>
                        <!-- Main Fields Card -->
                        <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,16">
                            <StackPanel>
                                <TextBlock Text="{Binding DbFormHeader}" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,16"/>

                                <!-- Quick Paste -->
                                <TextBlock Text="Quick Paste Connection String (Optional)" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                <TextBox Text="{Binding ConnectionStringPasteText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,16"/>

                                <TextBlock Text="Profile Name" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                <TextBox Text="{Binding DbName, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,12"/>

                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="2*"/>
                                        <ColumnDefinition Width="*"/>
                                    </Grid.ColumnDefinitions>
                                    <StackPanel Grid.Column="0" Margin="0,0,8,0">
                                        <TextBlock Text="Database Technology" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                        <ComboBox SelectedValue="{Binding DbTech, Mode=TwoWay}" SelectedValuePath="Tag">
                                            <ComboBoxItem Content="PostgreSQL" Tag="postgres"/>
                                            <ComboBoxItem Content="SQL Server" Tag="mssql"/>
                                        </ComboBox>
                                    </StackPanel>
                                    <StackPanel Grid.Column="1" Margin="8,0,0,0">
                                        <TextBlock Text="Port Number" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                        <TextBox Text="{Binding DbPort, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}"/>
                                    </StackPanel>
                                </Grid>

                                <TextBlock Text="Host Address / Server Name" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,12,0,4"/>
                                <TextBox Text="{Binding DbServer, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,12"/>

                                <TextBlock Text="Login Username" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                <TextBox Text="{Binding DbUsername, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,12"/>

                                <TextBlock Text="Login Password" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                                <PasswordBox helpers:PasswordBoxHelper.BindBehavior="True" 
                                             helpers:PasswordBoxHelper.BoundPassword="{Binding DbPassword, Mode=TwoWay}"
                                             Margin="0,0,0,16"/>

                                <Button Content="Test Credentials" Command="{Binding TestDatabaseConnectionCommand}" HorizontalAlignment="Left">
                                    <Button.Style>
                                        <Style TargetType="Button" BasedOn="{StaticResource StandardButtonStyle}">
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                                                    <Setter Property="ToolTip" Value="Stop the engine to save changes."/>
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </Button.Style>
                                </Button>
                            </StackPanel>
                        </Border>

                        <!-- Linked Sync Profiles Card -->
                        <Border Style="{StaticResource FluentCardStyle}" Visibility="{Binding SelectedDatabaseProfile, Converter={StaticResource NullToVisibilityConverter}}">
                            <StackPanel>
                                <TextBlock Text="Linked Sync Profiles using this Connection" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,8"/>
                                <TextBlock Text="If you modify or delete this profile, it will affect the following company sync schedules:" Style="{StaticResource CaptionTextStyle}" Margin="0,0,0,12"/>
                                
                                <ItemsControl ItemsSource="{Binding CompaniesUsingSelectedDb}">
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <TextBlock Text="{Binding Name, StringFormat='• {0}'}" Style="{StaticResource BodyTextStyle}" Margin="0,4"/>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </StackPanel>
                        </Border>
                    </StackPanel>
                </ScrollViewer>

                <!-- Actions Footer -->
                <Border Grid.Row="1" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0" Padding="0,16,0,0" Margin="0,16,0,0">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <Button Grid.Column="0" Content="Delete Profile" Command="{Binding DeleteDatabaseProfileCommand}" CommandParameter="{Binding SelectedDatabaseProfile.Id}" IsEnabled="{Binding SelectedDatabaseProfile, Converter={StaticResource NullToBoolConverter}}" Foreground="#EF4444">
                            <Button.Style>
                                <Style TargetType="Button" BasedOn="{StaticResource StandardButtonStyle}">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                                            <Setter Property="ToolTip" Value="Stop the engine to save changes."/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </Button.Style>
                        </Button>
                        <StackPanel Grid.Column="2" Orientation="Horizontal">
                            <Button Content="Cancel" Command="{Binding CancelDbEditCommand}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0"/>
                            <Button Content="{Binding DbSaveButtonText}" Command="{Binding SaveDatabaseProfileCommand}">
                                <Button.Style>
                                    <Style TargetType="Button" BasedOn="{StaticResource PrimaryButtonStyle}">
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                                                <Setter Property="ToolTip" Value="Stop the engine to save changes."/>
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Button.Style>
                            </Button>
                        </StackPanel>
                    </Grid>
                </Border>
            </Grid>
        </Grid>
    </Grid>
</Page>
```
Code-behind for `DatabasesPage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class DatabasesPage : Page
    {
        public DatabasesPage() => InitializeComponent();
    }
}
```

- [ ] **Step 5: Write LogPage.xaml UI & Code-Behind**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.LogPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:helpers="clr-namespace:TallyDbLoader.Wpf.Helpers"
      DataContext="{Binding}"
      Title="Execution Log">

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Page Header -->
        <Grid Grid.Row="0" Margin="0,0,0,16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel>
                <TextBlock Text="Extraction Process Log" Style="{StaticResource DisplayTextStyle}"/>
                <TextBlock Text="Live diagnostic output of active XML extractions and database writes." Style="{StaticResource CaptionTextStyle}"/>
            </StackPanel>
            
            <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                <Button Content="Start Engine" Command="{Binding StartSyncEngineCommand}" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0" Visibility="{Binding IsSyncNotRunning, Converter={StaticResource BooleanToVisibilityConverter}}"/>
                <Button Content="Stop Engine" Command="{Binding StopSyncEngineCommand}" Style="{StaticResource StandardButtonStyle}" Foreground="#EF4444" Margin="0,0,8,0" Visibility="{Binding IsSyncRunning, Converter={StaticResource BooleanToVisibilityConverter}}"/>
            </StackPanel>
        </Grid>

        <!-- Command Bar -->
        <Border Grid.Row="1" Background="{DynamicResource Layer2Brush}" Height="54" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4" Margin="0,0,0,16" Padding="8,4">
            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                <Button Content="Export Log File" Command="{Binding ExportLogCommand}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0"/>
                <Button Content="Clear Stream" Command="{Binding ClearLogCommand}" Style="{StaticResource StandardButtonStyle}"/>
            </StackPanel>
        </Border>

        <!-- Live Output Stream -->
        <Border Grid.Row="2" Background="#121212" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4">
            <RichTextBox x:Name="LogRichTextBox" IsReadOnly="True" VerticalScrollBarVisibility="Auto" FontFamily="Cascadia Mono" FontSize="12" Background="#121212" Foreground="#CCCCCC" BorderThickness="0" Padding="12"
                         helpers:RichTextBoxHelper.LogText="{Binding LogOutput}"/>
        </Border>
    </Grid>
</Page>
```
Code-behind for `LogPage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class LogPage : Page
    {
        public LogPage() => InitializeComponent();
    }
}
```

- [ ] **Step 6: Write HistoryPage.xaml UI & Code-Behind**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.HistoryPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      DataContext="{Binding}"
      Title="Sync Execution History">

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <StackPanel Grid.Row="0" Margin="0,0,0,16">
            <TextBlock Text="Sync Execution History" Style="{StaticResource DisplayTextStyle}"/>
            <TextBlock Text="View historical run details and row write metrics across all companies." Style="{StaticResource CaptionTextStyle}"/>
        </StackPanel>

        <!-- 2-Pane Layout -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="340"/>
            </Grid.ColumnDefinitions>

            <!-- Runs Grid (Left) -->
            <DataGrid Grid.Column="0" x:Name="RunsGrid" ItemsSource="{Binding RunHistory}" SelectedItem="{Binding SelectedRun, Mode=TwoWay}"
                      AutoGenerateColumns="False" IsReadOnly="True" GridLinesVisibility="None" BorderThickness="0" Background="Transparent" Margin="0,0,16,0">
                <DataGrid.Columns>
                    <DataGridTextColumn Header="When" Binding="{Binding StartedAt, StringFormat='{}{0:dd-MMM HH:mm}'}" Width="140"/>
                    <DataGridTextColumn Header="Company" Binding="{Binding CompanyName}" Width="1.4*"/>
                    <DataGridTextColumn Header="Mode" Binding="{Binding Mode}" Width="0.9*"/>
                    <DataGridTextColumn Header="Result Summary" Binding="{Binding ResultSummary}" Width="1*"/>
                    
                    <DataGridTemplateColumn Header="Duration" Width="70">
                        <DataGridTemplateColumn.CellTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Duration, StringFormat='{}{0:mm\\:ss}'}" FontFamily="Cascadia Mono" VerticalAlignment="Center"/>
                            </DataTemplate>
                        </DataGridTemplateColumn.CellTemplate>
                    </DataGridTemplateColumn>

                    <DataGridTemplateColumn Header="Status" Width="80">
                        <DataGridTemplateColumn.CellTemplate>
                            <DataTemplate>
                                <Border Background="{Binding Status, Converter={StaticResource StatusToToneConverter}}" CornerRadius="4" Padding="6,2" HorizontalAlignment="Left">
                                    <TextBlock Text="{Binding Status}" Foreground="White" FontSize="10" FontWeight="SemiBold" TextTransform="Uppercase"/>
                                </Border>
                            </DataTemplate>
                        </DataGridTemplateColumn.CellTemplate>
                    </DataGridTemplateColumn>
                </DataGrid.Columns>
            </DataGrid>

            <!-- Details Panel (Right) -->
            <ScrollViewer Grid.Column="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"
                          Visibility="{Binding SelectedRun, Converter={StaticResource NullToVisibilityConverter}}">
                <StackPanel>
                    <!-- Header Card -->
                    <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,12">
                        <StackPanel>
                            <TextBlock Text="{Binding SelectedRun.CompanyName}" Style="{StaticResource SubtitleTextStyle}" FontSize="16" Margin="0,0,0,8"/>
                            <StackPanel Orientation="Horizontal">
                                <Border Background="{Binding SelectedRun.Status, Converter={StaticResource StatusToToneConverter}}" CornerRadius="4" Padding="6,2" Margin="0,0,8,0">
                                    <TextBlock Text="{Binding SelectedRun.Status}" Foreground="White" FontSize="10" FontWeight="SemiBold" TextTransform="Uppercase"/>
                                </Border>
                                <Border Background="{DynamicResource Layer2Brush}" CornerRadius="4" Padding="6,2">
                                    <TextBlock Text="{Binding SelectedRun.Mode}" Foreground="{DynamicResource ForegroundBrush}" FontSize="10" FontWeight="SemiBold" TextTransform="Uppercase"/>
                                </Border>
                            </StackPanel>
                        </StackPanel>
                    </Border>

                    <!-- Stats Card -->
                    <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,12">
                        <StackPanel>
                            <TextBlock Text="Performance Metrics" Style="{StaticResource SubtitleTextStyle}" FontSize="12" Margin="0,0,0,12"/>
                            
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>

                                <StackPanel Grid.Row="0" Grid.Column="0" Margin="0,0,0,10">
                                    <TextBlock Text="Started At" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedRun.StartedAt, StringFormat='{}{0:HH:mm:ss}'}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>

                                <StackPanel Grid.Row="0" Grid.Column="1" Margin="0,0,0,10">
                                    <TextBlock Text="Ended At" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedRun.EndedAt, StringFormat='{}{0:HH:mm:ss}'}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>

                                <StackPanel Grid.Row="1" Grid.Column="0" Margin="0,0,0,10">
                                    <TextBlock Text="Duration" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedRun.Duration, StringFormat='{}{0:mm\\:ss}'}" Style="{StaticResource BodyStrongTextStyle}" FontFamily="Cascadia Mono"/>
                                </StackPanel>

                                <StackPanel Grid.Row="1" Grid.Column="1" Margin="0,0,0,10">
                                    <TextBlock Text="Retries" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedRun.Retries}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>

                                <StackPanel Grid.Row="2" Grid.Column="0">
                                    <TextBlock Text="Tally Rows" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedRun.RowsIn, Converter={StaticResource NumberConverter}}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>

                                <StackPanel Grid.Row="2" Grid.Column="1">
                                    <TextBlock Text="Written Rows" Style="{StaticResource CaptionMuteTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedRun.RowsWritten, Converter={StaticResource NumberConverter}}" Style="{StaticResource BodyStrongTextStyle}"/>
                                </StackPanel>
                            </Grid>
                        </StackPanel>
                    </Border>

                    <!-- Entity Breakdown Card -->
                    <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,12">
                        <StackPanel>
                            <TextBlock Text="Entity Breakdown" Style="{StaticResource SubtitleTextStyle}" FontSize="12" Margin="0,0,0,8"/>
                            
                            <StackPanel Margin="0,4,0,0">
                                <Grid Padding="0,4" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
                                    <TextBlock Text="Vouchers" Style="{StaticResource BodyTextStyle}"/>
                                    <TextBlock Text="{Binding SelectedRun.RowsWritten}" HorizontalAlignment="Right" FontFamily="Cascadia Mono" FontWeight="SemiBold"/>
                                </Grid>
                                <Grid Padding="0,4" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
                                    <TextBlock Text="Ledgers" Style="{StaticResource BodyTextStyle}"/>
                                    <TextBlock Text="Synced" HorizontalAlignment="Right" Foreground="#16a34a"/>
                                </Grid>
                                <Grid Padding="0,4" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
                                    <TextBlock Text="Stock Items" Style="{StaticResource BodyTextStyle}"/>
                                    <TextBlock Text="Synced" HorizontalAlignment="Right" Foreground="#16a34a"/>
                                </Grid>
                                <Grid Padding="0,4">
                                    <TextBlock Text="Groups" Style="{StaticResource BodyTextStyle}"/>
                                    <TextBlock Text="Synced" HorizontalAlignment="Right" Foreground="#16a34a"/>
                                </Grid>
                            </StackPanel>
                        </StackPanel>
                    </Border>

                    <!-- Log Excerpt Card -->
                    <Border Style="{StaticResource FluentCardStyle}">
                        <StackPanel>
                            <TextBlock Text="Log Excerpt" Style="{StaticResource SubtitleTextStyle}" FontSize="12" Margin="0,0,0,8"/>
                            <Border Background="#1E1E1E" CornerRadius="4" Padding="8">
                                <TextBlock Text="{Binding SelectedRun.LogExcerpt, FallbackValue='No logs captured for this run.'}" FontFamily="Cascadia Mono" FontSize="11" Foreground="#CCCCCC" TextWrapping="Wrap"/>
                            </Border>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </ScrollViewer>
        </Grid>
    </Grid>
</Page>
```
Code-behind for `HistoryPage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class HistoryPage : Page
    {
        public HistoryPage() => InitializeComponent();
    }
}
```

- [ ] **Step 7: Write SettingsPage.xaml UI & Code-Behind**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.SettingsPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      DataContext="{Binding}"
      Title="System Settings">

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Grid Grid.Row="0" Margin="0,0,0,20">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel Grid.Column="0">
                <TextBlock Text="System Settings" Style="{StaticResource DisplayTextStyle}"/>
                <TextBlock Text="Modify global parameters for Tally Prime interfaces and service workers." Style="{StaticResource CaptionTextStyle}"/>
            </StackPanel>
            
            <!-- Edits Locked Pill -->
            <Border Grid.Column="1" CornerRadius="12" Background="#FFFBEB" BorderBrush="#FCD34D" BorderThickness="1" Padding="8,4" VerticalAlignment="Center" Margin="8,0,0,0" Visibility="{Binding IsSyncRunning, Converter={StaticResource BooleanToVisibilityConverter}}">
                <TextBlock Text="Engine running — edits locked" Foreground="#B45309" FontSize="11" FontWeight="SemiBold" VerticalAlignment="Center"/>
            </Border>
        </Grid>

        <!-- Config Cards -->
        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <ScrollViewer.Style>
                <Style TargetType="ScrollViewer">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                            <Setter Property="Opacity" Value="0.94"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </ScrollViewer.Style>
            <StackPanel Width="540" HorizontalAlignment="Left">
                <!-- Tally Server Interface Card -->
                <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,16">
                    <StackPanel>
                        <TextBlock Text="Tally Prime Server Interface" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>

                        <TextBlock Text="Tally Host / Server" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                        <TextBox Text="{Binding TallyServer, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,12"/>

                        <TextBlock Text="Tally Port Number" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                        <TextBox Text="{Binding TallyPort, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,16"/>

                        <CheckBox Content="Automatically start Tally executable if offline" IsChecked="{Binding AutoStartTally, Mode=TwoWay}" Margin="0,0,0,12"/>
                        <Button Content="Test Interface Link" Command="{Binding TestTallyConnectionCommand}" Style="{StaticResource StandardButtonStyle}" HorizontalAlignment="Left"/>
                    </StackPanel>
                </Border>

                <!-- Tally Exe Paths Card -->
                <Border Style="{StaticResource FluentCardStyle}">
                    <StackPanel>
                        <TextBlock Text="Executable File Paths" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>

                        <TextBlock Text="Path to Tally.exe" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                        <TextBox Text="{Binding TallyExePath, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,12"/>

                        <TextBlock Text="Path to Tally.ini Configuration" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                        <TextBox Text="{Binding TallyIniPath, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}"/>
                    </StackPanel>
                </Border>
            </StackPanel>
        </ScrollViewer>

        <!-- Save Button Footer -->
        <Border Grid.Row="2" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0" Padding="0,16,0,0" Margin="0,16,0,0">
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button Content="Save Global Settings" Command="{Binding SaveTallySettingsCommand}">
                    <Button.Style>
                        <Style TargetType="Button" BasedOn="{StaticResource PrimaryButtonStyle}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsSyncRunning}" Value="True">
                                    <Setter Property="ToolTip" Value="Stop the engine to save changes."/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Button.Style>
                </Button>
            </StackPanel>
        </Border>
    </Grid>
</Page>
```
Code-behind for `SettingsPage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class SettingsPage : Page
    {
        public SettingsPage() => InitializeComponent();
    }
}
```

- [ ] **Step 8: Write SetupWizardPage.xaml UI & Code-Behind**
```xml
<Page x:Class="TallyDbLoader.Wpf.Views.SetupWizardPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:helpers="clr-namespace:TallyDbLoader.Wpf.Helpers"
      DataContext="{Binding}"
      Title="Setup Wizard">

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Top Step Indicators -->
        <Border Grid.Row="0" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1" Padding="0,0,0,16" Margin="0,0,0,16">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                
                <TextBlock Grid.Column="1" Style="{StaticResource DisplayTextStyle}" FontSize="18" TextAlignment="Center">
                    <Run Text="Step "/>
                    <Run Text="{Binding WizardStepIndex, Converter={StaticResource AddOneConverter}, Mode=OneWay}"/>
                    <Run Text=" of 6"/>
                </TextBlock>
            </Grid>
        </Border>

        <!-- Wizard Steps Container -->
        <TabControl Grid.Row="1" SelectedIndex="{Binding WizardStepIndex}" BorderThickness="0" Background="Transparent">
            <TabControl.ItemContainerStyle>
                <Style TargetType="TabItem">
                    <Setter Property="Visibility" Value="Collapsed"/>
                </Style>
            </TabControl.ItemContainerStyle>

            <!-- Step 1: Welcome -->
            <TabItem>
                <Grid VerticalAlignment="Center" HorizontalAlignment="Center" Width="480">
                    <StackPanel>
                        <TextBlock Text="Welcome to Tally Db Loader" Style="{StaticResource DisplayTextStyle}" FontSize="24" HorizontalAlignment="Center" Margin="0,0,0,12"/>
                        <TextBlock Text="This wizard will help you configure your database connection, link your Tally Prime company, and set up your initial extraction schedules." 
                                   Style="{StaticResource BodyTextStyle}" TextAlignment="Center" TextWrapping="Wrap" Margin="0,0,0,24"/>
                        <Button Content="Get Started →" Command="{Binding NavigateCommand}" CommandParameter="WizardNext" Style="{StaticResource PrimaryButtonStyle}" HorizontalAlignment="Center" Width="200" Height="40"/>
                    </StackPanel>
                </Grid>
            </TabItem>

            <!-- Step 2: Tally Settings -->
            <TabItem>
                <Grid VerticalAlignment="Center" HorizontalAlignment="Center" Width="480">
                    <StackPanel>
                        <TextBlock Text="Configure Tally Prime Connection" Style="{StaticResource DisplayTextStyle}" FontSize="20" Margin="0,0,0,4"/>
                        <TextBlock Text="Enter details of your local or remote Tally Prime port." Style="{StaticResource CaptionTextStyle}" Margin="0,0,0,16"/>

                        <TextBlock Text="Tally Host / Server" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding TallyServer, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>

                        <TextBlock Text="Tally Port" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding TallyPort, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,16"/>

                        <CheckBox Content="Automatically start Tally if not running" IsChecked="{Binding AutoStartTally, Mode=TwoWay}" Margin="0,0,0,16"/>

                        <Button Content="Test Tally Reachability" Command="{Binding TestTallyConnectionCommand}" Style="{StaticResource StandardButtonStyle}" HorizontalAlignment="Left" Margin="0,0,0,12"/>
                    </StackPanel>
                </Grid>
            </TabItem>

            <!-- Step 3: Database Destination -->
            <TabItem>
                <Grid VerticalAlignment="Center" HorizontalAlignment="Center" Width="480">
                    <StackPanel>
                        <TextBlock Text="Database Target Configuration" Style="{StaticResource DisplayTextStyle}" FontSize="20" Margin="0,0,0,4"/>
                        <TextBlock Text="Set up the database destination where company data will be loaded." Style="{StaticResource CaptionTextStyle}" Margin="0,0,0,16"/>

                        <TextBlock Text="Quick Paste Connection String" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding ConnectionStringPasteText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>

                        <TextBlock Text="Database Profile Name" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding DbName, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>

                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="2*"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,8,0">
                                <TextBlock Text="Technology" Style="{StaticResource CaptionMuteTextStyle}"/>
                                <ComboBox SelectedValue="{Binding DbTech, Mode=TwoWay}" SelectedValuePath="Tag" Margin="0,4,0,12">
                                    <ComboBoxItem Content="PostgreSQL" Tag="postgres"/>
                                    <ComboBoxItem Content="SQL Server" Tag="mssql"/>
                                </ComboBox>
                            </StackPanel>
                            <StackPanel Margin="8,0,0,0">
                                <TextBlock Text="Port" Style="{StaticResource CaptionMuteTextStyle}"/>
                                <TextBox Text="{Binding DbPort, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>
                            </StackPanel>
                        </Grid>

                        <TextBlock Text="Server Address" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding DbServer, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>

                        <TextBlock Text="Username" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding DbUsername, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>

                        <TextBlock Text="Password" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <PasswordBox helpers:PasswordBoxHelper.BindBehavior="True" 
                                     helpers:PasswordBoxHelper.BoundPassword="{Binding DbPassword, Mode=TwoWay}"
                                     Margin="0,4,0,16"/>

                        <Button Content="Test Connection" Command="{Binding TestDatabaseConnectionCommand}" Style="{StaticResource StandardButtonStyle}" HorizontalAlignment="Left" Margin="0,0,0,12"/>
                    </StackPanel>
                </Grid>
            </TabItem>

            <!-- Step 4: Choose Company -->
            <TabItem>
                <Grid VerticalAlignment="Center" HorizontalAlignment="Center" Width="480">
                    <StackPanel>
                        <TextBlock Text="Link Tally Company Profile" Style="{StaticResource DisplayTextStyle}" FontSize="20" Margin="0,0,0,4"/>
                        <TextBlock Text="Choose a company active in Tally Prime to sync." Style="{StaticResource CaptionTextStyle}" Margin="0,0,0,16"/>

                        <TextBlock Text="Selected Company" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding JobCompany, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,16"/>

                        <Button Content="Detect Active Company from Tally" Command="{Binding DetectActiveCompaniesCommand}" Style="{StaticResource PrimaryButtonStyle}" HorizontalAlignment="Left" Margin="0,0,0,16"/>

                        <TextBlock Text="Target Database Name" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding JobTargetCatalog, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>

                        <TextBlock Text="Table Prefix" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <TextBox Text="{Binding JobTablePrefix, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,4,0,12"/>
                    </StackPanel>
                </Grid>
            </TabItem>

            <!-- Step 5: Schedule Settings -->
            <TabItem>
                <Grid VerticalAlignment="Center" HorizontalAlignment="Center" Width="480">
                    <StackPanel>
                        <TextBlock Text="Configure Sync Schedule" Style="{StaticResource DisplayTextStyle}" FontSize="20" Margin="0,0,0,4"/>
                        <TextBlock Text="Define synchronization interval and active tables." Style="{StaticResource CaptionTextStyle}" Margin="0,0,0,16"/>

                        <TextBlock Text="Extraction Mode" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <ComboBox SelectedValue="{Binding JobSyncMode, Mode=TwoWay}" SelectedValuePath="Tag" Margin="0,4,0,12">
                            <ComboBoxItem Content="Full Extraction" Tag="full"/>
                            <ComboBoxItem Content="Incremental (AlterID)" Tag="incremental"/>
                        </ComboBox>

                        <TextBlock Text="Sync Interval" Style="{StaticResource CaptionMuteTextStyle}"/>
                        <ComboBox SelectedValue="{Binding JobInterval, Mode=TwoWay}" SelectedValuePath="Tag" Margin="0,4,0,16">
                            <ComboBoxItem Content="15 Minutes" Tag="15"/>
                            <ComboBoxItem Content="30 Minutes" Tag="30"/>
                            <ComboBoxItem Content="1 Hour" Tag="60"/>
                            <ComboBoxItem Content="2 Hours" Tag="120"/>
                        </ComboBox>

                        <TextBlock Text="Tables to Synchronize" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,8"/>
                        <UniformGrid Columns="2">
                            <CheckBox Content="Vouchers" IsChecked="{Binding JobSyncVouchers, Mode=TwoWay}" Margin="0,0,0,8"/>
                            <CheckBox Content="Ledgers" IsChecked="{Binding JobSyncLedgers, Mode=TwoWay}" Margin="0,0,0,8"/>
                            <CheckBox Content="Stock Items" IsChecked="{Binding JobSyncStockItems, Mode=TwoWay}" Margin="0,0,0,8"/>
                            <CheckBox Content="Groups" IsChecked="{Binding JobSyncGroups, Mode=TwoWay}" Margin="0,0,0,8"/>
                        </UniformGrid>
                    </StackPanel>
                </Grid>
            </TabItem>

            <!-- Step 6: Review -->
            <TabItem>
                <Grid VerticalAlignment="Center" HorizontalAlignment="Center" Width="480">
                    <StackPanel>
                        <TextBlock Text="Confirm Configuration" Style="{StaticResource DisplayTextStyle}" FontSize="20" Margin="0,0,0,4"/>
                        <TextBlock Text="Verify your configuration before finishing the setup." Style="{StaticResource CaptionTextStyle}" Margin="0,0,0,16"/>

                        <Border Style="{StaticResource FluentCardStyle}" Margin="0,0,0,16">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>
                                
                                <TextBlock Grid.Row="0" Text="{Binding TallyServer, StringFormat='Tally Host: {0}'}" Style="{StaticResource BodyTextStyle}" Margin="0,2"/>
                                <TextBlock Grid.Row="1" Text="{Binding DbName, StringFormat='Database Profile: {0}'}" Style="{StaticResource BodyTextStyle}" Margin="0,2"/>
                                <TextBlock Grid.Row="2" Text="{Binding JobCompany, StringFormat='Company Name: {0}'}" Style="{StaticResource BodyTextStyle}" Margin="0,2"/>
                                <TextBlock Grid.Row="3" Text="{Binding JobInterval, StringFormat='Sync Interval: {0} minutes'}" Style="{StaticResource BodyTextStyle}" Margin="0,2"/>
                            </Grid>
                        </Border>

                        <TextBlock Text="Clicking Finish will save all configuration profiles, initialize the database schema, and launch the background loader engine."
                                   Style="{StaticResource CaptionTextStyle}" TextWrapping="Wrap" Margin="0,0,0,16"/>
                    </StackPanel>
                </Grid>
            </TabItem>
        </TabControl>

        <!-- Navigation Footer -->
        <Grid Grid.Row="2" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0" Padding="0,16,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            
            <Button Grid.Column="0" Content="Back" Command="{Binding NavigateCommand}" CommandParameter="WizardBack" Style="{StaticResource StandardButtonStyle}"/>
            <Button Grid.Column="1" Content="Skip &amp; Finish Later" Command="{Binding NavigateCommand}" CommandParameter="Dashboard" Style="{StaticResource HyperlinkButtonStyle}" HorizontalAlignment="Center"/>
            <Button Grid.Column="2" Content="Continue" Command="{Binding NavigateCommand}" CommandParameter="WizardNext" Style="{StaticResource PrimaryButtonStyle}"/>
        </Grid>
    </Grid>
</Page>
```
Code-behind for `SetupWizardPage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class SetupWizardPage : Page
    {
        public SetupWizardPage() => InitializeComponent();
    }
}
```

## Phase 8: Verification & Smoke-Test Checklist

### Task 8.1: Run Verification Tests

- [ ] **Step 1: Execute test suite**
  Verify everything compiles cleanly and all unit tests pass:
  ```powershell
  dotnet test src/TallyDbLoader.sln
  ```

### Task 8.2: Interactive Smoke-Test Checklist

Execute and complete each manual checkpoint step prior to completing migration tasks:

- [ ] **1. Single Instance Mutex**: Launch app. Verify a second launcher is suppressed and focuses the original Window.
- [ ] **2. Navigation Rail**: Navigate to each drawer item. Check that rail styling indicates active screens correctly.
- [ ] **3. Profile Redirection**: Click Company profile editor (card click or list actions), verifying route updates, back navigation, and clickable breadcrumbs.
- [ ] **4. Companies Double-click**: In Companies list view, double-click a row to open the corresponding company profile editor screen.
- [ ] **5. Log Streaming**: Start sync worker, check logs append incrementally and batched without causing GC spikes.
- [ ] **6. Engine Stop**: Stop sync worker, check logs and engine pulse dot stop running.
- [ ] **7. Mutation Guards**: Click "Save Profile" while sync worker is running. Confirm warning toast appears and prevents configuration mutation.
- [ ] **8. Tally Port Update**: Open Settings, change Tally port, click Save. Check that success toast appears.
- [ ] **9. Connection Testing**: Open Databases screen, select database profile, and click Test connection, confirming the toast output.
- [ ] **10. Tally Company Picker**: Open picker dialog via "Detect Active Companies". Verify the list displays currently open Tally companies.
- [ ] **11. Close to Tray**: Click main Window close button. Confirm app hides to system tray and tray icon remains active.
- [ ] **12. Tray Exit Action**: Right-click tray icon and select Exit. Verify app shuts down completely and cleanly.
- [ ] **13. SessionEnding Support**: Initiate Windows sign-out or restart. Verify that `SessionEnding` fires, worker disposes, and Windows is not blocked from shutting down.
- [ ] **14. Dark Theme Toggle**: In Settings, toggle between light and dark theme modes. Verify all styling parameters and contrast ratios adjust correctly.
- [ ] **15. Run History Metrics**: Run at least one sync cycle. Verify history list displays metrics. Click a row and check the right-hand details pane updates.
