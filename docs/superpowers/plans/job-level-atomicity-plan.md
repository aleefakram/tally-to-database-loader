# Job-Level Atomicity and Promoter Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement job-level database transactional atomicity for the Full Sync runner, parameterize/harden the MSSQL promoter's staging table check, and add opt-in multi-provider integration smoke tests.

**Architecture:** Update `IFullSyncTablePromoter` to accept an optional `DbTransaction`. Refactor all database promoters to utilize this transaction when provided (and explicitly bind all database commands to it). Modify `FullSyncRunner` to run all tables inside a single job-level transaction. Add a parameter to the MSSQL promoter's table existence check, and write a set of integration smoke tests that skip dynamically unless opt-in connection string environment variables are set.

**Tech Stack:** .NET 8, ADO.NET (DbConnection, DbCommand, DbTransaction), xUnit, SQLite, Microsoft.Data.SqlClient, Npgsql, MySqlConnector.

---

### Task 1: Update IFullSyncTablePromoter Interface & Unsupported Promoter

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/IFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/UnsupportedFullSyncTablePromoter.cs`

- [ ] **Step 1: Modify interface signature**
  Update the `StageValidateAndPromoteAsync` method in `IFullSyncTablePromoter.cs` to accept an optional `DbTransaction`.
  ```csharp
  using System.Data;
  using System.Data.Common;
  using System.Threading.Tasks;
  using TallyDbLoader.Core.Tally;

  namespace TallyDbLoader.Core.Sync
  {
      public interface IFullSyncTablePromoter
      {
          /// <summary>
          /// Stages the data, runs validation, and promotes it to the live table inside a transaction.
          /// Returns the number of promoted rows.
          /// </summary>
          Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn, DbTransaction? transaction = null);
      }
  }
  ```

- [ ] **Step 2: Update UnsupportedFullSyncTablePromoter implementation**
  Update `UnsupportedFullSyncTablePromoter.cs` to match the new interface.
  ```csharp
  using System;
  using System.Data;
  using System.Data.Common;
  using System.Threading.Tasks;
  using TallyDbLoader.Core.Tally;

  namespace TallyDbLoader.Core.Sync
  {
      public class UnsupportedFullSyncTablePromoter : IFullSyncTablePromoter
      {
          public Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn, DbTransaction? transaction = null)
          {
              throw new NotSupportedException("Safe promotion is not supported for this database technology.");
          }
      }
  }
  ```

- [ ] **Step 3: Build & run tests to verify compilation**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Compile errors in the other promoters and tests due to interface mismatch. This is correct at this point.

---

### Task 2: Implement Transaction and Command binding in SQLite, PostgreSQL, and MySQL Promoters

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/SqliteFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/PostgresFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/MysqlFullSyncTablePromoter.cs`

- [ ] **Step 1: Refactor SqliteFullSyncTablePromoter**
  Ensure all internal commands explicitly check and assign `cmd.Transaction = activeTransaction`. Manage local transaction creation only when a job-level transaction is not passed.
  ```csharp
  // Modify method signature:
  public async Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn, DbTransaction? transaction = null)
  ```
  And refactor internal transaction block to:
  ```csharp
              var activeTransaction = transaction;
              var isLocalTransaction = false;
              if (activeTransaction == null)
              {
                  activeTransaction = targetConn.BeginTransaction();
                  isLocalTransaction = true;
              }

              // Update all CreateCommand blocks to bind:
              // cmd.Transaction = activeTransaction;
  ```
  Ensure cleanup in `finally` handles connection dropping correctly.

- [ ] **Step 2: Refactor PostgresFullSyncTablePromoter**
  Perform the exact same transaction-binding refactor for the PostgreSQL promoter.

- [ ] **Step 3: Refactor MysqlFullSyncTablePromoter**
  Perform the exact same transaction-binding refactor for the MySQL promoter.

---

### Task 3: Parameterize and Refactor MSSQL Promoter

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/MssqlFullSyncTablePromoter.cs`

- [ ] **Step 1: Refactor MssqlFullSyncTablePromoter & Parameterize OBJECT_ID**
  Refactor the promoter signature and bind active transactions to all commands. Parameterize the `stagingTableName` variable passed into SQL Server's `OBJECT_ID` function.
  ```csharp
  // Modify method signature:
  public async Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn, DbTransaction? transaction = null)
  ```
  Change staging table creation and drop commands to:
  ```csharp
              using (var cmd = targetConn.CreateCommand())
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
                  
                  if (activeTransaction != null) cmd.Transaction = activeTransaction;
                  await cmd.ExecuteNonQueryAsync();
              }
  ```
  And cleanup to:
  ```csharp
                      using (var cleanCmd = targetConn.CreateCommand())
                      {
                          cleanCmd.CommandText = $@"
                              IF OBJECT_ID(@stagingTable, 'U') IS NOT NULL 
                                  DROP TABLE {Quote(stagingTableName)};
                          ";
                          var param = cleanCmd.CreateParameter();
                          param.ParameterName = "@stagingTable";
                          param.Value = stagingTableName;
                          cleanCmd.Parameters.Add(param);
                          
                          if (activeTransaction != null) cleanCmd.Transaction = activeTransaction;
                          await cleanCmd.ExecuteNonQueryAsync();
                      }
  ```

---

### Task 4: Refactor FullSyncRunner to Execute in a Job-Level Transaction

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/FullSyncRunner.cs`

- [ ] **Step 1: Update FullSyncRunner.Run**
  Modify `FullSyncRunner.cs` to wrap the whole loop of table exports in a single connection-level transaction.
  ```csharp
          public async Task<long> Run(TallyExportConfig config, string companyName,
              DateTime fromDate, DateTime toDate, DbConnection targetConn)
          {
              long total = 0;
              var all = new System.Collections.Generic.List<TableConfig>();
              all.AddRange(config.Master);
              all.AddRange(config.Transaction);

              using (var transaction = targetConn.BeginTransaction())
              {
                  try
                  {
                      foreach (var table in all)
                      {
                          var xml = DynamicTdlXmlGenerator.GenerateXml(table, companyName,
                              fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                          var response = await _tally.PostXMLAsync(xml);
                          var dt = DynamicXmlParser.ParseXml(response, table);

                          var promotedCount = await _promoter.StageValidateAndPromoteAsync(dt, table, targetConn, transaction);
                          total += promotedCount;
                      }
                      transaction.Commit();
                  }
                  catch
                  {
                      transaction.Rollback();
                      throw;
                  }
              }
              return total;
          }
  ```

- [ ] **Step 2: Update existing unit tests to match signature**
  Modify `tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs` to update any mock promoter calls to include the new `DbTransaction` parameter.
  Verify compilation with `dotnet build src/TallyDbLoader.sln`.

---

### Task 5: Implement Job-Level Atomicity Regression Test

**Files:**
- Modify: `tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs`

- [ ] **Step 1: Write a job-level atomicity regression test**
  Add a test to `FullSyncRunnerTests.cs` executing a sync job with two tables. The first table is valid, but the second table has duplicate GUIDs causing a validation failure. Assert that the first live table remains completely unchanged.
  ```csharp
          [Fact]
          public async Task Test_FullSync_JobLevelAtomicity_RollsBackAllTablesOnFailure()
          {
              // Arrange
              // Set up target database schema and existing data for two tables
              // Set up TallyClient response mocks
              // Act: Run FullSyncRunner
              // Assert: First table has original data, second table has original data (rollback successful)
          }
  ```

- [ ] **Step 2: Run tests to verify they pass**
  Run: `dotnet test src/TallyDbLoader.sln --filter FullSyncRunnerTests`
  Expected: PASS

---

### Task 6: Add Provider Integration Smoke Tests (Opt-In)

**Files:**
- Create: `tests/TallyDbLoader.Tests/ProviderIntegrationTests.cs`

- [ ] **Step 1: Implement opt-in database tests**
  Create integration tests for SQLite, PostgreSQL, MSSQL, and MySQL that run a full smoke cycle: stage, validate, promote, rollback-on-bad-row, and cleanup.
  For Postgres, MSSQL, and MySql, dynamically skip the tests if connection strings are not provided in environment variables:
  `TALLY_TEST_POSTGRES_CONN`, `TALLY_TEST_MSSQL_CONN`, `TALLY_TEST_MYSQL_CONN`.

- [ ] **Step 2: Run all tests**
  Run: `dotnet test src/TallyDbLoader.sln`
  Expected: PASS (with non-opt-in provider tests skipped/ignored)
