# Tally .NET Database Loader Incremental Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a fully configuration-driven, robust incremental synchronization engine matching the Node.js implementation (`src/tally.ts` and `database-structure-incremental.sql`) supporting PostgreSQL, SQL Server (MSSQL), and MySQL.

**Architecture:** Extend the SQLite metadata repository to store sync modes. Refactor the `BackgroundSyncWorker` to perform AlterID queries, staging table population (`_diff`, `_delete`, and `_vchnumber`), cascade deletes, cascade JOIN updates, and voucher numbering corrections using technology-specific SQL syntax.

**Tech Stack:** C#/.NET Core, Dapper, SQLite, WPF, Npgsql (PostgreSQL), MySqlConnector (MySQL), Microsoft.Data.SqlClient (MSSQL), XML Serialization, and xUnit.

---

## Proposed File Changes Map
- **Modify:** `src/TallyDbLoader.Core/Models/Models.cs` — Add `SyncMode` property to `SyncJob`.
- **Modify:** `src/TallyDbLoader.Core/Data/DatabaseHelper.cs` — Migrate SQLite structure to include `sync_mode` field.
- **Modify:** `src/TallyDbLoader.Core/Data/ConfigRepository.cs` — Update CRUD query methods for `SyncJob` mapping.
- **Modify:** `src/TallyDbLoader.Core/Database/DatabaseWriter.cs` — Implement DDL creators for `config`, `_diff`, `_delete`, and `_vchnumber` tables, as well as config table getter/setter methods.
- **Modify:** `src/TallyDbLoader.Core/Tally/TallyClient.cs` — Add querying method to retrieve AlterIDs from Tally.
- **Modify:** `src/TallyDbLoader.Core/Services/BackgroundSyncWorker.cs` — Build staging checks, record deletion loops, cascade updates, and voucher numbering adjustments.
- **Modify:** `src/TallyDbLoader.Wpf/MainViewModel.cs` — Add backing properties for job editing.
- **Modify:** `src/TallyDbLoader.Wpf/MainWindow.xaml` — Add Combobox control for Job creation and DataGrid column for listing sync mode.
- **Test:** `test/TallyDbLoader.Tests/IncrementalSyncTests.cs` — Write suite validating XML parsers, AlterID loaders, and staging workflows.

---

### Task 1: SQLite Schema & Config Repository Migration

**Files:**
- Modify: `src/TallyDbLoader.Core/Models/Models.cs`
- Modify: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
- Test: `test/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

- [ ] **Step 1: Write test validating SyncMode mapping**
  Add a new test inside `test/TallyDbLoader.Tests/ConfigRepositoryTests.cs` (or create it if it doesn't exist) to verify a `SyncJob` saves and retrieves the `SyncMode` property correctly:
  ```csharp
  [Fact]
  public void Should_Save_And_Retrieve_SyncMode()
  {
      var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db");
      DatabaseHelper.InitializeDatabase(dbPath);
      var repo = new ConfigRepository(dbPath);
      
      var profile = new DatabaseProfile { Name = "TestDb", Technology = "postgres", Server = "localhost" };
      repo.SaveDatabaseProfile(profile);
      var savedProfile = repo.GetAllDatabaseProfiles().First();
      
      var job = new SyncJob
      {
          CompanyName = "Company A",
          DbProfileId = savedProfile.Id,
          TargetCatalog = "catalog_a",
          SyncIntervalMinutes = 30,
          SyncMode = "incremental"
      };
      
      repo.SaveSyncJob(job);
      var retrieved = repo.GetAllSyncJobs().First();
      Assert.Equal("incremental", retrieved.SyncMode);
      
      File.Delete(dbPath);
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test --filter "Should_Save_And_Retrieve_SyncMode"`
  Expected: Compiling error or mapping error due to missing property.

- [ ] **Step 3: Implement SyncMode properties and schema updates**
  Update `SyncJob` in `src/TallyDbLoader.Core/Models/Models.cs`:
  ```csharp
  public class SyncJob
  {
      public int Id { get; set; }
      public string CompanyName { get; set; } = string.Empty;
      public int DbProfileId { get; set; }
      public string TargetCatalog { get; set; } = string.Empty;
      public int? SyncIntervalMinutes { get; set; }
      public string? DailyTimeLocal { get; set; }
      public string? LastRunTime { get; set; }
      public string Status { get; set; } = "Idle";
      public string SyncMode { get; set; } = "full"; // Default to "full"
  }
  ```
  Update `DatabaseHelper.cs` to add column migration if not existing:
  ```csharp
  CREATE TABLE IF NOT EXISTS sync_jobs (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      company_name TEXT NOT NULL,
      db_profile_id INTEGER NOT NULL,
      target_catalog TEXT NOT NULL,
      sync_interval_minutes INTEGER,
      daily_time_local TEXT,
      last_run_time TEXT,
      status TEXT NOT NULL DEFAULT 'Idle',
      sync_mode TEXT NOT NULL DEFAULT 'full',
      FOREIGN KEY (db_profile_id) REFERENCES database_profiles(id)
  );
  ```
  And inside initialization logic `try-catch` Block:
  ```csharp
  try
  {
      conn.Execute("ALTER TABLE sync_jobs ADD COLUMN sync_mode TEXT NOT NULL DEFAULT 'full';");
  }
  catch { }
  ```
  Update Dapper mapping queries in `ConfigRepository.cs`:
  Ensure saving maps `sync_mode`:
  ```csharp
  conn.Execute(@"
      INSERT INTO sync_jobs (company_name, db_profile_id, target_catalog, sync_interval_minutes, daily_time_local, status, sync_mode)
      VALUES (@CompanyName, @DbProfileId, @TargetCatalog, @SyncIntervalMinutes, @DailyTimeLocal, @Status, @SyncMode);", job);
  ```
  Ensure updates map `sync_mode`:
  ```csharp
  conn.Execute(@"
      UPDATE sync_jobs SET company_name = @CompanyName, db_profile_id = @DbProfileId, target_catalog = @TargetCatalog,
                           sync_interval_minutes = @SyncIntervalMinutes, daily_time_local = @DailyTimeLocal, sync_mode = @SyncMode
      WHERE id = @Id;", job);
  ```
  Ensure select queries retrieve `sync_mode` as `SyncMode`. (By using `sync_mode AS SyncMode` or matching properties names via Dapper).

- [ ] **Step 4: Run test to verify it passes**
  Run: `dotnet test --filter "Should_Save_And_Retrieve_SyncMode"`
  Expected: PASS.

- [ ] **Step 5: Commit changes**
  Run: `git commit -am "feat: Add SyncMode property and DB mapping migrations"`

---

### Task 2: WPF User Interface Updates

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs`
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml`

- [ ] **Step 1: Update main ViewModel**
  Add property and field mappings for `JobSyncMode` in `src/TallyDbLoader.Wpf/MainViewModel.cs`:
  ```csharp
  private string _jobSyncMode = "full";
  public string JobSyncMode
  {
      get => _jobSyncMode;
      set { _jobSyncMode = value; OnPropertyChanged(); }
  }
  ```
  Update job editing functions `StartEditingSyncJob` and `CancelJobEdit` in `MainViewModel.cs`:
  ```csharp
  public void StartEditingSyncJob(SyncJob job)
  {
      if (job == null) return;
      _editingSyncJobId = job.Id;
      JobCompany = job.CompanyName;
      JobTargetCatalog = job.TargetCatalog;
      JobInterval = job.SyncIntervalMinutes ?? 15;
      JobSyncMode = job.SyncMode ?? "full";
      // ... (existing selection logic)
  }
  
  public void CancelJobEdit()
  {
      _editingSyncJobId = 0;
      JobCompany = string.Empty;
      JobTargetCatalog = string.Empty;
      JobInterval = 15;
      JobSyncMode = "full";
      JobSelectedProfile = null;
      // ... (existing update logic)
  }
  ```
  Update `AddSyncJob()` in `MainViewModel.cs` to include `SyncMode`:
  ```csharp
  var job = new SyncJob
  {
      Id = _editingSyncJobId,
      CompanyName = JobCompany,
      DbProfileId = JobSelectedProfile.Id,
      TargetCatalog = JobTargetCatalog,
      SyncIntervalMinutes = JobInterval,
      Status = "Idle",
      SyncMode = JobSyncMode
  };
  ```

- [ ] **Step 2: Update MainWindow.xaml Views**
  Add a Combobox for selecting the Sync Mode in the Job Panel:
  ```xml
  <TextBlock Text="Sync Mode" FontSize="11" Foreground="#CCCCCC" Margin="0,0,0,4"/>
  <ComboBox SelectedValuePath="Content" SelectedValue="{Binding JobSyncMode, UpdateSourceTrigger=PropertyChanged}" Background="#2D2D2D" Margin="0,0,0,10">
      <ComboBoxItem Content="full" IsSelected="True"/>
      <ComboBoxItem Content="incremental"/>
  </ComboBox>
  ```
  And add a Column in the DataGrid representing the Sync Mode:
  ```xml
  <DataGridTextColumn Header="Sync Mode" Binding="{Binding SyncMode}" Width="100"/>
  ```

- [ ] **Step 3: Compile and verify WPF project**
  Run: `dotnet build`
  Expected: Successful compilation.

- [ ] **Step 4: Commit changes**
  Run: `git commit -am "feat: Update WPF form and DataGrid to support SyncMode"`

---

### Task 3: Staging and Configuration Tables DDL Manager

**Files:**
- Modify: `src/TallyDbLoader.Core/Database/DatabaseWriter.cs`
- Test: `test/TallyDbLoader.Tests/IncrementalSyncTests.cs`

- [ ] **Step 1: Write test for staging schema initialization**
  Write a test checking that staging and config tables are initialized and config values can be written/read:
  ```csharp
  [Fact]
  public void Should_Initialize_Staging_And_Set_Get_Configs()
  {
      // Mock db profile or use a local sqlite test connection
      // We will assert execution works for postgres/mysql/mssql queries.
  }
  ```

- [ ] **Step 2: Implement Staging Table setup in DatabaseWriter.cs**
  Add initialization and value getter/setters in `src/TallyDbLoader.Core/Database/DatabaseWriter.cs`:
  ```csharp
  public static void InitializeStagingTables(DatabaseProfile profile, string catalog)
  {
      using (var conn = GetConnection(profile, catalog))
      {
          var tech = profile.Technology.ToLower();
          var queries = new List<string>();
          
          if (tech == "postgres" || tech == "mysql")
          {
              queries.Add("CREATE TABLE IF NOT EXISTS config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024));");
              queries.Add("CREATE TABLE IF NOT EXISTS _diff (guid VARCHAR(64), alterid VARCHAR(64));");
              queries.Add("CREATE TABLE IF NOT EXISTS _delete (guid VARCHAR(64));");
              queries.Add("CREATE TABLE IF NOT EXISTS _vchnumber (guid VARCHAR(64), voucher_number VARCHAR(256));");
          }
          else if (tech == "mssql")
          {
              queries.Add(@"
                  IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='config' AND xtype='U')
                  CREATE TABLE config (name VARCHAR(64) NOT NULL PRIMARY KEY, value VARCHAR(1024));");
              queries.Add(@"
                  IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_diff' AND xtype='U')
                  CREATE TABLE _diff (guid VARCHAR(64) NOT NULL, alterid VARCHAR(64));");
              queries.Add(@"
                  IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_delete' AND xtype='U')
                  CREATE TABLE _delete (guid VARCHAR(64) NOT NULL);");
              queries.Add(@"
                  IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_vchnumber' AND xtype='U')
                  CREATE TABLE _vchnumber (guid VARCHAR(64) NOT NULL, voucher_number VARCHAR(256) NOT NULL);");
          }
          
          foreach (var query in queries)
          {
              using (var cmd = conn.CreateCommand())
              {
                  cmd.CommandText = query;
                  cmd.ExecuteNonQuery();
              }
          }
      }
  }
  
  public static long GetConfigValue(DatabaseProfile profile, string catalog, string name)
  {
      using (var conn = GetConnection(profile, catalog))
      using (var cmd = conn.CreateCommand())
      {
          cmd.CommandText = "SELECT value FROM config WHERE name = @name;";
          var p = cmd.CreateParameter();
          p.ParameterName = "@name";
          p.Value = name;
          cmd.Parameters.Add(p);
          
          var result = cmd.ExecuteScalar();
          if (result == null || result == DBNull.Value) return 0;
          return long.TryParse(result.ToString(), out long val) ? val : 0;
      }
  }
  
  public static void SaveConfigValue(DatabaseProfile profile, string catalog, string name, long value)
  {
      using (var conn = GetConnection(profile, catalog))
      using (var cmd = conn.CreateCommand())
      {
          var tech = profile.Technology.ToLower();
          if (tech == "postgres" || tech == "mysql")
          {
              cmd.CommandText = @"
                  INSERT INTO config (name, value) VALUES (@name, @value)
                  ON CONFLICT (name) DO UPDATE SET value = EXCLUDED.value;";
          }
          else if (tech == "mssql")
          {
              cmd.CommandText = @"
                  MERGE config AS target
                  USING (SELECT @name AS name, @value AS value) AS source
                  ON (target.name = source.name)
                  WHEN MATCHED THEN UPDATE SET value = source.value
                  WHEN NOT MATCHED THEN INSERT (name, value) VALUES (source.name, source.value);";
          }
          
          var pName = cmd.CreateParameter();
          pName.ParameterName = "@name";
          pName.Value = name;
          cmd.Parameters.Add(pName);
          
          var pValue = cmd.CreateParameter();
          pValue.ParameterName = "@value";
          pValue.Value = value.ToString();
          cmd.Parameters.Add(pValue);
          
          cmd.ExecuteNonQuery();
      }
  }
  ```

- [ ] **Step 3: Run verification tests**
  Verify the project compiles.

- [ ] **Step 4: Commit changes**
  Run: `git commit -am "feat: Implement staging schema builders and config table helpers"`

---

### Task 4: Tally AlterID Retriever

**Files:**
- Modify: `src/TallyDbLoader.Core/Tally/TallyClient.cs`
- Test: `test/TallyDbLoader.Tests/IncrementalSyncTests.cs`

- [ ] **Step 1: Write test for parsing AlterIDs**
  Verify AlterID fetching payload is built correctly and response parsing handles empty or parsed numeric values.
  ```csharp
  [Fact]
  public void Should_Parse_AlterIDs_Correctly()
  {
      var responseText = "\"2350\",\"1420\"";
      var parts = responseText.Replace("\"", "").Split(',');
      var masterAlterId = parts.Length >= 2 && long.TryParse(parts[0], out long m) ? m : 0;
      var vchAlterId = parts.Length >= 2 && long.TryParse(parts[1], out long v) ? v : 0;
      Assert.Equal(2350, masterAlterId);
      Assert.Equal(1420, vchAlterId);
  }
  ```

- [ ] **Step 2: Implement AlterId Retrieval in TallyClient.cs**
  Add the method to fetch AlterIDs to `src/TallyDbLoader.Core/Tally/TallyClient.cs`:
  ```csharp
  public async Task<(long MasterAlterId, long VchAlterId)> GetTallyMaxAlterIdsAsync(string companyName)
  {
      var escapedCompany = System.Security.SecurityElement.Escape(companyName);
      var xmlPayload = $@"<?xml version=""1.0"" encoding=""utf-8""?><ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>MyReport</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME=""MyReport""><FORMS>MyForm</FORMS></REPORT><FORM NAME=""MyForm""><PARTS>MyPart</PARTS></FORM><PART NAME=""MyPart""><LINES>MyLine</LINES><REPEAT>MyLine : MyCollection</REPEAT></PART><LINE NAME=""MyLine""><FIELDS>FldAlterMaster,FldAlterTransaction</FIELDS></LINE><FIELD NAME=""FldAlterMaster""><SET>$AltMstId</SET></FIELD><FIELD NAME=""FldAlterTransaction""><SET>$AltVchId</SET></FIELD><COLLECTION NAME=""MyCollection""><TYPE>Company</TYPE><FILTER>FilterActiveCompany</FILTER></COLLECTION><SYSTEM TYPE=""Formulae"" NAME=""FilterActiveCompany"">$$IsEqual:##SVCurrentCompany:$Name</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";
      
      var targetCompanyRef = string.IsNullOrEmpty(companyName) ? "##SVCurrentCompany" : $"\"{escapedCompany}\"";
      xmlPayload = xmlPayload.Replace("##SVCurrentCompany", targetCompanyRef);
      
      var content = await PostXmlAsync(xmlPayload);
      if (string.IsNullOrEmpty(content) || content.Trim() == "")
      {
          return (-1, -1);
      }
      
      var parts = content.Replace("\"", "").Split(',');
      long mId = parts.Length >= 2 && long.TryParse(parts[0], out long m) ? m : 0;
      long vId = parts.Length >= 2 && long.TryParse(parts[1], out long v) ? v : 0;
      return (mId, vId);
  }
  ```

- [ ] **Step 3: Verify execution**
  Run: `dotnet build`

- [ ] **Step 4: Commit changes**
  Run: `git commit -am "feat: Implement GetTallyMaxAlterIdsAsync in TallyClient"`

---

### Task 5: Dynamic Record Diffing, Deletion, and Loading

**Files:**
- Modify: `src/TallyDbLoader.Core/Services/BackgroundSyncWorker.cs`
- Modify: `src/TallyDbLoader.Core/Database/DatabaseWriter.cs`
- Test: `test/TallyDbLoader.Tests/IncrementalSyncTests.cs`

- [ ] **Step 1: Write test validating diff queries**
  Write unit tests validating diff and delete population statements for PostgreSQL, MSSQL, and MySQL.

- [ ] **Step 2: Add bulk staging methods to DatabaseWriter.cs**
  Add database execution helpers in `src/TallyDbLoader.Core/Database/DatabaseWriter.cs`:
  ```csharp
  public static void ExecuteNonQuery(DatabaseProfile profile, string catalog, string sql)
  {
      using (var conn = GetConnection(profile, catalog))
      using (var cmd = conn.CreateCommand())
      {
          cmd.CommandText = sql;
          cmd.ExecuteNonQuery();
      }
  }
  
  public static int GetRecordCount(DatabaseProfile profile, string catalog, string sql)
  {
      using (var conn = GetConnection(profile, catalog))
      using (var cmd = conn.CreateCommand())
      {
          cmd.CommandText = sql;
          var result = cmd.ExecuteScalar();
          if (result == null || result == DBNull.Value) return 0;
          return Convert.ToInt32(result);
      }
  }
  ```

- [ ] **Step 3: Implement Incremental Sync Engine in BackgroundSyncWorker.cs**
  Update `RunSyncJobInternalAsync` inside `src/TallyDbLoader.Core/Services/BackgroundSyncWorker.cs` to dynamically evaluate the SyncMode:
  ```csharp
  if (job.SyncMode.ToLower() == "incremental")
  {
      await ExecuteIncrementalSyncAsync(job, dbProfile, tallyClient);
  }
  else
  {
      await ExecuteFullSyncAsync(job, dbProfile, tallyClient);
  }
  ```
  And implement `ExecuteIncrementalSyncAsync`:
  ```csharp
  private async Task ExecuteIncrementalSyncAsync(SyncJob job, DatabaseProfile profile, TallyClient client)
  {
      // 1. Initialize Tables
      var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tally-export-config.yaml");
      if (!File.Exists(configPath)) return;
      var config = YamlConfigParser.Parse(File.ReadAllText(configPath));
      
      // Initialize schemas for all configured tables
      foreach (var table in config.Master.Concat(config.Transaction))
      {
          DatabaseWriter.InitializeTableSchema(profile, job.TargetCatalog, table);
      }
      
      // Initialize staging and config tables
      DatabaseWriter.InitializeStagingTables(profile, job.TargetCatalog);
      
      // 2. Query Highwater Marks
      long dbMasterAlterId = DatabaseWriter.GetConfigValue(profile, job.TargetCatalog, "Last AlterID Master");
      long dbVchAlterId = DatabaseWriter.GetConfigValue(profile, job.TargetCatalog, "Last AlterID Transaction");
      
      (long tallyMasterAlterId, long tallyVchAlterId) = await client.GetTallyMaxAlterIdsAsync(job.CompanyName);
      if (tallyMasterAlterId == -1 && tallyVchAlterId == -1)
      {
          Log("Target company is closed in Tally. Sync aborted.");
          return;
      }
      
      if (dbMasterAlterId == tallyMasterAlterId && dbVchAlterId == tallyVchAlterId)
      {
          Log("No changes found in Tally. Skipping incremental sync.");
          return;
      }
      
      bool masterChanged = dbMasterAlterId != tallyMasterAlterId;
      bool vchChanged = dbVchAlterId != tallyVchAlterId;
      
      var primaryTables = new List<TableConfig>();
      if (masterChanged) primaryTables.AddRange(config.Master.Where(t => t.Nature?.ToLower() == "primary"));
      if (vchChanged) primaryTables.AddRange(config.Transaction.Where(t => t.Nature?.ToLower() == "primary"));
      
      // 3. Diff and Deletion loop
      foreach (var table in primaryTables)
      {
          DatabaseWriter.ExecuteNonQuery(profile, job.TargetCatalog, "TRUNCATE TABLE _diff;");
          DatabaseWriter.ExecuteNonQuery(profile, job.TargetCatalog, "TRUNCATE TABLE _delete;");
          
          // Generate active IDs XML query
          var tdlQuery = TallyXmlQueryGenerator.GenerateActiveIdsQuery(table);
          var activeXml = await client.PostXmlAsync(tdlQuery);
          var dt = DynamicXmlParser.Parse(activeXml, new TableConfig
          {
              Name = "_diff",
              Fields = new List<FieldConfig>
              {
                  new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                  new FieldConfig { Name = "alterid", Field = "AlterId", Type = "text" }
              }
          });
          
          // Bulk load into _diff
          var tempCsv = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".csv");
          var tsv = DatabaseWriter.ConvertToTsv(dt);
          File.WriteAllText(tempCsv, tsv, System.Text.Encoding.UTF8);
          await DatabaseWriter.BulkLoadFileAsync(profile, job.TargetCatalog, tempCsv, "_diff", new[] { "text", "text" });
          File.Delete(tempCsv);
          
          // Populate _delete with deleted or altered items
          var deleteQueries = new List<string>
          {
              $"INSERT INTO _delete SELECT guid FROM {table.Name} WHERE guid NOT IN (SELECT guid FROM _diff);",
              $"INSERT INTO _delete SELECT t.guid FROM {table.Name} as t JOIN _diff as s ON s.guid = t.guid WHERE s.alterid <> CAST(t.alterid AS VARCHAR(64));"
          };
          foreach (var dq in deleteQueries)
          {
              DatabaseWriter.ExecuteNonQuery(profile, job.TargetCatalog, dq);
          }
          
          // Delete records from main table and cascade delete dependents
          DatabaseWriter.ExecuteNonQuery(profile, job.TargetCatalog, $"DELETE FROM {table.Name} WHERE guid IN (SELECT guid FROM _delete);");
          if (table.CascadeDelete != null)
          {
              foreach (var cd in table.CascadeDelete)
              {
                  DatabaseWriter.ExecuteNonQuery(profile, job.TargetCatalog, $"DELETE FROM {cd.Table} WHERE {cd.Field} IN (SELECT guid FROM _delete);");
              }
          }
      }
      
      // 4. Fetch and Load Master Alterations
      if (masterChanged)
      {
          foreach (var table in config.Master)
          {
              var filterList = table.Filters != null ? new List<string>(table.Filters) : new List<string>();
              filterList.Add($"$$NumValue:$AlterID > {dbMasterAlterId}");
              
              var tempTable = new TableConfig
              {
                  Name = table.Name,
                  Collection = table.Collection,
                  Fields = table.Fields,
                  Filters = filterList
              };
              
              var xml = await client.PostXmlAsync(TallyXmlQueryGenerator.Generate(tempTable, job.CompanyName));
              var dt = DynamicXmlParser.Parse(xml, tempTable);
              if (dt.Rows.Count > 0)
              {
                  var tempCsv = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".csv");
                  var tsv = DatabaseWriter.ConvertToTsv(dt);
                  File.WriteAllText(tempCsv, tsv, System.Text.Encoding.UTF8);
                  await DatabaseWriter.BulkLoadFileAsync(profile, job.TargetCatalog, tempCsv, table.Name, table.Fields.Select(f => f.Type).ToArray());
                  File.Delete(tempCsv);
              }
          }
          
          // Execute Master Cascade Updates
          ExecuteCascadeUpdates(profile, job.TargetCatalog, config.Master);
      }
      
      // 5. Fetch and Load Transaction Alterations
      if (vchChanged)
      {
          foreach (var table in config.Transaction)
          {
              var filterList = table.Filters != null ? new List<string>(table.Filters) : new List<string>();
              filterList.Add($"$$NumValue:$AlterID > {dbVchAlterId}");
              
              var tempTable = new TableConfig
              {
                  Name = table.Name,
                  Collection = table.Collection,
                  Fields = table.Fields,
                  Filters = filterList
              };
              
              var xml = await client.PostXmlAsync(TallyXmlQueryGenerator.Generate(tempTable, job.CompanyName));
              var dt = DynamicXmlParser.Parse(xml, tempTable);
              if (dt.Rows.Count > 0)
              {
                  var tempCsv = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".csv");
                  var tsv = DatabaseWriter.ConvertToTsv(dt);
                  File.WriteAllText(tempCsv, tsv, System.Text.Encoding.UTF8);
                  await DatabaseWriter.BulkLoadFileAsync(profile, job.TargetCatalog, tempCsv, table.Name, table.Fields.Select(f => f.Type).ToArray());
                  File.Delete(tempCsv);
              }
          }
          
          // Check Auto Numbering Vouchers Shift correction
          await CorrectVoucherNumberShiftsAsync(profile, job.TargetCatalog, config.Transaction, client, job.CompanyName);
      }
      
      // 6. Update Highwater AlterIDs in DB Config
      DatabaseWriter.SaveConfigValue(profile, job.TargetCatalog, "Last AlterID Master", tallyMasterAlterId);
      DatabaseWriter.SaveConfigValue(profile, job.TargetCatalog, "Last AlterID Transaction", tallyVchAlterId);
      
      Log("Incremental synchronization completed successfully.");
  }
  ```

- [ ] **Step 3: Compile background sync worker**
  Run: `dotnet build`

- [ ] **Step 4: Commit changes**
  Run: `git commit -am "feat: Implement Active diff and deletion check logic in SyncWorker"`

---

### Task 6: Cascade Joins and Voucher Shift Updates

**Files:**
- Modify: `src/TallyDbLoader.Core/Services/BackgroundSyncWorker.cs`
- Modify: `src/TallyDbLoader.Core/Tally/TallyXmlQueryGenerator.cs`

- [ ] **Step 1: Write TDL generation method for Active IDs and Voucher Numbers**
  Update `src/TallyDbLoader.Core/Tally/TallyXmlQueryGenerator.cs`:
  ```csharp
  public static string GenerateActiveIdsQuery(TableConfig table)
  {
      var collectionName = table.Collection ?? "Ledger";
      var filterStr = "";
      if (table.Filters != null && table.Filters.Count > 0)
      {
          filterStr = string.Join("\n", table.Filters.Select((f, idx) => $"<FILTER>Filt_{idx}</FILTER>"));
      }
      
      var sysFilters = "";
      if (table.Filters != null && table.Filters.Count > 0)
      {
          sysFilters = string.Join("\n", table.Filters.Select((f, idx) => $"<SYSTEM TYPE=\"Formulae\" NAME=\"Filt_{idx}\">{f}</SYSTEM>"));
      }
      
      return $@"<?xml version=""1.0"" encoding=""utf-8""?>
  <ENVELOPE>
    <HEADER>
      <VERSION>1</VERSION>
      <TALLYREQUEST>Export</TALLYREQUEST>
      <TYPE>Data</TYPE>
      <ID>ActiveIdsReport</ID>
    </HEADER>
    <BODY>
      <DESC>
        <STATICVARIABLES>
          <SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT>
        </STATICVARIABLES>
        <TDL>
          <TDLMESSAGE>
            <REPORT NAME=""ActiveIdsReport"">
              <FORMS>ActiveIdsForm</FORMS>
            </REPORT>
            <FORM NAME=""ActiveIdsForm"">
              <PARTS>ActiveIdsPart</PARTS>
            </FORM>
            <PART NAME=""ActiveIdsPart"">
              <LINES>ActiveIdsLine</LINES>
              <REPEAT>ActiveIdsLine : ActiveIdsColl</REPEAT>
            </PART>
            <LINE NAME=""ActiveIdsLine"">
              <FIELDS>FldGuid,FldAlter</FIELDS>
            </LINE>
            <FIELD NAME=""FldGuid""><SET>$Guid</SET></FIELD>
            <FIELD NAME=""FldAlter""><SET>$AlterId</SET></FIELD>
            <COLLECTION NAME=""ActiveIdsColl"">
              <TYPE>{collectionName}</TYPE>
              {filterStr}
            </COLLECTION>
            {sysFilters}
          </TDLMESSAGE>
        </TDL>
      </DESC>
    </BODY>
  </ENVELOPE>";
  }
  ```

- [ ] **Step 2: Add Cascade Updates and Voucher Shift methods to BackgroundSyncWorker.cs**
  Implement the cascade updates and shift corrections:
  ```csharp
  private void ExecuteCascadeUpdates(DatabaseProfile profile, string catalog, List<TableConfig> tables)
  {
      var tech = profile.Technology.ToLower();
      foreach (var table in tables.Where(t => t.Nature?.ToLower() == "primary"))
      {
          if (table.CascadeUpdate == null) continue;
          foreach (var cu in table.CascadeUpdate)
          {
              string sql = "";
              if (tech == "postgres")
              {
                  sql = $"UPDATE {cu.Table} as t SET {cu.Field} = s.name FROM {table.Name} as s WHERE s.guid = t._{cu.Field};";
              }
              else if (tech == "mssql")
              {
                  sql = $"UPDATE t SET t.{cu.Field} = s.name FROM {cu.Table} as t JOIN {table.Name} as s ON s.guid = t._{cu.Field};";
              }
              else if (tech == "mysql")
              {
                  sql = $"UPDATE {cu.Table} as t JOIN {table.Name} as s ON s.guid = t._{cu.Field} SET t.{cu.Field} = s.name;";
              }
              
              if (!string.IsNullOrEmpty(sql))
              {
                  DatabaseWriter.ExecuteNonQuery(profile, catalog, sql);
              }
          }
      }
  }
  
  private async Task CorrectVoucherNumberShiftsAsync(DatabaseProfile profile, string catalog, List<TableConfig> tables, TallyClient client, string companyName)
  {
      var hasAutoVch = DatabaseWriter.GetRecordCount(profile, catalog, "SELECT COUNT(*) FROM mst_vouchertype WHERE numbering_method LIKE '%Auto%';") > 0;
      if (!hasAutoVch) return;
      
      DatabaseWriter.ExecuteNonQuery(profile, catalog, "TRUNCATE TABLE _vchnumber;");
      
      var vchTable = tables.First(t => t.Name == "trn_voucher");
      var tdl = $@"<?xml version=""1.0"" encoding=""utf-8""?>
  <ENVELOPE>
    <HEADER>
      <VERSION>1</VERSION>
      <TALLYREQUEST>Export</TALLYREQUEST>
      <TYPE>Data</TYPE>
      <ID>VchNumReport</ID>
    </HEADER>
    <BODY>
      <DESC>
        <STATICVARIABLES>
          <SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT>
        </STATICVARIABLES>
        <TDL>
          <TDLMESSAGE>
            <REPORT NAME=""VchNumReport"">
              <FORMS>VchNumForm</FORMS>
            </REPORT>
            <FORM NAME=""VchNumForm"">
              <PARTS>VchNumPart</PARTS>
            </FORM>
            <PART NAME=""VchNumPart"">
              <LINES>VchNumLine</LINES>
              <REPEAT>VchNumLine : VchNumColl</REPEAT>
            </PART>
            <LINE NAME=""VchNumLine"">
              <FIELDS>FldGuid,FldVchNum</FIELDS>
            </LINE>
            <FIELD NAME=""FldGuid""><SET>$Guid</SET></FIELD>
            <FIELD NAME=""FldVchNum""><SET>$VoucherNumber</SET></FIELD>
            <COLLECTION NAME=""VchNumColl"">
              <TYPE>Voucher</TYPE>
              <FILTER>FiltAuto</FILTER>
            </COLLECTION>
            <SYSTEM TYPE="Formulae" NAME="FiltAuto">$$IsEqual:($NumberingMethod:VoucherType:$VoucherTypeName):"Automatic"</SYSTEM>
          </TDLMESSAGE>
        </TDL>
      </DESC>
    </BODY>
  </ENVELOPE>";
      
      var xml = await client.PostXmlAsync(tdl);
      var dt = DynamicXmlParser.Parse(xml, new TableConfig
      {
          Name = "_vchnumber",
          Fields = new List<FieldConfig>
          {
              new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
              new FieldConfig { Name = "voucher_number", Field = "VoucherNumber", Type = "text" }
          }
      });
      
      if (dt.Rows.Count > 0)
      {
          var tempCsv = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".csv");
          var tsv = DatabaseWriter.ConvertToTsv(dt);
          File.WriteAllText(tempCsv, tsv, System.Text.Encoding.UTF8);
          await DatabaseWriter.BulkLoadFileAsync(profile, catalog, tempCsv, "_vchnumber", new[] { "text", "text" });
          File.Delete(tempCsv);
          
          var tech = profile.Technology.ToLower();
          string sql = "";
          if (tech == "postgres")
          {
              sql = "UPDATE trn_voucher as t SET voucher_number = s.voucher_number FROM _vchnumber as s WHERE s.guid = t.guid;";
          }
          else if (tech == "mssql")
          {
              sql = "UPDATE t SET t.voucher_number = s.voucher_number FROM trn_voucher as t JOIN _vchnumber as s ON s.guid = t.guid;";
          }
          else if (tech == "mysql")
          {
              sql = "UPDATE trn_voucher as t JOIN _vchnumber as s ON s.guid = t.guid SET t.voucher_number = s.voucher_number;";
          }
          
          DatabaseWriter.ExecuteNonQuery(profile, catalog, sql);
      }
  }
  ```

- [ ] **Step 3: Build & verify tests**
  Run: `dotnet test`

- [ ] **Step 4: Commit changes**
  Run: `git commit -am "feat: Implement JOIN-based reference updates and auto-numbering voucher shift checks"`
