using System;
using System.Linq;
using TallyDbLoader.Core.Models;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class BalanceSheetModelsTests
    {
        [Fact]
        public void BalanceSheetVerificationOptions_Defaults_MatchSpec()
        {
            var options = new BalanceSheetVerificationOptions();

            Assert.Equal(0.05m, options.BalanceTolerance);
            Assert.Equal("Profit & Loss A/c", options.ProfitAndLossLedgerName);
        }

        [Fact]
        public void BalanceSheetReport_Difference_UsesAbsoluteSideDifference()
        {
            var report = new BalanceSheetReport
            {
                LiabilitySide = new BalanceSheetSide
                {
                    Title = "Liabilities",
                    Lines =
                    {
                        new BalanceSheetLine { Name = "Capital Account", Amount = 100m }
                    }
                },
                AssetSide = new BalanceSheetSide
                {
                    Title = "Assets",
                    Lines =
                    {
                        new BalanceSheetLine { Name = "Fixed Assets", Amount = 98m }
                    }
                }
            };

            Assert.Equal(100m, report.LiabilityTotal);
            Assert.Equal(98m, report.AssetTotal);
            Assert.Equal(2m, report.Difference);
        }

        [Fact]
        public void BalanceSheetRawData_CarriesGroupHierarchyRows()
        {
            var raw = new BalanceSheetRawData
            {
                Groups =
                {
                    new BalanceSheetGroupRow { Name = "Regional Debtors", ParentName = "Sundry Debtors", PrimaryGroup = string.Empty },
                    new BalanceSheetGroupRow { Name = "Sundry Debtors", ParentName = "Current Assets", PrimaryGroup = "Current Assets" }
                }
            };

            Assert.Equal(2, raw.Groups.Count);
            Assert.Contains(raw.Groups, g => g.Name == "Regional Debtors" && g.ParentName == "Sundry Debtors");
        }
    }
}
