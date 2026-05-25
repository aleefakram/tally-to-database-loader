using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.DatabaseLoaders;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Tests.Fakes;
using Xunit;
using Dapper;

namespace TallyDbLoader.Tests
{
    public class IncrementalSyncRunnerTests
    {
        private static void SetupSchema(SqliteConnection conn)
        {
            conn.Execute("CREATE TABLE IF NOT EXISTS config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024))");
            conn.Execute("CREATE TABLE IF NOT EXISTS mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");
            conn.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), _parent VARCHAR(64), parent VARCHAR(1024), alterid BIGINT)");
            conn.Execute("CREATE TABLE IF NOT EXISTS mst_vouchertype (name VARCHAR(1024) PRIMARY KEY, numbering_method VARCHAR(1024))");
            conn.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid VARCHAR(64) PRIMARY KEY, voucher_number VARCHAR(1024), voucher_type_name VARCHAR(1024), alterid BIGINT)");
            conn.Execute("CREATE TABLE IF NOT EXISTS _diff (guid VARCHAR(64) PRIMARY KEY, alterid BIGINT)");
            conn.Execute("CREATE TABLE IF NOT EXISTS _delete (guid VARCHAR(64) PRIMARY KEY)");
            conn.Execute("CREATE TABLE IF NOT EXISTS _vchnumber (guid VARCHAR(64) PRIMARY KEY, voucher_number VARCHAR(64))");
        }

        [Fact]
        public async Task Phase1_DiffAndDelete_RemovesMissingRowsAndCascades()
        {
            const string connStr = "Data Source=IncrementalSyncPhase1;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            SetupSchema(conn);

            // Clean tables
            conn.Execute("DELETE FROM mst_group");
            conn.Execute("DELETE FROM mst_ledger");

            // Seed database
            conn.Execute("INSERT INTO mst_group(guid, name, alterid) VALUES ('g1','A',1),('g2','B',1)");
            conn.Execute("INSERT INTO mst_ledger(guid, name, _parent, parent, alterid) VALUES ('l1','LA','g1','A',1),('l2','LB','g2','B',1)");

            var loader = new SqliteLoader(connStr);
            var fake = new FakeTallyClient();
            // Tally returns only g1. g2 was deleted in Tally.
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g1</F01>
        <F02>1</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var groupTable = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "alterid", Field = "AlterId", Type = "number" }
                },
                CascadeDelete = new List<CascadeRelation>
                {
                    new() { Table = "mst_ledger", Field = "_parent" }
                }
            };

            var runner = new IncrementalSyncRunner(fake, loader);
            await runner.RunPhase1DiffAsync(new[] { groupTable }, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            Assert.Equal(1L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM mst_group"));
            Assert.Equal("g1", conn.ExecuteScalar<string>("SELECT guid FROM mst_group"));
            Assert.Equal(1L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM mst_ledger"));
            Assert.Equal("l1", conn.ExecuteScalar<string>("SELECT guid FROM mst_ledger"));
        }

        [Fact]
        public async Task Phase2_Refetch_AppendsAlterIdFilter()
        {
            const string connStr = "Data Source=IncrementalSyncPhase2;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            SetupSchema(conn);

            conn.Execute("DELETE FROM mst_group");

            var loader = new SqliteLoader(connStr);
            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>g3</F01>
        <F02>C</F02>
        <F03>10</F03>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Group", responseXml);

            var groupTable = new TableConfig
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

            var runner = new IncrementalSyncRunner(fake, loader);
            await runner.RunPhase2RefetchAsync(
                masterTables: new[] { groupTable },
                transactionTables: Array.Empty<TableConfig>(),
                lastMasterId: 5, lastTransactionId: 0,
                companyName: "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            Assert.Contains(fake.AllRequests, r => r.Contains("$AlterID > 5"));
            Assert.Equal(1L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM mst_group WHERE guid = 'g3'"));
        }

        [Fact]
        public async Task Phase3_CascadeUpdate_FlowsRenamesIntoChildren()
        {
            const string connStr = "Data Source=IncrementalSyncPhase3;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            SetupSchema(conn);

            conn.Execute("DELETE FROM mst_group");
            conn.Execute("DELETE FROM mst_ledger");

            conn.Execute("INSERT INTO mst_group (guid, name, alterid) VALUES ('g1', 'A-Renamed', 2)");
            conn.Execute("INSERT INTO mst_ledger (guid, name, _parent, parent, alterid) VALUES ('l1', 'LA', 'g1', 'OldName', 1)");

            var loader = new SqliteLoader(connStr);
            var fake = new FakeTallyClient();

            var groupTable = new TableConfig
            {
                Name = "mst_group",
                Collection = "Group",
                Nature = "Primary",
                CascadeUpdate = new List<CascadeRelation>
                {
                    new() { Table = "mst_ledger", Field = "parent" }
                }
            };

            var runner = new IncrementalSyncRunner(fake, loader);
            await runner.RunPhase3CascadeUpdateAsync(new[] { groupTable }, conn);

            Assert.Equal("A-Renamed", conn.ExecuteScalar<string>("SELECT parent FROM mst_ledger WHERE guid='l1'"));
        }

        [Fact]
        public async Task Phase3_AutoVoucherNumberRefresh_UpdatesNumbers()
        {
            const string connStr = "Data Source=IncrementalSyncPhase3Vch;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            SetupSchema(conn);

            conn.Execute("DELETE FROM mst_vouchertype");
            conn.Execute("DELETE FROM trn_voucher");

            conn.Execute("INSERT INTO mst_vouchertype (name, numbering_method) VALUES ('Sales', 'Automatic')");
            conn.Execute("INSERT INTO trn_voucher (guid, voucher_number, voucher_type_name, alterid) VALUES ('v1', 'OLD', 'Sales', 1)");

            var loader = new SqliteLoader(connStr);
            var fake = new FakeTallyClient();
            var responseXml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>v1</F01>
        <F02>NEW-1</F02>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";
            fake.Register("Voucher", responseXml);

            var voucherTable = new TableConfig
            {
                Name = "trn_voucher",
                Collection = "Voucher",
                Nature = "Primary"
            };

            var runner = new IncrementalSyncRunner(fake, loader);
            await runner.RunPhase3VoucherRefreshAsync(new[] { voucherTable }, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            Assert.Equal("NEW-1", conn.ExecuteScalar<string>("SELECT voucher_number FROM trn_voucher WHERE guid='v1'"));
        }

        [Fact]
        public async Task Run_NoChange_SkipsAllPhases()
        {
            const string connStr = "Data Source=IncrementalSyncNoChange;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            SetupSchema(conn);

            conn.Execute("DELETE FROM config");
            conn.Execute("INSERT INTO config (name, value) VALUES ('Last AlterID Master', '100'), ('Last AlterID Transaction', '200')");

            var loader = new SqliteLoader(connStr);
            var fake = new FakeTallyClient();
            fake.CompanyInfo = new TallyCompanyInfo
            {
                Name = "TestCo",
                AltMstId = 100,
                AltVchId = 200,
                BooksFrom = new DateTime(2026, 4, 1),
                BooksTo = new DateTime(2026, 5, 25)
            };

            var runner = new IncrementalSyncRunner(fake, loader);
            var config = new TallyExportConfig { Master = new List<TableConfig>(), Transaction = new List<TableConfig>() };

            await runner.RunAsync(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            // FetchCompanyInfoAsync is called, but PostXMLAsync is NOT called because AlterIDs matched.
            Assert.Empty(fake.AllRequests);
        }

        [Fact]
        public async Task Run_HappyPath_AdvancesWatermark()
        {
            const string connStr = "Data Source=IncrementalSyncHappy;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            SetupSchema(conn);

            conn.Execute("DELETE FROM config");
            conn.Execute("INSERT INTO config (name, value) VALUES ('Last AlterID Master', '0'), ('Last AlterID Transaction', '0')");

            var loader = new SqliteLoader(connStr);
            var fake = new FakeTallyClient();
            fake.CompanyInfo = new TallyCompanyInfo
            {
                Name = "TestCo",
                AltMstId = 50,
                AltVchId = 75,
                BooksFrom = new DateTime(2026, 4, 1),
                BooksTo = new DateTime(2026, 5, 25)
            };

            var runner = new IncrementalSyncRunner(fake, loader);
            var config = new TallyExportConfig { Master = new List<TableConfig>(), Transaction = new List<TableConfig>() };

            await runner.RunAsync(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            var repo = new WatermarkRepository(conn);
            var watermarks = await repo.ReadAsync();
            Assert.Equal(50, watermarks.MasterAlterId);
            Assert.Equal(75, watermarks.TransactionAlterId);
        }
    }
}
