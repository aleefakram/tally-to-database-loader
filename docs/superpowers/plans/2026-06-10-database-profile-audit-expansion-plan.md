# DatabaseProfile Audit Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add fail-closed audit rows to `SaveDatabaseProfile` (create and update paths) and `DeleteDatabaseProfile`, reusing the existing private `InsertConfigAuditLog` helper and following the same transactional pattern as `SaveCompanyProfile`.

**Architecture:** All changes are inside `ConfigRepository.cs`. Each of the DatabaseProfile mutation methods gains a hand-rolled snapshot SELECT, snapshot serialisation to an explicit anonymous object with snake_case keys (replacing `password` with the boolean `has_password`), and a call to `InsertConfigAuditLog` before `transaction.Commit()`. If the audit insert fails, the outer `catch` rolls back the mutation. Public interface `IConfigRepository` is unchanged. No WPF files are touched.

**Tech Stack:** C# · .NET 8 · Microsoft.Data.Sqlite · Dapper · System.Text.Json · xUnit

---

## File Structure

- **Modify:** `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
  - Replace `SaveDatabaseProfile` body — add transaction logic, snapshot SELECT, custom serialisation, and `InsertConfigAuditLog` calls.
  - Replace `DeleteDatabaseProfile` body — add transaction logic, snapshot SELECT, custom serialisation, delete statement execution, and `InsertConfigAuditLog` call.
- **Modify:** `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`
  - Add 16 new `[Fact]` tests.

No other files are touched.

---

## Preflight

- [ ] **Step 1: Capture base SHA**

  Run this command to capture the baseline commit SHA before starting implementation:

  ```powershell
  $base = git rev-parse HEAD
  ```

---

### Task 1: Replace `SaveDatabaseProfile` with audited create + update

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs` (lines 48-101)

The existing method has two branches (`profile.Id == 0` -> INSERT, `profile.Id != 0` -> UPDATE). Replace the entire method body. Key changes:
- Create path: encrypt password first, perform INSERT, read `last_insert_rowid()`, cast to `int` (narrowing), build `before_json = "{}"`, build `after_json` from the submitted object plus `has_password = !string.IsNullOrWhiteSpace(encryptedPassword)`, then call `InsertConfigAuditLog`.
- Update path: load the existing row using a hand-rolled projection of only snapshot fields and the password column; throw `InvalidOperationException` if missing; build `before_json` setting `has_password = !string.IsNullOrWhiteSpace(loaded.Password)`; encrypt the submitted password; perform UPDATE; assert `affected == 1`; build `after_json` from submitted object (setting `has_password = !string.IsNullOrWhiteSpace(encryptedPassword)`); call `InsertConfigAuditLog`.
- Use a single transaction for the mutations and the audit log inserts, rolling back on any exceptions.

- [ ] **Step 1: Replace `SaveDatabaseProfile`**

  Replace the `SaveDatabaseProfile` method entirely with:

  ```csharp
  public void SaveDatabaseProfile(DatabaseProfile profile)
  {
      var encryptedPassword = EncryptPassword(profile.Password);
      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Open();
          conn.Execute("PRAGMA foreign_keys = ON;");
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

                      long generatedId = conn.QuerySingle<long>("SELECT last_insert_rowid();", null, transaction);
                      int entityId = (int)generatedId;

                      string afterJson = JsonSerializer.Serialize(new
                      {
                          id = entityId,
                          name = profile.Name,
                          technology = profile.Technology,
                          server = profile.Server,
                          port = profile.Port,
                          username = profile.Username,
                          has_password = !string.IsNullOrWhiteSpace(encryptedPassword)
                      });

                      InsertConfigAuditLog(conn, transaction, DateTime.UtcNow, "system",
                          "create_database_profile", "database_profile", entityId,
                          profile.Name, "{}", afterJson, "Database profile created");
                  }
                  else
                  {
                      var loaded = conn.QueryFirstOrDefault<DatabaseProfile>(@"
                          SELECT id AS Id, name AS Name, technology AS Technology, server AS Server, port AS Port, username AS Username, password AS Password
                          FROM database_profiles WHERE id = @Id", new { profile.Id }, transaction);

                      if (loaded == null)
                          throw new InvalidOperationException(
                              $"Cannot update database profile: no row found with ID {profile.Id}.");

                      string beforeJson = JsonSerializer.Serialize(new
                      {
                          id = loaded.Id,
                          name = loaded.Name,
                          technology = loaded.Technology,
                          server = loaded.Server,
                          port = loaded.Port,
                          username = loaded.Username,
                          has_password = !string.IsNullOrWhiteSpace(loaded.Password)
                      });

                      int affected = conn.Execute(@"
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

                      if (affected != 1)
                          throw new InvalidOperationException(
                              $"Expected to update exactly 1 database profile (ID: {profile.Id}), but updated {affected}.");

                      string afterJson = JsonSerializer.Serialize(new
                      {
                          id = profile.Id,
                          name = profile.Name,
                          technology = profile.Technology,
                          server = profile.Server,
                          port = profile.Port,
                          username = profile.Username,
                          has_password = !string.IsNullOrWhiteSpace(encryptedPassword)
                      });

                      InsertConfigAuditLog(conn, transaction, DateTime.UtcNow, "system",
                          "update_database_profile", "database_profile", profile.Id,
                          profile.Name, beforeJson, afterJson, "Database profile updated");
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

  Run:
  ```powershell
  dotnet build src/TallyDbLoader.sln
  ```
  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(audit): add audit rows to SaveDatabaseProfile create and update paths"
  ```

---

### Task 2: Replace `DeleteDatabaseProfile` with audited delete

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs` (lines 540-560)

Replace the body of `DeleteDatabaseProfile` to load the database profile inside a transaction, build `before_json` (including `has_password`), execute the deletion, assert exactly one row was deleted, and insert the audit row.

- [ ] **Step 1: Replace `DeleteDatabaseProfile`**

  Replace the `DeleteDatabaseProfile` method entirely with:

  ```csharp
  public void DeleteDatabaseProfile(int id)
  {
      using (var conn = new SqliteConnection(_connectionString))
      {
          conn.Open();
          conn.Execute("PRAGMA foreign_keys = ON;");
          using (var transaction = conn.BeginTransaction())
          {
              try
              {
                  var loaded = conn.QueryFirstOrDefault<DatabaseProfile>(@"
                      SELECT id AS Id, name AS Name, technology AS Technology, server AS Server, port AS Port, username AS Username, password AS Password
                      FROM database_profiles WHERE id = @Id", new { Id = id }, transaction);

                  if (loaded == null)
                      throw new InvalidOperationException(
                          $"Cannot delete database profile: no row found with ID {id}.");

                  string beforeJson = JsonSerializer.Serialize(new
                  {
                      id = loaded.Id,
                      name = loaded.Name,
                      technology = loaded.Technology,
                      server = loaded.Server,
                      port = loaded.Port,
                      username = loaded.Username,
                      has_password = !string.IsNullOrWhiteSpace(loaded.Password)
                  });

                  int affected = conn.Execute(
                      "DELETE FROM database_profiles WHERE id = @Id", new { Id = id }, transaction);

                  if (affected != 1)
                      throw new InvalidOperationException(
                          $"Expected to delete exactly 1 database profile (ID: {id}), but deleted {affected}.");

                  InsertConfigAuditLog(conn, transaction, DateTime.UtcNow, "system",
                      "delete_database_profile", "database_profile", id,
                      loaded.Name, beforeJson, "{}", "Database profile deleted");

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

  Run:
  ```powershell
  dotnet build src/TallyDbLoader.sln
  ```
  Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

  Run:
  ```powershell
  git add src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(audit): add audit row to DeleteDatabaseProfile"
  ```

---

### Task 3: Add create, update, and error-path audit tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

Add tests verifying database profile create and update audit logging, metadata contents, exceptions on missing profiles, and rollback behavior.

- [ ] **Step 1: Add create/update tests and metadata assertions**

  Open `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`, locate the end of the `CompanyProfile` audit tests block (around line 791), and append the following:

  ```csharp
  // -- DatabaseProfile audit ----------------------------------------------

  [Fact]
  public void SaveDatabaseProfile_Create_WritesOneAuditRow()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_create_audit_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          repo.SaveDatabaseProfile(new DatabaseProfile
          {
              Name = "PostgresDev",
              Technology = "postgres",
              Server = "localhost",
              Port = 5432,
              Username = "dev_user"
          });

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'create_database_profile'");
          Assert.Equal(1, count);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveDatabaseProfile_Create_AuditUsesGeneratedIdInEntityIdAndAfterJson()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_create_id_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          repo.SaveDatabaseProfile(new DatabaseProfile
          {
              Name = "PostgresDev",
              Technology = "postgres",
              Server = "localhost",
              Port = 5432,
              Username = "dev_user"
          });

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          long entityId = conn.ExecuteScalar<long>("SELECT entity_id FROM config_audit_log WHERE action = 'create_database_profile'");
          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");
          long rowId = conn.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'PostgresDev'");
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
  public void SaveDatabaseProfile_Create_BeforeJsonIsEmptyObject()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_create_before_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          repo.SaveDatabaseProfile(new DatabaseProfile
          {
              Name = "PostgresDev",
              Technology = "postgres",
              Server = "localhost",
              Port = 5432,
              Username = "dev_user"
          });

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'create_database_profile'");
          Assert.Equal("{}", beforeJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveDatabaseProfile_Update_WritesOneAuditRow()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_update_audit_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var dp = new DatabaseProfile { Name = "MssqlDev", Technology = "mssql", Server = "127.0.0.1", Port = 1433, Username = "sa" };
          repo.SaveDatabaseProfile(dp);

          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'MssqlDev'");

          dp.Name = "MssqlDev Updated";
          repo.SaveDatabaseProfile(dp);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log WHERE action = 'update_database_profile'");
          Assert.Equal(1, count);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveDatabaseProfile_Update_BeforeJsonReflectsPreMutationState()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_update_before_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var dp = new DatabaseProfile { Name = "OldName", Technology = "postgres", Server = "localhost", Port = 5432, Username = "old_user" };
          repo.SaveDatabaseProfile(dp);

          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'OldName'");

          dp.Name = "NewName";
          dp.Server = "10.0.0.1";
          dp.Port = 5433;
          repo.SaveDatabaseProfile(dp);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string beforeJson = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_database_profile'");
          Assert.Contains("\"OldName\"", beforeJson);
          Assert.Contains("\"localhost\"", beforeJson);
          Assert.Contains("5432", beforeJson);

          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_database_profile'");
          Assert.Contains("\"NewName\"", afterJson);
          Assert.Contains("\"10.0.0.1\"", afterJson);
          Assert.Contains("5433", afterJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveDatabaseProfile_MetadataAssertions()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_metadata_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var dp = new DatabaseProfile { Name = "MetaDb", Technology = "postgres", Server = "localhost", Port = 5432, Username = "dev" };
          
          // Create path
          repo.SaveDatabaseProfile(dp);
          
          int dpId;
          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'MetaDb'");

          // Update path
          dp.Id = dpId;
          dp.Name = "MetaDb Updated";
          repo.SaveDatabaseProfile(dp);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          
          // Verify Create Row
          var createRow = conn.QuerySingle("SELECT actor, action, entity_type, entity_id, entity_name, reason FROM config_audit_log WHERE action = 'create_database_profile'");
          Assert.Equal("system", createRow.actor);
          Assert.Equal("create_database_profile", createRow.action);
          Assert.Equal("database_profile", createRow.entity_type);
          Assert.Equal((long)dpId, createRow.entity_id);
          Assert.Equal("MetaDb", createRow.entity_name);
          Assert.Equal("Database profile created", createRow.reason);

          // Verify Update Row
          var updateRow = conn.QuerySingle("SELECT actor, action, entity_type, entity_id, entity_name, reason FROM config_audit_log WHERE action = 'update_database_profile'");
          Assert.Equal("system", updateRow.actor);
          Assert.Equal("update_database_profile", updateRow.action);
          Assert.Equal("database_profile", updateRow.entity_type);
          Assert.Equal((long)dpId, updateRow.entity_id);
          Assert.Equal("MetaDb Updated", updateRow.entity_name); // submitted name
          Assert.Equal("Database profile updated", updateRow.reason);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveDatabaseProfile_Update_ThrowsInvalidOperationException_WhenProfileMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_miss_upd_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var ex = Assert.Throws<InvalidOperationException>(() =>
              repo.SaveDatabaseProfile(new DatabaseProfile { Id = 9999, Name = "Ghost", Server = "x" }));
          Assert.Contains("9999", ex.Message);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveDatabaseProfile_Create_RollsBack_WhenAuditTableMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_rb_create_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              conn.Execute("DROP TABLE config_audit_log;");

          Assert.Throws<InvalidOperationException>(() =>
              repo.SaveDatabaseProfile(new DatabaseProfile { Name = "ShouldNotExist", Server = "localhost" }));

          using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'ShouldNotExist'"));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void SaveDatabaseProfile_Update_RollsBack_WhenAuditTableMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_rb_update_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var dp = new DatabaseProfile { Name = "OriginalName", Server = "localhost" };
          repo.SaveDatabaseProfile(dp);

          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dp.Id = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'OriginalName'");

          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              conn.Execute("DROP TABLE config_audit_log;");

          dp.Name = "ShouldNotUpdate";
          Assert.Throws<InvalidOperationException>(() => repo.SaveDatabaseProfile(dp));

          using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE name = 'OriginalName'"));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }
  ```

- [ ] **Step 2: Run create/update tests**

  Run:
  ```powershell
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~SaveDatabaseProfile"
  ```
  Expected: All 9 tests pass.

- [ ] **Step 3: Commit**

  Run:
  ```powershell
  git add tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "test(audit): add SaveDatabaseProfile create, update, and rollback tests"
  ```

---

### Task 4: Add delete, snapshot fields, and has_password tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

Add remaining tests validating profile deletions, missing deletions, exact fields, and password transition/exclusion assertions.

- [ ] **Step 1: Append delete, snapshot, and has_password tests**

  Open `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs` and append:

  ```csharp
  [Fact]
  public void DeleteDatabaseProfile_WritesAuditRow_AndRemovesRow()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_delete_audit_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          repo.SaveDatabaseProfile(new DatabaseProfile { Name = "ZetaDb", Server = "localhost" });
          
          int dpId;
          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'ZetaDb'");

          repo.DeleteDatabaseProfile(dpId);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          
          // Verify audit log
          var deleteRow = conn.QuerySingle("SELECT actor, action, entity_type, entity_id, entity_name, reason, before_json, after_json FROM config_audit_log WHERE action = 'delete_database_profile'");
          Assert.Equal("system", deleteRow.actor);
          Assert.Equal("delete_database_profile", deleteRow.action);
          Assert.Equal("database_profile", deleteRow.entity_type);
          Assert.Equal((long)dpId, deleteRow.entity_id);
          Assert.Equal("ZetaDb", deleteRow.entity_name);
          Assert.Equal("Database profile deleted", deleteRow.reason);
          Assert.Contains("\"ZetaDb\"", (string)deleteRow.before_json);
          Assert.Equal("{}", (string)deleteRow.after_json);

          // Verify row is gone
          Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE id = @Id", new { Id = dpId }));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DeleteDatabaseProfile_AfterJsonIsEmptyObject()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_delete_after_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          repo.SaveDatabaseProfile(new DatabaseProfile { Name = "EtaDb", Server = "localhost" });
          
          int dpId;
          using (var cId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dpId = (int)cId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'EtaDb'");

          repo.DeleteDatabaseProfile(dpId);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'delete_database_profile'");
          Assert.Equal("{}", afterJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DeleteDatabaseProfile_RollsBack_WhenAuditTableMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_rb_delete_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          repo.SaveDatabaseProfile(new DatabaseProfile { Name = "ThetaDb", Server = "localhost" });
          
          int dpId;
          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'ThetaDb'");

          using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              conn.Execute("DROP TABLE config_audit_log;");

          Assert.Throws<InvalidOperationException>(() => repo.DeleteDatabaseProfile(dpId));

          using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM database_profiles WHERE id = @Id", new { Id = dpId }));
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DeleteDatabaseProfile_ThrowsInvalidOperationException_WhenProfileMissing()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_miss_del_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var ex = Assert.Throws<InvalidOperationException>(() => repo.DeleteDatabaseProfile(9999));
          Assert.Contains("9999", ex.Message);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DatabaseProfileAudit_SnapshotContainsExactlyAllowedFields()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_snap_fields_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var dp = new DatabaseProfile
          {
              Name = "IotaDb",
              Technology = "postgres",
              Server = "localhost",
              Port = 5432,
              Username = "iota_user",
              Password = "iota_password"
          };
          repo.SaveDatabaseProfile(dp);
          
          int dpId;
          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'IotaDb'");
          
          dp.Id = dpId;
          dp.Name = "IotaDb Updated";
          repo.SaveDatabaseProfile(dp);
          
          repo.DeleteDatabaseProfile(dpId);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          
          string createAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");
          string updateBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_database_profile'");
          string updateAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_database_profile'");
          string deleteBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'delete_database_profile'");

          var allowed = new System.Collections.Generic.HashSet<string>
          {
              "id", "name", "technology", "server", "port", "username", "has_password"
          };

          foreach (var json in new[] { createAfter, updateBefore, updateAfter, deleteBefore })
          {
              using var doc = System.Text.Json.JsonDocument.Parse(json);
              var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
              Assert.Equal(7, props.Count);
              Assert.True(allowed.SetEquals(props), $"JSON properties mismatch: {json}");
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DatabaseProfileAudit_SnapshotExcludesRuntimeFields()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_snap_excl_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          var dp = new DatabaseProfile
          {
              Name = "KappaDb",
              Technology = "mssql",
              Server = "localhost",
              Port = 1433,
              Username = "sa",
              Password = "super_secret_password",
              LastTestResult = "Success",
              LastTestedAt = DateTime.UtcNow,
              UsedByCount = 5
          };
          repo.SaveDatabaseProfile(dp);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
          string afterJson = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");
          
          using var doc = System.Text.Json.JsonDocument.Parse(afterJson);
          var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
          foreach (var excluded in new[] { "password", "last_test_result", "last_tested_at", "used_by_count" })
          {
              Assert.DoesNotContain(props, p => p.Equals(excluded, StringComparison.OrdinalIgnoreCase));
          }

          // Verify that password contents are completely absent
          Assert.DoesNotContain("super_secret_password", afterJson);
          Assert.DoesNotContain("dpapi:", afterJson);
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }

  [Fact]
  public void DatabaseProfileAudit_HasPasswordTransition()
  {
      string path = Path.Combine(Path.GetTempPath(), $"dp_pwd_trans_{Guid.NewGuid()}.db");
      try
      {
          DatabaseHelper.InitializeDatabase(path);
          var repo = new ConfigRepository(path);
          
          // 1. Create with password
          var dp = new DatabaseProfile
          {
              Name = "SecureDb",
              Technology = "postgres",
              Server = "localhost",
              Port = 5432,
              Username = "postgres",
              Password = "my_original_password"
          };
          repo.SaveDatabaseProfile(dp);

          int dpId;
          using (var connId = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
              dpId = (int)connId.ExecuteScalar<long>("SELECT id FROM database_profiles WHERE name = 'SecureDb'");

          // 2. Update same profile with empty password
          dp.Id = dpId;
          dp.Password = "";
          repo.SaveDatabaseProfile(dp);

          using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");

          string createAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'create_database_profile'");
          string updateBefore = conn.ExecuteScalar<string>("SELECT before_json FROM config_audit_log WHERE action = 'update_database_profile'");
          string updateAfter = conn.ExecuteScalar<string>("SELECT after_json FROM config_audit_log WHERE action = 'update_database_profile'");

          // Assert has_password values
          using (var doc = System.Text.Json.JsonDocument.Parse(createAfter))
              Assert.True(doc.RootElement.GetProperty("has_password").GetBoolean(), "Expected create after_json has_password == true");

          using (var doc = System.Text.Json.JsonDocument.Parse(updateBefore))
              Assert.True(doc.RootElement.GetProperty("has_password").GetBoolean(), "Expected update before_json has_password == true");

          using (var doc = System.Text.Json.JsonDocument.Parse(updateAfter))
              Assert.False(doc.RootElement.GetProperty("has_password").GetBoolean(), "Expected update after_json has_password == false");

          // Verify no plaintext or encrypted string contains secret keywords or signatures
          foreach (var json in new[] { createAfter, updateBefore, updateAfter })
          {
              Assert.DoesNotContain("my_original_password", json);
              Assert.DoesNotContain("dpapi:", json);
              
              // Verify "password" property itself is excluded, but "has_password" is fine
              using var doc = System.Text.Json.JsonDocument.Parse(json);
              var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
              Assert.DoesNotContain(props, p => p.Equals("password", StringComparison.OrdinalIgnoreCase));
          }
      }
      finally
      {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          if (File.Exists(path)) try { File.Delete(path); } catch { }
      }
  }
  ```

- [ ] **Step 2: Run all DatabaseProfile tests**

  Run:
  ```powershell
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~SaveDatabaseProfile|FullyQualifiedName~DeleteDatabaseProfile|FullyQualifiedName~DatabaseProfileAudit"
  ```
  Expected: All 16 tests pass.

- [ ] **Step 3: Run the full test suite**

  Run:
  ```powershell
  dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
  ```
  Expected: All test suites in the repository pass.

- [ ] **Step 4: Commit**

  Run:
  ```powershell
  git add tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
  git commit -m "test(audit): add DeleteDatabaseProfile, snapshot, and has_password transition tests"
  ```

- [ ] **Step 5: Perform surgical-scope check**

  Run:
  ```powershell
  git diff --name-only $base..HEAD
  ```
  Expected changed files should be only:
  - `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
  - `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`
