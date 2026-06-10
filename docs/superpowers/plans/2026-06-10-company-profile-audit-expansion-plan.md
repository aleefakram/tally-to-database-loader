# CompanyProfile Audit Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add fail-closed audit rows to `SaveCompanyProfile` (create and update paths) and `DeleteCompanyProfile`, reusing the existing private `InsertConfigAuditLog` helper and following the same transactional pattern as `SaveTallySettings`.

**Architecture:** All changes are inside `ConfigRepository.cs`. Each of the three mutation methods gains a hand-rolled snapshot SELECT, snapshot serialisation to an explicit anonymous object with snake_case keys, and a call to `InsertConfigAuditLog` before `transaction.Commit()`. If the audit insert fails, the outer `catch` rolls back the mutation. Public interface `IConfigRepository` is unchanged. No WPF files are touched.

**Tech Stack:** C# Â· .NET 8 Â· Microsoft.Data.Sqlite Â· Dapper Â· System.Text.Json Â· xUnit

**Spec:** `docs/superpowers/specs/2026-06-09-company-profile-audit-expansion-design.md`

---

## File Structure

- **Modify:** `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
  - Replace `SaveCompanyProfile` body â€” add `last_insert_rowid()` read on create path, snapshot SELECT + `InsertConfigAuditLog` call on both paths.
  - Replace `DeleteCompanyProfile` body â€” add snapshot SELECT + `InsertConfigAuditLog` call before delete commit.
- **Modify:** `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`
  - Add 13 new `[Fact]` tests after the existing `SaveTallySettings_*` block.

No other files are touched.

---

### Task 1: Replace `SaveCompanyProfile` with audited create + update

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs` (current lines 159â€“236)

The existing method has two branches (`Id == 0` â†’ INSERT, `Id != 0` â†’ UPDATE). Replace the entire method body. Key changes:
- Create path: after INSERT, read `last_insert_rowid()` into `long generatedId`, build `before_json = "{}"` and `after_json` from the submitted object (using `generatedId` for the `id` field), then call `InsertConfigAuditLog`.
- Update path: before UPDATE, load the row using the 16-column snapshot projection from the spec; throw `InvalidOperationException` if missing; build `before_json` from the loaded row; after UPDATE assert `affected == 1`; build `after_json` from the submitted object (not a re-read); call `InsertConfigAuditLog`.

- [ ] **Step 1: Replace `SaveCompanyProfile`**

  Locate the existing `SaveCompanyProfile` method (starts at `public void SaveCompanyProfile(CompanyProfile company)`) and replace it entirely with:

  ```csharp
  public void SaveCompanyProfile(CompanyProfile company)
  {
      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Open();
          conn.Execute("PRAGMA foreign_keys = ON;");
          using (var transaction = conn.BeginTransaction())
          {
              try
              {
                  var status = string.IsNullOrWhiteSpace(company.Status) ? "idle" : company.Status.Trim().ToLowerInvariant();
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
                      Status = status,
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

                      long generatedId = conn.QuerySingle<long>("SELECT last_insert_rowid();", null, transaction);

                      string afterJson = JsonSerializer.Serialize(new
                      {
                          id = generatedId,
                          name = company.Name,
                          tally_guid = company.TallyGuid,
                          consolidated = company.Consolidated,
                          books_from = company.BooksFrom?.ToString("o"),
                          books_to = company.BooksTo?.ToString("o"),
                          db_profile_id = company.DbProfileId,
                          target_catalog = company.TargetCatalog,
                          schema = company.Schema,
                          table_prefix = company.TablePrefix,
                          mode = company.Mode,
                          interval_minutes = company.IntervalMinutes,
                          enabled = company.Enabled,
                          notify_on_error = company.NotifyOnError,
                          pause_on_tally_close = company.PauseOnTallyClose,
                          entity_flags = company.EntityFlags
                      });

                      // DEBT: actor hardcoded â€” no actor context flows from WPF caller yet.
                      InsertConfigAuditLog(conn, transaction, DateTime.UtcNow, "system",
                          "create_company_profile", "company_profile", (int)generatedId,
                          company.Name, "{}", afterJson, "Company profile created");
                  }
                  else
                  {
                      var loaded = conn.QueryFirstOrDefault<CompanyProfile>(@"
                          SELECT
                              id AS Id, name AS Name, tally_guid AS TallyGuid,
                              consolidated AS Consolidated, books_from AS BooksFrom,
                              books_to AS BooksTo, db_profile_id AS DbProfileId,
                              target_catalog AS TargetCatalog, schema AS Schema,
                              table_prefix AS TablePrefix, mode AS Mode,
                              interval_minutes AS IntervalMinutes, enabled AS Enabled,
                              notify_on_error AS NotifyOnError, pause_on_tally_close AS PauseOnTallyClose,
                              entity_flags AS EntityFlags
                          FROM company_profiles WHERE id = @Id;",
                          new { company.Id }, transaction);

                      if (loaded == null)
                          throw new InvalidOperationException(
                              $"Cannot update company profile: no row found with ID {company.Id}.");

                      string beforeJson = JsonSerializer.Serialize(new
                      {
                          id = loaded.Id,
                          name = loaded.Name,
                          tally_guid = loaded.TallyGuid,
                          consolidated = loaded.Consolidated,
                          books_from = loaded.BooksFrom?.ToString("o"),
                          books_to = loaded.BooksTo?.ToString("o"),
                          db_profile_id = loaded.DbProfileId,
                          target_catalog = loaded.TargetCatalog,
                          schema = loaded.Schema,
                          table_prefix = loaded.TablePrefix,
                          mode = loaded.Mode,
                          interval_minutes = loaded.IntervalMinutes,
                          enabled = loaded.Enabled,
                          notify_on_error = loaded.NotifyOnError,
                          pause_on_tally_close = loaded.PauseOnTallyClose,
                          entity_flags = loaded.EntityFlags
                      });

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
                          throw new InvalidOperationException(
                              $"Expected to update exactly 1 company profile (ID: {company.Id}), but updated {affected}.");

                      string afterJson = JsonSerializer.Serialize(new
                      {
                          id = company.Id,
                          name = company.Name,
                          tally_guid = company.TallyGuid,
                          consolidated = company.Consolidated,
                          books_from = company.BooksFrom?.ToString("o"),
                          books_to = company.BooksTo?.ToString("o"),
                          db_profile_id = company.DbProfileId,
                          target_catalog = company.TargetCatalog,
                          schema = company.Schema,
                          table_prefix = company.TablePrefix,
                          mode = company.Mode,
                          interval_minutes = company.IntervalMinutes,
                          enabled = company.Enabled,
                          notify_on_error = company.NotifyOnError,
                          pause_on_tally_close = company.PauseOnTallyClose,
                          entity_flags = company.EntityFlags
                      });

                      InsertConfigAuditLog(conn, transaction, DateTime.UtcNow, "system",
                          "update_company_profile", "company_profile", company.Id,
                          company.Name, beforeJson, afterJson, "Company profile updated");
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
  ```

- [ ] **Step 2: Build to verify it compiles**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

  ```
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(audit): add audit rows to SaveCompanyProfile create and update paths"
  ```

---

### Task 2: Replace `DeleteCompanyProfile` with audited delete

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs` (current lines 296â€“316)

Replace the single-line DELETE with: snapshot SELECT â†’ throw if missing â†’ DELETE â†’ assert affected == 1 â†’ `InsertConfigAuditLog` with `after_json = "{}"` â†’ commit.

- [ ] **Step 1: Replace `DeleteCompanyProfile`**

  Locate the existing `DeleteCompanyProfile` method and replace it entirely with:

  ```csharp
  public void DeleteCompanyProfile(int id)
  {
      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Open();
          conn.Execute("PRAGMA foreign_keys = ON;");
          using (var transaction = conn.BeginTransaction())
          {
              try
              {
                  var loaded = conn.QueryFirstOrDefault<CompanyProfile>(@"
                      SELECT
                          id AS Id, name AS Name, tally_guid AS TallyGuid,
                          consolidated AS Consolidated, books_from AS BooksFrom,
                          books_to AS BooksTo, db_profile_id AS DbProfileId,
                          target_catalog AS TargetCatalog, schema AS Schema,
                          table_prefix AS TablePrefix, mode AS Mode,
                          interval_minutes AS IntervalMinutes, enabled AS Enabled,
                          notify_on_error AS NotifyOnError, pause_on_tally_close AS PauseOnTallyClose,
                          entity_flags AS EntityFlags
                      FROM company_profiles WHERE id = @Id;",
                      new { Id = id }, transaction);

                  if (loaded == null)
                      throw new InvalidOperationException(
                          $"Cannot delete company profile: no row found with ID {id}.");

                  string beforeJson = JsonSerializer.Serialize(new
                  {
                      id = loaded.Id,
                      name = loaded.Name,
                      tally_guid = loaded.TallyGuid,
                      consolidated = loaded.Consolidated,
                      books_from = loaded.BooksFrom?.ToString("o"),
                      books_to = loaded.BooksTo?.ToString("o"),
                      db_profile_id = loaded.DbProfileId,
                      target_catalog = loaded.TargetCatalog,
                      schema = loaded.Schema,
                      table_prefix = loaded.TablePrefix,
                      mode = loaded.Mode,
                      interval_minutes = loaded.IntervalMinutes,
                      enabled = loaded.Enabled,
                      notify_on_error = loaded.NotifyOnError,
                      pause_on_tally_close = loaded.PauseOnTallyClose,
                      entity_flags = loaded.EntityFlags
                  });

                  int affected = conn.Execute(
                      "DELETE FROM company_profiles WHERE id = @Id", new { Id = id }, transaction);

                  if (affected != 1)
                      throw new InvalidOperationException(
                          $"Expected to delete exactly 1 company profile (ID: {id}), but deleted {affected}.");

                  InsertConfigAuditLog(conn, transaction, DateTime.UtcNow, "system",
                      "delete_company_profile", "company_profile", id,
                      loaded.Name, beforeJson, "{}", "Company profile deleted");

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

- [ ] **Step 2: Build to verify it compiles**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

  ```
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(audit): add audit row to DeleteCompanyProfile"
  ```

---
### Task 3: Add create and update audit tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

All new tests use `Path.GetTempPath()` + `Guid.NewGuid()` for isolation. Each has a `finally` block that calls `SqliteConnection.ClearAllPools()` then deletes the file. `using Dapper;`, `using System.IO;`, and `using System.Linq;` are already present.

The private `SetupCompanyProfileDb` helper creates a fresh test database, initialises it, adds a `DatabaseProfile`, and returns the `ConfigRepository` + `dbProfileId`. It is defined once inside the class and reused by all company-profile tests.

- [ ] **Step 1: Add the helper and create/update tests**

  Inside the `ConfigRepositoryTests` class, after `SaveTallySettings_AuditRow_HasExpectedMetadata`, append:

  ```csharp
  // â”€â”€ CompanyProfile audit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  private static (ConfigRepository repo, int dbProfileId) SetupCompanyProfileDb(string testDbPath)
  {
      DatabaseHelper.InitializeDatabase(testDbPath);
      var repo = new ConfigRepository(testDbPath);
      repo.SaveDatabaseProfile(new DatabaseProfile { Name = "TestDb", Technology = "postgres", Server = "localhost" });
      int dbId = (int)new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={testDbPath}")
          .ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'TestDb'");
      return (repo, dbId);
  }

  [Fact]
  public void SaveCompanyProfile_Create_WritesOneAuditRow()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_create_audit_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile { Name = "Alpha", DbProfileId = dbId, TargetCatalog = "alpha_db" });
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'create_company_profile'");
          Assert.Equal(1, count);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveCompanyProfile_Create_AuditUsesGeneratedIdInEntityIdAndAfterJson()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_create_id_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile { Name = "Beta", DbProfileId = dbId, TargetCatalog = "beta_db" });
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          long entityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE action = 'create_company_profile'");
          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_company_profile'");
          long rowId = conn.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Beta'");
          Assert.Equal(rowId, entityId);
          using var doc = System.Text.Json.JsonDocument.Parse(afterJson);
          long idInJson = doc.RootElement.GetProperty("id").GetInt64();
          Assert.Equal(rowId, idInJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveCompanyProfile_Create_BeforeJsonIsEmptyObject()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_create_before_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile { Name = "Gamma", DbProfileId = dbId, TargetCatalog = "gamma_db" });
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'create_company_profile'");
          Assert.Equal("{}", beforeJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveCompanyProfile_Update_WritesOneAuditRow()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_update_audit_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          var cp = new CompanyProfile { Name = "Delta", DbProfileId = dbId, TargetCatalog = "delta_db" };
          repo.SaveCompanyProfile(cp);
          using var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          cp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Delta'");
          cp.Name = "Delta Updated";
          repo.SaveCompanyProfile(cp);
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'update_company_profile'");
          Assert.Equal(1, count);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveCompanyProfile_Update_BeforeJsonReflectsPreMutationState()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_update_before_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          // Step 1: create a known profile
          var cp = new CompanyProfile { Name = "Epsilon", DbProfileId = dbId, TargetCatalog = "eps_db", Mode = "full", IntervalMinutes = 30 };
          repo.SaveCompanyProfile(cp);
          using var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          cp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Epsilon'");
          // Step 2: update it with different values
          cp.Name = "Epsilon V2";
          cp.Mode = "incremental";
          cp.IntervalMinutes = 60;
          repo.SaveCompanyProfile(cp);
          // Step 3: before_json must contain the original values, not the updated ones
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_company_profile'");
          Assert.Contains("\"Epsilon\"", beforeJson);
          Assert.Contains("\"full\"", beforeJson);
          Assert.Contains("30", beforeJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }
  ```

- [ ] **Step 2: Build**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Run new tests**

  ```
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~SaveCompanyProfile"
  ```

  Expected: `5 passed, 0 failed`.

- [ ] **Step 4: Commit**

  ```
  git add tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "test(audit): add SaveCompanyProfile create and update audit tests"
  ```

---

### Task 4: Add delete, rollback, and missing-profile tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

- [ ] **Step 1: Append delete and error-path tests**

  Inside `ConfigRepositoryTests`, after the tests added in Task 3, append:

  ```csharp
  [Fact]
  public void DeleteCompanyProfile_WritesAuditRow_AndRemovesRow()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_delete_audit_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile { Name = "Zeta", DbProfileId = dbId, TargetCatalog = "zeta_db" });
          using var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          int cpId = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Zeta'");
          repo.DeleteCompanyProfile(cpId);
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'delete_company_profile'"));
          Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE id = @Id", new { Id = cpId }));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DeleteCompanyProfile_AfterJsonIsEmptyObject()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_delete_after_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile { Name = "Eta", DbProfileId = dbId, TargetCatalog = "eta_db" });
          using var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          int cpId = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Eta'");
          repo.DeleteCompanyProfile(cpId);
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'delete_company_profile'");
          Assert.Equal("{}", afterJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveCompanyProfile_Create_RollsBack_WhenAuditTableMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_rb_create_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              conn.Execute("DROP TABLE config_audit_log;");
          Assert.Throws<InvalidOperationException>(() =>
              repo.SaveCompanyProfile(new CompanyProfile { Name = "ShouldNotExist", DbProfileId = dbId, TargetCatalog = "x" }));
          using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE name = 'ShouldNotExist'"));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveCompanyProfile_Update_RollsBack_WhenAuditTableMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_rb_update_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          var cp = new CompanyProfile { Name = "OriginalName", DbProfileId = dbId, TargetCatalog = "orig_db" };
          repo.SaveCompanyProfile(cp);
          using var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          cp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'OriginalName'");
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              conn.Execute("DROP TABLE config_audit_log;");
          cp.Name = "ShouldNotUpdate";
          Assert.Throws<InvalidOperationException>(() => repo.SaveCompanyProfile(cp));
          using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE name = 'OriginalName'"));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DeleteCompanyProfile_RollsBack_WhenAuditTableMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_rb_delete_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile { Name = "Theta", DbProfileId = dbId, TargetCatalog = "theta_db" });
          using var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          int cpId = (int)connId.ExecuteScalar<long>("SELECT id FROM company_profiles WHERE name = 'Theta'");
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              conn.Execute("DROP TABLE config_audit_log;");
          Assert.Throws<InvalidOperationException>(() => repo.DeleteCompanyProfile(cpId));
          using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM company_profiles WHERE id = @Id", new { Id = cpId }));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveCompanyProfile_Update_ThrowsInvalidOperationException_WhenProfileMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_miss_upd_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var ex = Assert.Throws<InvalidOperationException>(() =>
              repo.SaveCompanyProfile(new CompanyProfile { Id = 9999, Name = "Ghost", DbProfileId = 1, TargetCatalog = "x" }));
          Assert.Contains("9999", ex.Message);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DeleteCompanyProfile_ThrowsInvalidOperationException_WhenProfileMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_miss_del_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var ex = Assert.Throws<InvalidOperationException>(() => repo.DeleteCompanyProfile(9999));
          Assert.Contains("9999", ex.Message);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }
  ```

- [ ] **Step 2: Build**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Run new tests**

  ```
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~DeleteCompanyProfile|FullyQualifiedName~RollsBack_When|FullyQualifiedName~ThrowsInvalidOperationException_When"
  ```

  Expected: `7 passed, 0 failed`.

- [ ] **Step 4: Commit**

  ```
  git add tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "test(audit): add DeleteCompanyProfile, rollback, and missing-profile tests"
  ```

---

### Task 5: Add snapshot field tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

- [ ] **Step 1: Append snapshot tests**

  Inside `ConfigRepositoryTests`, after the tests added in Task 4, append:

  ```csharp
  [Fact]
  public void CompanyProfileAudit_SnapshotContainsExactlyAllowedFields()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_snap_fields_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile
          {
              Name = "Iota", DbProfileId = dbId, TargetCatalog = "iota_db",
              TallyGuid = "G1", Consolidated = false, Mode = "full",
              IntervalMinutes = 15, Schema = "public", TablePrefix = "tally_",
              Enabled = true, NotifyOnError = true, PauseOnTallyClose = false, EntityFlags = 15
          });
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_company_profile'");
          var allowed = new System.Collections.Generic.HashSet<string>
          {
              "id", "name", "tally_guid", "consolidated", "books_from", "books_to",
              "db_profile_id", "target_catalog", "schema", "table_prefix", "mode",
              "interval_minutes", "enabled", "notify_on_error", "pause_on_tally_close", "entity_flags"
          };
          using var doc = System.Text.Json.JsonDocument.Parse(afterJson);
          var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
          Assert.Equal(16, props.Count);
          Assert.True(allowed.SetEquals(props));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void CompanyProfileAudit_SnapshotExcludesRuntimeFields()
  {
      string path = Path.Combine(Path.GetTempPath(), $"cp_snap_excl_{Guid.NewGuid()}.db");
      try
      {
          var (repo, dbId) = SetupCompanyProfileDb(path);
          repo.SaveCompanyProfile(new CompanyProfile
          {
              Name = "Kappa", DbProfileId = dbId, TargetCatalog = "kappa_db",
              Status = "running", LastRunAt = DateTime.UtcNow,
              LastDurationMs = 1234, LastRowsWritten = 99, ErrorCount24h = 3
          });
          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_company_profile'");
          foreach (var excluded in new[] {
              "status", "last_run_at", "last_duration_ms", "last_rows_written", "error_count_24h", "db",
              "Status", "LastRunAt", "LastDurationMs", "LastRowsWritten", "ErrorCount24h", "Db" })
          {
              Assert.DoesNotContain(excluded, afterJson);
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }
  ```

- [ ] **Step 2: Build**

  ```
  dotnet build src/TallyDbLoader.sln
  ```

  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Run snapshot tests**

  ```
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~SnapshotContains|FullyQualifiedName~SnapshotExcludes"
  ```

  Expected: `2 passed, 0 failed`.

- [ ] **Step 4: Commit**

  ```
  git add tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "test(audit): add CompanyProfile snapshot field coverage tests"
  ```

---

### Task 6: Full suite verification

**Files:** none â€” verification only.

- [ ] **Step 1: Run the full test suite**

  ```
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
  ```

  Expected: All tests pass, zero failures. Pre-existing tests in `SyncLifecycleSafetyTests.cs`, `BackgroundSyncWorkerTests.cs`, and the original `ConfigRepositoryTests` block must all still pass.

- [ ] **Step 2: Verify no WPF files changed**

  ```
  git diff HEAD~6 --name-only | Select-String "Wpf"
  ```

  Expected: no output.

- [ ] **Step 3: Verify interface is unchanged**

  ```
  git diff HEAD~6 -- src/TallyDbLoader.Core/Data/IConfigRepository.cs
  ```

  Expected: empty diff.

---

## Success Criteria Checklist

- [ ] `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes with 0 failures.
- [ ] `SaveCompanyProfile` (create) cannot commit without a `create_company_profile` audit row.
- [ ] `SaveCompanyProfile` (update) cannot commit without an `update_company_profile` audit row.
- [ ] `DeleteCompanyProfile` cannot commit without a `delete_company_profile` audit row.
- [ ] Create audit: `entity_id` and `after_json.id` both equal the generated `company_profiles.id`.
- [ ] Create audit: `before_json = "{}"`.
- [ ] Update audit: `before_json` reflects pre-mutation DB state (two-step test).
- [ ] Delete audit: `after_json = "{}"`.
- [ ] Snapshot JSON contains exactly 16 configuration fields; none of the 6 runtime fields are present.
- [ ] Updating or deleting a missing company profile throws `InvalidOperationException` containing the ID.
- [ ] `IConfigRepository.cs` is unchanged.
- [ ] No WPF files are modified.
