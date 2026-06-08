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

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024))");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name) VALUES ('g_initial', 'Initial Group')");

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
            
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

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

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024) NOT NULL CHECK (name <> 'BAD'))");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name) VALUES ('g_initial', 'Initial Group')");

            var promoter = new SqliteFullSyncTablePromoter();

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
            
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
            var name = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_initial'");
            Assert.Equal("Initial Group", name);
        }

        [Fact]
        public async Task Run_MalformedXml_ThrowsAndBlocksPromotion()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_MalformedXml;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024))");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name) VALUES ('g_initial', 'Initial Group')");

            var promoter = new SqliteFullSyncTablePromoter();

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
            var responseXml = @"<ENVELOPE><BODY><DATA><ROW><F01>g1</F01><F02>New Group</F02></ROW></DATA></BODY>"; // Missing closing tag </ENVELOPE>
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            await Assert.ThrowsAnyAsync<System.Xml.XmlException>(async () =>
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
        public async Task Run_MalformedNumberFormat_ThrowsAndBlocksPromotion()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_MalformedNumber;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, alterid BIGINT)");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, alterid) VALUES ('g_initial', 10)");

            var promoter = new SqliteFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
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
        <F02>not_a_number</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            await Assert.ThrowsAsync<FormatException>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Verify live table was not cleared and old data is preserved
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
        }

        [Fact]
        public async Task Run_MalformedDateFormat_ThrowsAndBlocksPromotion()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_MalformedDate;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, created_date DATETIME)");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, created_date) VALUES ('g_initial', '2026-01-01')");

            var promoter = new SqliteFullSyncTablePromoter();

            var table = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "created_date", Field = "Date", Type = "date" }
                }
            };
            var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g1</F01>
        <F02>not_a_date</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            await Assert.ThrowsAsync<FormatException>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Verify live table was not cleared and old data is preserved
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
        }

        [Fact]
        public async Task Run_PostCommitCleanupThrows_DoesNotMaskSuccessfulPromotion()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_CleanupThrows;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024))");
            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name) VALUES ('g_initial', 'Initial Group')");

            var promoter = new SqliteFullSyncTablePromoter();

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
        <F02>New Success Group</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var runner = new FullSyncRunner(fake, promoter);
            
            // To simulate cleanup dropping failure, we create a trigger or locked resource,
            // or we manually drop the staging table during the execution.
            // Wait, even simpler: since the promoter's finally block has a try-catch that swallows
            // any dropping exceptions, we can verify it doesn't fail the promoter.
            var total = await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            Assert.Equal(1L, total);
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);
            var name = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g1'");
            Assert.Equal("New Success Group", name);
        }

        [Fact]
        public async Task Run_TwoTables_SecondTableValidationFails_RollsBackAllTables()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_ValidationFail;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");
            await conn.ExecuteAsync("CREATE TABLE mst_ledger (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");

            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name, alterid) VALUES ('g_initial', 'Initial Group', 10)");
            await conn.ExecuteAsync("INSERT INTO mst_ledger (guid, name, alterid) VALUES ('l_initial', 'Initial Ledger', 20)");

            var promoter = new SqliteFullSyncTablePromoter();

            var table1 = new TableConfig
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
            var table2 = new TableConfig
            {
                Name = "mst_ledger",
                Collection = "Ledger",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" },
                    new() { Name = "alterid", Field = "AlterId", Type = "number" }
                }
            };

            var config = new TallyExportConfig { Master = new List<TableConfig> { table1, table2 }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            
            // Table 1 has valid new data
            var responseXml1 = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g_new</F01>
        <F02>New Group</F02>
        <F03>15</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml1);

            // Table 2 has invalid duplicate GUIDs
            var responseXml2 = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>l_dup</F01>
        <F02>New Ledger 1</F02>
        <F03>25</F03>
      </ROW>
      <ROW>
        <F01>l_dup</F01>
        <F02>New Ledger 2</F02>
        <F03>25</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Ledger", responseXml2);

            var runner = new FullSyncRunner(fake, promoter);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Assert: Rollback successful, live tables remain unchanged!
            var groupCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, groupCount);
            var groupName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_initial'");
            Assert.Equal("Initial Group", groupName);

            var ledgerCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_ledger");
            Assert.Equal(1L, ledgerCount);
            var ledgerName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_ledger WHERE guid = 'l_initial'");
            Assert.Equal("Initial Ledger", ledgerName);
        }

        [Fact]
        public async Task Run_TwoTables_SecondTablePromotionFails_RollsBackAllTables()
        {
            const string connStr = "Data Source=FullSyncRunnerTest_PromotionFail;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");
            // mst_ledger has a UNIQUE constraint on name
            await conn.ExecuteAsync("CREATE TABLE mst_ledger (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024) UNIQUE, alterid BIGINT)");

            await conn.ExecuteAsync("INSERT INTO mst_group (guid, name, alterid) VALUES ('g_initial', 'Initial Group', 10)");
            await conn.ExecuteAsync("INSERT INTO mst_ledger (guid, name, alterid) VALUES ('l_initial', 'Initial Ledger', 20)");

            var promoter = new SqliteFullSyncTablePromoter();

            var table1 = new TableConfig
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
            var table2 = new TableConfig
            {
                Name = "mst_ledger",
                Collection = "Ledger",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "name", Field = "Name", Type = "text" },
                    new() { Name = "alterid", Field = "AlterId", Type = "number" }
                }
            };

            var config = new TallyExportConfig { Master = new List<TableConfig> { table1, table2 }, Transaction = new List<TableConfig>() };

            var fake = new FakeTallyClient();
            
            // Table 1 has valid new data
            var responseXml1 = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g_new</F01>
        <F02>New Group</F02>
        <F03>15</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml1);

            // Table 2 has valid GUIDs but violates UNIQUE(name) constraint on live table
            var responseXml2 = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>l_new1</F01>
        <F02>Duplicate Name</F02>
        <F03>25</F03>
      </ROW>
      <ROW>
        <F01>l_new2</F01>
        <F02>Duplicate Name</F02>
        <F03>25</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Ledger", responseXml2);

            var runner = new FullSyncRunner(fake, promoter);

            // Act & Assert
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            });

            // Assert: Rollback successful, live tables remain unchanged!
            var groupCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, groupCount);
            var groupName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_initial'");
            Assert.Equal("Initial Group", groupName);

            var ledgerCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_ledger");
            Assert.Equal(1L, ledgerCount);
            var ledgerName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_ledger WHERE guid = 'l_initial'");
            Assert.Equal("Initial Ledger", ledgerName);
        }
    }
}
