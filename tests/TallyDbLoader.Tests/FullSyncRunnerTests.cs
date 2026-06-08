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
