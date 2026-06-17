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
        [Fact]
        public void PostgresPromoter_ConvertsBooleanValues_ForSmallintColumns()
        {
            var trueValue = PostgresFullSyncTablePromoter.ConvertValueForPostgresParameter(true, "smallint");
            var falseValue = PostgresFullSyncTablePromoter.ConvertValueForPostgresParameter(false, "int2");

            Assert.Equal((short)1, trueValue);
            Assert.Equal((short)0, falseValue);
        }

        [Fact]
        public void PostgresPromoter_LeavesBooleanValues_ForBooleanColumns()
        {
            var value = PostgresFullSyncTablePromoter.ConvertValueForPostgresParameter(true, "boolean");

            Assert.Equal(true, value);
        }

        [SkippableFact]
        public async Task Test_Postgres_SplitPhasePromotion_Atomicity()
        {
            var connStr = Environment.GetEnvironmentVariable("TALLY_TEST_POSTGRES_CONN");
            Skip.If(string.IsNullOrEmpty(connStr), "PostgreSQL connection string (TALLY_TEST_POSTGRES_CONN) not configured.");

            using var conn = new NpgsqlConnection(connStr);
            conn.Open();

            var promoter = new PostgresFullSyncTablePromoter();

            var runId = Guid.NewGuid().ToString("n").Substring(0, 8);
            var groupTable = $"__tally_test_mst_group_{runId}";
            var ledgerTable = $"__tally_test_mst_ledger_{runId}";

            await RunSmokeTestForProvider(
                conn,
                promoter,
                groupTable,
                ledgerTable,
                $"DROP TABLE IF EXISTS \"{groupTable}\" CASCADE;",
                $"CREATE TABLE \"{groupTable}\" (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT);",
                $"DROP TABLE IF EXISTS \"{ledgerTable}\" CASCADE;",
                $"CREATE TABLE \"{ledgerTable}\" (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(255) UNIQUE, alterid BIGINT);"
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

            var runId = Guid.NewGuid().ToString("n").Substring(0, 8);
            var groupTable = $"__tally_test_mst_group_{runId}";
            var ledgerTable = $"__tally_test_mst_ledger_{runId}";

            await RunSmokeTestForProvider(
                conn,
                promoter,
                groupTable,
                ledgerTable,
                $"IF OBJECT_ID('dbo.{groupTable}', 'U') IS NOT NULL DROP TABLE dbo.{groupTable};",
                $"CREATE TABLE dbo.{groupTable} (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT);",
                $"IF OBJECT_ID('dbo.{ledgerTable}', 'U') IS NOT NULL DROP TABLE dbo.{ledgerTable};",
                $"CREATE TABLE dbo.{ledgerTable} (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(255) UNIQUE, alterid BIGINT);"
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

            var runId = Guid.NewGuid().ToString("n").Substring(0, 8);
            var groupTable = $"__tally_test_mst_group_{runId}";
            var ledgerTable = $"__tally_test_mst_ledger_{runId}";

            await RunSmokeTestForProvider(
                conn,
                promoter,
                groupTable,
                ledgerTable,
                $"DROP TABLE IF EXISTS `{groupTable}`;",
                $"CREATE TABLE `{groupTable}` (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT) ENGINE=InnoDB;",
                $"DROP TABLE IF EXISTS `{ledgerTable}`;",
                $"CREATE TABLE `{ledgerTable}` (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(255) UNIQUE, alterid BIGINT) ENGINE=InnoDB;"
            );
        }

        private string Quote(string identifier, DbConnection conn)
        {
            var typeName = conn.GetType().Name;
            if (typeName.Contains("SqlConnection"))
            {
                return $"[{identifier.Replace("]", "]]")}]";
            }
            if (typeName.Contains("MySqlConnection"))
            {
                return $"`{identifier.Replace("`", "``")}`";
            }
            return $"\"{identifier.Replace("\"", "\"\"")}\""; // Default to double quotes for SQLite/Postgres
        }

        private async Task RunSmokeTestForProvider(
            DbConnection conn,
            IFullSyncTablePromoter promoter,
            string groupTable,
            string ledgerTable,
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
                var qGroup = Quote(groupTable, conn);
                var qLedger = Quote(ledgerTable, conn);

                // 2. Insert initial rows
                await conn.ExecuteAsync($"INSERT INTO {qGroup} (guid, name, alterid) VALUES ('g_initial', 'Initial Group', 10)");
                await conn.ExecuteAsync($"INSERT INTO {qLedger} (guid, name, alterid) VALUES ('l_initial', 'Initial Ledger', 20)");

                var table1 = new TableConfig
                {
                    Name = groupTable,
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
                    Name = ledgerTable,
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
                    var groupCount = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {qGroup}");
                    Assert.Equal(1L, groupCount);
                    var groupName = await conn.QueryFirstOrDefaultAsync<string>($"SELECT name FROM {qGroup} WHERE guid = 'g_new'");
                    Assert.Equal("New Group", groupName);

                    var ledgerCount = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {qLedger}");
                    Assert.Equal(1L, ledgerCount);
                    var ledgerName = await conn.QueryFirstOrDefaultAsync<string>($"SELECT name FROM {qLedger} WHERE guid = 'l_new'");
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
                    var groupName = await conn.QueryFirstOrDefaultAsync<string>($"SELECT name FROM {qGroup} WHERE guid = 'g_new'");
                    Assert.Equal("New Group", groupName);
                    var neverName = await conn.QueryFirstOrDefaultAsync<string>($"SELECT name FROM {qGroup} WHERE guid = 'g_never'");
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
                    var groupName = await conn.QueryFirstOrDefaultAsync<string>($"SELECT name FROM {qGroup} WHERE guid = 'g_new'");
                    Assert.Equal("New Group", groupName);
                    var neverName = await conn.QueryFirstOrDefaultAsync<string>($"SELECT name FROM {qGroup} WHERE guid = 'g_never2'");
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
