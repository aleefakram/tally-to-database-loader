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
    public class FullSyncRunnerTests
    {
        [Fact]
        public async Task Run_TwiceWithSameData_DoesNotDuplicateRows()
        {
            // Set up a shared in-memory SQLite database connection
            const string connStr = "Data Source=FullSyncRunnerTest;Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            // Create target table
            await conn.ExecuteAsync("CREATE TABLE mst_group (guid VARCHAR(64) PRIMARY KEY, name VARCHAR(1024), alterid BIGINT)");

            var loader = new SqliteLoader(connStr);

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
            // Stub XML matching DynamicXmlParser expectations (ROW / column elements)
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

            var runner = new FullSyncRunner(fake, loader);
            await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);
            await runner.Run(config, "TestCo", new DateTime(2026, 4, 1), new DateTime(2026, 5, 25), conn);

            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mst_group");
            Assert.Equal(1L, count);

            var name = await conn.QueryFirstOrDefaultAsync<string>("SELECT name FROM mst_group WHERE guid = 'g1'");
            Assert.Equal("Sundry Debtors", name);
        }
    }
}
