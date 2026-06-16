# Diagnostic Backup Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Implement a Core-owned diagnostic backup service that creates a point-in-time support ZIP archive including a live-safe copy of the SQLite config database, application logs with read-sharing, best-effort system information, and optional raw XML logs, and records a single successful audit log entry.

**Architecture:** Create a `DiagnosticBackupService` in `TallyDbLoader.Core.Data` and a new `RecordDiagnosticBackupExport` method in `ConfigRepository`. The service manages temporary staging, calls the SQLite Backup API, packages files via `ZipArchive` with relative paths and forward-slash separators, writes a JSON manifest, and audits the result.

**Tech Stack:** C#, .NET Core, Dapper, Microsoft.Data.Sqlite, System.IO.Compression, System.Text.Json, xUnit

---

## File Structure Map

We will create/modify the following files:
- **Modify**: `src/TallyDbLoader.Core/Data/IConfigRepository.cs` (Add narrow auditing method interface)
- **Modify**: `src/TallyDbLoader.Core/Data/ConfigRepository.cs` (Implement auditing method using existing helper)
- **Modify**: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs` (Add stub to internal `FakeConfigRepository`)
- **Modify**: `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs` (Add stub to internal `FakeConfigRepository`)
- **Create**: `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs` (Contains request, result models, validation, zip packaging, SQLite backup, log copy, system info, manifest generation)
- **Modify**: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs` (Unit tests for repository audit insertion)
- **Create**: `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs` (Unit tests for request validation, backup copy, ZIP packaging, manifest generation, audit integration)

---

### Task 1: Audit Log Repository Method and Fakes Update

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/IConfigRepository.cs`
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`

- [x] **Step 1: Write the failing test**
  Add the following test to `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`:
  ```csharp
  [Fact]
  public void RecordDiagnosticBackupExport_WritesAuditRow_WithCorrectMetadata()
  {
      string testDbPath = Path.Combine(Path.GetTempPath(), $"test_diag_audit_{System.Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(testDbPath);
          var repo = new ConfigRepository(testDbPath);

          long auditId = repo.RecordDiagnosticBackupExport(
              actor: "support_engineer",
              reason: "debug connection issues",
              fileName: "tally_diagnostic_20260615_120000.zip",
              fileSizeBytes: 204850L,
              includeRawXml: true,
              logFileCount: 3,
              rawXmlFileCount: 5,
              skippedFileCount: 1,
              createdAt: new System.DateTime(2026, 6, 15, 12, 0, 0, System.DateTimeKind.Utc)
          );

          Assert.True(auditId > 0);

          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              var row = conn.QuerySingleOrDefault<dynamic>(
                  "SELECT * FROM config_audit_log WHERE id = @Id", new { Id = auditId });

              Assert.NotNull(row);
              Assert.Equal("support_engineer", (string)row.actor);
              Assert.Equal("export_diagnostic_backup", (string)row.action);
              Assert.Equal("diagnostic_backup", (string)row.entity_type);
              Assert.Equal(0L, (long)row.entity_id);
              Assert.Equal("tally_diagnostic_20260615_120000.zip", (string)row.entity_name);
              Assert.Equal("{}", (string)row.before_json);
              Assert.Equal("debug connection issues", (string)row.reason);

              string afterJson = (string)row.after_json;
              using (var doc = System.Text.Json.JsonDocument.Parse(afterJson))
              {
                  var root = doc.RootElement;
                  Assert.Equal("tally_diagnostic_20260615_120000.zip", root.GetProperty("file_name").GetString());
                  Assert.Equal(204850L, root.GetProperty("file_size_bytes").GetInt64());
                  Assert.True(root.GetProperty("include_raw_xml").GetBoolean());
                  Assert.Equal(3, root.GetProperty("log_file_count").GetInt32());
                  Assert.Equal(5, root.GetProperty("raw_xml_file_count").GetInt32());
                  Assert.Equal(1, root.GetProperty("skipped_file_count").GetInt32());
              }
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
      }
  }
  ```

- [x] **Step 2: Run test to verify compile failure**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "Name=RecordDiagnosticBackupExport_WritesAuditRow_WithCorrectMetadata"`
  Expected: Compile error because `RecordDiagnosticBackupExport` is not defined in `IConfigRepository`.

- [x] **Step 3: Update interface, repository, and tests fakes**
  Add definition to `src/TallyDbLoader.Core/Data/IConfigRepository.cs`:
  ```csharp
  long RecordDiagnosticBackupExport(
      string actor,
      string reason,
      string fileName,
      long fileSizeBytes,
      bool includeRawXml,
      int logFileCount,
      int rawXmlFileCount,
      int skippedFileCount,
      System.DateTime createdAt);
  ```

  Implement in `src/TallyDbLoader.Core/Data/ConfigRepository.cs`:
  ```csharp
  public long RecordDiagnosticBackupExport(
      string actor,
      string reason,
      string fileName,
      long fileSizeBytes,
      bool includeRawXml,
      int logFileCount,
      int rawXmlFileCount,
      int skippedFileCount,
      System.DateTime createdAt)
  {
      if (string.IsNullOrWhiteSpace(actor))
          throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
      if (string.IsNullOrWhiteSpace(reason))
          throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));
      if (string.IsNullOrWhiteSpace(fileName))
          throw new ArgumentException("FileName cannot be null or empty.", nameof(fileName));

      var payload = new
      {
          file_name = fileName,
          file_size_bytes = fileSizeBytes,
          include_raw_xml = includeRawXml,
          log_file_count = logFileCount,
          raw_xml_file_count = rawXmlFileCount,
          skipped_file_count = skippedFileCount
      };

      string afterJson = JsonSerializer.Serialize(payload);
      string beforeJson = "{}";

      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Open();
          conn.Execute("PRAGMA foreign_keys = ON;");
          using (var transaction = conn.BeginTransaction())
          {
              try
              {
                  long auditId = InsertConfigAuditLog(
                      conn,
                      transaction,
                      createdAt,
                      actor,
                      "export_diagnostic_backup",
                      "diagnostic_backup",
                      0,
                      fileName,
                      beforeJson,
                      afterJson,
                      reason);

                  transaction.Commit();
                  return auditId;
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

  Add stub methods to `FakeConfigRepository` in `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs` (around line 41):
  ```csharp
              public long RecordDiagnosticBackupExport(
                  string actor,
                  string reason,
                  string fileName,
                  long fileSizeBytes,
                  bool includeRawXml,
                  int logFileCount,
                  int rawXmlFileCount,
                  int skippedFileCount,
                  DateTime createdAt) => throw new NotImplementedException();
  ```

  Add stub methods to `FakeConfigRepository` in `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs` (around line 59):
  ```csharp
              public long RecordDiagnosticBackupExport(
                  string actor,
                  string reason,
                  string fileName,
                  long fileSizeBytes,
                  bool includeRawXml,
                  int logFileCount,
                  int rawXmlFileCount,
                  int skippedFileCount,
                  DateTime createdAt) => throw new NotImplementedException();
  ```

- [x] **Step 4: Run test to verify it passes**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "Name=RecordDiagnosticBackupExport_WritesAuditRow_WithCorrectMetadata"`
  Expected: PASS

- [x] **Step 5: Commit**
  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/IConfigRepository.cs src/TallyDbLoader.Core/Data/ConfigRepository.cs tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs
  git commit -m "feat(config): add RecordDiagnosticBackupExport repository implementation and update fakes"
  ```

---

### Task 2: Service Request Models and Validation

**Files:**
- Create: `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`
- Create: `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs`

- [x] **Step 1: Write the failing test**
  Create `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs` with validation tests and our own internal `FakeDiagnosticBackupRepository`:
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.IO;
  using Xunit;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.Models;

  namespace TallyDbLoader.Tests
  {
      public class DiagnosticBackupServiceTests
      {
          public class FakeDiagnosticBackupRepository : IConfigRepository
          {
              public string? LastActor { get; set; }
              public string? LastReason { get; set; }
              public string? LastFileName { get; set; }
              public long LastFileSizeBytes { get; set; }
              public bool LastIncludeRawXml { get; set; }
              public int LastLogFileCount { get; set; }
              public int LastRawXmlFileCount { get; set; }
              public int LastSkippedFileCount { get; set; }
              public DateTime LastCreatedAt { get; set; }
              public long NextAuditId { get; set; } = 42;
              public bool ShouldThrowOnAudit { get; set; }

              public long RecordDiagnosticBackupExport(
                  string actor, string reason, string fileName, long fileSizeBytes,
                  bool includeRawXml, int logFileCount, int rawXmlFileCount, int skippedFileCount,
                  DateTime createdAt)
              {
                  if (ShouldThrowOnAudit)
                      throw new InvalidOperationException("Simulated audit database insertion failure");

                  LastActor = actor;
                  LastReason = reason;
                  LastFileName = fileName;
                  LastFileSizeBytes = fileSizeBytes;
                  LastIncludeRawXml = includeRawXml;
                  LastLogFileCount = logFileCount;
                  LastRawXmlFileCount = rawXmlFileCount;
                  LastSkippedFileCount = skippedFileCount;
                  LastCreatedAt = createdAt;
                  return NextAuditId;
              }

              public List<DatabaseProfile> GetAllDatabaseProfiles() => throw new NotImplementedException();
              public List<CompanyProfile> GetAllCompanyProfiles() => throw new NotImplementedException();
              public void SaveDatabaseProfile(DatabaseProfile profile) => throw new NotImplementedException();
              public DatabaseProfile? GetDatabaseProfileByName(string name) => throw new NotImplementedException();
              public DatabaseProfile? GetDatabaseProfileById(int id) => throw new NotImplementedException();
              public void SaveCompanyProfile(CompanyProfile company) => throw new NotImplementedException();
              public void DeleteCompanyProfile(int id) => throw new NotImplementedException();
              public TallySettings GetTallySettings() => throw new NotImplementedException();
              public void SaveTallySettings(TallySettings settings) => throw new NotImplementedException();
              public void DeleteDatabaseProfile(int id) => throw new NotImplementedException();
              public long AddSyncRun(SyncRun run) => throw new NotImplementedException();
              public List<SyncRun> GetRecentSyncRuns(int limit = 50) => throw new NotImplementedException();
              public List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50) => throw new NotImplementedException();
              public bool TryStartCompanyProfile(int id) => throw new NotImplementedException();
              public void MarkCompanyProfileUnknown(int id, string reason, DateTime now) => throw new NotImplementedException();
              public void CompleteCompanyProfileRun(int id, string finalStatus, DateTime endedAt, int durationMs, long rowsWritten, bool incrementErrorCount) => throw new NotImplementedException();
              public void UpdateSyncRun(SyncRun run) => throw new NotImplementedException();
              public void ReconcileStaleRuns(DateTime now) => throw new NotImplementedException();
              public long ResolveCompanyProfileSafetyState(int companyProfileId, string actor, string reason, DateTime resolvedAt) => throw new NotImplementedException();
              public void ImportSanitizedConfig(List<ResolvedDatabaseProfileImport> databaseProfiles, List<ResolvedCompanyProfileImport> companyProfiles, string actor, string reason, string beforeJson, string afterJson) => throw new NotImplementedException();
          }

          private readonly FakeDiagnosticBackupRepository _repoFake = new FakeDiagnosticBackupRepository();

          [Fact]
          public void Constructor_ThrowsArgumentNullException_WhenRepositoryIsNull()
          {
              Assert.Throws<ArgumentNullException>(() => new DiagnosticBackupService(null!));
          }

          [Fact]
          public void CreateBackup_ThrowsArgumentNullException_WhenRequestIsNull()
          {
              var service = new DiagnosticBackupService(_repoFake);
              Assert.Throws<ArgumentNullException>(() => service.CreateBackup(null!));
          }

          [Theory]
          [InlineData("", "out", "1.0", "actor", "reason")]
          [InlineData("db", "", "1.0", "actor", "reason")]
          [InlineData("db", "out", "", "actor", "reason")]
          [InlineData("db", "out", "1.0", "", "reason")]
          [InlineData("db", "out", "1.0", "actor", "")]
          public void CreateBackup_ThrowsArgumentException_WhenRequiredFieldsAreEmpty(
              string dbPath, string outPath, string appVersion, string actor, string reason)
          {
              var service = new DiagnosticBackupService(_repoFake);
              var req = new DiagnosticBackupRequest
              {
                  ConfigDatabasePath = dbPath,
                  OutputDirectoryPath = outPath,
                  ApplicationVersion = appVersion,
                  Actor = actor,
                  Reason = reason
              };
              Assert.Throws<ArgumentException>(() => service.CreateBackup(req));
          }
      }
  }
  ```

- [x] **Step 2: Run test to verify compilation failure**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~DiagnosticBackupServiceTests"`
  Expected: Compile error because `DiagnosticBackupService` does not exist in `TallyDbLoader.Core.Data`.

- [x] **Step 3: Create request, result models and validation logic**
  Create `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`:
  ```csharp
  using System;
  using System.IO;
  using TallyDbLoader.Core.Models;

  namespace TallyDbLoader.Core.Data
  {
      public sealed class DiagnosticBackupRequest
      {
          public string ConfigDatabasePath { get; init; } = "";
          public string LogDirectoryPath { get; init; } = "";
          public string? RawXmlDirectoryPath { get; init; }
          public string OutputDirectoryPath { get; init; } = "";
          public string ApplicationVersion { get; init; } = "";
          public string Actor { get; init; } = "";
          public string Reason { get; init; } = "";
          public bool IncludeRawXml { get; init; }
          public DateTimeOffset CreatedAt { get; init; }
      }

      public sealed class DiagnosticBackupResult
      {
          public string FilePath { get; init; } = "";
          public string FileName { get; init; } = "";
          public long FileSizeBytes { get; init; }
          public int LogFileCount { get; init; }
          public int RawXmlFileCount { get; init; }
          public long AuditId { get; init; }
      }

      public sealed class DiagnosticBackupService
      {
          private readonly IConfigRepository _repository;

          public DiagnosticBackupService(IConfigRepository repository)
          {
              _repository = repository ?? throw new ArgumentNullException(nameof(repository));
          }

          public DiagnosticBackupResult CreateBackup(DiagnosticBackupRequest request)
          {
              if (request == null)
                  throw new ArgumentNullException(nameof(request));

              if (string.IsNullOrWhiteSpace(request.ConfigDatabasePath))
                  throw new ArgumentException("ConfigDatabasePath is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.OutputDirectoryPath))
                  throw new ArgumentException("OutputDirectoryPath is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.ApplicationVersion))
                  throw new ArgumentException("ApplicationVersion is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.Actor))
                  throw new ArgumentException("Actor is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.Reason))
                  throw new ArgumentException("Reason is required.", nameof(request));

              if (!File.Exists(request.ConfigDatabasePath))
                  throw new FileNotFoundException("Config database file not found.", request.ConfigDatabasePath);
              if (!Directory.Exists(request.OutputDirectoryPath))
                  throw new DirectoryNotFoundException($"Output directory not found: {request.OutputDirectoryPath}");

              if (request.IncludeRawXml)
              {
                  if (string.IsNullOrWhiteSpace(request.RawXmlDirectoryPath))
                      throw new ArgumentException("RawXmlDirectoryPath is required when IncludeRawXml is true.");
                  if (!Directory.Exists(request.RawXmlDirectoryPath))
                      throw new DirectoryNotFoundException($"Raw XML directory not found: {request.RawXmlDirectoryPath}");
              }

              return new DiagnosticBackupResult();
          }
      }
  }
  ```

- [x] **Step 4: Run test to verify it passes**
  Add path checking tests to `DiagnosticBackupServiceTests.cs`:
  ```csharp
          [Fact]
          public void CreateBackup_ThrowsFileNotFound_WhenDbPathDoesNotExist()
          {
              var service = new DiagnosticBackupService(_repoFake);
              var req = new DiagnosticBackupRequest
              {
                  ConfigDatabasePath = "nonexistent_db.db",
                  OutputDirectoryPath = Path.GetTempPath(),
                  ApplicationVersion = "1.0",
                  Actor = "actor",
                  Reason = "reason"
              };
              Assert.Throws<FileNotFoundException>(() => service.CreateBackup(req));
          }

          [Fact]
          public void CreateBackup_ThrowsDirectoryNotFound_WhenOutputDirDoesNotExist()
          {
              string dbPath = Path.Combine(Path.GetTempPath(), $"dummy_db_{Guid.NewGuid()}.db");
              File.WriteAllText(dbPath, "dummy sqlite content");
              try
              {
                  var service = new DiagnosticBackupService(_repoFake);
                  var req = new DiagnosticBackupRequest
                  {
                      ConfigDatabasePath = dbPath,
                      OutputDirectoryPath = @"C:\NonexistentDir_" + Guid.NewGuid(),
                      ApplicationVersion = "1.0",
                      Actor = "actor",
                      Reason = "reason"
                  };
                  Assert.Throws<DirectoryNotFoundException>(() => service.CreateBackup(req));
              }
              finally
              {
                  if (File.Exists(dbPath)) File.Delete(dbPath);
              }
          }
  ```
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~DiagnosticBackupServiceTests"`
  Expected: PASS

- [x] **Step 5: Commit**
  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs
  git commit -m "feat(config): add DiagnosticBackupService skeleton and request validation"
  ```

---

### Task 3: SQLite Online Backup Mechanics and Helper Tests

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`
- Modify: `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs`

- [x] **Step 1: Write the failing test**
  Add test for live SQLite database backup capability in `DiagnosticBackupServiceTests.cs`:
  ```csharp
          [Fact]
          public void PerformSQLiteBackup_CopiesDatabase_SafelyAndSuccessfully()
          {
              string tempDir = Path.Combine(Path.GetTempPath(), $"diag_temp_{Guid.NewGuid()}");
              Directory.CreateDirectory(tempDir);
              string sourceDbPath = Path.Combine(tempDir, "source.db");
              string targetDb = Path.Combine(tempDir, "target.db");

              try
              {
                  DatabaseHelper.InitializeDatabase(sourceDbPath);

                  using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath}"))
                  {
                      conn.Open();
                      conn.Execute("INSERT INTO database_profiles (name, technology, server, port) VALUES ('LiveDb', 'mssql', 'localhost', 1433)");
                  }

                  var service = new DiagnosticBackupService(_repoFake);
                  service.PerformSQLiteBackup(sourceDbPath, targetDb);

                  Assert.True(File.Exists(targetDb));
                  using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={targetDb}"))
                  {
                      conn.Open();
                      int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'LiveDb'");
                      Assert.Equal(1, count);
                  }
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (Directory.Exists(tempDir))
                  {
                      try { Directory.Delete(tempDir, true); } catch { }
                  }
              }
          }
  ```

- [x] **Step 2: Run test to verify compile failure**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "Name=PerformSQLiteBackup_CopiesDatabase_SafelyAndSuccessfully"`
  Expected: Compile error because `PerformSQLiteBackup` is not defined.

- [x] **Step 3: Implement SQLite online backup**
  Add the method to `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`:
  ```csharp
          internal void PerformSQLiteBackup(string sourceDbPath, string destinationDbPath)
          {
              var destDir = Path.GetDirectoryName(destinationDbPath);
              if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
              {
                  Directory.CreateDirectory(destDir);
              }

              using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath}"))
              using (var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destinationDbPath}"))
              {
                  source.Open();
                  destination.Open();
                  source.BackupDatabase(destination);
              }
          }
  ```

- [x] **Step 4: Run test to verify it passes**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "Name=PerformSQLiteBackup_CopiesDatabase_SafelyAndSuccessfully"`
  Expected: PASS

- [x] **Step 5: Commit**
  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs
  git commit -m "feat(config): implement live SQLite Backup API copy"
  ```

---

### Task 4: Packaging and File System Helpers (Logs, Raw XML, System Info)

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`
- Modify: `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs`

- [x] **Step 1: Write the failing test**
  Add test for system info text formatting and security checks:
  ```csharp
          [Fact]
          public void GatherSystemInfo_ReturnsExpectedProperties_WithoutLeakingSecrets()
          {
              var service = new DiagnosticBackupService(_repoFake);
              var req = new DiagnosticBackupRequest
              {
                  ApplicationVersion = "2.0.0-beta",
                  CreatedAt = DateTimeOffset.UtcNow
              };

              string info = service.GenerateSystemInfoText(req);

              Assert.Contains("application_version=2.0.0-beta", info);
              Assert.Contains("os_version=", info);
              Assert.Contains("dotnet_version=", info);
              Assert.Contains("is_64_bit_process=", info);
              Assert.DoesNotContain("password", info);
              Assert.DoesNotContain("dpapi", info);
          }
  ```

- [x] **Step 2: Run test to verify compile failure**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "Name=GatherSystemInfo_ReturnsExpectedProperties_WithoutLeakingSecrets"`
  Expected: Compile error because `GenerateSystemInfoText` is not defined.

- [x] **Step 3: Implement system info, read-shared copy, and recursive scanning**
  Add the methods in `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`:
  ```csharp
          internal string GenerateSystemInfoText(DiagnosticBackupRequest request)
          {
              var sb = new System.Text.StringBuilder();
              sb.AppendLine($"created_at={request.CreatedAt:o}");
              sb.AppendLine($"application_version={request.ApplicationVersion}");
              sb.AppendLine($"os_version={GetSafeEnvironment(() => Environment.OSVersion.ToString())}");
              sb.AppendLine($"machine_name={GetSafeEnvironment(() => Environment.MachineName)}");
              sb.AppendLine($"user_name={GetSafeEnvironment(() => Environment.UserName)}");
              sb.AppendLine($"dotnet_version={GetSafeEnvironment(() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)}");
              sb.AppendLine($"process_name={GetSafeEnvironment(() => System.Diagnostics.Process.GetCurrentProcess().ProcessName)}");
              sb.AppendLine($"processor_count={GetSafeEnvironment(() => Environment.ProcessorCount.ToString(), "0")}");
              sb.AppendLine($"working_set_bytes={GetSafeEnvironment(() => System.Diagnostics.Process.GetCurrentProcess().WorkingSet64.ToString(), "0")}");
              sb.AppendLine($"is_64_bit_process={GetSafeEnvironment(() => Environment.Is64BitProcess.ToString().ToLowerInvariant())}");
              return sb.ToString();
          }

          private string GetSafeEnvironment(Func<string> propertySelector, string fallback = "Unknown")
          {
              try
              {
                  return propertySelector() ?? fallback;
              }
              catch
              {
                  return fallback;
              }
          }

          internal void CopyFileWithReadSharing(string sourcePath, string destinationPath)
          {
              var destDir = Path.GetDirectoryName(destinationPath);
              if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
              {
                  Directory.CreateDirectory(destDir);
              }

              using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
              using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
              {
                  sourceStream.CopyTo(destStream);
              }
          }
  ```

- [x] **Step 4: Run test to verify it passes**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "Name=GatherSystemInfo_ReturnsExpectedProperties_WithoutLeakingSecrets"`
  Expected: PASS

- [x] **Step 5: Commit**
  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs
  git commit -m "feat(config): implement system info format and read-shared copy routines"
  ```

---

### Task 5: ZIP Archiving, Manifest Generation and Validation

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`
- Modify: `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs`

- [x] **Step 1: Write the failing tests**
  Add unit tests for recursive packaging, skipped files tracking, raw XML default exclusion, missing log folder safety, manifest formatting, and security assertions inside `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs`:
  ```csharp
          [Fact]
          public void CreateBackup_PackagesRecursiveStructure_GeneratesValidManifestAndCleansWorkingFiles()
          {
              string tempDir = Path.Combine(Path.GetTempPath(), $"diag_zip_{Guid.NewGuid()}");
              Directory.CreateDirectory(tempDir);
              string sourceDbPath = Path.Combine(tempDir, "source.db");
              string outputDir = Path.Combine(tempDir, "output");
              Directory.CreateDirectory(outputDir);

              string logsDir = Path.Combine(tempDir, "logs");
              Directory.CreateDirectory(logsDir);
              string subLogsDir = Path.Combine(logsDir, "subfolder");
              Directory.CreateDirectory(subLogsDir);

              string xmlDir = Path.Combine(tempDir, "xml");
              Directory.CreateDirectory(xmlDir);
              string subXmlDir = Path.Combine(xmlDir, "subxml");
              Directory.CreateDirectory(subXmlDir);

              try
              {
                  DatabaseHelper.InitializeDatabase(sourceDbPath);

                  // Add dummy records to DB
                  using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath}"))
                  {
                      conn.Open();
                      conn.Execute("INSERT INTO database_profiles (name, technology, server, port, password) VALUES ('TestProfile', 'mssql', 'localhost', 1433, 'dpapi:xyz123')");
                  }

                  // Create nested logs and raw XMLs
                  File.WriteAllText(Path.Combine(logsDir, "root.log"), "Root log content");
                  File.WriteAllText(Path.Combine(subLogsDir, "nested.log"), "Nested log content");

                  File.WriteAllText(Path.Combine(xmlDir, "root.xml"), "<ENVELOPE></ENVELOPE>");
                  File.WriteAllText(Path.Combine(subXmlDir, "nested.xml"), "<DATA></DATA>");

                  var service = new DiagnosticBackupService(_repoFake);
                  var request = new DiagnosticBackupRequest
                  {
                      ConfigDatabasePath = sourceDbPath,
                      LogDirectoryPath = logsDir,
                      RawXmlDirectoryPath = xmlDir,
                      OutputDirectoryPath = outputDir,
                      ApplicationVersion = "2.0.0-beta",
                      Actor = "test_agent",
                      Reason = "troubleshoot",
                      IncludeRawXml = true,
                      CreatedAt = new DateTimeOffset(2026, 6, 15, 15, 30, 0, TimeSpan.FromHours(5.5))
                  };

                  var result = service.CreateBackup(request);

                  Assert.True(File.Exists(result.FilePath));
                  Assert.Equal("tally_diagnostic_20260615_153000.zip", result.FileName);
                  Assert.Equal(2, result.LogFileCount);
                  Assert.Equal(2, result.RawXmlFileCount);

                  // Extract and inspect ZIP contents
                  string extractDir = Path.Combine(tempDir, "extract");
                  Directory.CreateDirectory(extractDir);
                  System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                  Assert.True(File.Exists(Path.Combine(extractDir, "config/config.db")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "logs/root.log")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "logs/subfolder/nested.log")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "system/system_info.txt")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "raw_xml/root.xml")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "raw_xml/subxml/nested.xml")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "manifest.json")));

                  string manifestJson = File.ReadAllText(Path.Combine(extractDir, "manifest.json"));
                  using (var doc = System.Text.Json.JsonDocument.Parse(manifestJson))
                  {
                      var root = doc.RootElement;
                      Assert.Equal("tally-db-loader.diagnostic-backup", root.GetProperty("format").GetString());
                      Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
                      Assert.Equal("2.0.0-beta", root.GetProperty("application_version").GetString());
                      Assert.Equal("2026-06-15T15:30:00.0000000+05:30", root.GetProperty("created_at").GetString());
                      Assert.True(root.GetProperty("include_raw_xml").GetBoolean());

                      var entries = root.GetProperty("entries");
                      Assert.True(entries.GetProperty("config_database").GetBoolean());
                      Assert.True(entries.GetProperty("system_info").GetBoolean());
                      Assert.Equal(2, entries.GetProperty("log_file_count").GetInt32());
                      Assert.Equal(2, entries.GetProperty("raw_xml_file_count").GetInt32());
                      Assert.Equal(0, entries.GetProperty("skipped_file_count").GetInt32());
                  }

                  // Verify SQLite database in zip is readable and contains our record
                  using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(extractDir, "config/config.db")}"))
                  {
                      conn.Open();
                      int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'TestProfile'");
                      Assert.Equal(1, count);
                  }

                  // Assert security requirements: no passwords, no raw XML file contents, or absolute path leaks
                  Assert.DoesNotContain("dpapi:", manifestJson);
                  Assert.DoesNotContain("C:/", manifestJson);
                  Assert.DoesNotContain("C:\\", manifestJson);
                  Assert.DoesNotContain("diag_zip_", manifestJson);
                  Assert.DoesNotContain("<ENVELOPE>", manifestJson);
                  Assert.DoesNotContain("<DATA>", manifestJson);
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (Directory.Exists(tempDir))
                  {
                      try { Directory.Delete(tempDir, true); } catch { }
                  }
              }
          }

          [Fact]
          public void CreateBackup_ExcludeRawXmlByDefault_AndHandlesMissingLogDir()
          {
              string tempDir = Path.Combine(Path.GetTempPath(), $"diag_defaults_{Guid.NewGuid()}");
              Directory.CreateDirectory(tempDir);
              string sourceDbPath = Path.Combine(tempDir, "source.db");
              string outputDir = Path.Combine(tempDir, "output");
              Directory.CreateDirectory(outputDir);
              string nonexistentLogs = Path.Combine(tempDir, "nonexistent_logs");
              string xmlDir = Path.Combine(tempDir, "xml");
              Directory.CreateDirectory(xmlDir);

              try
              {
                  DatabaseHelper.InitializeDatabase(sourceDbPath);
                  File.WriteAllText(Path.Combine(xmlDir, "should_be_ignored.xml"), "<IGNORE />");

                  var service = new DiagnosticBackupService(_repoFake);
                  var request = new DiagnosticBackupRequest
                  {
                      ConfigDatabasePath = sourceDbPath,
                      LogDirectoryPath = nonexistentLogs,
                      RawXmlDirectoryPath = xmlDir,
                      OutputDirectoryPath = outputDir,
                      ApplicationVersion = "1.0",
                      Actor = "defaults_agent",
                      Reason = "test defaults",
                      IncludeRawXml = false,
                      CreatedAt = DateTimeOffset.Now
                  };

                  var result = service.CreateBackup(request);

                  Assert.True(File.Exists(result.FilePath));
                  Assert.Equal(0, result.LogFileCount);
                  Assert.Equal(0, result.RawXmlFileCount);

                  string extractDir = Path.Combine(tempDir, "extract");
                  Directory.CreateDirectory(extractDir);
                  System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                  // Assert XML and Log directories are not created inside the zip
                  Assert.False(Directory.Exists(Path.Combine(extractDir, "raw_xml")));
                  Assert.False(Directory.Exists(Path.Combine(extractDir, "logs")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "config/config.db")));
                  Assert.True(File.Exists(Path.Combine(extractDir, "system/system_info.txt")));
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (Directory.Exists(tempDir))
                  {
                      try { Directory.Delete(tempDir, true); } catch { }
                  }
              }
          }

          [Fact]
          public void CreateBackup_TracksSkippedFiles_WhenReadFailureOccurs()
          {
              string tempDir = Path.Combine(Path.GetTempPath(), $"diag_skipped_{Guid.NewGuid()}");
              Directory.CreateDirectory(tempDir);
              string sourceDbPath = Path.Combine(tempDir, "source.db");
              string outputDir = Path.Combine(tempDir, "output");
              Directory.CreateDirectory(outputDir);
              string logsDir = Path.Combine(tempDir, "logs");
              Directory.CreateDirectory(logsDir);

              try
              {
                  DatabaseHelper.InitializeDatabase(sourceDbPath);

                  string log1 = Path.Combine(logsDir, "app1.log");
                  string log2 = Path.Combine(logsDir, "app2.log");
                  File.WriteAllText(log1, "Log content 1");
                  File.WriteAllText(log2, "Log content 2");

                  // Lock log1 exclusively to simulate a read failure during file system copying
                  using (var lockStream = new FileStream(log1, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                  {
                      var service = new DiagnosticBackupService(_repoFake);
                      var request = new DiagnosticBackupRequest
                      {
                          ConfigDatabasePath = sourceDbPath,
                          LogDirectoryPath = logsDir,
                          OutputDirectoryPath = outputDir,
                          ApplicationVersion = "1.0",
                          Actor = "skipped_agent",
                          Reason = "test skips",
                          IncludeRawXml = false,
                          CreatedAt = DateTimeOffset.Now
                      };

                      var result = service.CreateBackup(request);

                      Assert.Equal(1, result.LogFileCount);
                      Assert.True(result.AuditId > 0);

                      string extractDir = Path.Combine(tempDir, "extract");
                      Directory.CreateDirectory(extractDir);
                      System.IO.Compression.ZipFile.ExtractToDirectory(result.FilePath, extractDir);

                      // Confirm app2.log exists, but app1.log is missing
                      Assert.True(File.Exists(Path.Combine(extractDir, "logs/app2.log")));
                      Assert.False(File.Exists(Path.Combine(extractDir, "logs/app1.log")));

                      string manifestJson = File.ReadAllText(Path.Combine(extractDir, "manifest.json"));
                      using (var doc = System.Text.Json.JsonDocument.Parse(manifestJson))
                      {
                          var root = doc.RootElement;
                          var entries = root.GetProperty("entries");
                          Assert.Equal(1, entries.GetProperty("skipped_file_count").GetInt32());

                          var skippedArray = root.GetProperty("skipped_files");
                          Assert.Single(skippedArray);
                          var item = skippedArray[0].GetString()!;
                          Assert.StartsWith("logs/app1.log: IOException", item);

                          // Ensure absolute paths do not leak through exception details
                          Assert.DoesNotContain("C:/", item);
                          Assert.DoesNotContain("C:\\", item);
                          Assert.DoesNotContain("diag_skipped_", item);
                      }
                  }
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (Directory.Exists(tempDir))
                  {
                      try { Directory.Delete(tempDir, true); } catch { }
                  }
              }
          }
  ```

- [x] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~DiagnosticBackupServiceTests"`
  Expected: FAIL because `CreateBackup` still returns an empty dummy result.

- [x] **Step 3: Implement ZIP bundling and Manifest generation**
  Complete the implementation of `CreateBackup` in `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs` using recursive file scanning:
  ```csharp
          public DiagnosticBackupResult CreateBackup(DiagnosticBackupRequest request)
          {
              if (request == null)
                  throw new ArgumentNullException(nameof(request));

              if (string.IsNullOrWhiteSpace(request.ConfigDatabasePath))
                  throw new ArgumentException("ConfigDatabasePath is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.OutputDirectoryPath))
                  throw new ArgumentException("OutputDirectoryPath is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.ApplicationVersion))
                  throw new ArgumentException("ApplicationVersion is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.Actor))
                  throw new ArgumentException("Actor is required.", nameof(request));
              if (string.IsNullOrWhiteSpace(request.Reason))
                  throw new ArgumentException("Reason is required.", nameof(request));

              if (!File.Exists(request.ConfigDatabasePath))
                  throw new FileNotFoundException("Config database file not found.", request.ConfigDatabasePath);
              if (!Directory.Exists(request.OutputDirectoryPath))
                  throw new DirectoryNotFoundException($"Output directory not found: {request.OutputDirectoryPath}");

              if (request.IncludeRawXml)
              {
                  if (string.IsNullOrWhiteSpace(request.RawXmlDirectoryPath))
                      throw new ArgumentException("RawXmlDirectoryPath is required when IncludeRawXml is true.");
                  if (!Directory.Exists(request.RawXmlDirectoryPath))
                      throw new DirectoryNotFoundException($"Raw XML directory not found: {request.RawXmlDirectoryPath}");
              }

              // Create unique temporary staging folder
              string workingDir = Path.Combine(Path.GetTempPath(), $"tally_diag_working_{Guid.NewGuid()}");
              Directory.CreateDirectory(workingDir);

              string zipFileName = $"tally_diagnostic_{request.CreatedAt:yyyyMMdd_HHmmss}.zip";
              string targetZipPath = Path.Combine(request.OutputDirectoryPath, zipFileName);

              int logFileCount = 0;
              int rawXmlFileCount = 0;
              int skippedFileCount = 0;
              var skippedFiles = new System.Collections.Generic.List<string>();

              try
              {
                  // 1. Perform SQLite online backup
                  string stagingDbPath = Path.Combine(workingDir, "config", "config.db");
                  PerformSQLiteBackup(request.ConfigDatabasePath, stagingDbPath);

                  // 2. Generate System Info
                  string systemInfoText = GenerateSystemInfoText(request);
                  string stagingSystemInfoPath = Path.Combine(workingDir, "system", "system_info.txt");
                  var systemDir = Path.GetDirectoryName(stagingSystemInfoPath);
                  if (!string.IsNullOrEmpty(systemDir)) Directory.CreateDirectory(systemDir);
                  File.WriteAllText(stagingSystemInfoPath, systemInfoText);

                  // 3. Copy logs recursively
                  if (Directory.Exists(request.LogDirectoryPath))
                  {
                      var logFiles = Directory.GetFiles(request.LogDirectoryPath, "*", SearchOption.AllDirectories);
                      System.Array.Sort(logFiles, StringComparer.OrdinalIgnoreCase);

                      foreach (var logFile in logFiles)
                      {
                          string relativeName = Path.GetRelativePath(request.LogDirectoryPath, logFile).Replace('\\', '/');
                          string destLogPath = Path.Combine(workingDir, "logs", relativeName);
                          try
                          {
                              CopyFileWithReadSharing(logFile, destLogPath);
                              logFileCount++;
                          }
                          catch (Exception ex)
                          {
                              skippedFileCount++;
                              // Use ex.GetType().Name to safely exclude machine-specific paths present in Windows exception messages
                              skippedFiles.Add($"logs/{relativeName}: {ex.GetType().Name}");
                          }
                      }
                  }

                  // 4. Copy raw XML recursively if opt-in
                  if (request.IncludeRawXml && Directory.Exists(request.RawXmlDirectoryPath))
                  {
                      var xmlFiles = Directory.GetFiles(request.RawXmlDirectoryPath, "*", SearchOption.AllDirectories);
                      System.Array.Sort(xmlFiles, StringComparer.OrdinalIgnoreCase);

                      foreach (var xmlFile in xmlFiles)
                      {
                          string relativeName = Path.GetRelativePath(request.RawXmlDirectoryPath, xmlFile).Replace('\\', '/');
                          string destXmlPath = Path.Combine(workingDir, "raw_xml", relativeName);
                          try
                          {
                              CopyFileWithReadSharing(xmlFile, destXmlPath);
                              rawXmlFileCount++;
                          }
                          catch (Exception ex)
                          {
                              skippedFileCount++;
                              // Use ex.GetType().Name to safely exclude machine-specific paths present in Windows exception messages
                              skippedFiles.Add($"raw_xml/{relativeName}: {ex.GetType().Name}");
                          }
                      }
                  }

                  // 5. Generate and write Manifest
                  var manifest = new
                  {
                      format = "tally-db-loader.diagnostic-backup",
                      schema_version = 1,
                      application_version = request.ApplicationVersion,
                      created_at = request.CreatedAt.ToString("o"),
                      include_raw_xml = request.IncludeRawXml,
                      entries = new
                      {
                          config_database = true,
                          system_info = true,
                          log_file_count = logFileCount,
                          raw_xml_file_count = rawXmlFileCount,
                          skipped_file_count = skippedFileCount
                      },
                      skipped_files = skippedFiles
                  };

                  string manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                  File.WriteAllText(Path.Combine(workingDir, "manifest.json"), manifestJson);

                  // 6. Archive to output directory using ZipArchive
                  if (File.Exists(targetZipPath))
                  {
                      File.Delete(targetZipPath);
                  }

                  using (var zipStream = new FileStream(targetZipPath, FileMode.Create))
                  using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
                  {
                      AddDirectoryToZip(archive, workingDir, workingDir);
                  }

                  long fileSizeBytes = new FileInfo(targetZipPath).Length;

                  // 7. Audit execution (Task 6 details the RecordDiagnosticBackupExport DB hook integration)
                  long auditId = _repository.RecordDiagnosticBackupExport(
                      request.Actor,
                      request.Reason,
                      zipFileName,
                      fileSizeBytes,
                      request.IncludeRawXml,
                      logFileCount,
                      rawXmlFileCount,
                      skippedFileCount,
                      request.CreatedAt.UtcDateTime);

                  return new DiagnosticBackupResult
                  {
                      FilePath = targetZipPath,
                      FileName = zipFileName,
                      FileSizeBytes = fileSizeBytes,
                      LogFileCount = logFileCount,
                      RawXmlFileCount = rawXmlFileCount,
                      AuditId = auditId
                  };
              }
              finally
              {
                  // Ensure working staging files are always purged
                  if (Directory.Exists(workingDir))
                  {
                      try { Directory.Delete(workingDir, true); } catch { }
                  }
              }
          }

          private void AddDirectoryToZip(System.IO.Compression.ZipArchive archive, string sourceDir, string baseDir)
          {
              var files = Directory.GetFiles(sourceDir);
              System.Array.Sort(files, StringComparer.OrdinalIgnoreCase);

              foreach (var file in files)
              {
                  string relativePath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                  archive.CreateEntryFromFile(file, relativePath);
              }

              var subDirs = Directory.GetDirectories(sourceDir);
              System.Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

              foreach (var subDir in subDirs)
              {
                  AddDirectoryToZip(archive, subDir, baseDir);
              }
          }
  ```

- [x] **Step 4: Run test to verify it passes**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~DiagnosticBackupServiceTests"`
  Expected: PASS

- [x] **Step 5: Commit**
  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs
  git commit -m "feat(config): implement ZIP packaging, manifest serialization, and recursive scanning"
  ```

---

### Task 6: Audit Integration, Fail-Closed, and No-Audit-on-Failure Constraints

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs`
- Modify: `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs`

- [x] **Step 1: Write the failing tests**
  Add unit tests validating audit failures (ZIP kept on disk, throw propagated) and ZIP creation failure (no audit row written) in `tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs`:
  ```csharp
          [Fact]
          public void CreateBackup_PropagatesException_AndLeavesZipOnDisk_WhenAuditFails()
          {
              string tempDir = Path.Combine(Path.GetTempPath(), $"diag_audit_fail_{Guid.NewGuid()}");
              Directory.CreateDirectory(tempDir);
              string sourceDbPath = Path.Combine(tempDir, "source.db");
              string outputDir = Path.Combine(tempDir, "output");
              Directory.CreateDirectory(outputDir);

              try
              {
                  DatabaseHelper.InitializeDatabase(sourceDbPath);

                  var failingRepoFake = new FakeDiagnosticBackupRepository { ShouldThrowOnAudit = true };
                  var service = new DiagnosticBackupService(failingRepoFake);
                  var request = new DiagnosticBackupRequest
                  {
                      ConfigDatabasePath = sourceDbPath,
                      OutputDirectoryPath = outputDir,
                      ApplicationVersion = "1.0",
                      Actor = "actor",
                      Reason = "reason",
                      CreatedAt = DateTimeOffset.Now
                  };

                  Assert.Throws<InvalidOperationException>(() => service.CreateBackup(request));

                  // Verify ZIP file is NOT deleted and remains on disk per the approved spec
                  string zipFileName = $"tally_diagnostic_{request.CreatedAt:yyyyMMdd_HHmmss}.zip";
                  string targetZipPath = Path.Combine(outputDir, zipFileName);
                  Assert.True(File.Exists(targetZipPath));
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (Directory.Exists(tempDir))
                  {
                      try { Directory.Delete(tempDir, true); } catch { }
                  }
              }
          }

          [Fact]
          public void CreateBackup_DoesNotWriteAuditRow_WhenZipCreationThrowsException()
          {
              string tempDir = Path.Combine(Path.GetTempPath(), $"diag_zip_fail_{Guid.NewGuid()}");
              Directory.CreateDirectory(tempDir);
              string sourceDbPath = Path.Combine(tempDir, "source.db");
              string outputDir = Path.Combine(tempDir, "output");
              Directory.CreateDirectory(outputDir);

              try
              {
                  DatabaseHelper.InitializeDatabase(sourceDbPath);

                  // Make ConfigDatabasePath point to an invalid sqlite file, forcing a SQLite backup exception
                  string corruptedDbPath = Path.Combine(tempDir, "corrupted.db");
                  File.WriteAllText(corruptedDbPath, "invalid database data");

                  var service = new DiagnosticBackupService(_repoFake);
                  var request = new DiagnosticBackupRequest
                  {
                      ConfigDatabasePath = corruptedDbPath,
                      OutputDirectoryPath = outputDir,
                      ApplicationVersion = "1.0",
                      Actor = "failing_zip_actor",
                      Reason = "should throw",
                      CreatedAt = DateTimeOffset.Now
                  };

                  Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => service.CreateBackup(request));

                  // Assert that RecordDiagnosticBackupExport was NEVER called on the fake repository
                  Assert.Null(_repoFake.LastActor);
                  Assert.Null(_repoFake.LastFileName);
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (Directory.Exists(tempDir))
                  {
                      try { Directory.Delete(tempDir, true); } catch { }
                  }
              }
          }
  ```

- [x] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~DiagnosticBackupServiceTests"`
  Expected: FAIL (or PASS depending on whether Task 5 implementation already met these constraints. Let's make sure both tests execute and pass).

- [x] **Step 3: Verify and refine final codebase code styling**
  Ensure all unused namespace imports are cleaned up, file formats are correct, and nullable reference types compile cleanly.

- [x] **Step 4: Run the full test suite**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
  Expected: All 100+ tests pass cleanly.

- [x] **Step 5: Commit**
  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/DiagnosticBackupService.cs tests/TallyDbLoader.Tests/DiagnosticBackupServiceTests.cs
  git commit -m "test(config): add unit tests verifying ZIP retention on audit error and no audit on ZIP failure"
  ```
