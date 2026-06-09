# Audit Infrastructure Tracer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the existing `config_audit_log` write behaviour into a private helper in `ConfigRepository`, then use that helper in `ResolveCompanyProfileSafetyState` (no behaviour change) and in `SaveTallySettings` (new audit tracer).

**Architecture:** A private static helper `InsertConfigAuditLog` receives pre-serialised JSON strings and an open connection + transaction, inserts one row, and returns the new audit id. Callers own serialisation, transaction boundaries, and rollback. `SaveTallySettings` gains a mandatory singleton-row guard — if the row is missing it throws before touching anything. All changes stay inside `ConfigRepository.cs`; no public interface changes.

**Tech Stack:** C# · .NET 8 · Microsoft.Data.Sqlite · Dapper · System.Text.Json · xUnit

**Spec:** `docs/superpowers/specs/2026-06-09-audit-infrastructure-tracer-design.md`

---

## File Structure

- **Modify:** `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
  - Add private static `InsertConfigAuditLog(...)` helper
  - Refactor `ResolveCompanyProfileSafetyState` to call the helper
  - Update `SaveTallySettings` to load singleton row, guard, upsert, audit, commit
- **Modify:** `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`
  - Add 6 new `[Fact]` tests for `SaveTallySettings` audit behaviour

No other files are touched.

---

### Task 1: Add `InsertConfigAuditLog` private helper

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`

The helper accepts pre-serialised JSON (callers must serialise before calling). It validates required string fields, inserts one row into `config_audit_log`, reads `last_insert_rowid()` on the same connection, and returns the new audit id. Any insert or rowid-read failure is wrapped in `InvalidOperationException` with the original exception preserved as `InnerException`.

- [ ] **Step 1: Add the helper method to ConfigRepository**

  Open `src/TallyDbLoader.Core/Data/ConfigRepository.cs`. Insert the following private static method immediately before the closing `}` of the class (after `ResolveCompanyProfileSafetyState`, before the final `}`):

  ```csharp
  private static long InsertConfigAuditLog(
      SqliteConnection conn,
      SqliteTransaction transaction,
      DateTime createdAt,
      string actor,
      string action,
      string entityType,
      int entityId,
      string? entityName,
      string beforeJson,
      string afterJson,
      string reason)
  {
      if (string.IsNullOrWhiteSpace(actor))
          throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
      if (string.IsNullOrWhiteSpace(action))
          throw new ArgumentException("Action cannot be null or empty.", nameof(action));
      if (string.IsNullOrWhiteSpace(entityType))
          throw new ArgumentException("EntityType cannot be null or empty.", nameof(entityType));
      if (string.IsNullOrWhiteSpace(reason))
          throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));
      if (string.IsNullOrWhiteSpace(beforeJson))
          throw new ArgumentException("BeforeJson cannot be null or empty.", nameof(beforeJson));
      if (string.IsNullOrWhiteSpace(afterJson))
          throw new ArgumentException("AfterJson cannot be null or empty.", nameof(afterJson));

      try
      {
          conn.Execute(@"
              INSERT INTO config_audit_log (created_at, actor, action, entity_type, entity_id, entity_name, before_json, after_json, reason)
              VALUES (@CreatedAt, @Actor, @Action, @EntityType, @EntityId, @EntityName, @BeforeJson, @AfterJson, @Reason);",
              new
              {
                  CreatedAt = createdAt.ToString("o"),
                  Actor = actor.Trim(),
                  Action = action.Trim(),
                  EntityType = entityType.Trim(),
                  EntityId = entityId,
                  EntityName = entityName,
                  BeforeJson = beforeJson,
                  AfterJson = afterJson,
                  Reason = reason.Trim()
              }, transaction);

          return conn.QuerySingle<long>("SELECT last_insert_rowid();", null, transaction);
      }
      catch (Exception ex)
      {
          throw new InvalidOperationException("Failed to write to the config audit log table.", ex);
      }
  }
  ```

- [ ] **Step 2: Build to verify it compiles**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

  ```
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(audit): add private InsertConfigAuditLog helper in ConfigRepository"
  ```

---

### Task 2: Refactor `ResolveCompanyProfileSafetyState` to use the helper

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`

Replace the inline audit INSERT + rowid SELECT block inside `ResolveCompanyProfileSafetyState` with a call to `InsertConfigAuditLog`. Public signature and all behaviour remain identical.

- [ ] **Step 1: Replace the inline audit block with a helper call**

  In `ConfigRepository.cs`, locate this block inside `ResolveCompanyProfileSafetyState` (the `// 7. Insert audit log row` comment block):

  ```csharp
  // 7. Insert audit log row
  long auditId;
  try
  {
      conn.Execute(@"
          INSERT INTO config_audit_log (created_at, actor, action, entity_type, entity_id, entity_name, before_json, after_json, reason)
          VALUES (@CreatedAt, @Actor, @Action, @EntityType, @EntityId, @EntityName, @BeforeJson, @AfterJson, @Reason);",
          new
          {
              CreatedAt = resolvedAt.ToString("o"),
              Actor = actor.Trim(),
              Action = "resolve_safety_state",
              EntityType = "company_profile",
              EntityId = companyProfileId,
              EntityName = profile.Name,
              BeforeJson = beforeJson,
              AfterJson = afterJson,
              Reason = reason.Trim()
          }, transaction);
      auditId = conn.QuerySingle<long>("SELECT last_insert_rowid();", null, transaction);
  }
  catch (Exception ex)
  {
      throw new InvalidOperationException("Failed to write to the config audit log table.", ex);
  }
  ```

  Replace with:

  ```csharp
  // 7. Insert audit log row via shared helper
  long auditId = InsertConfigAuditLog(
      conn,
      transaction,
      resolvedAt,
      actor,
      "resolve_safety_state",
      "company_profile",
      companyProfileId,
      profile.Name,
      beforeJson,
      afterJson,
      reason);
  ```

- [ ] **Step 2: Build to verify no regressions**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Run the existing safety-state tests to confirm no behaviour change**

  ```
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ResolveCompanyProfileSafetyState"
  ```

  Expected: All 6 `ResolveCompanyProfileSafetyState` tests pass.

- [ ] **Step 4: Commit**

  ```
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "refactor(audit): replace inline audit INSERT in ResolveCompanyProfileSafetyState with shared helper"
  ```

---

### Task 3: Update `SaveTallySettings` with singleton guard and audit write

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`

Replace the entire `SaveTallySettings` method body. New flow:
1. Open connection, begin transaction.
2. Load the current singleton row (`id = 1`). Throw `InvalidOperationException` if missing.
3. Build `before_json` (only `server`, `port`, `auto_start_tally`).
4. Upsert new settings via `INSERT OR REPLACE`.
5. Build `after_json` from the submitted parameter (same three fields, no re-read).
6. Call `InsertConfigAuditLog` — if this fails, rollback via the outer `catch`.
7. Commit.

- [ ] **Step 1: Replace `SaveTallySettings` method body**

  Locate the existing `SaveTallySettings` in `ConfigRepository.cs` and replace it entirely with:

  ```csharp
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
                  // Step 1: Load current singleton row for before_json
                  var current = conn.QueryFirstOrDefault<TallySettings>(@"
                      SELECT id AS Id,
                             server AS Server,
                             port AS Port,
                             auto_start_tally AS AutoStartTally
                      FROM tally_settings
                      WHERE id = 1", transaction: transaction);

                  // Step 2: Guard — singleton row must exist
                  if (current == null)
                      throw new InvalidOperationException(
                          "tally_settings singleton row (id=1) is missing. Database may be corrupt.");

                  // Step 3: Build before_json (compact — server, port, auto_start_tally only)
                  string beforeJson = JsonSerializer.Serialize(new
                  {
                      server = current.Server,
                      port = current.Port,
                      auto_start_tally = current.AutoStartTally
                  });

                  // Step 4: Upsert new settings
                  conn.Execute(@"
                      INSERT OR REPLACE INTO tally_settings (id, server, port, tally_exe_path, tally_ini_path, auto_start_tally)
                      VALUES (1, @Server, @Port, @TallyExePath, @TallyIniPath, @AutoStartTally)",
                      settings, transaction);

                  // Step 5: Build after_json from submitted values (no re-read)
                  string afterJson = JsonSerializer.Serialize(new
                  {
                      server = settings.Server,
                      port = settings.Port,
                      auto_start_tally = settings.AutoStartTally
                  });

                  // Step 6: Write audit row — fail-closed
                  // DEBT: actor is hardcoded to "system" because SaveTallySettings has no actor
                  // context parameter. Operator attribution requires a future signature change
                  // that passes actor from the UI caller into Core.
                  InsertConfigAuditLog(
                      conn,
                      transaction,
                      DateTime.UtcNow,
                      "system",
                      "update_tally_settings",
                      "tally_settings",
                      1,
                      null,
                      beforeJson,
                      afterJson,
                      "Tally settings updated");

                  // Step 7: Commit
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

  Note: `JsonSerializer` is already imported via `using System.Text.Json;` at the top of the file (line 7).

- [ ] **Step 2: Build to verify it compiles**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

  ```
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(audit): add singleton guard and audit write to SaveTallySettings"
  ```

---

### Task 4: Add audit tests in `ConfigRepositoryTests.cs`

**Files:**
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

Add 6 new `[Fact]` methods after `Test_FailClosed_Metadata_Updates_Row_Assertion`. Each test creates its own isolated temporary database using `Path.GetTempPath()` + `Guid.NewGuid()`, matching the existing pattern in the file. Each test cleans up in a `finally` block.

The `using Dapper;` and `using System.IO;` directives are already present at the top of the file.

- [ ] **Step 1: Add 6 new test methods**

  Append the following inside the `ConfigRepositoryTests` class, before its closing `}`:

  ```csharp
  [Fact]
  public void SaveTallySettings_WritesAuditRow()
  {
      string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_audit_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(testDbPath);
          var repo = new ConfigRepository(testDbPath);

          repo.SaveTallySettings(new TallySettings
          {
              Server = "tallyhost",
              Port = 9001,
              AutoStartTally = true,
              TallyExePath = @"C:\Tally\tally.exe",
              TallyIniPath = @"C:\Tally\tally.ini"
          });

          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              int count = conn.ExecuteScalar<int>(
                  "SELECT COUNT(*) FROM config_audit_log WHERE action = 'update_tally_settings'");
              Assert.Equal(1, count);
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
      }
  }

  [Fact]
  public void SaveTallySettings_AuditRow_ContainsOnlyAllowedFields()
  {
      string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_fields_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(testDbPath);
          var repo = new ConfigRepository(testDbPath);

          repo.SaveTallySettings(new TallySettings
          {
              Server = "myserver",
              Port = 9999,
              AutoStartTally = false,
              TallyExePath = @"C:\Tally\tally.exe",
              TallyIniPath = @"C:\Tally\tally.ini"
          });

          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              string beforeJson = conn.ExecuteScalar<string>(
                  "SELECT before_json FROM config_audit_log WHERE action = 'update_tally_settings'");
              string afterJson = conn.ExecuteScalar<string>(
                  "SELECT after_json FROM config_audit_log WHERE action = 'update_tally_settings'");

              // Assert exact property set and count for both snapshots.
              // JsonDocument is used so the test catches extra fields regardless of casing
              // (e.g. an accidental whole-object serialization would emit TallyExePath, not tally_exe_path).
              var allowedProperties = new System.Collections.Generic.HashSet<string>
              {
                  "server", "port", "auto_start_tally"
              };

              using (var beforeDoc = System.Text.Json.JsonDocument.Parse(beforeJson))
              {
                  var beforeProps = beforeDoc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                  Assert.Equal(3, beforeProps.Count);
                  // SetEquals directly expresses set membership — order of enumeration is irrelevant.
                  Assert.True(allowedProperties.SetEquals(beforeProps));
              }

              using (var afterDoc = System.Text.Json.JsonDocument.Parse(afterJson))
              {
                  var afterProps = afterDoc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                  Assert.Equal(3, afterProps.Count);
                  Assert.True(allowedProperties.SetEquals(afterProps));
              }

              // after_json reflects submitted values
              Assert.Contains("\"myserver\"", afterJson);
              Assert.Contains("9999", afterJson);

              // Excluded field names — both snake_case and PascalCase variants — must not appear.
              // Also check path values, which are distinctive enough to catch value leaks.
              foreach (var json in new[] { beforeJson, afterJson })
              {
                  Assert.DoesNotContain("tally_exe_path", json);
                  Assert.DoesNotContain("tally_ini_path", json);
                  Assert.DoesNotContain("TallyExePath", json);
                  Assert.DoesNotContain("TallyIniPath", json);
                  Assert.DoesNotContain("tally.exe", json);
                  Assert.DoesNotContain("tally.ini", json);
              }
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
      }
  }

  [Fact]
  public void SaveTallySettings_AuditRow_BeforeJsonReflectsLoadedRow()
  {
      string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_before_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(testDbPath);
          var repo = new ConfigRepository(testDbPath);

          // First save: establish known prior state
          repo.SaveTallySettings(new TallySettings { Server = "original", Port = 9000, AutoStartTally = false });

          // Second save: before_json must reflect the first save's values ("original"/9000/false)
          repo.SaveTallySettings(new TallySettings { Server = "updated", Port = 9001, AutoStartTally = true });

          // Query the second audit row (the most recent one) without deleting anything.
          // The audit log is append-only; deleting rows from it in tests sets a bad example.
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              string beforeJson = conn.ExecuteScalar<string>(
                  "SELECT before_json FROM config_audit_log WHERE action = 'update_tally_settings' ORDER BY id DESC LIMIT 1");

              Assert.Contains("\"original\"", beforeJson);
              Assert.Contains("9000", beforeJson);
              Assert.Contains("false", beforeJson);
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
      }
  }

  [Fact]
  public void SaveTallySettings_RollsBack_WhenAuditTableMissing()
  {
      string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_rollback_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(testDbPath);
          var repo = new ConfigRepository(testDbPath);

          // Record original settings before the test
          var original = repo.GetTallySettings();

          // Drop audit table to force rollback
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              conn.Execute("DROP TABLE config_audit_log;");
          }

          var ex = Assert.Throws<InvalidOperationException>(() =>
              repo.SaveTallySettings(new TallySettings
              {
                  Server = "should-not-persist",
                  Port = 1234,
                  AutoStartTally = true
              }));

          Assert.NotNull(ex.InnerException);

          // Settings must be unchanged because the entire transaction rolled back
          var after = repo.GetTallySettings();
          Assert.Equal(original.Server, after.Server);
          Assert.Equal(original.Port, after.Port);
          Assert.Equal(original.AutoStartTally, after.AutoStartTally);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
      }
  }

  [Fact]
  public void SaveTallySettings_ThrowsInvalidOperationException_WhenSingletonRowMissing()
  {
      string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_nosingle_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(testDbPath);
          var repo = new ConfigRepository(testDbPath);

          // Remove the singleton row to simulate corrupt database
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              conn.Execute("DELETE FROM tally_settings WHERE id = 1;");
          }

          var ex = Assert.Throws<InvalidOperationException>(() =>
              repo.SaveTallySettings(new TallySettings { Server = "x", Port = 9000, AutoStartTally = false }));

          Assert.Contains("tally_settings singleton row (id=1) is missing", ex.Message);

          // Confirm no audit row was written
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log");
              Assert.Equal(0, count);
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
      }
  }

  [Fact]
  public void SaveTallySettings_AuditRow_HasExpectedMetadata()
  {
      string testDbPath = Path.Combine(Path.GetTempPath(), $"test_tally_meta_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(testDbPath);
          var repo = new ConfigRepository(testDbPath);

          repo.SaveTallySettings(new TallySettings { Server = "localhost", Port = 9000, AutoStartTally = false });

          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}"))
          {
              string actor = conn.ExecuteScalar<string>(
                  "SELECT actor FROM config_audit_log WHERE action = 'update_tally_settings'");
              string action = conn.ExecuteScalar<string>(
                  "SELECT action FROM config_audit_log WHERE action = 'update_tally_settings'");
              string entityType = conn.ExecuteScalar<string>(
                  "SELECT entity_type FROM config_audit_log WHERE action = 'update_tally_settings'");
              long entityId = conn.ExecuteScalar<long>(
                  "SELECT entity_id FROM config_audit_log WHERE action = 'update_tally_settings'");
              string? entityName = conn.ExecuteScalar<string?>(
                  "SELECT entity_name FROM config_audit_log WHERE action = 'update_tally_settings'");
              string reason = conn.ExecuteScalar<string>(
                  "SELECT reason FROM config_audit_log WHERE action = 'update_tally_settings'");

              Assert.Equal("system", actor);
              Assert.Equal("update_tally_settings", action);
              Assert.Equal("tally_settings", entityType);
              Assert.Equal(1L, entityId);
              Assert.Null(entityName);
              Assert.Equal("Tally settings updated", reason);
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(testDbPath)) try { File.Delete(testDbPath); } catch { }
      }
  }
  ```

- [ ] **Step 2: Build to verify the test file compiles**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Run only the new tests**

  ```
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~SaveTallySettings"
  ```

  Expected: `6 passed, 0 failed`.

- [ ] **Step 4: Commit**

  ```
  git add tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "test(audit): add SaveTallySettings audit tests in ConfigRepositoryTests"
  ```

---

### Task 5: Full test suite verification

**Files:** none — verification only.

- [ ] **Step 1: Run full test suite**

  ```
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
  ```

  Expected: All tests pass. Zero failures. The existing `ResolveCompanyProfileSafetyState` tests in `SyncLifecycleSafetyTests.cs` must still pass (they verify Task 2 was behaviour-neutral).

---

## Success Criteria Checklist

- [ ] `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes with 0 failures.
- [ ] `ResolveCompanyProfileSafetyState` behaviour is unchanged (all 6 existing tests pass).
- [ ] `SaveTallySettings` cannot commit without a written audit row.
- [ ] `SaveTallySettings` throws `InvalidOperationException` with message containing `"tally_settings singleton row (id=1) is missing"` when the singleton row is absent.
- [ ] Audit JSON for `update_tally_settings` contains only `server`, `port`, `auto_start_tally`.
- [ ] `tally_exe_path` and `tally_ini_path` are absent from all audit JSON.
- [ ] No WPF code was touched.
- [ ] No database profile or company profile auditing was added.
- [ ] No retention, purge, UI viewer, or import/export code was added.
