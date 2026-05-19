# Tally-to-Database Loader .NET Port - Stage 4 (Sync Orchestrator Loop & Target Writing) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the background scheduler worker loop in the WPF application, add database writer targets (PostgreSQL / MSSQL) for dynamic ledger/voucher storage, and integrate Start/Stop execution buttons in the WPF user interface.

**Architecture:** Create a `BackgroundSyncWorker` in WPF that coordinates Tally loading, HTTP fetching, XML parsing, and SQL destination writing. Provide a Toggle Command in `MainViewModel` to boot and terminate the worker loop.

**Tech Stack:** WPF, C#, SQL Server Client, Npgsql (Postgres Client), SQLite.

---

## Tasks

### Task 11: Database Target Writer (PostgreSQL / MSSQL)

**Files:**
- Create: `src/TallyDbLoader.Core/Data/DatabaseWriter.cs`
- Test: `tests/TallyDbLoader.Tests/DatabaseWriterTests.cs`

- [ ] **Step 1: Implement DatabaseWriter**
  
  Create `src/TallyDbLoader.Core/Data/DatabaseWriter.cs` to handle target database table initialization and record UPSERTs:
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.Data;
  using Microsoft.Data.SqlClient;
  using Npgsql;
  using TallyDbLoader.Core.Models;
  
  namespace TallyDbLoader.Core.Data
  {
      public static class DatabaseWriter
      {
          private static IDbConnection GetConnection(DatabaseProfile profile, string catalog)
          {
              if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
              {
                  string connStr = $"Host={profile.Server};Port={profile.Port};Username={profile.Username};Password={profile.Password};Database={catalog};";
                  var conn = new NpgsqlConnection(connStr);
                  conn.Open();
                  return conn;
              }
              else if (profile.Technology.Equals("mssql", StringComparison.OrdinalIgnoreCase))
              {
                  string connStr = $"Server={profile.Server},{profile.Port};User Id={profile.Username};Password={profile.Password};Database={catalog};TrustServerCertificate=True;";
                  var conn = new SqlConnection(connStr);
                  conn.Open();
                  return conn;
              }
              throw new NotSupportedException($"Database technology '{profile.Technology}' is not supported.");
          }
  
          public static void InitializeTargetTables(DatabaseProfile profile, string catalog)
          {
              using (var conn = GetConnection(profile, catalog))
              {
                  using (var cmd = conn.CreateCommand())
                  {
                      if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                      {
                          cmd.CommandText = @"
                              CREATE TABLE IF NOT EXISTS ledgers (
                                  guid TEXT PRIMARY KEY,
                                  name TEXT NOT NULL,
                                  parent TEXT,
                                  opening_balance NUMERIC,
                                  closing_balance NUMERIC
                              );
                              CREATE TABLE IF NOT EXISTS vouchers (
                                  guid TEXT PRIMARY KEY,
                                  date TEXT,
                                  voucher_number TEXT,
                                  voucher_type TEXT,
                                  amount NUMERIC
                              );";
                      }
                      else if (profile.Technology.Equals("mssql", StringComparison.OrdinalIgnoreCase))
                      {
                          cmd.CommandText = @"
                              IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ledgers' AND xtype='U')
                              CREATE TABLE ledgers (
                                  guid VARCHAR(100) PRIMARY KEY,
                                  name VARCHAR(255) NOT NULL,
                                  parent VARCHAR(255),
                                  opening_balance DECIMAL(18,2),
                                  closing_balance DECIMAL(18,2)
                              );
                              IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='vouchers' AND xtype='U')
                              CREATE TABLE vouchers (
                                  guid VARCHAR(100) PRIMARY KEY,
                                  date VARCHAR(50),
                                  voucher_number VARCHAR(100),
                                  voucher_type VARCHAR(100),
                                  amount DECIMAL(18,2)
                              );";
                      }
                      cmd.ExecuteNonQuery();
                  }
              }
          }
  
          public static void WriteLedgers(DatabaseProfile profile, string catalog, List<Ledger> ledgers)
          {
              using (var conn = GetConnection(profile, catalog))
              {
                  foreach (var ledger in ledgers)
                  {
                      using (var cmd = conn.CreateCommand())
                      {
                          if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                          {
                              cmd.CommandText = @"
                                  INSERT INTO ledgers (guid, name, parent, opening_balance, closing_balance)
                                  VALUES (@guid, @name, @parent, @opening_balance, @closing_balance)
                                  ON CONFLICT (guid) DO UPDATE 
                                  SET name = EXCLUDED.name, parent = EXCLUDED.parent, 
                                      opening_balance = EXCLUDED.opening_balance, closing_balance = EXCLUDED.closing_balance;";
                          }
                          else
                          {
                              cmd.CommandText = @"
                                  MERGE ledgers AS target
                                  USING (SELECT @guid AS guid, @name AS name, @parent AS parent, @opening_balance AS opening_balance, @closing_balance AS closing_balance) AS source
                                  ON (target.guid = source.guid)
                                  WHEN MATCHED THEN
                                      UPDATE SET name = source.name, parent = source.parent, 
                                                 opening_balance = source.opening_balance, closing_balance = source.closing_balance
                                  WHEN NOT MATCHED THEN
                                      INSERT (guid, name, parent, opening_balance, closing_balance)
                                      VALUES (source.guid, source.name, source.parent, source.opening_balance, source.closing_balance);";
                          }
  
                          AddParameter(cmd, "@guid", ledger.Guid);
                          AddParameter(cmd, "@name", ledger.Name);
                          AddParameter(cmd, "@parent", ledger.Parent);
                          AddParameter(cmd, "@opening_balance", ledger.OpeningBalance);
                          AddParameter(cmd, "@closing_balance", ledger.ClosingBalance);
                          cmd.ExecuteNonQuery();
                      }
                  }
              }
          }
  
          private static void AddParameter(IDbCommand cmd, string name, object? value)
          {
              var param = cmd.CreateParameter();
              param.ParameterName = name;
              param.Value = value ?? DBNull.Value;
              cmd.Parameters.Add(param);
          }
      }
  }
  ```

- [ ] **Step 2: Write Verification Test**
  
  Create `tests/TallyDbLoader.Tests/DatabaseWriterTests.cs` using a SQLite wrapper mock (or direct mock structure) to verify parameters binding code executes warning-free.

- [ ] **Step 3: Commit**
  
  Run:
  ```bash
  git add src/ tests/
  git commit -m "feat: implement target database writer supporting SQL server and PostgreSQL"
  ```

---

### Task 12: Sync Worker Engine Loop

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`
- Test: `tests/TallyDbLoader.Tests/BackgroundSyncWorkerTests.cs`

- [ ] **Step 1: Implement BackgroundSyncWorker**
  
  Create `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` containing the main task polling thread:
  ```csharp
  using System;
  using System.Threading;
  using System.Threading.Tasks;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.Tally;
  
  namespace TallyDbLoader.Core.Sync
  {
      public class BackgroundSyncWorker
      {
          private readonly ConfigRepository _repo;
          private readonly string _tallyServer;
          private readonly int _tallyPort;
          private CancellationTokenSource? _cts;
          private Task? _runTask;
  
          public event Action<string>? OnLogMessage;
          public event Action? OnSyncCompleted;
  
          public bool IsRunning => _cts != null;
  
          public BackgroundSyncWorker(ConfigRepository repo, string tallyServer, int tallyPort)
          {
              _repo = repo;
              _tallyServer = tallyServer;
              _tallyPort = tallyPort;
          }
  
          public void Start()
          {
              if (IsRunning) return;
              _cts = new CancellationTokenSource();
              _runTask = Task.Run(() => WorkerLoop(_cts.Token));
              OnLogMessage?.Invoke("Background Sync Engine started.");
          }
  
          public void Stop()
          {
              if (!IsRunning) return;
              _cts?.Cancel();
              try { _runTask?.Wait(); } catch { }
              _cts = null;
              OnLogMessage?.Invoke("Background Sync Engine stopped.");
          }
  
          private async Task WorkerLoop(CancellationToken token)
          {
              var client = new TallyClient(_tallyServer, _tallyPort);
              
              while (!token.IsCancellationRequested)
              {
                  try
                  {
                      var jobs = _repo.GetAllSyncJobs();
                      foreach (var job in jobs)
                      {
                          if (token.IsCancellationRequested) break;
                          
                          if (SyncOrchestrator.ShouldRun(job, DateTime.Now))
                          {
                              OnLogMessage?.Invoke($"Starting job '{job.CompanyName}'...");
                              
                              job.Status = "Running";
                              _repo.SaveSyncJob(job);
                              OnSyncCompleted?.Invoke();
                              
                              try
                              {
                                  // Fetch and parse ledgers
                                  var ledgersXml = await client.FetchLedgersXmlAsync(job.CompanyName);
                                  var ledgers = TallyXmlParser.ParseLedgers(ledgersXml);
                                  
                                  // Find database profile
                                  var profiles = _repo.GetAllSyncJobs(); // Or custom fetch
                                  var dbProfile = _repo.GetDatabaseProfileByName("PostgreSqlLocal"); // Simple lookup
                                  
                                  if (dbProfile != null)
                                  {
                                      DatabaseWriter.InitializeTargetTables(dbProfile, job.TargetCatalog);
                                      DatabaseWriter.WriteLedgers(dbProfile, job.TargetCatalog, ledgers);
                                      
                                      job.Status = "Idle";
                                      job.LastRunTime = DateTime.UtcNow.ToString("o");
                                      OnLogMessage?.Invoke($"Job '{job.CompanyName}' completed successfully.");
                                  }
                                  else
                                  {
                                      job.Status = "Failed";
                                      OnLogMessage?.Invoke($"Job '{job.CompanyName}' failed: Database profile not found.");
                                  }
                              }
                              catch (Exception ex)
                              {
                                  job.Status = "Failed";
                                  OnLogMessage?.Invoke($"Job '{job.CompanyName}' failed: {ex.Message}");
                              }
                              
                              _repo.SaveSyncJob(job);
                              OnSyncCompleted?.Invoke();
                          }
                      }
                  }
                  catch (Exception ex)
                  {
                      OnLogMessage?.Invoke($"Loop error: {ex.Message}");
                  }
                  
                  try { await Task.Delay(TimeSpan.FromSeconds(60), token); } catch { break; }
              }
          }
      }
  }
  ```

- [ ] **Step 2: Commit**
  
  Run:
  ```bash
  git add src/
  git commit -m "feat: implement background runner loop coordinates tally xml and database writer"
  ```

---

### Task 13: WPF Button Controls & Real-Time Sync Logs

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml`
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs`

- [ ] **Step 1: Add Controls & Logger to View Model**
  
  Modify `src/TallyDbLoader.Wpf/MainViewModel.cs` to instantiate `BackgroundSyncWorker` and link UI controls:
  ```csharp
  using System.Windows.Input;
  using TallyDbLoader.Core.Sync;
  
  // Inside MainViewModel class:
  private BackgroundSyncWorker? _worker;
  private string _logOutput = "Ready to start sync loop.";
  
  public string LogOutput
  {
      get => _logOutput;
      set { _logOutput = value; OnPropertyChanged(); }
  }
  
  public void StartSyncEngine()
  {
      if (_worker == null)
      {
          _worker = new BackgroundSyncWorker(_repo, "localhost", 9000);
          _worker.OnLogMessage += (msg) => {
              LogOutput = $"[{System.DateTime.Now:HH:mm:ss}] {msg}\n" + LogOutput;
              StatusText = msg;
          };
          _worker.OnSyncCompleted += () => {
              // Reload jobs table status
              System.Windows.Application.Current.Dispatcher.Invoke(() => LoadConfiguration());
          };
      }
      _worker.Start();
  }
  
  public void StopSyncEngine()
  {
      _worker?.Stop();
  }
  ```

- [ ] **Step 2: Bind Start/Stop Buttons in WPF Dashboard**
  
  Modify `src/TallyDbLoader.Wpf/MainWindow.xaml` to feature a layout containing control actions and logs:
  - Add "Start Sync Loop" and "Stop Sync Loop" buttons in header/action deck.
  - Add a Scrollable text area showing real-time `LogOutput`.

- [ ] **Step 3: Verify and Build**
  
  Run: `dotnet test src/TallyDbLoader.sln`
  Ensure all tests compile cleanly.

- [ ] **Step 4: Commit**
  
  Run:
  ```bash
  git add -A
  git commit -m "feat: integrate Start/Stop UI commands and real-time scrolling logging panel"
  ```
