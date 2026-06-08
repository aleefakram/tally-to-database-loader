# Full Sync Safe Promotion Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the unsafe live table truncation in `FullSyncRunner` with a safe stage-validate-promote flow using SQLite in-memory tables.

**Architecture:** Encapsulate staging, schema-validation, custom GUID-uniqueness validation, and transactional promotion inside a new `IFullSyncTablePromoter` abstraction. Pass the promoter via constructor dependency to `FullSyncRunner`, resolving the promoter technology dynamically in `BackgroundSyncWorker`. Remove the unused `IDatabaseLoader` dependency from `FullSyncRunner` to simplify coupling. Use a distinct prefix (`__tally_fullsync_staging_`) for staging tables to reduce accidental collisions until a formal identifier policy lands.

**Tech Stack:** C#, .NET 8.0, Microsoft.Data.Sqlite, Dapper (for tests)

---

### Task 1: Add Promoter Stubs, Refactor Runner, and Write Failing Tests (TDD Red Phase)

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/IFullSyncTablePromoter.cs`
- Create: `src/TallyDbLoader.Core/Sync/UnsupportedFullSyncTablePromoter.cs`
- Create: `src/TallyDbLoader.Core/Sync/SqliteFullSyncTablePromoter.cs`
- Modify: `src/TallyDbLoader.Core/Sync/FullSyncRunner.cs`
- Modify: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`
- Modify: `tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs`

- [ ] **Step 1: Write IFullSyncTablePromoter.cs**

Create `src/TallyDbLoader.Core/Sync/IFullSyncTablePromoter.cs` with the following content:
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
        Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn);
    }
}
```

- [ ] **Step 2: Write UnsupportedFullSyncTablePromoter stub**

Create `src/TallyDbLoader.Core/Sync/UnsupportedFullSyncTablePromoter.cs` with the following content:
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
        public Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn)
        {
            throw new NotSupportedException("Safe promotion is not supported for this database technology.");
        }
    }
}
```

- [ ] **Step 3: Write SqliteFullSyncTablePromoter stub**

Create `src/TallyDbLoader.Core/Sync/SqliteFullSyncTablePromoter.cs` with the following stub content (which throws `NotImplementedException` to ensure tests fail initially):
```csharp
using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class SqliteFullSyncTablePromoter : IFullSyncTablePromoter
    {
        public Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn)
        {
            throw new NotImplementedException("Sqlite promoter not implemented yet.");
        }
    }
}
```

- [ ] **Step 4: Update FullSyncRunner constructor and promotion delegation**

Replace `src/TallyDbLoader.Core/Sync/FullSyncRunner.cs` to remove the unused `IDatabaseLoader` dependency and call the promoter instead:
```csharp
using System;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class FullSyncRunner
    {
        private readonly ITallyClient _tally;
        private readonly IFullSyncTablePromoter _promoter;

        public FullSyncRunner(ITallyClient tally, IFullSyncTablePromoter promoter)
        {
            _tally = tally ?? throw new ArgumentNullException(nameof(tally));
            _promoter = promoter ?? throw new ArgumentNullException(nameof(promoter));
        }

        public async Task<long> Run(TallyExportConfig config, string companyName,
            DateTime fromDate, DateTime toDate, DbConnection targetConn)
        {
            long total = 0;
            var all = new System.Collections.Generic.List<TableConfig>();
            all.AddRange(config.Master);
            all.AddRange(config.Transaction);

            foreach (var table in all)
            {
                var xml = DynamicTdlXmlGenerator.GenerateXml(table, companyName,
                    fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                var response = await _tally.PostXMLAsync(xml);
                var dt = DynamicXmlParser.ParseXml(response, table);

                var promotedCount = await _promoter.StageValidateAndPromoteAsync(dt, table, targetConn);
                total += promotedCount;
            }
            return total;
        }
    }
}
```

- [ ] **Step 5: Update BackgroundSyncWorker constructor call**

In `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` line 410-412:
```csharp
                        IFullSyncTablePromoter promoter = tech.Contains("sqlite")
                            ? new SqliteFullSyncTablePromoter()
                            : new UnsupportedFullSyncTablePromoter();
                        var runner = new FullSyncRunner(client, promoter);
                        totalRows = await runner.Run(config, company.Name, fromDate, toDate, targetConn);
```

- [ ] **Step 6: Update existing tests and add new TDD failing test cases**

Replace `tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs` to use the new constructor and write our TDD failing tests:
```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Tests.Fakes;
using Xunit;
using Dapper;

namespace TallyDbLoader.Tests
{
    public class FullSyncRunnerTests
    {
        [Fact]
        public async Task Run_TwiceWithSameData_DoesNotDuplicateRows()
        {
            const string connStr = "Data Source=FullSyncRunnerTest;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");

            var promoter = new SqliteFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" },
                    new() { Name = "alterid", Field = "AlterId", Type = "number" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g1</F01>
        <F02>Sundry Debtors</F02>
        <F03>5</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            
            // Note: In TDD Red phase, this throws NotImplementedException because SqliteFullSyncTablePromoter is a stub
            await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
        }

        [Fact]
        public async Task Run_PrimaryTableDuplicateGuid_FailsAndPreservesLiveData()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_Duplicate;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name, alterid) VALUES ('g_initial', 'Initial Group', 10)");

            var promoter = new SqliteFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" },
                    new() { Name = "alterid", Field = "AlterId", Type = "number" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g_dup</F01>
        <F02>Sundry Debtors 1</F02>
        <F03>5</F03>
      </ROW>
      <ROW>
        <F01>g_dup</F01>
        <F02>Sundry Debtors 2</F02>
        <F03>6</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Verify live table was not cleared and old data is preserved
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
            var name = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_initial'");
            Assert.Equal("Initial Group", name);
        }

        [Fact]
        public async Task Run_PrimaryTableEmptyGuid_FailsAndPreservesLiveData()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_EmptyGuid;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name, alterid) VALUES ('g_initial', 'Initial Group', 10)");

            var promoter = new SqliteFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" },
                    new() { Name = "alterid", Field = "AlterId", Type = "number" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01></F01>
        <F02>Empty GUID Row</F02>
        <F03>5</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Verify live table was not cleared and old data is preserved
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
        }

        [Fact]
        public async Task Run_DerivedTableWithNoGuid_PromotesSuccessfully()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_DerivedNoGuid;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE trn_closingstock_ledger (ledger VARCHAR(1024), stock_value DECIMAL(17,2))");

            var promoter = new SqliteFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "trn_closingstock_ledger",
                Collection = "Ledger",
                Nature = "Derived",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "ledger", Field = "Name", Type = "text" },
                    new() { Name = "stock_value", Field = "Amount", Type = "number" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig>(), Transaction = new List<TableConfig> { table } };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>Stock Ledger A</F01>
        <F02>1000.50</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Ledger", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            // Will fail in Red phase
            var total = await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            Assert.Equal(1L, total);
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM trn_closingstock_ledger");
            Assert.Equal(1L, count);
        }

        [Fact]
        public async Task Run_DerivedTableWithDuplicateGuid_PromotesSuccessfully()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_DerivedDuplicate;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE trn_accounting (guid VARCHAR(64), ledger VARCHAR(1024))");

            var promoter = new SqliteFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "trn_accounting",
                Collection = "Voucher",
                Nature = "Derived",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "ledger", Field = "LedgerName", Type = "text" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig>(), Transaction = new List<TableConfig> { table } };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>voucher_guid_1</F01>
        <F02>Sales Ledger</F02>
      </ROW>
      <ROW>
        <F01>voucher_guid_1</F01>
        <F02>Cash Ledger</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Voucher", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            // Will fail in Red phase
            var total = await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            Assert.Equal(2L, total);
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM trn_accounting");
            Assert.Equal(2L, count);
        }

        [Fact]
        public async Task Run_UnsupportedPromoter_FailsClosedAndPreservesLiveData()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_Unsupported;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024))");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name) VALUES ('g1', 'Old Group Name')");

            var promoter = new UnsupportedFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g1</F01>
        <F02>New Group Name</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Verify live table was not cleared
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
            var name = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g1'");
            Assert.Equal("Old Group Name", name);
        }

        [Fact]
        public async Task Run_SchemaMismatch_FailsAndPreservesLiveData()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_SchemaMismatch;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            // Live table has guid and name
            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024))");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name) VALUES ('g_initial', 'Initial Group')");

            var promoter = new SqliteFullSyncTablePromoter();

            // DataTable tries to load an extra column (extra_col) not present in DB
            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" },
                    new() { Name = "extra_col", Field = "ExtraCol", Type = "text" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g1</F01>
        <F02>Group A</F02>
        <F03>Some Value</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            
            // Expected to fail because extra_col doesn't exist in target table
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Verify live table remains unchanged
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
            var name = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_initial'");
            Assert.Equal("Initial Group", name);
        }

        [Fact]
        public async Task Run_PromotionTransactionFailure_RollsBackAndPreservesLiveData()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_PromotionRollback;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            // Live table has a NOT NULL column with a CHECK constraint.
            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024) NOT NULL CHECK (name <> 'BAD'))");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name) VALUES ('g_initial', 'Initial Group')");

            var promoter = new SqliteFullSyncTablePromoter();

            // We configure table to load GUID and name, sending 'BAD' to violate check constraint in live table
            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g1</F01>
        <F02>BAD</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            
            // Expected to fail during insertion/promotion due to CHECK constraint violation on name
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Verify live table was not cleared and old data is preserved
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
            var name = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_initial'");
            Assert.Equal("Initial Group", name);
        }
    }
}
```

- [ ] **Step 7: Run compilation and test execution to verify RED state**

Run: `dotnet test src/TallyDbLoader.sln`
Expected: Compile succeeds. The unsupported promoter test (`Run_UnsupportedPromoter_FailsClosedAndPreservesLiveData`) PASSES. All tests using the SQLite promoter fail specifically with `NotImplementedException` or throw stub reasons.

---

### Task 2: Implement SqliteFullSyncTablePromoter (TDD Green Phase)

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/SqliteFullSyncTablePromoter.cs`

- [ ] **Step 1: Write SqliteFullSyncTablePromoter complete logic**

Replace the contents of `src/TallyDbLoader.Core/Sync/SqliteFullSyncTablePromoter.cs` with the full implementation:
```csharp
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class SqliteFullSyncTablePromoter : IFullSyncTablePromoter
    {
        public async Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (targetConn == null) throw new ArgumentNullException(nameof(targetConn));

            var tableName = table.Name;
            var stagingTableName = $"__tally_fullsync_staging_{tableName}";

            // 1. Create Staging Table by copying live table columns (without constraints)
            using (var cmd = targetConn.CreateCommand())
            {
                cmd.CommandText = $"DROP TABLE IF EXISTS {stagingTableName};";
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = targetConn.CreateCommand())
            {
                cmd.CommandText = $"CREATE TABLE {stagingTableName} AS SELECT * FROM {tableName} WHERE 1=0;";
                await cmd.ExecuteNonQueryAsync();
            }

            try
            {
                // 2. Load data into staging table directly using targetConn
                if (data.Rows.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append($"INSERT INTO {stagingTableName} (");
                    for (int i = 0; i < data.Columns.Count; i++)
                    {
                        sb.Append(data.Columns[i].ColumnName);
                        if (i < data.Columns.Count - 1) sb.Append(", ");
                    }
                    sb.Append(") VALUES (");
                    for (int i = 0; i < data.Columns.Count; i++)
                    {
                        sb.Append($"@p{i}");
                        if (i < data.Columns.Count - 1) sb.Append(", ");
                    }
                    sb.Append(")");

                    using (var insertCmd = targetConn.CreateCommand())
                    {
                        insertCmd.CommandText = sb.ToString();
                        for (int i = 0; i < data.Columns.Count; i++)
                        {
                            var param = insertCmd.CreateParameter();
                            param.ParameterName = $"@p{i}";
                            insertCmd.Parameters.Add(param);
                        }

                        foreach (DataRow row in data.Rows)
                        {
                            for (int i = 0; i < data.Columns.Count; i++)
                            {
                                var param = insertCmd.Parameters[i];
                                param.Value = row[i] ?? DBNull.Value;
                            }
                            await insertCmd.ExecuteNonQueryAsync();
                        }
                    }
                }

                // 3. Validation
                var hasGuid = false;
                foreach (DataColumn col in data.Columns)
                {
                    if (col.ColumnName.Equals("guid", StringComparison.OrdinalIgnoreCase))
                    {
                        hasGuid = true;
                        break;
                    }
                }

                var isPrimary = table.Nature?.Equals("Primary", StringComparison.OrdinalIgnoreCase) == true;

                if (isPrimary)
                {
                    if (!hasGuid)
                    {
                        throw new InvalidOperationException($"GUID column is missing from Table {tableName} config/data, but Nature is Primary.");
                    }

                    // Check for null or empty GUIDs
                    using (var cmd = targetConn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT COUNT(*) FROM {stagingTableName} WHERE guid IS NULL OR guid = '';";
                        var nullOrEmptyCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                        if (nullOrEmptyCount > 0)
                        {
                            throw new InvalidOperationException($"Table {tableName} contains {nullOrEmptyCount} rows with null or empty GUID.");
                        }
                    }

                    // Check for duplicate GUIDs
                    using (var cmd = targetConn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT COUNT(*) FROM (SELECT guid FROM {stagingTableName} GROUP BY guid HAVING COUNT(*) > 1);";
                        var duplicateCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                        if (duplicateCount > 0)
                        {
                            throw new InvalidOperationException($"Table {tableName} contains duplicate GUIDs.");
                        }
                    }
                }

                // 4. Promote within one transaction using explicit column lists
                using (var transaction = targetConn.BeginTransaction())
                {
                    try
                    {
                        // Delete live table
                        using (var deleteCmd = targetConn.CreateCommand())
                        {
                            deleteCmd.Transaction = transaction;
                            deleteCmd.CommandText = $"DELETE FROM {tableName};";
                            await deleteCmd.ExecuteNonQueryAsync();
                        }

                        // Copy staged rows using explicit column lists
                        if (data.Rows.Count > 0)
                        {
                            var cols = new List<string>();
                            for (int i = 0; i < data.Columns.Count; i++)
                            {
                                cols.Add(data.Columns[i].ColumnName);
                            }
                            var colsStr = string.Join(", ", cols);

                            using (var promoteCmd = targetConn.CreateCommand())
                            {
                                promoteCmd.Transaction = transaction;
                                promoteCmd.CommandText = $"INSERT INTO {tableName} ({colsStr}) SELECT {colsStr} FROM {stagingTableName};";
                                await promoteCmd.ExecuteNonQueryAsync();
                            }
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }

                return data.Rows.Count;
            }
            finally
            {
                // Clean up staging table
                using (var cleanCmd = targetConn.CreateCommand())
                {
                    cleanCmd.CommandText = $"DROP TABLE IF EXISTS {stagingTableName};";
                    await cleanCmd.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
```

- [ ] **Step 2: Run all tests to verify GREEN state**

Run: `dotnet test src/TallyDbLoader.sln`
Expected: Compile succeeds, and all tests pass (including existing ones and the newly added TDD verification tests).

- [ ] **Step 3: Commit all changes**

```bash
git add src/TallyDbLoader.Core/Sync/IFullSyncTablePromoter.cs src/TallyDbLoader.Core/Sync/UnsupportedFullSyncTablePromoter.cs src/TallyDbLoader.Core/Sync/SqliteFullSyncTablePromoter.cs src/TallyDbLoader.Core/Sync/FullSyncRunner.cs src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs
git commit -m "feat: implement sqlite full sync table promoter with staging name prefixes, schema/guid validation, and atomic promotion"
```
