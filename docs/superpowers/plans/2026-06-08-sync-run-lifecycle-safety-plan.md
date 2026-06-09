# Sync Run Lifecycle & Safety State Handling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish an explicit, safety-hardened lifecycle for sync runs in the .NET utility, tracking status in both CompanyProfile and SyncRun, preventing concurrent execution, and reconciling stale states on startup.

**Architecture:** Use SQLite-persisted statuses and atomic transitions inside the database to block invalid states. Track run history using SyncRun and update runtime statistics targeting only execution fields. Reconcile stale statuses on startup.

**Tech Stack:** C#, .NET 8.0, Microsoft.Data.Sqlite, Dapper, Xunit

---

## File Structure

- **Modify**: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs` (to migrate NULL/blank statuses to `'idle'` and bump database version to 3)
- **Modify**: `src/TallyDbLoader.Core/Data/IConfigRepository.cs` (to add the new status management and update APIs)
- **Modify**: `src/TallyDbLoader.Core/Data/ConfigRepository.cs` (to implement the SQL operations for lifecycle management)
- **Modify**: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` (to integrate safety guards, reconciliation on start, manual preflight check, and conservative error mapping)
- **Create**: `tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs` (to write comprehensive unit tests verifying all state transitions, scheduling rules, and recovery behaviors)

---

### Task 1: Database Schema Status Migration & Version Bump

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`

- [ ] **Step 1: Write schema migration code**
  Modify the `InitializeDatabase` method in `src/TallyDbLoader.Core/Data/DatabaseHelper.cs` to add a migration step for `version < 3` to clean up any NULL or empty statuses and bump the version to 3.

  ```csharp
  // Around line 155 in DatabaseHelper.cs, insert before transaction.Commit():
  if (version < 3)
  {
      conn.Execute("UPDATE company_profiles SET status = 'idle' WHERE status IS NULL OR TRIM(status) = '';", null, transaction);
      conn.Execute("PRAGMA user_version = 3;", null, transaction);
  }
  ```

- [ ] **Step 2: Run build to verify code compiles**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**
  ```bash
  git add src/TallyDbLoader.Core/Data/DatabaseHelper.cs
  git commit -m "feat(sync): add database migration for company status column"
  ```

---

### Task 2: Repository API Extensions

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/IConfigRepository.cs`
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`

- [ ] **Step 1: Update IConfigRepository interface**
  Add lifecycle management methods and update the signature of `AddSyncRun` in `src/TallyDbLoader.Core/Data/IConfigRepository.cs`.

  ```csharp
  using System;
  using System.Collections.Generic;
  using TallyDbLoader.Core.Models;

  namespace TallyDbLoader.Core.Data
  {
      public interface IConfigRepository
      {
          // Existing methods...
          void SaveDatabaseProfile(DatabaseProfile profile);
          DatabaseProfile? GetDatabaseProfileByName(string name);
          DatabaseProfile? GetDatabaseProfileById(int id);
          List<DatabaseProfile> GetAllDatabaseProfiles();
          void SaveCompanyProfile(CompanyProfile company);
          List<CompanyProfile> GetAllCompanyProfiles();
          void DeleteCompanyProfile(int id);
          TallySettings GetTallySettings();
          void SaveTallySettings(TallySettings settings);
          void DeleteDatabaseProfile(int id);
          
          // Updated & New methods:
          long AddSyncRun(SyncRun run);
          List<SyncRun> GetRecentSyncRuns(int limit = 50);
          List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50);

          bool TryStartCompanyProfile(int id);
          void MarkCompanyProfileUnknown(int id, string reason, DateTime now);
          void CompleteCompanyProfileRun(
              int id,
              string finalStatus,
              DateTime endedAt,
              int durationMs,
              long rowsWritten,
              bool incrementErrorCount);
          void UpdateSyncRun(SyncRun run);
          void ReconcileStaleRuns(DateTime now);
      }
  }
  ```

- [ ] **Step 2: Implement methods in ConfigRepository**
  Implement these methods in `src/TallyDbLoader.Core/Data/ConfigRepository.cs`. Update `AddSyncRun` to return `long` and store `ended_at` as NULL while the run is active (i.e. status is `"running"` or EndedAt has not been explicitly set).

  ```csharp
  // Modify AddSyncRun in ConfigRepository.cs to return long:
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
                  string? endedAtStr = (run.Status == "running" || run.EndedAt == default(DateTime)) 
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

  // Add the following new methods to ConfigRepository.cs:
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

  public void MarkCompanyProfileUnknown(int id, string reason, DateTime now)
  {
      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Open();
          conn.Execute("PRAGMA foreign_keys = ON;");
          using (var transaction = conn.BeginTransaction())
          {
              try
              {
                  var affected = conn.Execute(@"
                      UPDATE company_profiles
                      SET status = 'unknown',
                          last_run_at = @Now
                      WHERE id = @Id;", new { Id = id, Now = now.ToString("o") }, transaction);
                  if (affected != 1)
                  {
                      throw new InvalidOperationException($"Expected to update exactly 1 company profile (ID: {id}), but updated {affected}.");
                  }
                  transaction.Commit();
              }
              catch
              {
                  transaction.Rollback();
                  throw;
              }
          }

          // Durable audit logging using existing FileLogger
          TallyDbLoader.Core.Logging.FileLogger.LogMessage($"[Safety] Company profile {id} marked unknown. Reason: {reason}");
      }
  }

  public void CompleteCompanyProfileRun(
      int id,
      string finalStatus,
      DateTime endedAt,
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
                  var affected = conn.Execute(@"
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
                          FinalStatus = finalStatus,
                          EndedAt = endedAt.ToString("o"),
                          DurationMs = durationMs,
                          RowsWritten = rowsWritten,
                          IncrementErrorCount = incrementErrorCount ? 1 : 0
                      }, transaction);
                  if (affected != 1)
                  {
                      throw new InvalidOperationException($"Expected to update exactly 1 company profile (ID: {id}), but updated {affected}.");
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
                  string? endedAtStr = (run.EndedAt == default(DateTime)) ? null : run.EndedAt.ToString("o");

                  var affected = conn.Execute(@"
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
                  if (affected != 1)
                  {
                      throw new InvalidOperationException($"Expected to update exactly 1 sync run (ID: {run.Id}), but updated {affected}.");
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

  public void ReconcileStaleRuns(DateTime now)
  {
      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Open();
          conn.Execute("PRAGMA foreign_keys = ON;");
          using (var transaction = conn.BeginTransaction())
          {
              try
              {
                  // Reconcile stale runs
                  conn.Execute(@"
                      UPDATE sync_runs
                      SET status = 'unknown',
                          ended_at = @Now,
                          result_summary = 'Interrupted by application restart before completion',
                          log_excerpt = 'Startup reconciliation found stale running state.'
                      WHERE status = 'running';", new { Now = now.ToString("o") }, transaction);

                  // Reconcile stale profiles
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
  ```

- [ ] **Step 3: Run build to verify compilation**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds.

- [ ] **Step 4: Commit**
  ```bash
  git add src/TallyDbLoader.Core/Data/IConfigRepository.cs src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(sync): extend repository contract and implement SQLite data storage for lifecycle"
  ```

---

### Task 3: Unit Testing Status Transitions & Reconciliations

**Files:**
- Create: `tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs`

- [ ] **Step 1: Write unit tests for transitions & reconciliation**
  Create the test suite targeting status locking, metadata recovery, and startup reconciliation.

  ```csharp
  using System;
  using System.IO;
  using Xunit;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.Models;
  using Microsoft.Data.Sqlite;
  using Dapper;

  namespace TallyDbLoader.Tests
  {
      public class SyncLifecycleSafetyTests : IDisposable
      {
          private readonly string _dbPath;
          private readonly ConfigRepository _repo;

          public SyncLifecycleSafetyTests()
          {
              _dbPath = Path.Combine(Path.GetTempPath(), $"tally_test_{Guid.NewGuid()}.db");
              DatabaseHelper.InitializeDatabase(_dbPath);
              _repo = new ConfigRepository(_dbPath);
          }

          public void Dispose()
          {
              if (File.Exists(_dbPath))
              {
                  try { File.Delete(_dbPath); } catch { }
              }
          }

          private CompanyProfile SeedCompany(string status, bool enabled = true)
          {
              var dbProfile = new DatabaseProfile { Name = "TestDb", Technology = "sqlite" };
              _repo.SaveDatabaseProfile(dbProfile);
              var dbFromDb = _repo.GetDatabaseProfileByName("TestDb");

              var profile = new CompanyProfile
              {
                  Name = Guid.NewGuid().ToString(),
                  DbProfileId = dbFromDb.Id,
                  TargetCatalog = "test",
                  Status = status,
                  Enabled = enabled
              };
              _repo.SaveCompanyProfile(profile);
              
              // Load back to get auto-generated ID
              var all = _repo.GetAllCompanyProfiles();
              return all.Find(x => x.Name == profile.Name);
          }

          [Fact]
          public void TryStartCompanyProfile_WithIdleStatus_Succeeds()
          {
              var profile = SeedCompany("idle");
              bool started = _repo.TryStartCompanyProfile(profile.Id);
              Assert.True(started);

              var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
              Assert.Equal("running", updated.Status);
          }

          [Fact]
          public void TryStartCompanyProfile_WithRunningOrBlockedStatus_Fails()
          {
              foreach (var status in new[] { "running", "review_required", "attention_required", "unknown" })
              {
                  var profile = SeedCompany(status);
                  bool started = _repo.TryStartCompanyProfile(profile.Id);
                  Assert.False(started);
              }
          }

          [Fact]
          public void MarkCompanyProfileUnknown_SetsStatusToUnknown()
          {
              var profile = SeedCompany("running");
              _repo.MarkCompanyProfileUnknown(profile.Id, "Metadata failed", DateTime.Now);

              var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
              Assert.Equal("unknown", updated.Status);
          }

          [Fact]
          public void ReconcileStaleRuns_ReconcilesRunningJobsAndSyncRuns()
          {
              var profile = SeedCompany("running");
              
              var run = new SyncRun
              {
                  CompanyId = profile.Id,
                  CompanyName = profile.Name,
                  StartedAt = DateTime.Now.AddMinutes(-5),
                  Mode = "full",
                  Status = "running"
              };
              _repo.AddSyncRun(run);

              _repo.ReconcileStaleRuns(DateTime.Now);

              var updatedProfile = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
              Assert.Equal("unknown", updatedProfile.Status);

              var runs = _repo.GetSyncRunsForCompany(profile.Id);
              Assert.Single(runs);
              Assert.Equal("unknown", runs[0].Status);
              Assert.Contains("Interrupted by application restart", runs[0].ResultSummary);
          }

          [Fact]
          public void AddSyncRun_SetsEndedAtToNullForActiveRuns()
          {
              var profile = SeedCompany("idle");
              var run = new SyncRun
              {
                  CompanyId = profile.Id,
                  CompanyName = profile.Name,
                  StartedAt = DateTime.Now,
                  Mode = "full",
                  Status = "running"
              };
              _repo.AddSyncRun(run);

              // Query database directly to assert SQLite column is written as NULL
              using (var conn = new SqliteConnection(_dbPath))
              {
                  var endedAt = conn.QuerySingle<string?>("SELECT ended_at FROM sync_runs WHERE company_id = @CompanyId", new { CompanyId = profile.Id });
                  Assert.Null(endedAt);
              }
          }
      }
  }
  ```

- [ ] **Step 2: Run tests to verify they pass**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter SyncLifecycleSafetyTests`
  Expected: 5 passed, 0 failed.

- [ ] **Step 3: Commit**
  ```bash
  git add tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs
  git commit -m "test(sync): add unit tests for sync run lifecycle and reconciliation"
  ```

---

### Task 4: Scheduler & Sync Worker Integration

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`

- [ ] **Step 1: Integrate ReconcileStaleRuns on Worker Start and Fail Closed**
  Update the `Start()` method in `BackgroundSyncWorker.cs` to run startup reconciliation. Accept a `bool startScheduler = true` parameter. If reconciliation fails, fail closed: log the error and do not spin up the background worker thread. If `startScheduler` is false, initialize the cancellation token source to mark `IsRunning = true` but do NOT spawn the scheduler worker loop task (eliminating test race conditions).

  ```csharp
  // Modify Start(bool startScheduler = true) in BackgroundSyncWorker.cs:
  public void Start(bool startScheduler = true)
  {
      lock (_syncLock)
      {
          if (IsRunning) return;
          _isPaused = !startScheduler; // Start paused if scheduler is not started
          IsBlocked = false;
          
          // Reconcile stale runs before spawning the background worker thread
          try
          {
              _repo.ReconcileStaleRuns(DateTime.Now);
              Log("[Engine] Startup reconciliation of stale runs completed.");
          }
          catch (Exception ex)
          {
              IsBlocked = true;
              Log($"[Engine ERROR] Startup reconciliation failed: {ex.Message}. Scheduler will not start.");
              return; // Fail-closed: do not set _cts, do not spawn task.
          }

          _cts = new CancellationTokenSource();
          if (startScheduler)
          {
              var token = _cts.Token;
              _runTask = Task.Run(() => WorkerLoop(token));
              Log("Background Sync Engine started.");
          }
          else
          {
              Log("Background Sync Engine initialized in mock preflight-only mode.");
          }
      }
  }
  ```

- [ ] **Step 2: Update SyncCompany with Safe Lifecycle Steps & Error Mapping**
  Revamp `SyncCompany` to:
  1. Atomically attempt to lock profile status to `running`.
  2. Safe-insert the `SyncRun` (reverting to `unknown` if metadata write fails).
  3. Execute sync task.
  4. Preserve watermarks (avoid updating watermark if final status is not `completed`).
  5. Apply conservative status mapping inside the `finally` block and call `CompleteCompanyProfileRun` & `UpdateSyncRun`. Ensure `isError` is marked true on metadata failures to increment error count.

  ```csharp
  // Replace SyncCompany in BackgroundSyncWorker.cs with the following safety-hardened lifecycle implementation:
  private async Task SyncCompany(CompanyProfile company, TallyClient client, CancellationToken token)
  {
      Log($"[Sync] Initiating sync execution for company '{company.Name}'...");

      if (string.IsNullOrWhiteSpace(company.TargetCatalog))
      {
          _repo.MarkCompanyProfileUnknown(company.Id, "Target catalog empty", DateTime.Now);
          Log($"[Sync ERROR] Company '{company.Name}' failed: Target database name is empty.");
          OnSyncCompleted?.Invoke();
          return;
      }

      // 1. Authoritative check & atomic state transition
      bool started = _repo.TryStartCompanyProfile(company.Id);
      if (!started)
      {
          Log($"[Sync Skipped] Company '{company.Name}' is currently running or safety blocked.");
          return;
      }

      // 2. Create and insert SyncRun record
      var run = new SyncRun
      {
          CompanyId = company.Id,
          CompanyName = company.Name,
          StartedAt = DateTime.Now,
          Mode = company.Mode,
          Status = "running",
          ResultSummary = "Sync execution started."
      };

      try
      {
          _repo.AddSyncRun(run); // Populates run.Id
      }
      catch (Exception ex)
      {
          // Crucial Safety Gate: Revert company profile to unknown if SyncRun metadata save fails
          Log($"[Sync ERROR] Metadata write error during run initiation. Failing closed: {ex.Message}");
          try
          {
              _repo.MarkCompanyProfileUnknown(company.Id, "Metadata run registration failed", DateTime.Now);
          }
          catch (Exception revertEx)
          {
              Log($"[Sync FATAL] Failed to revert company status to unknown after run registration failure: {revertEx.Message}. PERSISTED SAFETY STATE IS COMPROMISED. Blocking scheduler.");
              IsBlocked = true;
          }
          OnSyncCompleted?.Invoke();
          return;
      }

      string finalStatus = "completed";
      string summary = "Sync completed successfully.";
      string? logExcerpt = null;
      long totalRows = 0;
      bool isError = false;

      try
      {
          var dbProfile = company.Db ?? _repo.GetDatabaseProfileById(company.DbProfileId);
          if (dbProfile == null)
          {
              throw new InvalidOperationException("Target database profile not found.");
          }

          // Verify Tally connectivity & open company
          List<string> activeCompanies;
          try
          {
              activeCompanies = await client.FetchActiveCompaniesAsync();
          }
          catch (Exception ex)
          {
              // Wrap Tally connection issues into a distinct exception type
              throw new TimeoutException("Tally Prime server is offline or unreachable.", ex);
          }

          if (!activeCompanies.Contains(company.Name))
          {
              throw new InvalidOperationException($"Target company '{company.Name}' is not loaded in Tally Prime.");
          }

          IDatabaseLoader dbLoader;
          string connStr;
          var tech = dbProfile.Technology.ToLower();
          if (tech.Contains("postgres") || tech.Contains("npgsql"))
          {
              var builder = new Npgsql.NpgsqlConnectionStringBuilder
              {
                  Host = dbProfile.Server,
                  Port = dbProfile.Port,
                  Username = dbProfile.Username,
                  Password = dbProfile.Password,
                  Database = company.TargetCatalog
              };
              if (!dbProfile.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) && 
                  !dbProfile.Server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
              {
                  builder.SslMode = Npgsql.SslMode.Require;
                  builder.TrustServerCertificate = true;
              }
              connStr = builder.ConnectionString;
              dbLoader = new PostgreSqlLoader(connStr);
          }
          else if (tech.Contains("mssql") || tech.Contains("sqlserver"))
          {
              var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
              {
                  DataSource = $"{dbProfile.Server},{dbProfile.Port}",
                  UserID = dbProfile.Username,
                  Password = dbProfile.Password,
                  InitialCatalog = company.TargetCatalog,
                  TrustServerCertificate = true
              };
              connStr = builder.ConnectionString;
              dbLoader = new MSSqlLoader(connStr);
          }
          else if (tech.Contains("mysql"))
          {
              connStr = $"Server={dbProfile.Server};Port={dbProfile.Port};User Id={dbProfile.Username};Password={dbProfile.Password};Database={company.TargetCatalog};";
              dbLoader = new MySqlLoader(connStr);
          }
          else if (tech.Contains("sqlite"))
          {
              string dbFile = company.TargetCatalog.EndsWith(".db") ? company.TargetCatalog : $"{company.TargetCatalog}.db";
              connStr = $"Data Source={dbFile}";
              dbLoader = new TallyDbLoader.Core.DatabaseLoaders.SqliteLoader(connStr);
          }
          else
          {
              throw new NotSupportedException($"Technology '{dbProfile.Technology}' not supported.");
          }

          var configFilename = (company.Mode?.ToLowerInvariant() == "incremental")
              ? "tally-export-config-incremental.yaml"
              : "tally-export-config.yaml";

          var yamlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFilename);
          if (!System.IO.File.Exists(yamlPath))
          {
              yamlPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), configFilename);
          }

          if (!System.IO.File.Exists(yamlPath))
          {
              throw new FileNotFoundException($"Tally definition file '{yamlPath}' not found.");
          }

          var yamlContent = System.IO.File.ReadAllText(yamlPath);
          var config = YamlConfigParser.Parse(yamlContent);

          DbConnection targetConn;
          if (tech.Contains("postgres") || tech.Contains("npgsql"))
          {
              targetConn = new Npgsql.NpgsqlConnection(connStr);
          }
          else if (tech.Contains("mssql") || tech.Contains("sqlserver"))
          {
              targetConn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
          }
          else if (tech.Contains("mysql"))
          {
              targetConn = new MySqlConnector.MySqlConnection(connStr);
          }
          else if (tech.Contains("sqlite"))
          {
              targetConn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
          }
          else
          {
              throw new NotSupportedException($"Technology '{dbProfile.Technology}' not supported.");
          }

          using (targetConn)
          {
              await targetConn.OpenAsync(token);

              var staging = new StagingTableManager(targetConn);
              await staging.EnsureStagingTablesAsync();

              long? prevMaster = null;
              long? prevTxn = null;
              if (company.Mode?.ToLowerInvariant() == "incremental")
              {
                  var repo = new WatermarkRepository(targetConn);
                  var (m, t) = await repo.ReadAsync();
                  prevMaster = m;
                  prevTxn = t;
              }

              var fetcher = new CompanyInfoFetcher(client);
              var companyInfo = await fetcher.FetchAndPersist(company.Name, targetConn);
              var fromDate = companyInfo.BooksFrom ?? new DateTime(2000, 1, 1);
              var toDate = companyInfo.BooksTo ?? DateTime.Today;

              if (company.Mode?.ToLowerInvariant() == "incremental")
              {
                  var runner = new IncrementalSyncRunner(client, dbLoader);
                  // IncrementalSyncRunner internally updates the watermark only on success
                  await runner.RunAsync(config, company.Name, fromDate, toDate, targetConn, prevMaster, prevTxn);
                  totalRows = 0;
              }
              else
              {
                  IFullSyncTablePromoter promoter;
                  if (tech.Contains("sqlite")) promoter = new SqliteFullSyncTablePromoter();
                  else if (tech.Contains("postgres") || tech.Contains("npgsql")) promoter = new PostgresFullSyncTablePromoter();
                  else if (tech.Contains("mssql") || tech.Contains("sqlserver")) promoter = new MssqlFullSyncTablePromoter();
                  else if (tech.Contains("mysql")) promoter = new MysqlFullSyncTablePromoter();
                  else throw new NotSupportedException($"Technology '{dbProfile.Technology}' not supported.");

                  var runner = new FullSyncRunner(client, promoter);
                  totalRows = await runner.Run(config, company.Name, fromDate, toDate, targetConn);
              }
          }

          finalStatus = "completed";
          summary = $"Sync completed successfully. Wrote {totalRows} records.";
      }
      catch (OperationCanceledException)
      {
          finalStatus = "unknown";
          summary = "Sync operation was cancelled or interrupted.";
          isError = true;
      }
      catch (TimeoutException ex)
      {
          // Tally unavailable / offline -> attention_required
          finalStatus = "attention_required";
          summary = ex.Message;
          logExcerpt = ex.StackTrace;
          isError = true;
      }
      catch (InvalidOperationException ex) when (ex.Message.Contains("not loaded in Tally"))
      {
          // Company mismatch / not loaded -> attention_required
          finalStatus = "attention_required";
          summary = ex.Message;
          logExcerpt = ex.StackTrace;
          isError = true;
      }
      catch (FileNotFoundException ex)
      {
          // Missing config/definition files -> review_required
          finalStatus = "review_required";
          summary = ex.Message;
          logExcerpt = ex.StackTrace;
          isError = true;
      }
      catch (Exception ex)
      {
          // Other exceptions, validation errors -> failed
          finalStatus = "failed";
          summary = ex.Message;
          logExcerpt = ex.StackTrace;
          isError = true;
      }
      finally
      {
          // 4. Update the SyncRun execution ledger
          run.EndedAt = DateTime.Now;
          run.Status = finalStatus;
          run.ResultSummary = summary;
          run.LogExcerpt = logExcerpt;
          run.RowsIn = totalRows;
          run.RowsWritten = totalRows;

          try
          {
              _repo.UpdateSyncRun(run);
          }
          catch (Exception ex)
          {
              Log($"[Sync ERROR] Failed to update SyncRun record: {ex.Message}");
              finalStatus = "unknown"; // Post-commit metadata failure -> fail-closed to unknown. The SyncRun row may remain 'running' until startup reconciliation recovers it.
              isError = true;          // Force isError = true to increment error count
          }

          // 5. Update Company Profile runtime status safely (Targeted repository method)
          try
          {
              int durationMs = (int)(run.EndedAt - run.StartedAt).TotalMilliseconds;
              _repo.CompleteCompanyProfileRun(company.Id, finalStatus, run.EndedAt, durationMs, totalRows, isError);
          }
          catch (Exception ex)
          {
              Log($"[Sync ERROR] Failed to save runtime status update for company {company.Id}: {ex.Message}");
              try
              {
                  _repo.MarkCompanyProfileUnknown(company.Id, $"Runtime status update failed: {ex.Message}", DateTime.Now);
              }
              catch (Exception revertEx)
              {
                  Log($"[Sync FATAL] Failed to revert company status to unknown: {revertEx.Message}. PERSISTED SAFETY STATE IS COMPROMISED. Blocking scheduler.");
                  IsBlocked = true;
              }
          }

          Log($"[Sync Finished] Result: {finalStatus}. Wrote {totalRows} records.");
          OnSyncCompleted?.Invoke();
      }
  }
  ```

- [ ] **Step 3: Run build to verify compilation**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds.

- [ ] **Step 4: Commit**
  ```bash
  git add src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs
  git commit -m "feat(sync): integrate safety eligibility, startup reconciliation, and error mapping in background worker"
  ```

---

### Task 5: Manual Trigger and Preflight Implementation

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`
- Modify: `tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs`

- [ ] **Step 1: Implement preflight check TryRequestManualSync**
  Add the `SyncStartResult` type and implement `TryRequestManualSync` in `BackgroundSyncWorker.cs`. Replace the existing `TriggerManualSync` method.

  ```csharp
  // Add SyncStartResult definition to BackgroundSyncWorker.cs (or Models.cs):
  public sealed class SyncStartResult
  {
      public bool Accepted { get; init; }
      public string ReasonCode { get; init; } = string.Empty;
      public string Message { get; init; } = string.Empty;
  }

  // Inside BackgroundSyncWorker class, replace TriggerManualSync(int? companyId) with:
  private int? _manualCompanyProfileId = null;

  public SyncStartResult TryRequestManualSync(int companyProfileId)
  {
      lock (_syncLock)
      {
          if (_disposed)
          {
              return new SyncStartResult
              {
                  Accepted = false,
                  ReasonCode = "Disposed",
                  Message = "Engine is disposed."
              };
          }

          if (!IsRunning)
          {
              return new SyncStartResult
              {
                  Accepted = false,
                  ReasonCode = "EngineNotRunning",
                  Message = "Background sync engine is stopped."
              };
          }

          // Retrieve active profile state directly from DB
          var profiles = _repo.GetAllCompanyProfiles();
          var profile = profiles.Find(p => p.Id == companyProfileId);

          if (profile == null)
          {
              return new SyncStartResult
              {
                  Accepted = false,
                  ReasonCode = "NotFound",
                  Message = "Company profile not found."
              };
          }

          if (!profile.Enabled)
          {
              return new SyncStartResult
              {
                  Accepted = false,
                  ReasonCode = "Disabled",
                  Message = "This sync job is disabled."
              };
          }

          if (profile.Status == "running")
          {
              return new SyncStartResult
              {
                  Accepted = false,
                  ReasonCode = "AlreadyRunning",
                  Message = "This sync job is already executing."
              };
          }

          if (profile.Status != "idle" && profile.Status != "completed" && profile.Status != "failed")
          {
              return new SyncStartResult
              {
                  Accepted = false,
                  ReasonCode = "SafetyBlocked",
                  Message = $"Sync is blocked because current status is '{profile.Status}'."
              };
          }

          if (_manualCompanyProfileId != null)
          {
              return new SyncStartResult
              {
                  Accepted = false,
                  ReasonCode = "WorkerBusy",
                  Message = "Another manual run is already pending dispatch."
              };
          }

          // Set the target request & notify the worker thread loop
          _manualCompanyProfileId = companyProfileId;
          _forceSyncOnce = true;
          TriggerWakeUp();

          return new SyncStartResult
          {
              Accepted = true,
              ReasonCode = "PendingDispatch",
              Message = "Manual run request accepted and pending dispatch."
          };
      }
  }
  ```

- [ ] **Step 2: Update WorkerLoop authoritative manual request recheck**
  Refactor the manual trigger evaluation block inside the worker loop to read `_manualCompanyProfileId` and recheck its status before running. Align worker checks with the (idle, completed, failed) allow-list.

  ```csharp
  // In BackgroundSyncWorker.WorkerLoop, update the manual run check logic (approx lines 197-225):
  bool runManualSync;
  int? manualCompanyId;

  lock (_syncLock)
  {
      runManualSync = _forceSyncOnce;
      manualCompanyId = _manualCompanyProfileId;
      _forceSyncOnce = false;
      _manualCompanyProfileId = null; // Clear request
  }

  var companies = _repo.GetAllCompanyProfiles();
  bool manualTargetSeen = false;
  foreach (var company in companies)
  {
      if (token.IsCancellationRequested) break;

      bool shouldSync = false;
      string skipReason = string.Empty;

      if (runManualSync)
      {
          // Authoritative check on the manual target profile
          if (manualCompanyId.HasValue && manualCompanyId.Value == company.Id)
          {
              manualTargetSeen = true;
              if (!company.Enabled)
              {
                  skipReason = "JobDisabled";
              }
              else if (company.Status == "running")
              {
                  skipReason = "AlreadyRunning";
              }
              else if (company.Status != "idle" && company.Status != "completed" && company.Status != "failed")
              {
                  skipReason = "SafetyBlocked";
              }
              else
              {
                  shouldSync = true;
              }
          }
      }
      else
      {
          // Scheduler evaluation rules:
          if (!company.Enabled)
          {
              skipReason = "JobDisabled";
          }
          else if (company.Status == "running")
          {
              skipReason = "AlreadyRunning";
          }
          else if (company.Status != "idle" && company.Status != "completed" && company.Status != "failed")
          {
              skipReason = "SafetyBlocked";
          }
          else if (!SyncOrchestrator.ShouldRun(company, DateTime.Now))
          {
              skipReason = "IntervalNotMet";
          }
          else
          {
              shouldSync = true;
          }
      }

      if (shouldSync)
      {
          await SyncCompany(company, client, token);
      }
      else if (!string.IsNullOrEmpty(skipReason) && skipReason != "IntervalNotMet")
      {
          Log($"[Sync Skipped] Skipping job '{company.Name}' (Reason: {skipReason}, Current Status: {company.Status})");
      }
  }

  if (runManualSync && manualCompanyId.HasValue && !manualTargetSeen)
  {
      Log($"[Engine WARNING] Manual trigger targets company ID {manualCompanyId.Value}, but it was not found in active profiles (ManualTriggerDropped/NotFound).");
  }
  ```

- [ ] **Step 3: Add unit tests verifying preflight checks and watermark safety on failure**
  Add unit tests in `tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs` to assert the behavior of manual triggers (using `worker.Start(startScheduler: false)` to prevent spawning the active scheduling loop) and check incremental sync watermark behavior by injecting a database loader write failure.

  ```csharp
  // Add the following test-only Mock classes to SyncLifecycleSafetyTests.cs:
  public class FakeTallyClient : ITallyClient
  {
      public Task<string> PostXMLAsync(string xmlRequest) => Task.FromResult("");
      public Task<string> FetchLedgersXmlAsync(string companyName) => Task.FromResult("");
      public Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync() => Task.FromResult(new List<TallyCompanyInfo>());
      public Task<List<string>> FetchActiveCompaniesAsync() => Task.FromResult(new List<string> { "TestCompany" });
      public Task<TallyCompanyInfo?> FetchCompanyInfoAsync(string companyName) => Task.FromResult<TallyCompanyInfo?>(new TallyCompanyInfo
      {
          Name = "TestCompany",
          Guid = "guid",
          AltMstId = 999, // New alter ID
          AltVchId = 999  // New alter ID
      });
  }

  public class FakeFailingDatabaseLoader : IDatabaseLoader
  {
      public Task LoadBulkDataAsync(System.Data.DataTable data, string tableName) => Task.CompletedTask;
      public string TruncateSql(string tableName) => throw new InvalidOperationException("Simulated db write failure");
      public string CascadeUpdateSql(string primaryTable, string childTable, string field) => "";
      public string VoucherNumberUpdateSql() => "";
      public string CountAutoNumberVoucherTypesSql() => "";
  }

  // Add the following test methods to SyncLifecycleSafetyTests.cs:
  [Fact]
  public void TryRequestManualSync_Accepts_EligibleJob()
  {
      var profile = SeedCompany("idle");
      using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
      {
          worker.Start(startScheduler: false); // Starts in preflight-only mock mode without background thread loop
          var result = worker.TryRequestManualSync(profile.Id);
          Assert.True(result.Accepted);
          Assert.Equal("PendingDispatch", result.ReasonCode);
      }
  }

  [Fact]
  public void TryRequestManualSync_Rejects_DisabledJob()
  {
      var profile = SeedCompany("idle", enabled: false);
      using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
      {
          worker.Start(startScheduler: false);
          var result = worker.TryRequestManualSync(profile.Id);
          Assert.False(result.Accepted);
          Assert.Equal("Disabled", result.ReasonCode);
      }
  }

  [Fact]
  public void TryRequestManualSync_Rejects_SafetyBlockedJob()
  {
      foreach (var status in new[] { "review_required", "attention_required", "unknown" })
      {
          var profile = SeedCompany(status);
          using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
          {
              worker.Start(startScheduler: false);
              var result = worker.TryRequestManualSync(profile.Id);
              Assert.False(result.Accepted);
              Assert.Equal("SafetyBlocked", result.ReasonCode);
          }
      }
  }

  [Fact]
  public void TryRequestManualSync_Rejects_AlreadyRunningJob()
  {
      using (var worker = new BackgroundSyncWorker(_repo, "localhost", 9000))
      {
          worker.Start(startScheduler: false);
          var profile = SeedCompany("running");
          var result = worker.TryRequestManualSync(profile.Id);
          Assert.False(result.Accepted);
          Assert.Equal("AlreadyRunning", result.ReasonCode);
      }
  }

  [Fact]
  public async Task IncrementalSync_DoesNotAdvanceWatermark_OnFailure()
  {
      // 1. Initialize temporary test database
      string dbFile = Path.Combine(Path.GetTempPath(), $"tally_watermark_test_{Guid.NewGuid()}.db");
      var connStr = $"Data Source={dbFile}";
      
      using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
      {
          await conn.OpenAsync();
          
          // Initialize watermark schema
          var watermarkRepo = new WatermarkRepository(conn);
          await watermarkRepo.EnsureWatermarkTableAsync();
          
          // Seed initial watermarks
          await watermarkRepo.UpdateAsync(100, 200);
          
          // 2. Instantiate loader and inject a failing run simulation
          var client = new FakeTallyClient();
          var dbLoader = new FakeFailingDatabaseLoader();
          var runner = new IncrementalSyncRunner(client, dbLoader);

          // Configure config with some master/txn tables so it does work and triggers the loader
          var config = new TallyExportConfig
          {
              Master = new List<TableConfig>
              {
                  new TableConfig
                  {
                      Name = "mst_group",
                      Collection = "Group",
                      Nature = "Primary",
                      Fields = new List<FieldConfig> { new() { Name = "guid", Field = "Guid", Type = "text" } }
                  }
              },
              Transaction = new List<TableConfig>()
          };
          
          await Assert.ThrowsAsync<InvalidOperationException>(async () =>
          {
              await runner.RunAsync(config, "TestCompany", DateTime.Today, DateTime.Today, conn, 100, 200);
          });
          
          // 3. Confirm watermarks remain unchanged
          var (master, txn) = await watermarkRepo.ReadAsync();
          Assert.Equal(100, master);
          Assert.Equal(200, txn);
      }
      
      if (File.Exists(dbFile))
      {
          try { File.Delete(dbFile); } catch { }
      }
  }
  ```

- [ ] **Step 4: Run all unit tests**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: All 98 tests pass successfully.

- [ ] **Step 5: Commit**
  ```bash
  git add src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs
  git commit -m "feat(sync): implement manual trigger preflight checks and safety tests"
  ```
