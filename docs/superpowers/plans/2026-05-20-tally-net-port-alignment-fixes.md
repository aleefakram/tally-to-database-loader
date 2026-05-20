# Tally .NET Port Alignment Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the critical incremental sync bugs, database type mapping mismatches, memory footprint overheads, and testing gaps in the .NET Core port of the Tally Database Loader to ensure full functional alignment with the Node.js application.

**Architecture:** 
1. Rectify the SQL schema definitions for the `_diff` staging table and load all columns directly via `LoadBulkDataAsync` to prevent join crashes.
2. Update the dynamic DDL generator to use `nvarchar` for MSSQL text targets, ensuring native Unicode/regional language support.
3. Replace DOM-based `XDocument` parsing with a streaming `XmlReader` approach to process datasets sequentially with a low memory footprint.
4. Improve unit test robustness by mocking or intercepting connection setups so integration tests validate query execution paths without failing on real network connections.

**Tech Stack:** C#/.NET Core, ADO.NET (MSSQL, PostgreSQL, MySQL), XML (System.Xml.XmlReader), and xUnit.

---

## Proposed File Changes Map
- **Modify:** `src/TallyDbLoader.Core/Data/DatabaseWriter.cs` — Include `alterid` in `_diff` staging schemas.
- **Modify:** `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` — Directly load parsed `DataTable` to staging and handle streaming.
- **Modify:** `src/TallyDbLoader.Core/Data/DynamicTableSchemaGenerator.cs` — Use `nvarchar(1024)` for text columns in MSSQL.
- **Modify:** `src/TallyDbLoader.Core/Tally/DynamicXmlParser.cs` — Re-implement parser using streaming `XmlReader`.
- **Modify:** `tests/TallyDbLoader.Tests/BackgroundSyncWorkerTests.cs` — Mock connection checks to verify worker logic without network failures.

---

### Task 1: Fix `_diff` Staging Table Schema & Loader Logic

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DatabaseWriter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`
- Test: `tests/TallyDbLoader.Tests/StagingLoaderHelperTests.cs`

- [ ] **Step 1: Update staging table schema definitions**
  Modify the `_diff` table creation queries in `src/TallyDbLoader.Core/Data/DatabaseWriter.cs` (lines 120, 139, 160) to include the `alterid` column:
  
  For Postgres (line 120):
  ```csharp
  CREATE TABLE IF NOT EXISTS _diff (
      guid VARCHAR(64) PRIMARY KEY,
      alterid VARCHAR(64) NOT NULL DEFAULT '0'
  );
  ```
  
  For MSSQL (line 139):
  ```csharp
  IF OBJECT_ID('_diff', 'U') IS NULL 
  CREATE TABLE _diff (
      guid VARCHAR(64) PRIMARY KEY,
      alterid VARCHAR(64) NOT NULL DEFAULT '0'
  );
  ```
  
  For MySQL (line 160):
  ```csharp
  CREATE TABLE IF NOT EXISTS _diff (
      guid VARCHAR(64) PRIMARY KEY,
      alterid VARCHAR(64) NOT NULL DEFAULT '0'
  );
  ```

- [ ] **Step 2: Update background sync worker to load both guid and alterid**
  Modify `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` (lines 248–251) to load the parsed `diffDataTable` directly instead of dropping the `alterid` field:
  ```csharp
  if (diffDataTable.Rows.Count > 0)
  {
      await dbLoader.LoadBulkDataAsync(diffDataTable, "_diff");
  }
  ```

- [ ] **Step 3: Update `StagingLoaderHelperTests.cs` to align with the new schema**
  Update the test `Test_LoadGuidsToStagingAsync_BuildsCorrectDataTable` in `tests/TallyDbLoader.Tests/StagingLoaderHelperTests.cs` if needed, or verify compile status.

---

### Task 2: Fix Unicode Support for MSSQL in DDL Generation

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DynamicTableSchemaGenerator.cs`
- Test: `tests/TallyDbLoader.Tests/DynamicTableSchemaGeneratorTests.cs`

- [ ] **Step 1: Write/Update the failing test**
  Modify `Test_GenerateCreateTableSql_ProducesValidDDLForMssql` in `tests/TallyDbLoader.Tests/DynamicTableSchemaGeneratorTests.cs` (lines 56–71) to assert that text fields generate as `nvarchar(1024)`:
  ```csharp
  [Fact]
  public void Test_GenerateCreateTableSql_ProducesValidDDLForMssql()
  {
      var tableConfig = new TableConfig
      {
          Name = "mst_custom_ledger",
          Fields = new List<FieldConfig>
          {
              new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
              new FieldConfig { Name = "name", Field = "Name", Type = "text" },
              new FieldConfig { Name = "is_revenue", Field = "IsRevenue", Type = "logical" }
          }
      };

      var mssqlSql = DynamicTableSchemaGenerator.GenerateCreateTableSql(tableConfig, "mssql");
      Assert.Contains("IF OBJECT_ID('mst_custom_ledger', 'U') IS NULL CREATE TABLE mst_custom_ledger", mssqlSql);
      Assert.Contains("name nvarchar(1024) not null default ''", mssqlSql);
      Assert.Contains("is_revenue smallint default 0", mssqlSql);
  }
  ```

- [ ] **Step 2: Implement dynamic type mapping**
  Modify `src/TallyDbLoader.Core/Data/DynamicTableSchemaGenerator.cs` (lines 55–58) to check if the target technology is MSSQL and return `nvarchar(1024)`:
  ```csharp
  else // text
  {
      if (isMssql)
      {
          sqlColumnType = "nvarchar(1024) not null default ''";
      }
      else
      {
          sqlColumnType = "varchar(1024) not null default ''";
      }
  }
  ```

---

### Task 3: Refactor XML Parser to Streaming `XmlReader`

**Files:**
- Modify: `src/TallyDbLoader.Core/Tally/DynamicXmlParser.cs`
- Test: `tests/TallyDbLoader.Tests/DynamicXmlParserTests.cs`

- [ ] **Step 1: Verify dynamic XML parser tests**
  Inspect `tests/TallyDbLoader.Tests/DynamicXmlParserTests.cs` to ensure that our new streaming parsing strategy complies with existing expectations.

- [ ] **Step 2: Implement streaming parser in `DynamicXmlParser.cs`**
  Rewrite `ParseXml` in `src/TallyDbLoader.Core/Tally/DynamicXmlParser.cs` to parse XML nodes using `XmlReader` sequentially:
  ```csharp
  using System;
  using System.Data;
  using System.Xml;
  using System.IO;
  using System.Globalization;

  namespace TallyDbLoader.Core.Tally
  {
      public static class DynamicXmlParser
      {
          public static DataTable ParseXml(string xmlContent, TableConfig tableConfig)
          {
              var dataTable = new DataTable(tableConfig.Name);
              
              // Build the DataTable schema based on Fields
              foreach (var field in tableConfig.Fields)
              {
                  var columnType = typeof(string);
                  if (field.Type == "logical")
                  {
                      columnType = typeof(bool);
                  }
                  else if (field.Type == "date")
                  {
                      columnType = typeof(DateTime);
                  }
                  else if (field.Type == "number" || field.Type == "amount" || field.Type == "quantity" || field.Type == "rate")
                  {
                      columnType = typeof(decimal);
                  }
                  
                  var column = new DataColumn(field.Name, columnType);
                  if (field.Type == "date")
                  {
                      column.AllowDBNull = true;
                  }
                  dataTable.Columns.Add(column);
              }
              
              if (string.IsNullOrEmpty(xmlContent))
              {
                  return dataTable;
              }
              
              try
              {
                  using (var sr = new StringReader(xmlContent))
                  using (var reader = XmlReader.Create(sr))
                  {
                      // Search for elements that represent a row.
                      // Since rows contain elements F01, F02, etc., we track row data when we enter a node that has child elements matching the fields.
                      // Tally XML rows are wrapped inside repeating elements (e.g. elements containing F01).
                      
                      string[] rowValues = new string[tableConfig.Fields.Count];
                      bool inRow = false;
                      
                      while (reader.Read())
                      {
                          if (reader.NodeType == XmlNodeType.Element)
                          {
                              string name = reader.Name;
                              
                              if (name == "F01")
                              {
                                  // Reset row values
                                  for (int j = 0; j < rowValues.Length; j++) rowValues[j] = null;
                                  inRow = true;
                              }
                              
                              if (inRow && name.StartsWith("F") && name.Length > 1 && int.TryParse(name.Substring(1), out int fieldIdx))
                                  rowValues[fieldIdx - 1] = reader.ReadElementContentAsString();
                          }
                          else if (reader.NodeType == XmlNodeType.EndElement)
                          {
                              // In Tally XML, each row is contained within an outer element (e.g. <ROW> or parent).
                              // When we hit an end tag of the row element, or when we are inRow and see the next row/parent ending, we write the row.
                              if (inRow && !reader.Name.StartsWith("F"))
                              {
                                  AddRowToTable(dataTable, rowValues, tableConfig);
                                  inRow = false;
                              }
                          }
                      }
                      
                      // Fallback in case the last row didn't trigger EndElement cleanly
                      if (inRow)
                      {
                          AddRowToTable(dataTable, rowValues, tableConfig);
                      }
                  }
              }
              catch
              {
                  // Fallback gracefully on parsing errors
              }
              
              return dataTable;
          }

          private static void AddRowToTable(DataTable dataTable, string[] rowValues, TableConfig tableConfig)
          {
              var row = dataTable.NewRow();
              bool hasData = false;
              
              for (int i = 0; i < tableConfig.Fields.Count; i++)
              {
                  var field = tableConfig.Fields[i];
                  var valStr = rowValues[i];
                  
                  if (valStr == null)
                  {
                      row[field.Name] = DBNull.Value;
                      continue;
                  }
                  
                  hasData = true;
                  
                  if (field.Type == "logical")
                  {
                      row[field.Name] = valStr == "1" || valStr.Equals("true", StringComparison.OrdinalIgnoreCase) || valStr.Equals("yes", StringComparison.OrdinalIgnoreCase);
                  }
                  else if (field.Type == "date")
                  {
                      if (string.IsNullOrEmpty(valStr) || valStr.Contains("ñ") || valStr == "0")
                      {
                          row[field.Name] = DBNull.Value;
                      }
                      else if (DateTime.TryParse(valStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                      {
                          row[field.Name] = parsedDate;
                      }
                      else
                      {
                          row[field.Name] = DBNull.Value;
                      }
                  }
                  else if (field.Type == "number" || field.Type == "amount" || field.Type == "quantity" || field.Type == "rate")
                  {
                      if (decimal.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDecimal))
                      {
                          row[field.Name] = parsedDecimal;
                      }
                      else
                      {
                          row[field.Name] = 0m;
                      }
                  }
                  else
                  {
                      row[field.Name] = valStr;
                  }
              }
              
              if (hasData)
              {
                  dataTable.Rows.Add(row);
              }
          }
      }
  }
  ```

---

### Task 4: Fix Unit Test Suite Failure on Real Database Connection

**Files:**
- Modify: `tests/TallyDbLoader.Tests/BackgroundSyncWorkerTests.cs`

- [ ] **Step 1: Catch and handle database connection failure gracefully in unit test mode**
  In `BackgroundSyncWorker.cs`, if the database profile specifies a remote server but fails to connect, it throws an exception. We can configure our mock job configuration inside `BackgroundSyncWorkerTests.cs` to use `sqlite` for testing dynamic SQL schema generation.
  Alternatively, update the tests to configure SQLite profile and test dynamic SQL schema mapping.
  
  Let's keep the fixes highly isolated: we can adjust the database profile used in `Test_BackgroundSyncWorker_IncrementalOrchestration` to use SQLite (or avoid hitting real connection setup if we stub/mock out database interactions).
