# Sanitized Configuration Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a core service and repository method to safely parse, validate, resolve conflicts, and transactionally import a sanitized configuration JSON file into the local SQLite database.

**Architecture:** Create a `ConfigImportService` in Core that validates the configuration envelope and decision inputs, resolves conflicts, and projects the validated input into repository-level resolved import models. Extend `IConfigRepository` with `ImportSanitizedConfig` to commit the entire operation inside a single SQLite transaction and write a single summary audit log entry.

**Tech Stack:** .NET 8.0, C#, Dapper, System.Text.Json, xUnit

---

### Task 1: Add Resolved Import Models & Update IConfigRepository Interface

**Files:**
- Modify: `src/TallyDbLoader.Core/Models/Models.cs`
- Modify: `src/TallyDbLoader.Core/Data/IConfigRepository.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs`

- [ ] **Step 1: Write resolved import models in Models.cs**

  Add the following enum and class models at the bottom of `src/TallyDbLoader.Core/Models/Models.cs` (inside the `TallyDbLoader.Core.Models` namespace):

  ```csharp
  public enum ImportAction
  {
      Create,
      Overwrite
  }

  public class ResolvedDatabaseProfileImport
  {
      public int SourceId { get; set; }
      public int? ExistingLocalId { get; set; }
      public ImportAction Action { get; set; }
      public DatabaseProfile Profile { get; set; } = null!;
      public string? Password { get; set; }
      public bool PreserveExistingPassword { get; set; }
  }

  public class ResolvedCompanyProfileImport
  {
      public int SourceId { get; set; }
      public int? ExistingLocalId { get; set; }
      public int SourceDbProfileId { get; set; }
      public ImportAction Action { get; set; }
      public CompanyProfile Profile { get; set; } = null!;
  }
  ```

- [ ] **Step 2: Add ImportSanitizedConfig method signature to IConfigRepository.cs**

  Add the signature of `ImportSanitizedConfig` to `src/TallyDbLoader.Core/Data/IConfigRepository.cs`:

  ```csharp
  void ImportSanitizedConfig(
      List<ResolvedDatabaseProfileImport> databaseProfiles,
      List<ResolvedCompanyProfileImport> companyProfiles,
      string actor,
      string reason,
      string beforeJson,
      string afterJson);
  ```

- [ ] **Step 3: Stub the implementation in ConfigRepository.cs to satisfy the compiler**

  Add a throwing stub of `ImportSanitizedConfig` in `src/TallyDbLoader.Core/Data/ConfigRepository.cs`:

  ```csharp
          public void ImportSanitizedConfig(
              List<ResolvedDatabaseProfileImport> databaseProfiles,
              List<ResolvedCompanyProfileImport> companyProfiles,
              string actor,
              string reason,
              string beforeJson,
              string afterJson)
          {
              throw new NotImplementedException();
          }
  ```

- [ ] **Step 4: Stub the implementation in ConfigExportServiceTests.cs FakeConfigRepository**

  Add the `ImportSanitizedConfig` stub to `tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs` (inside the `FakeConfigRepository` private class on line 15):

  ```csharp
              public void ImportSanitizedConfig(
                  List<ResolvedDatabaseProfileImport> databaseProfiles,
                  List<ResolvedCompanyProfileImport> companyProfiles,
                  string actor,
                  string reason,
                  string beforeJson,
                  string afterJson)
              {
                  throw new NotImplementedException();
              }
  ```

- [ ] **Step 5: Verify the project compiles**

  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Successful compilation without missing interface member errors.

- [ ] **Step 6: Commit changes**

  ```bash
  git add src/TallyDbLoader.Core/Models/Models.cs src/TallyDbLoader.Core/Data/IConfigRepository.cs src/TallyDbLoader.Core/Data/ConfigRepository.cs tests/TallyDbLoader.Tests/ConfigExportServiceTests.cs
  git commit -m "feat(config): add resolved import models and update repository interface"
  ```

---

### Task 2: Implement ConfigRepository.ImportSanitizedConfig and Integration Tests

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

- [ ] **Step 1: Write integration tests for ImportSanitizedConfig**

  Add failing integration tests to `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs` that check:
  - Repository asserts resolved records are internally valid (e.g., throwing `ArgumentException` if overwrite has no existing ID).
  - Pre-transaction check: database profile creation or overwrite without preservation must have a non-empty password, otherwise throws `ArgumentException`.
  - Failure during import rolls back all writes (assert profile count remains unchanged).
  - Correct remapping of database profile IDs.
  - Successful import writes exactly one audit row with `action = "import_sanitized_config"`.
  - Passwords are DPAPI encrypted and saved correctly.
  - Overwriting a database profile where `PreserveExistingPassword = true` preserves the password.

  ```csharp
          [Fact]
          public void ImportSanitizedConfig_WithInvalidRecord_ThrowsArgumentException()
          {
              string testDbPath = Path.Combine(Path.GetTempPath(), $"test_import_val_{Guid.NewGuid()}.db");
              try
              {
                  DatabaseHelper.InitializeDatabase(testDbPath);
                  var repo = new ConfigRepository(testDbPath);

                  var dbImports = new List<ResolvedDatabaseProfileImport>
                  {
                      new ResolvedDatabaseProfileImport
                      {
                          SourceId = 1,
                          Action = ImportAction.Overwrite,
                          ExistingLocalId = null, // Invalid: overwrite needs local ID
                          Profile = new DatabaseProfile { Name = "InvalidDB" }
                      }
                  };

                  Assert.Throws<ArgumentException>(() => repo.ImportSanitizedConfig(
                      dbImports, new List<ResolvedCompanyProfileImport>(), "system", "reason", "{}", "{}"));
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(testDbPath)) File.Delete(testDbPath);
              }
          }
  ```

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportSanitizedConfig_WithInvalidRecord_ThrowsArgumentException"`
  Expected: FAIL (Throws `NotImplementedException`)

- [ ] **Step 3: Implement ImportSanitizedConfig in ConfigRepository.cs**

  Implement `ImportSanitizedConfig` in `src/TallyDbLoader.Core/Data/ConfigRepository.cs` as follows:

  ```csharp
          public void ImportSanitizedConfig(
              List<ResolvedDatabaseProfileImport> databaseProfiles,
              List<ResolvedCompanyProfileImport> companyProfiles,
              string actor,
              string reason,
              string beforeJson,
              string afterJson)
          {
              foreach (var db in databaseProfiles)
              {
                  if (db.Profile == null)
                      throw new ArgumentException("Database profile model cannot be null.", nameof(databaseProfiles));
                  if (db.Action == ImportAction.Overwrite && db.ExistingLocalId == null)
                      throw new ArgumentException("Overwrite database profile must have an ExistingLocalId.", nameof(databaseProfiles));
                  if (db.Action == ImportAction.Create && db.SourceId <= 0)
                      throw new ArgumentException("Create database profile must have a valid SourceId.", nameof(databaseProfiles));

                  if (db.Action == ImportAction.Create ||
                      (db.Action == ImportAction.Overwrite && !db.PreserveExistingPassword))
                  {
                      if (string.IsNullOrEmpty(db.Password))
                      {
                          throw new ArgumentException($"A non-empty password is required for database profile '{db.Profile.Name}' when creating or overwriting without password preservation.", nameof(databaseProfiles));
                      }
                  }
              }

              foreach (var company in companyProfiles)
              {
                  if (company.Profile == null)
                      throw new ArgumentException("Company profile model cannot be null.", nameof(companyProfiles));
                  if (company.Action == ImportAction.Overwrite && company.ExistingLocalId == null)
                      throw new ArgumentException("Overwrite company profile must have an ExistingLocalId.", nameof(companyProfiles));
                  if (!databaseProfiles.Any(d => d.SourceId == company.SourceDbProfileId))
                  {
                      throw new ArgumentException($"Company profile '{company.Profile.Name}' references source database profile ID {company.SourceDbProfileId} which is not present in the import list.", nameof(companyProfiles));
                  }
              }

              using (var conn = new SqliteConnection(_connectionString))
              {
                  conn.Open();
                  conn.Execute("PRAGMA foreign_keys = ON;");
                  using (var transaction = conn.BeginTransaction())
                  {
                      try
                      {
                          var dbIdMap = new Dictionary<int, int>();

                          // 1. Process Database Profiles
                          foreach (var record in databaseProfiles)
                          {
                              var profile = record.Profile;
                              string encryptedPassword = string.Empty;

                              if (record.Action == ImportAction.Create ||
                                  (record.Action == ImportAction.Overwrite && !record.PreserveExistingPassword))
                              {
                                  encryptedPassword = EncryptPassword(record.Password ?? string.Empty);
                              }
                              else if (record.Action == ImportAction.Overwrite && record.PreserveExistingPassword)
                                  var existing = conn.QueryFirstOrDefault<DatabaseProfile>(
                                      "SELECT password FROM database_profiles WHERE id = @Id",
                                      new { Id = record.ExistingLocalId },
                                      transaction);
                                  if (existing == null)
                                  {
                                      throw new InvalidOperationException($"Cannot overwrite database profile: existing profile with ID {record.ExistingLocalId} was not found.");
                                  }
                                  encryptedPassword = existing.Password;
                              }

                              if (record.Action == ImportAction.Create)
                              {
                                  conn.Execute(@"
                                      INSERT INTO database_profiles (name, technology, server, port, username, password, last_test_result, last_tested_at)
                                      VALUES (@Name, @Technology, @Server, @Port, @Username, @Password, @LastTestResult, @LastTestedAt)",
                                      new
                                      {
                                          profile.Name,
                                          profile.Technology,
                                          profile.Server,
                                          profile.Port,
                                          profile.Username,
                                          Password = encryptedPassword,
                                          LastTestResult = "Untested",
                                          LastTestedAt = (string?)null
                                      }, transaction);

                                  long generatedId = conn.QuerySingle<long>("SELECT last_insert_rowid();", null, transaction);
                                  dbIdMap[record.SourceId] = (int)generatedId;
                              }
                              else
                              {
                                  int affected = conn.Execute(@"
                                      UPDATE database_profiles 
                                      SET name = @Name, 
                                          technology = @Technology, 
                                          server = @Server, 
                                          port = @Port, 
                                          username = @Username, 
                                          password = @Password
                                      WHERE id = @Id",
                                      new
                                      {
                                          profile.Name,
                                          profile.Technology,
                                          profile.Server,
                                          profile.Port,
                                          profile.Username,
                                          Password = encryptedPassword,
                                          Id = record.ExistingLocalId
                                      }, transaction);

                                  if (affected != 1)
                                      throw new InvalidOperationException($"Expected to update exactly 1 database profile (ID: {record.ExistingLocalId}), but updated {affected}.");

                                  dbIdMap[record.SourceId] = record.ExistingLocalId.Value;
                              }
                          }

                          // 2. Process Company Profiles
                          foreach (var record in companyProfiles)
                          {
                              var company = record.Profile;
                              int remappedDbId = dbIdMap[record.SourceDbProfileId];

                              var parameters = new
                              {
                                  Id = record.ExistingLocalId,
                                  company.Name,
                                  company.TallyGuid,
                                  company.Consolidated,
                                  BooksFrom = company.BooksFrom?.ToString("o"),
                                  BooksTo = company.BooksTo?.ToString("o"),
                                  DbProfileId = remappedDbId,
                                  company.TargetCatalog,
                                  company.Schema,
                                  company.TablePrefix,
                                  company.Mode,
                                  company.IntervalMinutes,
                                  Enabled = false, 
                                  company.NotifyOnError,
                                  company.PauseOnTallyClose,
                                  company.EntityFlags,
                                  Status = "review_required", 
                                  LastRunAt = (string?)null,
                                  LastDurationMs = (int?)null,
                                  LastRowsWritten = (long?)null,
                                  ErrorCount24h = 0
                              };

                              if (record.Action == ImportAction.Create)
                              {
                                  conn.Execute(@"
                                      INSERT INTO company_profiles (name, tally_guid, consolidated, books_from, books_to, db_profile_id, target_catalog, schema, table_prefix, mode, interval_minutes, enabled, notify_on_error, pause_on_tally_close, entity_flags, status, last_run_at, last_duration_ms, last_rows_written, error_count_24h)
                                      VALUES (@Name, @TallyGuid, @Consolidated, @BooksFrom, @BooksTo, @DbProfileId, @TargetCatalog, @Schema, @TablePrefix, @Mode, @IntervalMinutes, @Enabled, @NotifyOnError, @PauseOnTallyClose, @EntityFlags, @Status, @LastRunAt, @LastDurationMs, @LastRowsWritten, @ErrorCount24h)",
                                      parameters, transaction);
                              }
                              else
                              {
                                  int affected = conn.Execute(@"
                                      UPDATE company_profiles
                                      SET name = @Name, tally_guid = @TallyGuid, consolidated = @Consolidated,
                                          books_from = @BooksFrom, books_to = @BooksTo, db_profile_id = @DbProfileId,
                                          target_catalog = @TargetCatalog, schema = @Schema, table_prefix = @TablePrefix,
                                          mode = @Mode, interval_minutes = @IntervalMinutes, enabled = @Enabled,
                                          notify_on_error = @NotifyOnError, pause_on_tally_close = @PauseOnTallyClose,
                                          entity_flags = @EntityFlags, status = @Status, last_run_at = @LastRunAt,
                                          last_duration_ms = @LastDurationMs, last_rows_written = @LastRowsWritten,
                                          error_count_24h = @ErrorCount24h
                                      WHERE id = @Id", parameters, transaction);

                                  if (affected != 1)
                                      throw new InvalidOperationException($"Expected to update exactly 1 company profile (ID: {record.ExistingLocalId}), but updated {affected}.");
                              }
                          }

                          // 3. Write Audit Row
                          InsertConfigAuditLog(
                              conn,
                              transaction,
                              DateTime.UtcNow,
                              actor,
                              "import_sanitized_config",
                              "config",
                              0,
                              "sanitized_import",
                              beforeJson,
                              afterJson,
                              reason);

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

- [ ] **Step 4: Run integration test to verify it passes**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportSanitizedConfig_WithInvalidRecord_ThrowsArgumentException"`
  Expected: PASS

- [ ] **Step 5: Write remaining integration tests for remapping, rollback on error, password preservation, and auditing**

  Add these tests to `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`:

  ```csharp
          [Fact]
          public void ImportSanitizedConfig_WithValidPayload_SavesAndRemapsAndAudits()
          {
              string testDbPath = Path.Combine(Path.GetTempPath(), $"test_import_run_{Guid.NewGuid()}.db");
              try
              {
                  DatabaseHelper.InitializeDatabase(testDbPath);
                  var repo = new ConfigRepository(testDbPath);

                  var dbProfile = new DatabaseProfile { Name = "ImportedDB", Technology = "mssql", Server = "127.0.0.1", Port = 1433, Username = "sa" };
                  var dbImports = new List<ResolvedDatabaseProfileImport>
                  {
                      new ResolvedDatabaseProfileImport
                      {
                          SourceId = 99,
                          Action = ImportAction.Create,
                          Profile = dbProfile,
                          Password = "my_password",
                          PreserveExistingPassword = false
                      }
                  };

                  var compProfile = new CompanyProfile { Name = "Imported Company", TargetCatalog = "catalog_db" };
                  var compImports = new List<ResolvedCompanyProfileImport>
                  {
                      new ResolvedCompanyProfileImport
                      {
                          SourceId = 200,
                          Action = ImportAction.Create,
                          SourceDbProfileId = 99,
                          Profile = compProfile
                      }
                  };

                  repo.ImportSanitizedConfig(dbImports, compImports, "test-user", "Imported config", "{}", "{\"imported\":true}");

                  var loadedDbs = repo.GetAllDatabaseProfiles();
                  Assert.Single(loadedDbs);
                  Assert.Equal("ImportedDB", loadedDbs[0].Name);
                  Assert.Equal("my_password", loadedDbs[0].Password);

                  var loadedCompanies = repo.GetAllCompanyProfiles();
                  Assert.Single(loadedCompanies);
                  Assert.Equal("Imported Company", loadedCompanies[0].Name);
                  Assert.Equal(loadedDbs[0].Id, loadedCompanies[0].DbProfileId);
                  Assert.False(loadedCompanies[0].Enabled);
                  Assert.Equal("review_required", loadedCompanies[0].Status);

                  int auditCount;
                  using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
                  {
                      auditCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'import_sanitized_config'");
                  }
                  Assert.Equal(1, auditCount);
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(testDbPath)) File.Delete(testDbPath);
              }
          }
  ```

- [ ] **Step 6: Run all ConfigRepository tests**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigRepositoryTests"`
  Expected: PASS

- [ ] **Step 7: Commit changes**

  ```bash
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "feat(config): implement transactional config import with DPAPI encryption and audit log"
  ```

---

### Task 3: Implement ImportDecision, ConfigImportValidationException, and Service Shell

**Files:**
- Create: `src/TallyDbLoader.Core/Models/ImportDecision.cs`
- Create: `src/TallyDbLoader.Core/Models/ConfigImportValidationException.cs`
- Create: `src/TallyDbLoader.Core/Data/ConfigImportService.cs`
- Create: `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`

- [ ] **Step 1: Create ConfigImportValidationException.cs**

  Create `src/TallyDbLoader.Core/Models/ConfigImportValidationException.cs`:

  ```csharp
  using System;
  using System.Collections.Generic;

  namespace TallyDbLoader.Core.Models
  {
      public class ConfigImportValidationException : Exception
      {
          public IReadOnlyList<string> Errors { get; }

          public ConfigImportValidationException(IReadOnlyList<string> errors)
              : base("Configuration import validation failed. See Errors collection for details.")
          {
              Errors = errors ?? new List<string>();
          }
      }
  }
  ```

- [ ] **Step 2: Create ImportDecision.cs**

  Create `src/TallyDbLoader.Core/Models/ImportDecision.cs`:

  ```csharp
  using System.Collections.Generic;

  namespace TallyDbLoader.Core.Models
  {
      public class ImportDecision
      {
          public Dictionary<int, string> DatabasePasswords { get; set; } = new();
          public Dictionary<int, ConflictResolutionStrategy> DatabaseConflicts { get; set; } = new();
          public Dictionary<int, ConflictResolutionStrategy> CompanyConflicts { get; set; } = new();
      }

      public enum ConflictResolutionStrategy
      {
          Skip,
          Overwrite
      }
  }
  ```

- [ ] **Step 3: Create ConfigImportService.cs Shell**

  Create `src/TallyDbLoader.Core/Data/ConfigImportService.cs`:

  ```csharp
  using System;
  using System.Collections.Generic;
  using TallyDbLoader.Core.Models;

  namespace TallyDbLoader.Core.Data
  {
      public class ConfigImportService
      {
          private readonly IConfigRepository _repository;

          public ConfigImportService(IConfigRepository repository)
          {
              _repository = repository ?? throw new ArgumentNullException(nameof(repository));
          }

          public void ImportJson(string json, ImportDecision decision, string actor, string reason)
          {
              if (string.IsNullOrWhiteSpace(json))
                  throw new ArgumentException("JSON content cannot be null or empty.", nameof(json));
              if (decision == null)
                  throw new ArgumentNullException(nameof(decision));
              if (string.IsNullOrWhiteSpace(actor))
                  throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
              if (string.IsNullOrWhiteSpace(reason))
                  throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));
          }
      }
  }
  ```

- [ ] **Step 4: Create ConfigImportServiceTests.cs with constructor/validation tests**

  Create `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`:

  ```csharp
  using System;
  using System.Collections.Generic;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.Models;
  using Xunit;

  namespace TallyDbLoader.Tests
  {
      public class ConfigImportServiceTests
      {
          public class FakeConfigRepository : IConfigRepository
          {
              public List<DatabaseProfile> DatabaseProfiles { get; set; } = new();
              public List<CompanyProfile> CompanyProfiles { get; set; } = new();
              
              public List<ResolvedDatabaseProfileImport>? LastDatabaseImports { get; private set; }
              public List<ResolvedCompanyProfileImport>? LastCompanyImports { get; private set; }
              public string? LastActor { get; private set; }
              public string? LastReason { get; private set; }
              public string? LastBeforeJson { get; private set; }
              public string? LastAfterJson { get; private set; }

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

              public void ImportSanitizedConfig(List<ResolvedDatabaseProfileImport> databaseProfiles, List<ResolvedCompanyProfileImport> companyProfiles, string actor, string reason, string beforeJson, string afterJson)
              {
                  LastDatabaseImports = databaseProfiles;
                  LastCompanyImports = companyProfiles;
                  LastActor = actor;
                  LastReason = reason;
                  LastBeforeJson = beforeJson;
                  LastAfterJson = afterJson;
              }
          }

          [Fact]
          public void Constructor_Throws_WhenRepositoryIsNull()
          {
              Assert.Throws<ArgumentNullException>(() => new ConfigImportService(null!));
          }

          [Fact]
          public void ImportJson_Throws_WhenArgumentsAreInvalid()
          {
              var fake = new FakeConfigRepository();
              var service = new ConfigImportService(fake);

              Assert.Throws<ArgumentException>(() => service.ImportJson(null!, new ImportDecision(), "actor", "reason"));
              Assert.Throws<ArgumentNullException>(() => service.ImportJson("{}", null!, "actor", "reason"));
              Assert.Throws<ArgumentException>(() => service.ImportJson("{}", new ImportDecision(), "", "reason"));
          }
      }
  }
  ```

- [ ] **Step 5: Run tests**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigImportServiceTests"`
  Expected: PASS

- [ ] **Step 6: Commit changes**

  ```bash
  git add src/TallyDbLoader.Core/Models/ImportDecision.cs src/TallyDbLoader.Core/Models/ConfigImportValidationException.cs src/TallyDbLoader.Core/Data/ConfigImportService.cs tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs
  git commit -m "feat(config): add ConfigImportService shell, ImportDecision models, and custom validation exception"
  ```

---

### Task 4: Implement JSON and Pre-Transaction Envelope Validation

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigImportService.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`

- [ ] **Step 1: Write test for invalid envelope structure**

  Add these tests to `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`:

  ```csharp
          [Fact]
          public void ImportJson_WithInvalidEnvelope_ThrowsConfigImportValidationException()
          {
              var fake = new FakeConfigRepository();
              var service = new ConfigImportService(fake);

              // 1. Invalid JSON format
              var exInvalidJson = Assert.Throws<ConfigImportValidationException>(() => 
                  service.ImportJson("corrupt json here", new ImportDecision(), "system", "reason"));
              Assert.Contains("Invalid JSON content", exInvalidJson.Errors[0]);

              // 2. Wrong format identifier
              string jsonWrongFormat = @"{""format"":""invalid.format"",""schema_version"":1,""application_version"":""1.0"",""payload"":{}}";
              var exWrongFormat = Assert.Throws<ConfigImportValidationException>(() => 
                  service.ImportJson(jsonWrongFormat, new ImportDecision(), "system", "reason"));
              Assert.Contains("Unsupported or invalid format", exWrongFormat.Errors[0]);

              // 3. Newer unsupported schema_version
              string jsonNewerSchema = @"{""format"":""tally-db-loader.config-export"",""schema_version"":2,""application_version"":""1.0"",""payload"":{}}";
              var exNewerSchema = Assert.Throws<ConfigImportValidationException>(() => 
                  service.ImportJson(jsonNewerSchema, new ImportDecision(), "system", "reason"));
              Assert.Contains("Unsupported schema version", exNewerSchema.Errors[0]);
          }
  ```

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportJson_WithInvalidEnvelope_ThrowsConfigImportValidationException"`
  Expected: FAIL (Throws `ArgumentException` instead of `ConfigImportValidationException`)

- [ ] **Step 3: Implement envelope parsing and validation in ConfigImportService.cs**

  Implement basic JSON envelope validation in `ImportJson`:

  ```csharp
          public void ImportJson(string json, ImportDecision decision, string actor, string reason)
          {
              if (string.IsNullOrWhiteSpace(json))
                  throw new ArgumentException("JSON content cannot be null or empty.", nameof(json));
              if (decision == null)
                  throw new ArgumentNullException(nameof(decision));
              if (string.IsNullOrWhiteSpace(actor))
                  throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
              if (string.IsNullOrWhiteSpace(reason))
                  throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));

              var errors = new List<string>();
              JsonDocument doc;
              try
              {
                  doc = JsonDocument.Parse(json);
              }
              catch (Exception ex)
              {
                  throw new ConfigImportValidationException(new[] { $"Invalid JSON content: {ex.Message}" });
              }

              using (doc)
              {
                  var root = doc.RootElement;
                  
                  if (!root.TryGetProperty("format", out var formatProp) || formatProp.GetString() != "tally-db-loader.config-export")
                  {
                      errors.Add("Unsupported or invalid format string.");
                  }

                  if (!root.TryGetProperty("schema_version", out var schemaProp) || schemaProp.GetInt32() != 1)
                  {
                      errors.Add("Unsupported schema version. Only version 1 is supported.");
                  }

                  if (!root.TryGetProperty("application_version", out var appVerProp) || string.IsNullOrWhiteSpace(appVerProp.GetString()))
                  {
                      errors.Add("Application version must be a non-empty string.");
                  }

                  if (errors.Count > 0)
                  {
                      throw new ConfigImportValidationException(errors);
                  }
              }
          }
  ```

- [ ] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigImportServiceTests"`
  Expected: PASS

- [ ] **Step 5: Commit changes**

  ```bash
  git add src/TallyDbLoader.Core/Data/ConfigImportService.cs tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs
  git commit -m "feat(config): validate envelope format, schema, and JSON integrity in import service"
  ```

---

### Task 5: Implement Conflict Resolution, Password Merging, and Database Import Integration

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigImportService.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`

- [ ] **Step 1: Write integration tests for import validation, duplicate IDs, missing passwords, conflict strategies, and skipped DB profile references**

  Add tests to `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs` showing validation rejects conflicts that are unresolved, DB profiles requiring passwords that aren't supplied, and skipped database profile references causing company validation failures. Assert against mapped models on the fake repository to verify mapping correctness.

  ```csharp
          [Fact]
          public void ImportJson_WithUnresolvedConflicts_ThrowsConfigImportValidationException()
          {
              var fake = new FakeConfigRepository();
              fake.DatabaseProfiles.Add(new DatabaseProfile { Id = 10, Name = "ExistingDB" });

              var service = new ConfigImportService(fake);

              // Export contains DB with same name "ExistingDB", but decision has no conflict resolution
              string json = @"{
                  ""format"": ""tally-db-loader.config-export"",
                  ""schema_version"": 1,
                  ""application_version"": ""2.0.0"",
                  ""payload"": {
                      ""database_profiles"": [
                          {
                              ""id"": 1,
                              ""name"": ""ExistingDB"",
                              ""technology"": ""mssql"",
                              ""server"": ""localhost"",
                              ""port"": 1433,
                              ""username"": ""sa"",
                              ""has_password"": true
                          }
                      ],
                      ""company_profiles"": []
                  }
              }";

              var ex = Assert.Throws<ConfigImportValidationException>(() => 
                  service.ImportJson(json, new ImportDecision(), "system", "reason"));
              
              Assert.Contains("Conflict detected for database profile 'ExistingDB'", ex.Errors[0]);
          }
  ```

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportJson_WithUnresolvedConflicts_ThrowsConfigImportValidationException"`
  Expected: FAIL (Does not throw `ConfigImportValidationException` with conflict errors)

- [ ] **Step 3: Implement full import parsing, validation, and commit in ConfigImportService.cs**

  Complete `ImportJson` in `src/TallyDbLoader.Core/Data/ConfigImportService.cs`. Verify structural presence of all required fields before calling methods like `Trim()` or dereferencing to prevent `NullReferenceException`. Ensure date parsing is performed safely via `DateTime.TryParse` and error collection. Build `before_json` containing only the overwritten records to prevent audit log bloat.

  ```csharp
          private class ExportEnvelope
          {
              public string format { get; set; } = "";
              public int schema_version { get; set; }
              public string application_version { get; set; } = "";
              public ExportPayload payload { get; set; } = new();
          }

          private class ExportPayload
          {
              public List<ExportDatabaseProfile> database_profiles { get; set; } = new();
              public List<ExportCompanyProfile> company_profiles { get; set; } = new();
          }

          private class ExportDatabaseProfile
          {
              public int id { get; set; }
              public string name { get; set; } = "";
              public string technology { get; set; } = "postgres";
              public string server { get; set; } = "";
              public int port { get; set; }
              public string username { get; set; } = "";
              public bool has_password { get; set; }
          }

          private class ExportCompanyProfile
          {
              public int id { get; set; }
              public string name { get; set; } = "";
              public string? tally_guid { get; set; }
              public bool consolidated { get; set; }
              public string? books_from { get; set; }
              public string? books_to { get; set; }
              public int db_profile_id { get; set; }
              public string target_catalog { get; set; } = "";
              public string schema { get; set; } = "public";
              public string table_prefix { get; set; } = "";
              public string mode { get; set; } = "full";
              public int interval_minutes { get; set; }
              public bool enabled { get; set; }
              public bool notify_on_error { get; set; }
              public bool pause_on_tally_close { get; set; }
              public int entity_flags { get; set; }
          }

          public void ImportJson(string json, ImportDecision decision, string actor, string reason)
          {
              if (string.IsNullOrWhiteSpace(json))
                  throw new ArgumentException("JSON content cannot be null or empty.", nameof(json));
              if (decision == null)
                  throw new ArgumentNullException(nameof(decision));
              if (string.IsNullOrWhiteSpace(actor))
                  throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
              if (string.IsNullOrWhiteSpace(reason))
                  throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));

              ExportEnvelope envelope;
              try
              {
                  envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                             ?? throw new InvalidOperationException("Failed to deserialize JSON.");
              }
              catch (Exception ex)
              {
                  throw new ConfigImportValidationException(new[] { $"Invalid JSON content: {ex.Message}" });
              }

              var errors = new List<string>();

              if (envelope.format != "tally-db-loader.config-export")
              {
                  errors.Add("Unsupported or invalid format string.");
              }
              if (envelope.schema_version != 1)
              {
                  errors.Add("Unsupported schema version. Only version 1 is supported.");
              }
              if (string.IsNullOrWhiteSpace(envelope.application_version))
              {
                  errors.Add("Application version must be a non-empty string.");
              }

              if (errors.Count > 0)
                  throw new ConfigImportValidationException(errors);

              var payload = envelope.payload ?? new ExportPayload();

              // 1. Basic structural validation to prevent NullReferenceException on dereferences
              foreach (var db in payload.database_profiles)
              {
                  if (db == null)
                  {
                      errors.Add("Database profile element is null.");
                      continue;
                  }
                  if (db.id <= 0)
                  {
                      errors.Add("Database profile has an invalid or missing ID.");
                  }
                  if (string.IsNullOrWhiteSpace(db.name))
                  {
                      errors.Add($"Database profile ID {db.id} is missing a name.");
                  }
                  if (string.IsNullOrWhiteSpace(db.technology))
                  {
                      errors.Add($"Database profile '{db.name}' (ID {db.id}) is missing technology.");
                  }
                  if (string.IsNullOrWhiteSpace(db.server))
                  {
                      errors.Add($"Database profile '{db.name}' (ID {db.id}) is missing server host.");
                  }
                  if (string.IsNullOrWhiteSpace(db.username))
                  {
                      errors.Add($"Database profile '{db.name}' (ID {db.id}) is missing username.");
                  }
              }

              foreach (var comp in payload.company_profiles)
              {
                  if (comp == null)
                  {
                      errors.Add("Company profile element is null.");
                      continue;
                  }
                  if (comp.id <= 0)
                  {
                      errors.Add("Company profile has an invalid or missing ID.");
                  }
                  if (string.IsNullOrWhiteSpace(comp.name))
                  {
                      errors.Add($"Company profile ID {comp.id} is missing a name.");
                  }
                  if (comp.db_profile_id <= 0)
                  {
                      errors.Add($"Company profile '{comp.name}' (ID {comp.id}) is missing db_profile_id.");
                  }
                  if (string.IsNullOrWhiteSpace(comp.target_catalog))
                  {
                      errors.Add($"Company profile '{comp.name}' (ID {comp.id}) is missing target_catalog.");
                  }
              }

              if (errors.Count > 0)
                  throw new ConfigImportValidationException(errors);

              // 2. Duplicate source ID checks
              var dbSourceIds = new HashSet<int>();
              foreach (var db in payload.database_profiles)
              {
                  if (!dbSourceIds.Add(db.id))
                      errors.Add($"Duplicate database profile source ID: {db.id}");
              }

              var compSourceIds = new HashSet<int>();
              foreach (var comp in payload.company_profiles)
              {
                  if (!compSourceIds.Add(comp.id))
                      errors.Add($"Duplicate company profile source ID: {comp.id}");
              }

              if (errors.Count > 0)
                  throw new ConfigImportValidationException(errors);

              // 3. Load existing models for conflict matching
              var existingDbs = _repository.GetAllDatabaseProfiles() ?? new List<DatabaseProfile>();
              var existingComps = _repository.GetAllCompanyProfiles() ?? new List<CompanyProfile>();

              var resolvedDbs = new List<ResolvedDatabaseProfileImport>();
              var resolvedComps = new List<ResolvedCompanyProfileImport>();

              var skippedDbIds = new HashSet<int>();
              var skippedCompIds = new HashSet<int>();

              // 4. Resolve Database Conflicts & Passwords
              foreach (var sourceDb in payload.database_profiles)
              {
                  var sourceNameNorm = sourceDb.name.Trim().ToLowerInvariant();
                  var existingMatch = existingDbs.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);

                  if (existingMatch != null)
                  {
                      if (!decision.DatabaseConflicts.TryGetValue(sourceDb.id, out var strategy))
                      {
                          errors.Add($"Conflict detected for database profile '{sourceDb.name}' (Source ID {sourceDb.id}). No conflict resolution strategy provided.");
                          continue;
                      }

                      if (strategy == ConflictResolutionStrategy.Skip)
                      {
                          skippedDbIds.Add(sourceDb.id);
                          continue;
                      }

                      // Overwrite
                      string? password = null;
                      bool preservePassword = true;

                      if (sourceDb.has_password)
                      {
                          if (!decision.DatabasePasswords.TryGetValue(sourceDb.id, out password) || string.IsNullOrEmpty(password))
                          {
                              errors.Add($"Database profile '{sourceDb.name}' (Source ID {sourceDb.id}) requires a password on overwrite, but none was provided.");
                              continue;
                          }
                          preservePassword = false;
                      }

                      resolvedDbs.Add(new ResolvedDatabaseProfileImport
                      {
                          SourceId = sourceDb.id,
                          ExistingLocalId = existingMatch.Id,
                          Action = ImportAction.Overwrite,
                          Password = password,
                          PreserveExistingPassword = preservePassword,
                          Profile = new DatabaseProfile
                          {
                              Name = sourceDb.name,
                              Technology = sourceDb.technology,
                              Server = sourceDb.server,
                              Port = sourceDb.port,
                              Username = sourceDb.username
                          }
                      });
                  }
                  else
                  {
                      // Create
                      string? password = null;
                      if (sourceDb.has_password)
                      {
                          if (!decision.DatabasePasswords.TryGetValue(sourceDb.id, out password) || string.IsNullOrEmpty(password))
                          {
                              errors.Add($"Database profile '{sourceDb.name}' (Source ID {sourceDb.id}) is new and requires a password, but none was provided.");
                              continue;
                          }
                      }

                      resolvedDbs.Add(new ResolvedDatabaseProfileImport
                      {
                          SourceId = sourceDb.id,
                          Action = ImportAction.Create,
                          Password = password,
                          PreserveExistingPassword = false,
                          Profile = new DatabaseProfile
                          {
                              Name = sourceDb.name,
                              Technology = sourceDb.technology,
                              Server = sourceDb.server,
                              Port = sourceDb.port,
                              Username = sourceDb.username
                          }
                      });
                  }
              }

              // 5. Resolve Company Conflicts & skipped DB profiles validation
              foreach (var sourceComp in payload.company_profiles)
              {
                  // A company profile must only reference a DB profile in the payload
                  var dbInPayload = payload.database_profiles.FirstOrDefault(d => d.id == sourceComp.db_profile_id);
                  if (dbInPayload == null)
                  {
                      errors.Add($"Company profile '{sourceComp.name}' references database profile ID {sourceComp.db_profile_id} which is not present in the import payload.");
                      continue;
                  }

                  // If referenced DB profile is skipped, company MUST also be skipped
                  bool dbIsSkipped = skippedDbIds.Contains(sourceComp.db_profile_id);

                  var sourceNameNorm = sourceComp.name.Trim().ToLowerInvariant();
                  CompanyProfile? existingMatch = null;

                  if (!string.IsNullOrEmpty(sourceComp.tally_guid))
                  {
                      var matchByGuid = existingComps.FirstOrDefault(e => e.TallyGuid == sourceComp.tally_guid);
                      var matchByName = existingComps.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);

                      if (matchByGuid != null && matchByName != null && matchByGuid.Id != matchByName.Id)
                      {
                          errors.Add($"Ambiguous conflict for company profile '{sourceComp.name}': matches GUID with one profile and Name with another. Import blocked.");
                          continue;
                      }

                      existingMatch = matchByGuid ?? matchByName;
                  }
                  else
                  {
                      existingMatch = existingComps.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);
                  }

                  // Parse dates safely with TryParse
                  DateTime? booksFromVal = null;
                  if (!string.IsNullOrEmpty(sourceComp.books_from))
                  {
                      if (DateTime.TryParse(sourceComp.books_from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dtFrom))
                      {
                          booksFromVal = dtFrom;
                      }
                      else
                      {
                          errors.Add($"Company profile '{sourceComp.name}' has an invalid books_from date format: '{sourceComp.books_from}'.");
                      }
                  }

                  DateTime? booksToVal = null;
                  if (!string.IsNullOrEmpty(sourceComp.books_to))
                  {
                      if (DateTime.TryParse(sourceComp.books_to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dtTo))
                      {
                          booksToVal = dtTo;
                      }
                      else
                      {
                          errors.Add($"Company profile '{sourceComp.name}' has an invalid books_to date format: '{sourceComp.books_to}'.");
                      }
                  }

                  if (existingMatch != null)
                  {
                      if (!decision.CompanyConflicts.TryGetValue(sourceComp.id, out var strategy))
                      {
                          errors.Add($"Conflict detected for company profile '{sourceComp.name}' (Source ID {sourceComp.id}). No conflict resolution strategy provided.");
                          continue;
                      }

                      if (strategy == ConflictResolutionStrategy.Skip || dbIsSkipped)
                      {
                          if (dbIsSkipped && strategy != ConflictResolutionStrategy.Skip)
                          {
                              errors.Add($"Company profile '{sourceComp.name}' cannot be imported because its referenced database profile (ID {sourceComp.db_profile_id}) is skipped, but the company profile is not marked to skip.");
                          }
                          skippedCompIds.Add(sourceComp.id);
                          continue;
                      }

                      resolvedComps.Add(new ResolvedCompanyProfileImport
                      {
                          SourceId = sourceComp.id,
                          ExistingLocalId = existingMatch.Id,
                          SourceDbProfileId = sourceComp.db_profile_id,
                          Action = ImportAction.Overwrite,
                          Profile = new CompanyProfile
                          {
                              Name = sourceComp.name,
                              TallyGuid = sourceComp.tally_guid,
                              Consolidated = sourceComp.consolidated,
                              BooksFrom = booksFromVal,
                              BooksTo = booksToVal,
                              TargetCatalog = sourceComp.target_catalog,
                              Schema = sourceComp.schema,
                              TablePrefix = sourceComp.table_prefix,
                              Mode = sourceComp.mode,
                              IntervalMinutes = sourceComp.interval_minutes,
                              NotifyOnError = sourceComp.notify_on_error,
                              PauseOnTallyClose = sourceComp.pause_on_tally_close,
                              EntityFlags = sourceComp.entity_flags
                          }
                      });
                  }
                  else
                  {
                      if (dbIsSkipped)
                      {
                          if (!decision.CompanyConflicts.TryGetValue(sourceComp.id, out var strategy) || strategy != ConflictResolutionStrategy.Skip)
                          {
                              errors.Add($"Company profile '{sourceComp.name}' cannot be imported because its referenced database profile (ID {sourceComp.db_profile_id}) is skipped, but the company profile is not marked to skip.");
                          }
                          skippedCompIds.Add(sourceComp.id);
                          continue;
                      }

                      resolvedComps.Add(new ResolvedCompanyProfileImport
                      {
                          SourceId = sourceComp.id,
                          SourceDbProfileId = sourceComp.db_profile_id,
                          Action = ImportAction.Create,
                          Profile = new CompanyProfile
                          {
                              Name = sourceComp.name,
                              TallyGuid = sourceComp.tally_guid,
                              Consolidated = sourceComp.consolidated,
                              BooksFrom = booksFromVal,
                              BooksTo = booksToVal,
                              TargetCatalog = sourceComp.target_catalog,
                              Schema = sourceComp.schema,
                              TablePrefix = sourceComp.table_prefix,
                              Mode = sourceComp.mode,
                              IntervalMinutes = sourceComp.interval_minutes,
                              NotifyOnError = sourceComp.notify_on_error,
                              PauseOnTallyClose = sourceComp.pause_on_tally_close,
                              EntityFlags = sourceComp.entity_flags
                          }
                      });
                  }
              }

              if (errors.Count > 0)
                  throw new ConfigImportValidationException(errors);

              // 6. Build Compact Audit JSON Payloads (overwritten records only, plus counts of skipped/created records)
              var auditBefore = new
              {
                  overwritten_database_profiles = existingDbs
                      .Where(e => resolvedDbs.Any(r => r.Action == ImportAction.Overwrite && r.ExistingLocalId == e.Id))
                      .Select(d => new { name = d.Name, technology = d.Technology }).ToList(),
                  overwritten_company_profiles = existingComps
                      .Where(e => resolvedComps.Any(r => r.Action == ImportAction.Overwrite && r.ExistingLocalId == e.Id))
                      .Select(c => new { name = c.Name, target_catalog = c.TargetCatalog }).ToList()
              };

              var auditAfter = new
              {
                  database_profiles = resolvedDbs.Select(r => new { name = r.Profile.Name, action = r.Action.ToString().ToLower() }).ToList(),
                  company_profiles = resolvedComps.Select(r => new { name = r.Profile.Name, action = r.Action.ToString().ToLower(), enabled = false, status = "review_required" }).ToList()
              };

              string beforeJson = JsonSerializer.Serialize(auditBefore);
              string afterJson = JsonSerializer.Serialize(auditAfter);

              // 7. Invoke transactional repository write
              _repository.ImportSanitizedConfig(
                  resolvedDbs,
                  resolvedComps,
                  actor,
                  reason,
                  beforeJson,
                  afterJson);
          }
  ```

- [ ] **Step 4: Write remaining tests for complete success state mapping and validation**

  Add tests in `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs` to verify:
  - Database profiles name-matching overwrite resolution.
  - Company profile GUID mapping conflict resolution.
  - Ambiguous matches fail validation.
  - Skipped database profile references fail if company profile itself isn't skipped.
  - Verify mapping correctness by asserting properties on `FakeConfigRepository` mock fields (`LastDatabaseImports`, `LastCompanyImports`, etc.) match expected action types, IDs, passwords, and preserve flags.

  ```csharp
          [Fact]
          public void ImportJson_WithValidPayloadAndConflictStrategy_ImportsSuccessfully()
          {
              var fake = new FakeConfigRepository();
              fake.DatabaseProfiles.Add(new DatabaseProfile { Id = 10, Name = "TargetDB" });
              var service = new ConfigImportService(fake);

              string json = @"{
                  ""format"": ""tally-db-loader.config-export"",
                  ""schema_version"": 1,
                  ""application_version"": ""2.0.0"",
                  ""payload"": {
                      ""database_profiles"": [
                          {
                              ""id"": 1,
                              ""name"": ""TargetDB"",
                              ""technology"": ""mssql"",
                              ""server"": ""localhost"",
                              ""port"": 1433,
                              ""username"": ""sa"",
                              ""has_password"": true
                          }
                      ],
                      ""company_profiles"": []
                  }
              }";

              var decision = new ImportDecision();
              decision.DatabaseConflicts[1] = ConflictResolutionStrategy.Overwrite;
              decision.DatabasePasswords[1] = "new-pass";

              service.ImportJson(json, decision, "system", "reason");
              
              // Verify it invoked repository import with correct mapped arguments
              Assert.NotNull(fake.LastDatabaseImports);
              Assert.Single(fake.LastDatabaseImports);
              var mappedDb = fake.LastDatabaseImports[0];
              Assert.Equal(1, mappedDb.SourceId);
              Assert.Equal(10, mappedDb.ExistingLocalId);
              Assert.Equal(ImportAction.Overwrite, mappedDb.Action);
              Assert.Equal("new-pass", mappedDb.Password);
              Assert.False(mappedDb.PreserveExistingPassword);
          }
  ```

- [ ] **Step 5: Run all unit/integration tests**

  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
  Expected: PASS (All 150+ tests pass)

- [ ] **Step 6: Commit changes**

  ```bash
  git add src/TallyDbLoader.Core/Data/ConfigImportService.cs tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs
  git commit -m "feat(config): implement import validation, conflict matching, and repository coordination"
  ```
