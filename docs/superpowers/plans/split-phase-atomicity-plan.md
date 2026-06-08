# Split-Phase Job-Level Atomicity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a split-phase safe promotion model in full sync. Tables are staged (one-by-one), validated, and promoted in separate phases. Database transactions cover only live-table mutations to prevent implicit DDL commits in MySQL and lock contention.

**Architecture:**
1. Split `IFullSyncTablePromoter` into four phases:
   - `Task<StageResult> StageAsync(DataTable data, TableConfig table, DbConnection conn)` (where `StageResult` holds RowCount and Columns).
   - `Task ValidateStagingAsync(TableConfig table, DbConnection conn)`.
   - `Task PromoteStagedAsync(TableConfig table, System.Collections.Generic.List<string> columns, DbConnection conn, DbTransaction transaction)`.
   - `Task CleanupStagingAsync(TableConfig table, DbConnection conn)`.
2. Refactor `FullSyncRunner.Run` to:
   - Loop over each table, extract, parse, stage, and discard the DataTable sequentially.
   - Wrap staging, validation, and promotion in a single `try/finally` block.
   - Ensure `StageAsync` is responsible for self-cleaning its staging table on any failure before rethrowing.
   - Track successfully staged tables in `stagedTables` so that the runner's `finally` block can clean them up.
   - Open a single, short database transaction covering only live-table promotion across all tables.
   - Perform best-effort cleanup of staging tables in the `finally` block (outside the transaction).
3. Parameterize the `stagingTableName` check in the MSSQL promoter.
4. Add opt-in integration smoke tests for all supported databases using `Xunit.SkippableFact` for proper test skip visualization.

**Tech Stack:** .NET 8, ADO.NET (DbConnection, DbCommand, DbTransaction), xUnit, Xunit.SkippableFact, Dapper, Sqlite, Postgres, MSSQL, MySQL.

---

### Task 1: Add Xunit.SkippableFact Dependency

**Files:**
- Modify: `tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj`

- [ ] **Step 1: Install Package**
  Run: `dotnet add tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj package Xunit.SkippableFact`
  Expected: Package added successfully.

---

### Task 2: Update IFullSyncTablePromoter & Implement Split-Phase Promoters

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/IFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/UnsupportedFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/SqliteFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/PostgresFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/MysqlFullSyncTablePromoter.cs`

- [ ] **Step 1: Modify promoter interface and add StageResult**
  Update `IFullSyncTablePromoter.cs` to split staging, validation, promotion, and cleanup:
  ```csharp
  using System.Data;
  using System.Data.Common;
  using System.Threading.Tasks;
  using TallyDbLoader.Core.Tally;

  namespace TallyDbLoader.Core.Sync
  {
      public class StageResult
      {
          public int RowCount { get; set; }
          public System.Collections.Generic.List<string> Columns { get; set; } = new System.Collections.Generic.List<string>();
      }

      public interface IFullSyncTablePromoter
      {
          Task<StageResult> StageAsync(DataTable data, TableConfig table, DbConnection conn);
          Task ValidateStagingAsync(TableConfig table, DbConnection conn);
          Task PromoteStagedAsync(TableConfig table, System.Collections.Generic.List<string> columns, DbConnection conn, DbTransaction transaction);
          Task CleanupStagingAsync(TableConfig table, DbConnection conn);
      }
  }
  ```

- [ ] **Step 2: Update UnsupportedFullSyncTablePromoter**
  Implement the new split interface methods throwing `NotSupportedException`.

- [ ] **Step 3: Refactor SqliteFullSyncTablePromoter**
  Implement split methods. `StageAsync` must populate Columns unconditionally from `data.Columns` and clean up its own staging table on failure before rethrowing.
  ```csharp
  public class SqliteFullSyncTablePromoter : IFullSyncTablePromoter
  {
      private string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

      public async Task<StageResult> StageAsync(DataTable data, TableConfig table, DbConnection conn)
      {
          var tableName = table.Name;
          var stagingTableName = $"__tally_fullsync_staging_{tableName}";
          
          try
          {
              using (var cmd = conn.CreateCommand())
              {
                  cmd.CommandText = $"DROP TABLE IF EXISTS {Quote(stagingTableName)};";
                  await cmd.ExecuteNonQueryAsync();
              }
              using (var cmd = conn.CreateCommand())
              {
                  cmd.CommandText = $"CREATE TABLE {Quote(stagingTableName)} AS SELECT * FROM {Quote(tableName)} WHERE 1=0;";
                  await cmd.ExecuteNonQueryAsync();
              }
              
              var columns = new System.Collections.Generic.List<string>();
              for (int i = 0; i < data.Columns.Count; i++)
              {
                  columns.Add(data.Columns[i].ColumnName);
              }

              if (data.Rows.Count > 0)
              {
                  // ... bulk insert logic using Quote() ...
              }
              
              return new StageResult { RowCount = data.Rows.Count, Columns = columns };
          }
          catch
          {
              // Clean up on failure to prevent leaks
              try { await CleanupStagingAsync(table, conn); } catch { }
              throw;
          }
      }

      public async Task ValidateStagingAsync(TableConfig table, DbConnection conn)
      {
          var tableName = table.Name;
          var stagingTableName = $"__tally_fullsync_staging_{tableName}";
          
          var isPrimary = table.Nature?.Equals("Primary", StringComparison.OrdinalIgnoreCase) == true;
          if (isPrimary)
          {
              // Validate that table.Fields contains a configured guid field first
              var hasGuidConfig = false;
              if (table.Fields != null)
              {
                  foreach (var field in table.Fields)
                  {
                      if (field.Name.Equals("guid", StringComparison.OrdinalIgnoreCase))
                      {
                          hasGuidConfig = true;
                          break;
                      }
                  }
              }
              if (!hasGuidConfig)
              {
                  throw new InvalidOperationException($"GUID column is missing from Table {tableName} config, but Nature is Primary.");
              }

              // Check null GUIDs or duplicates using: SELECT COUNT(*) ...
          }
      }

      public async Task PromoteStagedAsync(TableConfig table, System.Collections.Generic.List<string> columns, DbConnection conn, DbTransaction transaction)
      {
          var tableName = table.Name;
          var stagingTableName = $"__tally_fullsync_staging_{tableName}";
          using (var deleteCmd = conn.CreateCommand())
          {
              deleteCmd.Transaction = transaction;
              deleteCmd.CommandText = $"DELETE FROM {Quote(tableName)};";
              await deleteCmd.ExecuteNonQueryAsync();
          }
          if (columns.Count > 0)
          {
              var quotedCols = new System.Collections.Generic.List<string>();
              foreach (var col in columns) quotedCols.Add(Quote(col));
              var colsStr = string.Join(", ", quotedCols);

              using (var promoteCmd = conn.CreateCommand())
              {
                  promoteCmd.Transaction = transaction;
                  promoteCmd.CommandText = $"INSERT INTO {Quote(tableName)} ({colsStr}) SELECT {colsStr} FROM {Quote(stagingTableName)};";
                  await promoteCmd.ExecuteNonQueryAsync();
              }
          }
      }

      public async Task CleanupStagingAsync(TableConfig table, DbConnection conn)
      {
          var tableName = table.Name;
          var stagingTableName = $"__tally_fullsync_staging_{tableName}";
          using (var cmd = conn.CreateCommand())
          {
              cmd.CommandText = $"DROP TABLE IF EXISTS {Quote(stagingTableName)};";
              await cmd.ExecuteNonQueryAsync();
          }
      }
  }
  ```

- [ ] **Step 4: Refactor PostgresFullSyncTablePromoter**
  Apply the same split-phase structure using Postgres quoting and commands.

- [ ] **Step 5: Refactor MysqlFullSyncTablePromoter**
  Apply the same split-phase structure using MySQL backticks quoting and commands. MySQL staging/cleanup DDL will now execute entirely outside the promotion transaction, preventing implicit commits.

---

### Task 3: Parameterize and Refactor MSSQL Promoter

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/MssqlFullSyncTablePromoter.cs`

- [ ] **Step 1: Parameterize OBJECT_ID staging table checks**
  Modify `MssqlFullSyncTablePromoter.cs` to split the phases and parameterize the table existence checks.
  ```csharp
  public class MssqlFullSyncTablePromoter : IFullSyncTablePromoter
  {
      private string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";

      public async Task<StageResult> StageAsync(DataTable data, TableConfig table, DbConnection conn)
      {
          var tableName = table.Name;
          var stagingTableName = $"__tally_fullsync_staging_{tableName}";
          try
          {
              using (var cmd = conn.CreateCommand())
              {
                  cmd.CommandText = $@"
                      IF OBJECT_ID(@stagingTable, 'U') IS NOT NULL 
                          DROP TABLE {Quote(stagingTableName)};
                      SELECT * INTO {Quote(stagingTableName)} FROM {Quote(tableName)} WHERE 1=0;
                  ";
                  var param = cmd.CreateParameter();
                  param.ParameterName = "@stagingTable";
                  param.Value = stagingTableName;
                  cmd.Parameters.Add(param);
                  await cmd.ExecuteNonQueryAsync();
              }
              
              var columns = new System.Collections.Generic.List<string>();
              for (int i = 0; i < data.Columns.Count; i++)
              {
                  columns.Add(data.Columns[i].ColumnName);
              }

              if (data.Rows.Count > 0)
              {
                  // Bulk insert staging
              }
              return new StageResult { RowCount = data.Rows.Count, Columns = columns };
          }
          catch
          {
              try { await CleanupStagingAsync(table, conn); } catch { }
              throw;
          }
      }

      public async Task ValidateStagingAsync(TableConfig table, DbConnection conn) { /* ... */ }

      public async Task PromoteStagedAsync(TableConfig table, System.Collections.Generic.List<string> columns, DbConnection conn, DbTransaction transaction)
      {
          var tableName = table.Name;
          var stagingTableName = $"__tally_fullsync_staging_{tableName}";
          // Delete & Copy under the transaction using the columns parameter
      }

      public async Task CleanupStagingAsync(TableConfig table, DbConnection conn)
      {
          var tableName = table.Name;
          var stagingTableName = $"__tally_fullsync_staging_{tableName}";
          using (var cmd = conn.CreateCommand())
          {
              cmd.CommandText = $@"
                  IF OBJECT_ID(@stagingTable, 'U') IS NOT NULL 
                      DROP TABLE {Quote(stagingTableName)};
              ";
              var param = cmd.CreateParameter();
              param.ParameterName = "@stagingTable";
              param.Value = stagingTableName;
              cmd.Parameters.Add(param);
              await cmd.ExecuteNonQueryAsync();
          }
      }
  }
  ```

---

### Task 4: Refactor FullSyncRunner for Split-Phase Orchestration

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/FullSyncRunner.cs`

- [ ] **Step 1: Implement split-phase execution inside FullSyncRunner.Run**
  Ensure Tally HTTP extraction, parsing, staging, and validation are all completed before starting a database transaction. Perform staging and validation outside the transaction, and commit promotion for all tables inside a single short transaction.
  ```csharp
          public async Task<long> Run(TallyExportConfig config, string companyName,
              DateTime fromDate, DateTime toDate, DbConnection targetConn)
          {
              var all = new System.Collections.Generic.List<TableConfig>();
              all.AddRange(config.Master);
              all.AddRange(config.Transaction);

              var stagedTables = new System.Collections.Generic.List<TableConfig>();
              var stageResults = new System.Collections.Generic.Dictionary<TableConfig, StageResult>();
              long totalRows = 0;

              try
              {
                  // 1. Fetch, Parse, and Stage one-by-one (outside transaction)
                  foreach (var table in all)
                  {
                      var xml = DynamicTdlXmlGenerator.GenerateXml(table, companyName,
                          fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                      var response = await _tally.PostXMLAsync(xml);
                      var dt = DynamicXmlParser.ParseXml(response, table);

                      var stageResult = await _promoter.StageAsync(dt, table, targetConn);
                      stagedTables.Add(table);
                      stageResults[table] = stageResult;
                      totalRows += stageResult.RowCount;
                  }

                  // 2. Validate all staging tables (outside transaction)
                  foreach (var table in stagedTables)
                  {
                      await _promoter.ValidateStagingAsync(table, targetConn);
                  }

                  // 3. Promote all staged tables (inside short transaction)
                  using (var transaction = targetConn.BeginTransaction())
                  {
                      try
                      {
                          foreach (var table in stagedTables)
                          {
                              var result = stageResults[table];
                              await _promoter.PromoteStagedAsync(table, result.Columns, targetConn, transaction);
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
              finally
              {
                  // 4. Cleanup staging tables (best-effort, outside transaction)
                  foreach (var table in stagedTables)
                  {
                      try
                      {
                          await _promoter.CleanupStagingAsync(table, targetConn);
                      }
                      catch (Exception ex)
                      {
                          TallyDbLoader.Core.Logging.FileLogger.LogMessage($"[Staging Cleanup Warning] Failed to drop staging table for '{table.Name}': {ex.Message}");
                      }
                  }
              }

              return totalRows;
          }
  ```

---

### Task 5: Implement Multi-Table Regression Tests in FullSyncRunnerTests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs`

- [ ] **Step 1: Write test for Validation failure rollback**
  Create a test where the second table fails pre-promotion validation (e.g. duplicate GUID) and assert the first table is NOT modified.
  
- [ ] **Step 2: Write test for Promotion execution failure rollback**
  Create a test where both tables pass validation, but the second table fails during actual SQL promotion (e.g., database constraint error during insert), proving that the live database changes to both tables are correctly rolled back.

- [ ] **Step 3: Verify all unit tests compile and pass**
  Run: `dotnet test src/TallyDbLoader.sln --filter FullSyncRunnerTests`
  Expected: PASS

---

### Task 6: Implement Opt-in Integration Smoke Tests

**Files:**
- Create: `tests/TallyDbLoader.Tests/ProviderIntegrationTests.cs`

- [ ] **Step 1: Add ProviderIntegrationTests class**
  Implement test logic using `[SkippableFact]`. Check environment variables `TALLY_TEST_POSTGRES_CONN`, `TALLY_TEST_MSSQL_CONN`, `TALLY_TEST_MYSQL_CONN`.
  - Create live tables.
  - Stage, validate, and promote valid data.
  - Assert live table is updated.
  - Run a sync where the second table has duplicate GUIDs (validation failure). Assert rollback.
  - Run a sync where the second table fails promotion (insert failure). Assert rollback.
  - Run cleanup.
  
  *Skip Mechanism:* Use `Skip.If` to properly report skipped tests to the xUnit runner:
  ```csharp
  Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TALLY_TEST_POSTGRES_CONN")), "PostgreSQL connection string not set");
  ```

- [ ] **Step 2: Run all tests**
  Run: `dotnet test src/TallyDbLoader.sln`
  Expected: PASS (with non-configured databases marked as skipped in output)
