using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Data.SqlClient;
using Npgsql;
using MySqlConnector;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Tests.Fakes;
using Dapper;

namespace TallyDbLoader.Tests
{
    public class ProviderIntegrationTests
    {
        [SkippableFact]
        public async Task Test_Postgres_SplitPhasePromotion_Atomicity()
        {
            var connStr = Environment.GetEnvironmentVariable("TALLY_TEST_POSTGRES_CONN");
            Skip.If(string.IsNullOrEmpty(connStr), "PostgreSQL connection string (TALLY_TEST_POSTGRES_CONN) not configured.");

            using var conn = new NpgsqlConnection(connStr);
            conn.Open();

            var promoter = new PostgresFullSyncTablePromoter();

            await RunSmokeTestForProvider(
                conn,
                promoter,
                "DROP TABLE IF EXISTS \"mst_group\" CASCADE;",
                "CREATE TABLE \"mst_group\" (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT);",
                "DROP TABLE IF EXISTS \"mst_ledger\" CASCADE;",
                "CREATE TABLE \"mst_ledger\" (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(255) UNIQUE, alterid BIGINT);"
            );
        }

        [SkippableFact]
        public async Task Test_Mssql_SplitPhasePromotion_Atomicity()
        {
            var connStr = Environment.GetEnvironmentVariable("TALLY_TEST_MSSQL_CONN");
            Skip.If(string.IsNullOrEmpty(connStr), "SQL Server connection string (TALLY_TEST_MSSQL_CONN) not configured.");

            using var conn = new SqlConnection(connStr);
            conn.Open();

            var promoter = new MssqlFullSyncTablePromoter();

            await RunSmokeTestForProvider(
                conn,
                promoter,
                "IF OBJECT_ID('dbo.mst_group', 'U') IS NOT NULL DROP TABLE dbo.mst_group;",
                "CREATE TABLE dbo.mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT);",
                "IF OBJECT_ID('dbo.mst_ledger', 'U') IS NOT NULL DROP TABLE dbo.mst_ledger;",
                "CREATE TABLE dbo.mst_ledger (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(255) UNIQUE, alterid BIGINT);"
            );
        }

        [SkippableFact]
        public async Task Test_Mysql_SplitPhasePromotion_Atomicity()
        {
            var connStr = Environment.GetEnvironmentVariable("TALLY_TEST_MYSQL_CONN");
            Skip.If(string.IsNullOrEmpty(connStr), "MySQL connection string (TALLY_TEST_MYSQL_CONN) not configured.");

            using var conn = new MySqlConnection(connStr);
            conn.Open();

            var promoter = new MysqlFullSyncTablePromoter();

            await RunSmokeTestForProvider(
                conn,
                promoter,
                "DROP TABLE IF EXISTS `mst_group`;",
                "CREATE TABLE `mst_group` (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT) ENGINE=InnoDB;",
                "DROP TABLE IF EXISTS `mst_ledger`;",
                "CREATE TABLE `mst_ledger` (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(255) UNIQUE, alterid BIGINT) ENGINE=InnoDB;"
            );
        }

        private async Task RunSmokeTestForProvider(
            DbConnection conn,
            IFullSyncTablePromoter promoter,
            string dropGroupSql,
            string createGroupSql,
            string dropLedgerSql,
            string createLedgerSql)
        {
            // 1. Setup tables
            await conn.ExecuteAsync(dropGroupSql);
            await conn.ExecuteAsync(dropLedgerSql);
            await conn.ExecuteAsync(createGroupSql);
            await conn.ExecuteAsync(createLedgerSql);

            try
            {
                // 2. Insert initial rows
                await conn.ExecuteAsync("INSERT INTO mst_group (guid, name, alterid) VALUES ('g_initial', 'Initial Group', 10)");
                await conn.ExecuteAsync("INSERT INTO mst_ledger (guid, name, alterid) VALUES ('l_initial', 'Initial Ledger', 20)");

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

                // Test A: Happy path sync
                {
                    var fake = new FakeTallyClient();
                    fake.Register("Group", @"<ENVELOPE><BODY><DATA><ROW><F01>g_new</F01><F02>New Group</F02><F03>15</F03></ROW></DATA></BODY></ENVELOPE>");
                    fake.Register("Ledger", @"<ENVELOPE><BODY><DATA><ROW><F01>l_new</F01><F02>New Ledger</F02><F03>25</F03></ROW></DATA></BODY></ENVELOPE>");

                    var runner = new FullSyncRunner(fake, promoter);
                    var total = await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

                    Assert.Equal(2L, total);
                    var groupCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
                    Assert.Equal(1L, groupCount);
                    var groupName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_new'");
                    Assert.Equal("New Group", groupName);

                    var ledgerCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_ledger");
                    Assert.Equal(1L, ledgerCount);
                    var ledgerName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_ledger WHERE guid = 'l_new'");
                    Assert.Equal("New Ledger", ledgerName);
                }

                // Test B: Validation failure rollback (duplicate GUIDs in second table)
                {
                    var fake = new FakeTallyClient();
                    fake.Register("Group", @"<ENVELOPE><BODY><DATA><ROW><F01>g_never</F01><F02>Never Group</F02><F03>16</F03></ROW></DATA></BODY></ENVELOPE>");
                    // Table 2 has duplicate GUIDs
                    fake.Register("Ledger", @"<ENVELOPE><BODY><DATA><ROW><F01>l_dup</F01><F02>Ledger A</F02><F03>26</F03></ROW><ROW><F01>l_dup</F01><F02>Ledger B</F02><F03>26</F03></ROW></DATA></BODY></ENVELOPE>");

                    var runner = new FullSyncRunner(fake, promoter);
                    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    {
                        await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
                    });

                    // Assert group table still has 'g_new' and not 'g_never'
                    var groupName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_new'");
                    Assert.Equal("New Group", groupName);
                    var neverName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_never'");
                    Assert.Null(neverName);
                }

                // Test C: Promotion database error rollback (duplicate names violating UNIQUE constraint in second table)
                {
                    var fake = new FakeTallyClient();
                    fake.Register("Group", @"<ENVELOPE><BODY><DATA><ROW><F01>g_never2</F01><F02>Never Group 2</F02><F03>17</F03></ROW></DATA></BODY></ENVELOPE>");
                    // Table 2 has two rows with duplicate name 'Duplicate Name' violating UNIQUE constraint during insert
                    fake.Register("Ledger", @"<ENVELOPE><BODY><DATA><ROW><F01>l_u1</F01><F02>Duplicate Name</F02><F03>27</F03></ROW><ROW><F01>l_u2</F01><F02>Duplicate Name</F02><F03>27</F03></ROW></DATA></BODY></ENVELOPE>");

                    var runner = new FullSyncRunner(fake, promoter);
                    await Assert.ThrowsAnyAsync<Exception>(async () =>
                    {
                        await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
                    });

                    // Assert group table still has 'g_new' and not 'g_never2'
                    var groupName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_new'");
                    Assert.Equal("New Group", groupName);
                    var neverName = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g_never2'");
                    Assert.Null(neverName);
                }
            }
            finally
            {
                // Clean up tables at the end
                try { await conn.ExecuteAsync(dropGroupSql); } catch { }
                try { await conn.ExecuteAsync(dropLedgerSql); } catch { }
            }
        }
    }
}
