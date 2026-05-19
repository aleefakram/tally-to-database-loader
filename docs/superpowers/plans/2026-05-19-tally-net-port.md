# Tally-to-Database Loader .NET Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the Tally-to-Database Sync Utility from Node.js to a native Windows .NET 8 WPF system tray application with SQLite storage and multi-engine bulk loading.

**Architecture:** A native Windows WPF GUI dashboard communicating with a SQLite configuration database (`sync_config.db`) and executing timer-based sync tasks in background C# threads. It performs direct Tally HTTP UTF-16 XML exports and uploads them to target databases (MSSQL, MySQL, PostgreSQL, etc.) using high-performance native bulk loaders.

**Tech Stack:** .NET 8.0, C#, WPF, SQLite (`Microsoft.Data.Sqlite`), Dapper, `Microsoft.Data.SqlClient` (SQL Server Bulk Copy), `Npgsql` (PostgreSQL Binary Importer), `MySqlConnector` (MySQL Bulk Copy).

---

## Proposed Project File Structure

```
c:\Users\user\Desktop\tally-to-database-loader\
├── docs/superpowers/plans/2026-05-19-tally-net-port.md  (This plan)
├── src/
│   ├── TallyDbLoader.sln
│   ├── TallyDbLoader.Core/
│   │   ├── TallyDbLoader.Core.csproj
│   │   ├── Models/
│   │   │   └── Models.cs
│   │   ├── Data/
│   │   │   ├── DatabaseHelper.cs
│   │   │   └── ConfigRepository.cs
│   │   ├── Tally/
│   │   │   ├── TallyClient.cs
│   │   │   └── TallyLauncher.cs
│   │   ├── DatabaseLoaders/
│   │   │   ├── IDatabaseLoader.cs
│   │   │   ├── MSSqlLoader.cs
│   │   │   ├── PostgreSqlLoader.cs
│   │   │   └── MySqlLoader.cs
│   │   └── Sync/
│   │       └── SyncOrchestrator.cs
│   └── TallyDbLoader.Wpf/
│       ├── TallyDbLoader.Wpf.csproj
│       ├── App.xaml
│       ├── App.xaml.cs
│       ├── MainWindow.xaml
│       └── MainWindow.xaml.cs
└── tests/
    └── TallyDbLoader.Tests/
        ├── TallyDbLoader.Tests.csproj
        ├── ConfigRepositoryTests.cs
        ├── TallyClientTests.cs
        └── DatabaseLoaderTests.cs
```

---

## Tasks

### Task 0: Project Solution Initialization & NuGet Setup

**Files:**
- Create: `src/TallyDbLoader.sln`
- Create: `src/TallyDbLoader.Core/TallyDbLoader.Core.csproj`
- Create: `src/TallyDbLoader.Wpf/TallyDbLoader.Wpf.csproj`
- Create: `tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`

- [ ] **Step 1: Create folders and initialize C# projects using CLI**
  
  Run in `c:\Users\user\Desktop\tally-to-database-loader`:
  ```powershell
  mkdir src
  mkdir src/TallyDbLoader.Core
  mkdir src/TallyDbLoader.Wpf
  mkdir tests
  mkdir tests/TallyDbLoader.Tests
  
  dotnet new sln -o src -n TallyDbLoader
  dotnet new classlib -o src/TallyDbLoader.Core -n TallyDbLoader.Core -f net8.0
  dotnet new wpf -o src/TallyDbLoader.Wpf -n TallyDbLoader.Wpf -f net8.0-windows
  dotnet new xunit -o tests/TallyDbLoader.Tests -n TallyDbLoader.Tests -f net8.0
  
  dotnet sln src/TallyDbLoader.sln add src/TallyDbLoader.Core/TallyDbLoader.Core.csproj
  dotnet sln src/TallyDbLoader.sln add src/TallyDbLoader.Wpf/TallyDbLoader.Wpf.csproj
  dotnet sln src/TallyDbLoader.sln add tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj
  
  dotnet add src/TallyDbLoader.Wpf/TallyDbLoader.Wpf.csproj reference src/TallyDbLoader.Core/TallyDbLoader.Core.csproj
  dotnet add tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj reference src/TallyDbLoader.Core/TallyDbLoader.Core.csproj
  ```
  Expected: Successful exit code, solution file created.

- [ ] **Step 2: Add NuGet dependencies to TallyDbLoader.Core**
  
  Run:
  ```powershell
  dotnet add src/TallyDbLoader.Core/TallyDbLoader.Core.csproj package Dapper --version 2.1.35
  dotnet add src/TallyDbLoader.Core/TallyDbLoader.Core.csproj package Microsoft.Data.Sqlite --version 8.0.4
  dotnet add src/TallyDbLoader.Core/TallyDbLoader.Core.csproj package Microsoft.Data.SqlClient --version 5.2.0
  dotnet add src/TallyDbLoader.Core/TallyDbLoader.Core.csproj package Npgsql --version 8.0.2
  dotnet add src/TallyDbLoader.Core/TallyDbLoader.Core.csproj package MySqlConnector --version 2.3.6
  ```
  Expected: Packages installed successfully.

- [ ] **Step 3: Commit**
  
  Run:
  ```bash
  git add src/ tests/
  git commit -m "chore: initialize .NET solution, projects, and NuGet dependencies"
  ```

---

### Task 1: SQLite Storage Schema & Config Repository

**Files:**
- Create: `src/TallyDbLoader.Core/Models/Models.cs`
- Create: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`
- Create: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
- Create: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

- [ ] **Step 1: Write failing integration test for Database Setup & CRUD**
  
  Create `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`:
  ```csharp
  using System.IO;
  using Xunit;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.Models;
  
  public class ConfigRepositoryTests
  {
      private readonly string _testDbPath = "test_config.db";
  
      [Fact]
      public void Test_Database_Initialization_And_CRUD()
      {
          if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
          
          DatabaseHelper.InitializeDatabase(_testDbPath);
          var repo = new ConfigRepository(_testDbPath);
          
          var profile = new DatabaseProfile
          {
              Name = "LocalSQL",
              Technology = "mssql",
              Server = "localhost",
              Port = 1433,
              Username = "sa",
              Password = "encryptedpwd"
          };
          
          repo.SaveDatabaseProfile(profile);
          var saved = repo.GetDatabaseProfileByName("LocalSQL");
          
          Assert.NotNull(saved);
          Assert.Equal("mssql", saved.Technology);
          Assert.Equal("localhost", saved.Server);
          
          if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
      }
  }
  ```

- [ ] **Step 2: Run test and verify compilation/execution fails**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: Fails due to missing types.

- [ ] **Step 3: Implement Models, DatabaseHelper, and ConfigRepository**
  
  Create `src/TallyDbLoader.Core/Models/Models.cs`:
  ```csharp
  namespace TallyDbLoader.Core.Models
  {
      public class DatabaseProfile
      {
          public int Id { get; set; }
          public string Name { get; set; }
          public string Technology { get; set; }
          public string Server { get; set; }
          public int Port { get; set; }
          public string Username { get; set; }
          public string Password { get; set; }
      }
      
      public class TallySettings
      {
          public string Server { get; set; } = "localhost";
          public int Port { get; set; } = 9000;
          public string TallyExePath { get; set; }
          public string TallyIniPath { get; set; }
      }
  }
  ```
  
  Create `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`:
  ```csharp
  using System.IO;
  using Microsoft.Data.Sqlite;
  using Dapper;
  
  namespace TallyDbLoader.Core.Data
  {
      public static class DatabaseHelper
      {
          public static void InitializeDatabase(string dbPath)
          {
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
  ```
  
  Create `src/TallyDbLoader.Core/Data/ConfigRepository.cs`:
  ```csharp
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
  ```

- [ ] **Step 4: Verify test passes**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: PASS.

- [ ] **Step 5: Commit**
  
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Data/ src/TallyDbLoader.Core/Models/ tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "feat: implement SQLite database helper and config repository with CRUD tests"
  ```

---

### Task 2: Tally Client (XML Generation and HTTP POST UTF-16)

**Files:**
- Create: `src/TallyDbLoader.Core/Tally/TallyClient.cs`
- Create: `tests/TallyDbLoader.Tests/TallyClientTests.cs`

- [ ] **Step 1: Write failing unit test for Tally XML posting format**
  
  Create `tests/TallyDbLoader.Tests/TallyClientTests.cs`:
  ```csharp
  using Xunit;
  using TallyDbLoader.Core.Tally;
  using System.Text;
  
  public class TallyClientTests
  {
      [Fact]
      public void Test_Unicode_Content_Encoding()
      {
          var xml = "<ENVELOPE><HEADER><VERSION>1</VERSION></HEADER></ENVELOPE>";
          var content = TallyClient.CreateTallyContent(xml);
          
          Assert.Equal("text/xml", content.Headers.ContentType.MediaType);
          Assert.Equal("utf-16", content.Headers.ContentType.CharSet);
      }
  }
  ```

- [ ] **Step 2: Verify test fails**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: Fails due to missing `TallyClient`.

- [ ] **Step 3: Implement TallyClient**
  
  Create `src/TallyDbLoader.Core/Tally/TallyClient.cs`:
  ```csharp
  using System.Net.Http;
  using System.Text;
  using System.Threading.Tasks;
  
  namespace TallyDbLoader.Core.Tally
  {
      public class TallyClient
      {
          private readonly HttpClient _httpClient;
          private readonly string _tallyUrl;
  
          public TallyClient(HttpClient httpClient, string server, int port)
          {
              _httpClient = httpClient;
              _tallyUrl = $"http://{server}:{port}";
          }
  
          public static StringContent CreateTallyContent(string xmlRequest)
          {
              // Tally Solutions developer doc specifies UTF-16 (Unicode) encoding
              return new StringContent(xmlRequest, Encoding.Unicode, "text/xml");
          }
  
          public async Task<string> PostXMLAsync(string xmlRequest)
          {
              using (var content = CreateTallyContent(xmlRequest))
              {
                  var response = await _httpClient.PostAsync(_tallyUrl, content);
                  response.EnsureSuccessStatusCode();
                  return await response.Content.ReadAsStringAsync();
              }
          }
      }
  }
  ```

- [ ] **Step 4: Verify test passes**
  
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`
  Expected: PASS.

- [ ] **Step 5: Commit**
  
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Tally/TallyClient.cs tests/TallyDbLoader.Tests/TallyClientTests.cs
  git commit -m "feat: implement Tally XML client using UTF-16 Unicode HttpClient requests"
  ```

---

### Task 3: Database Bulk Loaders

**Files:**
- Create: `src/TallyDbLoader.Core/DatabaseLoaders/IDatabaseLoader.cs`
- Create: `src/TallyDbLoader.Core/DatabaseLoaders/PostgreSqlLoader.cs`
- Create: `src/TallyDbLoader.Core/DatabaseLoaders/MySqlLoader.cs`
- Create: `src/TallyDbLoader.Core/DatabaseLoaders/MSSqlLoader.cs`

- [ ] **Step 1: Define IDatabaseLoader Interface**
  
  Create `src/TallyDbLoader.Core/DatabaseLoaders/IDatabaseLoader.cs`:
  ```csharp
  using System.Data;
  using System.Threading.Tasks;
  
  namespace TallyDbLoader.Core.DatabaseLoaders
  {
      public interface IDatabaseLoader
      {
          Task LoadBulkDataAsync(DataTable data, string tableName);
      }
  }
  ```

- [ ] **Step 2: Implement Postgres Importer using NpgsqlBinaryImporter**
  
  Create `src/TallyDbLoader.Core/DatabaseLoaders/PostgreSqlLoader.cs`:
  ```csharp
  using System.Data;
  using System.Threading.Tasks;
  using Npgsql;
  
  namespace TallyDbLoader.Core.DatabaseLoaders
  {
      public class PostgreSqlLoader : IDatabaseLoader
      {
          private readonly string _connectionString;
  
          public PostgreSqlLoader(string connectionString)
          {
              _connectionString = connectionString;
          }
  
          public async Task LoadBulkDataAsync(DataTable data, string tableName)
          {
              using (var conn = new NpgsqlConnection(_connectionString))
              {
                  await conn.OpenAsync();
                  
                  // Construct COPY command columns list dynamically
                  var cols = new System.Collections.Generic.List<string>();
                  foreach (DataColumn col in data.Columns) cols.Add($"\"{col.ColumnName}\"");
                  var colString = string.Join(",", cols);
                  
                  using (var writer = await conn.BeginBinaryImportAsync($"COPY \"{tableName}\" ({colString}) FROM STDIN (FORMAT BINARY)"))
                  {
                      foreach (DataRow row in data.Rows)
                      {
                          await writer.StartRowAsync();
                          foreach (DataColumn col in data.Columns)
                          {
                              await writer.WriteAsync(row[col.ColumnName]);
                          }
                      }
                      await writer.CompleteAsync();
                  }
              }
          }
      }
  }
  ```

- [ ] **Step 3: Implement MySQL Importer using MySqlBulkCopy**
  
  Create `src/TallyDbLoader.Core/DatabaseLoaders/MySqlLoader.cs`:
  ```csharp
  using System.Data;
  using System.Threading.Tasks;
  using MySqlConnector;
  
  namespace TallyDbLoader.Core.DatabaseLoaders
  {
      public class MySqlLoader : IDatabaseLoader
      {
          private readonly string _connectionString;
  
          public MySqlLoader(string connectionString)
          {
              _connectionString = connectionString;
          }
  
          public async Task LoadBulkDataAsync(DataTable data, string tableName)
          {
              using (var conn = new MySqlConnection(_connectionString))
              {
                  await conn.OpenAsync();
                  var bulkCopy = new MySqlBulkCopy(conn)
                  {
                      DestinationTableName = tableName
                  };
                  await bulkCopy.WriteToServerAsync(data);
              }
          }
      }
  }
  ```

- [ ] **Step 4: Implement MSSQL Loader using SqlBulkCopy**
  
  Create `src/TallyDbLoader.Core/DatabaseLoaders/MSSqlLoader.cs`:
  ```csharp
  using System.Data;
  using System.Threading.Tasks;
  using Microsoft.Data.SqlClient;
  
  namespace TallyDbLoader.Core.DatabaseLoaders
  {
      public class MSSqlLoader : IDatabaseLoader
      {
          private readonly string _connectionString;
  
          public MSSqlLoader(string connectionString)
          {
              _connectionString = connectionString;
          }
  
          public async Task LoadBulkDataAsync(DataTable data, string tableName)
          {
              using (var bulkCopy = new SqlBulkCopy(_connectionString))
              {
                  bulkCopy.DestinationTableName = tableName;
                  await bulkCopy.WriteToServerAsync(data);
              }
          }
      }
  }
  ```

- [ ] **Step 5: Compile solution to verify loaders compile correctly**
  
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Successful compilation with 0 errors.

- [ ] **Step 6: Commit**
  
  Run:
  ```bash
  git add src/TallyDbLoader.Core/DatabaseLoaders/
  git commit -m "feat: implement high-performance C# bulk data loaders for PostgreSQL, MySQL, and MSSQL"
  ```

---

### Task 4: Single-Instance Mutex Startup & App Lifecycle

**Files:**
- Create: `src/TallyDbLoader.Wpf/App.xaml.cs`

- [ ] **Step 1: Implement App.xaml.cs containing single instance validation & Mutex**
  
  Modify `src/TallyDbLoader.Wpf/App.xaml.cs`:
  ```csharp
  using System;
  using System.Threading;
  using System.Windows;
  
  namespace TallyDbLoader.Wpf
  {
      public partial class App : Application
      {
          private const string AppMutexName = "Global\\TallyToDbLoaderMutex_662bd342-d285-4831-a40d";
          private static Mutex _mutex;
  
          protected override void OnStartup(StartupEventArgs e)
          {
              _mutex = new Mutex(true, AppMutexName, out bool isNewInstance);
  
              if (!isNewInstance)
              {
                  MessageBox.Show("Another instance of Tally-to-Database Sync is already running.", 
                                  "Already Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                  Application.Current.Shutdown();
                  return;
              }
  
              base.OnStartup(e);
          }
  
          protected override void OnExit(ExitEventArgs e)
          {
              if (_mutex != null)
              {
                  _mutex.ReleaseMutex();
                  _mutex.Dispose();
              }
              base.OnExit(e);
          }
      }
  }
  ```

- [ ] **Step 2: Compile solution**
  
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Compile passes.

- [ ] **Step 3: Commit**
  
  Run:
  ```bash
  git add src/TallyDbLoader.Wpf/App.xaml.cs
  git commit -m "feat: implement single-instance process protection using a System Mutex"
  ```
