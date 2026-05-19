using Xunit;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Tally;
using System.Collections.Generic;

namespace TallyDbLoader.Tests
{
    public class DynamicTableSchemaGeneratorTests
    {
        [Fact]
        public void Test_GenerateCreateTableSql_ProducesValidDDLForPostgres()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_custom_ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "name", Field = "Name", Type = "text" },
                    new FieldConfig { Name = "alterid", Field = "AlterId", Type = "number" },
                    new FieldConfig { Name = "is_revenue", Field = "IsRevenue", Type = "logical" },
                    new FieldConfig { Name = "opening_balance", Field = "OpeningBalance", Type = "amount" },
                    new FieldConfig { Name = "created_date", Field = "CreatedDate", Type = "date" }
                }
            };

            var postgresSql = DynamicTableSchemaGenerator.GenerateCreateTableSql(tableConfig, "postgres");
            Assert.Contains("CREATE TABLE IF NOT EXISTS mst_custom_ledger", postgresSql);
            Assert.Contains("guid varchar(64) not null primary key", postgresSql);
            Assert.Contains("name varchar(1024) not null default ''", postgresSql);
            Assert.Contains("alterid int not null default 0", postgresSql);
            Assert.Contains("is_revenue smallint default 0", postgresSql);
            Assert.Contains("opening_balance decimal(17,2) default 0", postgresSql);
            Assert.Contains("created_date date", postgresSql);
        }

        [Fact]
        public void Test_GenerateCreateTableSql_ProducesValidDDLForMysql()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_custom_ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "is_revenue", Field = "IsRevenue", Type = "logical" }
                }
            };

            var mysqlSql = DynamicTableSchemaGenerator.GenerateCreateTableSql(tableConfig, "mysql");
            Assert.Contains("CREATE TABLE IF NOT EXISTS mst_custom_ledger", mysqlSql);
            Assert.Contains("is_revenue tinyint default 0", mysqlSql);
        }

        [Fact]
        public void Test_GenerateCreateTableSql_ProducesValidDDLForMssql()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_custom_ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "is_revenue", Field = "IsRevenue", Type = "logical" }
                }
            };

            var mssqlSql = DynamicTableSchemaGenerator.GenerateCreateTableSql(tableConfig, "mssql");
            Assert.Contains("IF OBJECT_ID('mst_custom_ledger', 'U') IS NULL CREATE TABLE mst_custom_ledger", mssqlSql);
            Assert.Contains("is_revenue smallint default 0", mssqlSql);
        }
    }
}
