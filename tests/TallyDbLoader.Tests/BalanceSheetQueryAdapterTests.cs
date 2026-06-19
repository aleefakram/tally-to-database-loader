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
    }
}
