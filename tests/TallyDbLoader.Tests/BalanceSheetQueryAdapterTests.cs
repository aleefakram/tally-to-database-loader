using System;
using TallyDbLoader.Core.Reports;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class BalanceSheetQueryAdapterTests
    {
        [Fact]
        public void BalanceSheetTableNames_WithPrefix_QualifiesRequiredTables()
        {
            var names = BalanceSheetTableNames.Create("public", "tally_", "NpgsqlConnection");

            Assert.Equal("public", names.Schema);
            Assert.Equal("tally_mst_group", names.MstGroup);
            Assert.Equal("tally_mst_ledger", names.MstLedger);
            Assert.Equal("tally_trn_voucher", names.TrnVoucher);
            Assert.Equal("tally_trn_accounting", names.TrnAccounting);
            Assert.Equal("tally_trn_closingstock_ledger", names.TrnClosingStockLedger);
        }

        [Theory]
        [InlineData("public;drop table x", "tally_")]
        [InlineData("public", "tally;drop_")]
        [InlineData("public", "123bad")]
        public void BalanceSheetTableNames_WithUnsafeIdentifiers_Throws(string schema, string prefix)
        {
            Assert.Throws<InvalidOperationException>(() =>
                BalanceSheetTableNames.Create(schema, prefix, "SqliteConnection"));
        }
        [Fact]
        public void SqliteAdapter_BuildLedgerSql_UsesPrefixedTablesAndDateParameters()
        {
            var adapter = new SqliteBalanceSheetQueryAdapter();
            var names = BalanceSheetTableNames.Create("main", "tally_", "SqliteConnection");

            string sql = adapter.BuildLedgerSql(names, includeClosingStock: true);

            Assert.Contains("\"tally_mst_ledger\"", sql);
            Assert.Contains("\"tally_trn_accounting\"", sql);
            Assert.Contains("@FinancialYearStart", sql);
            Assert.Contains("@AsAtDate", sql);
            Assert.DoesNotContain("2025-04-01", sql);
        }

        [Theory]
        [InlineData("mssql", "[dbo].[tally_mst_ledger]")]
        [InlineData("postgres", "\"public\".\"tally_mst_ledger\"")]
        [InlineData("mysql", "`public`.`tally_mst_ledger`")]
        public void ProviderAdapters_BuildLedgerSql_QualifySchemaAndTablePrefix(string providerName, string expectedLedgerTable)
        {
            IBalanceSheetQueryAdapter adapter = providerName switch
            {
                "mssql" => new MssqlBalanceSheetQueryAdapter(),
                "postgres" => new PostgresBalanceSheetQueryAdapter(),
                "mysql" => new MySqlBalanceSheetQueryAdapter(),
                _ => throw new InvalidOperationException()
            };
            var provider = providerName == "mssql" ? "SqlConnection" : providerName == "postgres" ? "NpgsqlConnection" : "MySqlConnection";
            var schema = providerName == "mssql" ? "dbo" : "public";
            var names = BalanceSheetTableNames.Create(schema, "tally_", provider);

            string sql = adapter.BuildLedgerSql(names, includeClosingStock: true);

            Assert.Contains(expectedLedgerTable, sql);
            Assert.Contains("@FinancialYearStart", sql);
            Assert.Contains("@AsAtDate", sql);
        }

        [Fact]
        public void SqliteAdapter_BuildLedgerSql_WithoutClosingStock_DoesNotReferenceClosingStockTable()
        {
            var adapter = new SqliteBalanceSheetQueryAdapter();
            var names = BalanceSheetTableNames.Create("main", "tally_", "SqliteConnection");

            string sql = adapter.BuildLedgerSql(names, includeClosingStock: false);

            Assert.DoesNotContain("trn_closingstock_ledger", sql);
            Assert.Contains("0 AS ClosingStockValue", sql);
            Assert.Contains("0 AS HasClosingStockValue", sql);
        }

        [Fact]
        public void SqliteAdapter_BuildGroupSql_SelectsGroupHierarchy()
        {
            var adapter = new SqliteBalanceSheetQueryAdapter();
            var names = BalanceSheetTableNames.Create("main", "tally_", "SqliteConnection");

            string sql = adapter.BuildGroupSql(names);

            Assert.Contains("\"tally_mst_group\"", sql);
            Assert.Contains("COALESCE(parent", sql);
            Assert.Contains("AS ParentName", sql);
            Assert.Contains("COALESCE(primary_group", sql);
            Assert.Contains("AS PrimaryGroup", sql);
        }
    }
}
