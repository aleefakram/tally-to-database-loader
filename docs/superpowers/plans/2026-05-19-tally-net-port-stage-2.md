# Tally-to-Database Loader .NET Port - Stage 2 (Background Engine & Scheduling) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the background scheduler, job runner, SQLite schema updates, and Tally Prime launcher/ini configuration manager.

**Architecture:** Extend SQLite configuration storage to hold sync jobs mapped to database profiles. Build a background scheduler that monitors time-of-day and interval sync requirements, serializes concurrent Tally XML queries using a Semaphore lock, and checks and launches `tally.exe` by modifying `tally.ini`.

**Tech Stack:** .NET 8.0, C#, SQLite (`Microsoft.Data.Sqlite`), Dapper, System.Diagnostics (Process management).

---

## Tasks

### Task 5: Sync Job Model & DB Schema Update

**Files:**
- Modify: `src/TallyDbLoader.Core/Models/Models.cs`
- Modify: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
- Test: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

- [ ] **Step 1: Write integration test for Sync Job CRUD operations**
  
  Add to `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`:
  ```csharp
  [Fact]
  public void Test_SyncJob_CRUD()
  {
      string testDbPath = "test_jobs.db";
      if (System.IO.File.Exists(testDbPath)) System.IO.File.Delete(testDbPath);

      DatabaseHelper.InitializeDatabase(testDbPath);
      var repo = new ConfigRepository(testDbPath);

      var profile = new DatabaseProfile
      {
          Name = "TargetPostgres",
          Technology = "postgres",
          Server = "localhost",
          Port = 5432,
          Username = "postgres",
          Password = "password"
      };
      repo.SaveDatabaseProfile(profile);
      var savedProfile = repo.GetDatabaseProfileByName("TargetPostgres");

      var job = new SyncJob
      {
          CompanyName = "Yaghma Kababs",
          DbProfileId = savedProfile.Id,
          TargetCatalog = "yaghma_db",
          SyncIntervalMinutes = 15,
          DailyTimeLocal = null,
          Status = "Idle"
      };

      repo.SaveSyncJob(job);
      var jobs = repo.GetAllSyncJobs();

      Assert.Single(jobs);
      Assert.Equal("Yaghma Kababs", jobs[0].CompanyName);
      Assert.Equal(savedProfile.Id, jobs[0].DbProfileId);

      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      if (System.IO.File.Exists(testDbPath)) System.IO.File.Delete(testDbPath);
  }
  ```

- [ ] **Step 2: Verify test fails during compilation**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: Compile errors due to missing `SyncJob` class and repository methods.

- [ ] **Step 3: Implement SyncJob model and SQLite tables**
  
  Add class to `src/TallyDbLoader.Core/Models/Models.cs`:
  ```csharp
  public class SyncJob
  {
      public int Id { get; set; }
      public string CompanyName { get; set; } = string.Empty;
      public int DbProfileId { get; set; }
      public string TargetCatalog { get; set; } = string.Empty;
      public int? SyncIntervalMinutes { get; set; }
      public string? DailyTimeLocal { get; set; }
      public string? LastRunTime { get; set; }
      public string Status { get; set; } = "Idle";
  }
  ```
  
  Modify `src/TallyDbLoader.Core/Data/DatabaseHelper.cs` to add the `sync_jobs` table:
  ```csharp
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
  ```
  
  Modify `src/TallyDbLoader.Core/Data/ConfigRepository.cs` to add SyncJob queries:
  ```csharp
  public void SaveSyncJob(SyncJob job)
  {
      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Execute(@"
              INSERT OR REPLACE INTO sync_jobs (company_name, db_profile_id, target_catalog, sync_interval_minutes, daily_time_local, last_run_time, status)
              VALUES (@CompanyName, @DbProfileId, @TargetCatalog, @SyncIntervalMinutes, @DailyTimeLocal, @LastRunTime, @Status)", job);
      }
  }

  public List<SyncJob> GetAllSyncJobs()
  {
      using (var conn = new SqliteConnection(_connectionString))
      {
          return conn.Query<SyncJob>("SELECT * FROM sync_jobs").AsList();
      }
  }
  ```

- [ ] **Step 4: Verify tests pass**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: PASS.

- [ ] **Step 5: Commit**
  
  Run:
  ```bash
  git add src/ tests/
  git commit -m "feat: add SyncJob model, schema migration, and repository methods"
  ```

---

### Task 6: Tally Process Check & `tally.ini` Configuration Parser

**Files:**
- Create: `src/TallyDbLoader.Core/Tally/TallyLauncher.cs`
- Create: `tests/TallyDbLoader.Tests/TallyLauncherTests.cs`

- [ ] **Step 1: Write unit test for `tally.ini` auto-open parsing**
  
  Create `tests/TallyDbLoader.Tests/TallyLauncherTests.cs`:
  ```csharp
  using System.IO;
  using Xunit;
  using TallyDbLoader.Core.Tally;
  
  public class TallyLauncherTests
  {
      [Fact]
      public void Test_TallyIni_Modification()
      {
          var testIni = "test_tally.ini";
          File.WriteAllLines(testIni, new[] {
              "[Setting]",
              "Port = 9000",
              "UserOpen = C:\\Data\\OldCompany"
          });
          
          TallyLauncher.AddCompanyToIni(testIni, "C:\\Data\\NewCompany");
          
          var lines = File.ReadAllLines(testIni);
          Assert.Contains("UserOpen = C:\\Data\\NewCompany", lines);
          
          if (File.Exists(testIni)) File.Delete(testIni);
      }
  }
  ```

- [ ] **Step 2: Verify test fails during compilation**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: Compile fails on `TallyLauncher`.

- [ ] **Step 3: Implement TallyLauncher**
  
  Create `src/TallyDbLoader.Core/Tally/TallyLauncher.cs`:
  ```csharp
  using System;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  
  namespace TallyDbLoader.Core.Tally
  {
      public static class TallyLauncher
      {
          public static bool IsTallyRunning()
          {
              return Process.GetProcessesByName("tally").Length > 0;
          }
  
          public static void LaunchTally(string tallyExePath)
          {
              if (string.IsNullOrEmpty(tallyExePath) || !File.Exists(tallyExePath))
              {
                  throw new FileNotFoundException("Tally.exe executable not found at specified path.", tallyExePath);
              }
              Process.Start(new ProcessStartInfo
              {
                  FileName = tallyExePath,
                  UseShellExecute = true
              });
          }
  
          public static void AddCompanyToIni(string iniPath, string companyFolderPath)
          {
              if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath)) return;
              
              var lines = File.ReadAllLines(iniPath).ToList();
              var settingIndex = lines.FindIndex(l => l.Trim().Equals("[Setting]", StringComparison.OrdinalIgnoreCase));
              
              if (settingIndex == -1)
              {
                  lines.Add("[Setting]");
                  settingIndex = lines.Count - 1;
              }
              
              string targetLine = $"UserOpen = {companyFolderPath}";
              if (!lines.Any(l => l.Trim().Equals(targetLine, StringComparison.OrdinalIgnoreCase)))
              {
                  lines.Insert(settingIndex + 1, targetLine);
                  File.WriteAllLines(iniPath, lines);
              }
          }
      }
  }
  ```

- [ ] **Step 4: Verify tests pass**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: PASS.

- [ ] **Step 5: Commit**
  
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Tally/TallyLauncher.cs tests/TallyDbLoader.Tests/TallyLauncherTests.cs
  git commit -m "feat: implement Tally running detection and tally.ini configuration updates"
  ```

---

### Task 7: Background Scheduler Loop

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/SyncOrchestrator.cs`
- Create: `tests/TallyDbLoader.Tests/SyncOrchestratorTests.cs`

- [ ] **Step 1: Write test to verify interval/time logic**
  
  Create `tests/TallyDbLoader.Tests/SyncOrchestratorTests.cs`:
  ```csharp
  using System;
  using Xunit;
  using TallyDbLoader.Core.Models;
  using TallyDbLoader.Core.Sync;
  
  public class SyncOrchestratorTests
  {
      [Fact]
      public void Test_ShouldRunJob_Interval()
      {
          var job = new SyncJob
          {
              SyncIntervalMinutes = 15,
              LastRunTime = DateTime.UtcNow.AddMinutes(-16).ToString("o")
          };
          
          bool shouldRun = SyncOrchestrator.ShouldRun(job, DateTime.UtcNow);
          Assert.True(shouldRun);
      }
      
      [Fact]
      public void Test_ShouldRunJob_TimeOfDay()
      {
          var job = new SyncJob
          {
              DailyTimeLocal = "02:00:00",
              LastRunTime = DateTime.Today.AddDays(-1).AddHours(2).ToString("o")
          };
          
          // Test running at exactly 02:05 AM today
          var now = DateTime.Today.AddHours(2).AddMinutes(5);
          bool shouldRun = SyncOrchestrator.ShouldRun(job, now);
          Assert.True(shouldRun);
      }
  }
  ```

- [ ] **Step 2: Verify test fails during compilation**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: Fails due to missing `SyncOrchestrator`.

- [ ] **Step 3: Implement SyncOrchestrator**
  
  Create `src/TallyDbLoader.Core/Sync/SyncOrchestrator.cs`:
  ```csharp
  using System;
  using System.Globalization;
  using TallyDbLoader.Core.Models;
  
  namespace TallyDbLoader.Core.Sync
  {
      public static class SyncOrchestrator
      {
          public static bool ShouldRun(SyncJob job, DateTime now)
          {
              if (job.SyncIntervalMinutes.HasValue)
              {
                  if (string.IsNullOrEmpty(job.LastRunTime)) return true;
                  if (DateTime.TryParse(job.LastRunTime, null, DateTimeStyles.RoundtripKind, out var lastRun))
                  {
                      return (now - lastRun).TotalMinutes >= job.SyncIntervalMinutes.Value;
                  }
                  return true;
              }
              
              if (!string.IsNullOrEmpty(job.DailyTimeLocal))
              {
                  if (TimeSpan.TryParse(job.DailyTimeLocal, out var targetTime))
                  {
                      var targetToday = now.Date.Add(targetTime);
                      
                      // If target time is past in the current day, check if we already ran it today
                      if (now >= targetToday)
                      {
                          if (string.IsNullOrEmpty(job.LastRunTime)) return true;
                          if (DateTime.TryParse(job.LastRunTime, null, DateTimeStyles.RoundtripKind, out var lastRun))
                          {
                              return lastRun.Date < now.Date;
                          }
                          return true;
                      }
                  }
              }
              
              return false;
          }
      }
  }
  ```

- [ ] **Step 4: Verify tests pass**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: PASS.

- [ ] **Step 5: Commit**
  
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Sync/SyncOrchestrator.cs tests/TallyDbLoader.Tests/SyncOrchestratorTests.cs
  git commit -m "feat: implement scheduling criteria for interval and local time-of-day jobs"
  ```
