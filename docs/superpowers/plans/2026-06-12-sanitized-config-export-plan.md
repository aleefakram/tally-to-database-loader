# Sanitized Configuration Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Add a Core-owned, read-only sanitized configuration export service that writes a versioned JSON envelope for database and company profiles, excluding credentials and runtime state.

**Architecture:** Create a `ConfigExportService` class inside the `TallyDbLoader.Core.Data` namespace. The service takes an `IConfigRepository` and application version in its constructor, projects loaded configurations into a sanitized envelope using anonymous objects, and serializes the envelope using `System.Text.Json` with write indentation.

**Tech Stack:** .NET 8.0, C#, System.Text.Json, xUnit

---

### Task 1: Constructor Validation Tests and Service Setup

**Files:**
- Create: `src/TallyDbLoader.Core/Data/ConfigExportService.cs`
- Create: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`

- [x] **Step 1: Write constructor validation tests**

  Create `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs` with validation tests and a private helper `FakeConfigRepository`:

  ```csharp
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Text.Json;
  using Dapper;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.Models;
  using Xunit;

  namespace TallyDbLoader.Tests
  {
      public class ConfigExportServiceTests
      {
          private class FakeConfigRepository : IConfigRepository
          {
              public List<DatabaseProfile> DatabaseProfiles { get; set; } = new List<DatabaseProfile>();
              public List<CompanyProfile> CompanyProfiles { get; set; } = new List<CompanyProfile>();

              public List<DatabaseProfile> GetAllDatabaseProfiles() => DatabaseProfiles;
              public List<CompanyProfile> GetAllCompanyProfiles() => CompanyProfiles;

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
          }

          [Fact]
          public void Constructor_Throws_WhenParametersAreInvalid()
          {
              var fakeRepo = new FakeConfigRepository();

              Assert.Throws<ArgumentNullException>(() => new ConfigExportService(null!, "1.0.0"));
              Assert.Throws<ArgumentException>(() => new ConfigExportService(fakeRepo, null!));
              Assert.Throws<ArgumentException>(() => new ConfigExportService(fakeRepo, ""));
              Assert.Throws<ArgumentException>(() => new ConfigExportService(fakeRepo, "   "));
          }
      }
  }
  ```

- [x] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigExportServiceTests"`
  Expected: FAIL (Compilation error: ConfigExportService does not exist)

- [x] **Step 3: Write implementation of ConfigExportService**

  Create `src/TallyDbLoader.Core/Data/ConfigExportService.cs`:

  ```csharp
  using System;
  using TallyDbLoader.Core.Models;

  namespace TallyDbLoader.Core.Data
  {
      public sealed class ConfigExportService
      {
          private readonly IConfigRepository _repository;
          private readonly string _applicationVersion;

          public ConfigExportService(IConfigRepository repository, string applicationVersion)
          {
              _repository = repository ?? throw new ArgumentNullException(nameof(repository));
              if (string.IsNullOrWhiteSpace(applicationVersion))
              {
                  throw new ArgumentException("Application version cannot be null, empty, or whitespace.", nameof(applicationVersion));
              }
              _applicationVersion = applicationVersion;
          }

          public string ExportJson(DateTimeOffset exportedAt)
          {
              throw new NotImplementedException();
          }
      }
  }
  ```

- [x] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigExportServiceTests"`
  Expected: PASS

- [x] **Step 5: Commit**

  ```bash
  git add src/TallyDbLoader.Core/Data/ConfigExportService.cs tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs
  git commit -m "feat(config): add ConfigExportService and constructor validations"
  ```

---

### Task 2: JSON Envelope Shape and Empty State Serialization

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigExportService.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`

- [x] **Step 1: Write test for empty state serialization**

  Add this test to `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`:

  ```csharp
          [Fact]
          public void ExportJson_WithEmptyRepository_ReturnsValidEmptyEnvelope()
          {
              var fakeRepo = new FakeConfigRepository();
              var service = new ConfigExportService(fakeRepo, "2.0.0-beta");
              var exportedAt = new DateTimeOffset(2026, 6, 12, 10, 15, 30, TimeSpan.FromHours(5.5));

              string json = service.ExportJson(exportedAt);

              using var doc = JsonDocument.Parse(json);
              var root = doc.RootElement;

              Assert.Equal("tally-db-loader.config-export", root.GetProperty("format").GetString());
              Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
              Assert.Equal("2.0.0-beta", root.GetProperty("application_version").GetString());
              Assert.Equal("2026-06-12T10:15:30.0000000+05:30", root.GetProperty("exported_at").GetString());

              var payload = root.GetProperty("payload");
              Assert.Empty(payload.GetProperty("database_profiles").EnumerateArray());
              Assert.Empty(payload.GetProperty("company_profiles").EnumerateArray());
          }
  ```

- [x] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ExportJson_WithEmptyRepository_ReturnsValidEmptyEnvelope"`
  Expected: FAIL with `NotImplementedException`

- [x] **Step 3: Implement empty state envelope projection**

  Modify `ExportJson` in `src/TallyDbLoader.Core/Data/ConfigExportService.cs`:

  ```csharp
          public string ExportJson(DateTimeOffset exportedAt)
          {
              var envelope = new
              {
                  format = "tally-db-loader.config-export",
                  schema_version = 1,
                  application_version = _applicationVersion,
                  exported_at = exportedAt.ToString("o"),
                  payload = new
                  {
                      database_profiles = new object[0],
                      company_profiles = new object[0]
                  }
              };

              var options = new JsonSerializerOptions
              {
                  WriteIndented = true
              };

              return JsonSerializer.Serialize(envelope, options);
          }
  ```

  *(Make sure to import `System.Text.Json` at the top of the file. Note: Unused repository reads are omitted in this step to avoid compiler warnings.)*

- [x] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigExportServiceTests"`
  Expected: PASS

- [x] **Step 5: Commit**

  ```bash
  git add src/TallyDbLoader.Core/Data/ConfigExportService.cs tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs
  git commit -m "feat(config): serialize versioned JSON envelope and empty payloads"
  ```

---

### Task 3: Sanitized Database Profiles Export

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigExportService.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`

- [x] **Step 1: Write database profile sanitization and shape tests**

  Add this test to `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`:

  ```csharp
          [Fact]
          public void ExportJson_SanitizesSecrets_AndOmittedFields()
          {
              var fakeRepo = new FakeConfigRepository();
              fakeRepo.DatabaseProfiles.Add(new DatabaseProfile
              {
                  Id = 42,
                  Name = "SecretDB",
                  Technology = "mssql",
                  Server = "secret-server",
                  Port = 1433,
                  Username = "sa",
                  Password = "SuperSecretPassword123",
                  LastTestResult = "Passed",
                  LastTestedAt = DateTime.UtcNow,
                  UsedByCount = 5
              });

              var service = new ConfigExportService(fakeRepo, "1.0.0");
              string json = service.ExportJson(DateTimeOffset.Now);

              // Assert secrets are absolutely absent
              Assert.DoesNotContain("SuperSecretPassword123", json);
              Assert.DoesNotContain("dpapi:", json);

              using var doc = JsonDocument.Parse(json);
              var root = doc.RootElement;
              var dbProfiles = root.GetProperty("payload").GetProperty("database_profiles");

              var element = dbProfiles[0];
              Assert.Equal(42, element.GetProperty("id").GetInt32());
              Assert.Equal("SecretDB", element.GetProperty("name").GetString());
              Assert.Equal("mssql", element.GetProperty("technology").GetString());
              Assert.Equal("secret-server", element.GetProperty("server").GetString());
              Assert.Equal(1433, element.GetProperty("port").GetInt32());
              Assert.Equal("sa", element.GetProperty("username").GetString());
              Assert.True(element.GetProperty("has_password").GetBoolean());

              // Enforce exact payload shape
              var allowedProperties = new System.Collections.Generic.HashSet<string>
              {
                  "id", "name", "technology", "server", "port", "username", "has_password"
              };
              var actualProperties = new System.Collections.Generic.HashSet<string>();
              foreach (var prop in element.EnumerateObject())
              {
                  actualProperties.Add(prop.Name);
              }
              Assert.True(allowedProperties.SetEquals(actualProperties), "Database profile keys mismatch");
          }
  ```

- [x] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ExportJson_SanitizesSecrets_AndOmittedFields"`
  Expected: FAIL (Database profiles count matches 0 or fields mismatched)

- [x] **Step 3: Implement database profile projection**

  Modify `ExportJson` in `src/TallyDbLoader.Core/Data/ConfigExportService.cs`:

  ```csharp
          public string ExportJson(DateTimeOffset exportedAt)
          {
              var dbProfiles = _repository.GetAllDatabaseProfiles() ?? new List<DatabaseProfile>();

              var envelope = new
              {
                  format = "tally-db-loader.config-export",
                  schema_version = 1,
                  application_version = _applicationVersion,
                  exported_at = exportedAt.ToString("o"),
                  payload = new
                  {
                      database_profiles = dbProfiles.Select(p => new
                      {
                          id = p.Id,
                          name = p.Name,
                          technology = p.Technology,
                          server = p.Server,
                          port = p.Port,
                          username = p.Username,
                          has_password = !string.IsNullOrEmpty(p.Password)
                      }).ToList(),
                      company_profiles = new object[0]
                  }
              };

              var options = new JsonSerializerOptions
              {
                  WriteIndented = true
              };

              return JsonSerializer.Serialize(envelope, options);
          }
  ```

  *(Make sure to import `System.Linq` and `System.Collections.Generic` at the top of the file.)*

- [x] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigExportServiceTests"`
  Expected: PASS

- [x] **Step 5: Commit**

  ```bash
  git add src/TallyDbLoader.Core/Data/ConfigExportService.cs tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs
  git commit -m "feat(config): project database profiles in export payload without credentials"
  ```

---

### Task 4: Sanitized Company Profiles Export

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigExportService.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`

- [x] **Step 1: Write company profile projection and shape tests**

  Add this test to `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`:

  ```csharp
          [Fact]
          public void ExportJson_ProjectsCompanyProfilesCorrectly()
          {
              var fakeRepo = new FakeConfigRepository();
              fakeRepo.CompanyProfiles.Add(new CompanyProfile
              {
                  Id = 101,
                  Name = "Acme Corp",
                  TallyGuid = "guid-123",
                  Consolidated = true,
                  BooksFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Unspecified),
                  BooksTo = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Unspecified),
                  DbProfileId = 42,
                  TargetCatalog = "acme_db",
                  Schema = "custom_schema",
                  TablePrefix = "custom_",
                  Mode = "incremental",
                  IntervalMinutes = 10,
                  Enabled = true,
                  NotifyOnError = false,
                  PauseOnTallyClose = true,
                  EntityFlags = 7,
                  Status = "ok",
                  LastRunAt = DateTime.UtcNow,
                  LastDurationMs = 200,
                  LastRowsWritten = 1000,
                  ErrorCount24h = 0
              });

              var service = new ConfigExportService(fakeRepo, "1.0.0");
              string json = service.ExportJson(DateTimeOffset.Now);

              using var doc = JsonDocument.Parse(json);
              var root = doc.RootElement;
              var companyProfiles = root.GetProperty("payload").GetProperty("company_profiles");

              var element = companyProfiles[0];
              Assert.Equal(101, element.GetProperty("id").GetInt32());
              Assert.Equal("Acme Corp", element.GetProperty("name").GetString());
              Assert.Equal("guid-123", element.GetProperty("tally_guid").GetString());
              Assert.True(element.GetProperty("consolidated").GetBoolean());
              Assert.Equal("2026-04-01T00:00:00.0000000", element.GetProperty("books_from").GetString());
              Assert.Equal("2026-06-30T00:00:00.0000000", element.GetProperty("books_to").GetString());
              Assert.Equal(42, element.GetProperty("db_profile_id").GetInt32());
              Assert.Equal("acme_db", element.GetProperty("target_catalog").GetString());
              Assert.Equal("custom_schema", element.GetProperty("schema").GetString());
              Assert.Equal("custom_", element.GetProperty("table_prefix").GetString());
              Assert.Equal("incremental", element.GetProperty("mode").GetString());
              Assert.Equal(10, element.GetProperty("interval_minutes").GetInt32());
              Assert.True(element.GetProperty("enabled").GetBoolean());
              Assert.False(element.GetProperty("notify_on_error").GetBoolean());
              Assert.True(element.GetProperty("pause_on_tally_close").GetBoolean());
              Assert.Equal(7, element.GetProperty("entity_flags").GetInt32());

              // Enforce exact payload shape
              var allowedProperties = new System.Collections.Generic.HashSet<string>
              {
                  "id", "name", "tally_guid", "consolidated", "books_from", "books_to",
                  "db_profile_id", "target_catalog", "schema", "table_prefix", "mode",
                  "interval_minutes", "enabled", "notify_on_error", "pause_on_tally_close",
                  "entity_flags"
              };
              var actualProperties = new System.Collections.Generic.HashSet<string>();
              foreach (var prop in element.EnumerateObject())
              {
                  actualProperties.Add(prop.Name);
              }
              Assert.True(allowedProperties.SetEquals(actualProperties), "Company profile keys mismatch");
          }
  ```

- [x] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ExportJson_ProjectsCompanyProfilesCorrectly"`
  Expected: FAIL (Company profiles array is empty)

- [x] **Step 3: Implement company profile projection**

  Modify `ExportJson` in `src/TallyDbLoader.Core/Data/ConfigExportService.cs` to map company profile fields and explicitly format dates:

  ```csharp
          public string ExportJson(DateTimeOffset exportedAt)
          {
              var dbProfiles = _repository.GetAllDatabaseProfiles() ?? new List<DatabaseProfile>();
              var companyProfiles = _repository.GetAllCompanyProfiles() ?? new List<CompanyProfile>();

              var envelope = new
              {
                  format = "tally-db-loader.config-export",
                  schema_version = 1,
                  application_version = _applicationVersion,
                  exported_at = exportedAt.ToString("o"),
                  payload = new
                  {
                      database_profiles = dbProfiles.Select(p => new
                      {
                          id = p.Id,
                          name = p.Name,
                          technology = p.Technology,
                          server = p.Server,
                          port = p.Port,
                          username = p.Username,
                          has_password = !string.IsNullOrEmpty(p.Password)
                      }).ToList(),
                      company_profiles = companyProfiles.Select(c => new
                      {
                          id = c.Id,
                          name = c.Name,
                          tally_guid = c.TallyGuid,
                          consolidated = c.Consolidated,
                          books_from = c.BooksFrom?.ToString("o"),
                          books_to = c.BooksTo?.ToString("o"),
                          db_profile_id = c.DbProfileId,
                          target_catalog = c.TargetCatalog,
                          schema = c.Schema,
                          table_prefix = c.TablePrefix,
                          mode = c.Mode,
                          interval_minutes = c.IntervalMinutes,
                          enabled = c.Enabled,
                          notify_on_error = c.NotifyOnError,
                          pause_on_tally_close = c.PauseOnTallyClose,
                          entity_flags = c.EntityFlags
                      }).ToList()
                  }
              };

              var options = new JsonSerializerOptions
              {
                  WriteIndented = true
              };

              return JsonSerializer.Serialize(envelope, options);
          }
  ```

- [x] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigExportServiceTests"`
  Expected: PASS

- [x] **Step 5: Commit**

  ```bash
  git add src/TallyDbLoader.Core/Data/ConfigExportService.cs tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs
  git commit -m "feat(config): project company profiles in export payload without runtime state"
  ```

---

### Task 5: SQLite Database Integration Testing

**Files:**
- Modify: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`

- [x] **Step 1: Write integration test using a temporary SQLite database**

  Add this test to `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`:

  ```csharp
          [Fact]
          public void ExportJson_WithRealDatabase_WorksCorrectly()
          {
              string testDbPath = Path.Combine(Path.GetTempPath(), $"test_export_real_{Guid.NewGuid()}.db");
              try
              {
                  DatabaseHelper.InitializeDatabase(testDbPath);
                  var repo = new ConfigRepository(testDbPath);

                  var dbProfile = new DatabaseProfile
                  {
                      Name = "RealPostgres",
                      Technology = "postgres",
                      Server = "127.0.0.1",
                      Port = 5432,
                      Username = "user",
                      Password = "RealSecretPassword"
                  };
                  repo.SaveDatabaseProfile(dbProfile);
                  var savedDb = repo.GetDatabaseProfileByName("RealPostgres");
                  Assert.NotNull(savedDb);

                  var company = new CompanyProfile
                  {
                      Name = "Real Company",
                      DbProfileId = savedDb.Id,
                      TargetCatalog = "real_db",
                      BooksFrom = new DateTime(2026, 1, 1),
                      Enabled = true
                  };
                  repo.SaveCompanyProfile(company);

                  int dbCountBefore, companyCountBefore, syncRunsCountBefore, auditLogsCountBefore;
                  using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                  {
                      dbCountBefore = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles");
                      companyCountBefore = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles");
                      syncRunsCountBefore = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sync_runs");
                      auditLogsCountBefore = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log");
                  }

                  var service = new ConfigExportService(repo, "1.2.3");
                  string json = service.ExportJson(DateTimeOffset.Now);

                  int dbCountAfter, companyCountAfter, syncRunsCountAfter, auditLogsCountAfter;
                  using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                  {
                      dbCountAfter = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles");
                      companyCountAfter = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles");
                      syncRunsCountAfter = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sync_runs");
                      auditLogsCountAfter = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log");
                  }

                  // Verify read-only guarantees (no mutations, no new sync runs, no new audit log rows from export)
                  Assert.Equal(dbCountBefore, dbCountAfter);
                  Assert.Equal(companyCountBefore, companyCountAfter);
                  Assert.Equal(syncRunsCountBefore, syncRunsCountAfter);
                  Assert.Equal(auditLogsCountBefore, auditLogsCountAfter);

                  // Assert secrets are absolutely absent
                  Assert.DoesNotContain("RealSecretPassword", json);
                  Assert.DoesNotContain("dpapi:", json);

                  using var doc = JsonDocument.Parse(json);
                  var root = doc.RootElement;
                  Assert.Equal("1.2.3", root.GetProperty("application_version").GetString());

                  var payload = root.GetProperty("payload");
                  var dbs = payload.GetProperty("database_profiles");
                  var comps = payload.GetProperty("company_profiles");

                  Assert.Single(dbs);
                  Assert.Single(comps);

                  Assert.Equal("RealPostgres", dbs[0].GetProperty("name").GetString());

                  // Note: This integration test assumes standard DPAPI works on the Windows local test runner.
                  // If DPAPI decryption fails, has_password will report false, which is accepted in Phase 1.
                  Assert.True(dbs[0].GetProperty("has_password").GetBoolean());
                  Assert.Equal("Real Company", comps[0].GetProperty("name").GetString());
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(testDbPath))
                  {
                      try { File.Delete(testDbPath); } catch { }
                  }
              }
          }
  ```

- [x] **Step 2: Run all tests to verify they pass**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigExportServiceTests"`
  Expected: PASS (All tests compile and run successfully)

- [x] **Step 3: Commit**

  ```bash
  git add tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs
  git commit -m "test(config): add config export integration test using real SQLite database"
  ```
