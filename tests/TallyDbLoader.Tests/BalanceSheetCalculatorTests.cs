using System;
using System.Collections.Generic;
using System.Linq;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Reports;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class BalanceSheetCalculatorTests
    {
        private static BalanceSheetVerificationRequest Request() => new BalanceSheetVerificationRequest
        {
            CompanyProfileId = 1,
            FinancialYearStart = new DateTime(2025, 4, 1),
            AsAtDate = new DateTime(2025, 6, 5)
        };

        [Fact]
        public void Calculate_IncludesInvestmentsOnAssetSide()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Owner Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Mutual Fund", PrimaryGroup = "Investments", OpeningBalance = -1000m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Contains(report.AssetSide.Lines, l => l.Name == "Investments" && l.Amount == 1000m);
            Assert.Equal("balanced", report.Status);
        }

        [Fact]
        public void Calculate_UsesCreditPositiveDebitNegativeConvention()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 500m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -500m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(500m, report.LiabilityTotal);
            Assert.Equal(500m, report.AssetTotal);
        }

        [Fact]
        public void Calculate_BlankPrimaryGroup_WalksParentRecursively()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Regional Bank", ParentGroupName = "Regional Current Assets", PrimaryGroup = string.Empty, OpeningBalance = -1000m }
                },
                Groups = new List<BalanceSheetGroupRow>
                {
                    new() { Name = "Regional Current Assets", ParentName = "Current Assets", PrimaryGroup = string.Empty },
                    new() { Name = "Current Assets", ParentName = string.Empty, PrimaryGroup = "Current Assets" }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal("balanced", report.Status);
            Assert.Contains(report.AssetSide.Lines, l => l.Name == "Current Assets" && l.Amount == 1000m);
        }

        [Fact]
        public void Calculate_GroupCycle_ReturnsFailedStatus()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Bad Ledger", ParentGroupName = "Cycle A", PrimaryGroup = string.Empty, OpeningBalance = -1000m }
                },
                Groups = new List<BalanceSheetGroupRow>
                {
                    new() { Name = "Cycle A", ParentName = "Cycle B", PrimaryGroup = string.Empty },
                    new() { Name = "Cycle B", ParentName = "Cycle A", PrimaryGroup = string.Empty }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal("failed", report.Status);
            Assert.Contains("Circular group hierarchy", report.ErrorSummary ?? string.Empty);
            Assert.Contains("Cycle A", report.ErrorSummary ?? string.Empty);
        }

        [Fact]
        public void Calculate_ProfitAndLoss_CurrentPeriod_IncludesStockDelta()
        {
            var raw = new BalanceSheetRawData
            {
                HasClosingStockTable = true,
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1200m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -1000m },
                    new() { LedgerName = "Stock", PrimaryGroup = "Stock-in-hand", OpeningBalance = -200m, ClosingStockValue = 300m, HasClosingStockValue = true },
                    new() { LedgerName = "Sales", PrimaryGroup = "Sales Accounts", IsRevenue = true, CurrentPeriodMovement = 400m },
                    new() { LedgerName = "Purchase", PrimaryGroup = "Purchase Accounts", IsRevenue = true, CurrentPeriodMovement = -200m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(100m, report.ProfitAndLoss.CurrentPeriod);
            Assert.Contains(report.LiabilitySide.Lines, l => l.Name == "Profit & Loss A/c" && l.Amount == 100m);
            var pnlLine = report.LiabilitySide.Lines.Single(l => l.Kind == "profit_and_loss");
            Assert.Equal(3, pnlLine.BreakdownLines.Count);
            Assert.Contains(pnlLine.BreakdownLines, l => l.Name == "Less: Transferred");
        }

        [Fact]
        public void Calculate_ProfitAndLoss_OpeningBalance_UsesReservedLedgerOpening()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Profit & Loss A/c", PrimaryGroup = "Capital Account", OpeningBalance = 250m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -250m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(250m, report.ProfitAndLoss.OpeningBalance);
        }

        [Fact]
        public void Calculate_LessTransferred_UsesDirectDebitPostingToProfitAndLossLedger()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Profit & Loss A/c", PrimaryGroup = "Capital Account", CurrentPeriodMovement = -150m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -850m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(150m, report.ProfitAndLoss.LessTransferred);
        }

        [Fact]
        public void Calculate_SmallDifferenceWithinTolerance_IsBalanced()
        {
            var request = Request();
            request.Options.BalanceTolerance = 0.05m;
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 100m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -99.98m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, request);

            Assert.Equal("balanced", report.Status);
            Assert.Equal(0.02m, report.Difference);
        }

        [Fact]
        public void Calculate_PartialStockValuation_AppliesPerLedgerFallback()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1300m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -1000m },
                    // Stock A has closing stock value 150m (positive) which translates to -150m signed balance
                    new() { LedgerName = "Stock A", PrimaryGroup = "Stock-in-hand", OpeningBalance = -100m, ClosingStockValue = 150m, HasClosingStockValue = true },
                    // Stock B has no closing stock value, falls back to its ledger balance of -150m
                    new() { LedgerName = "Stock B", PrimaryGroup = "Stock-in-hand", OpeningBalance = -100m, CurrentPeriodMovement = -50m, HasClosingStockValue = false },
                    // Revenue to offset the stock increase of 100m (Stock A +50m, Stock B +50m)
                    new() { LedgerName = "Sales", PrimaryGroup = "Sales Accounts", IsRevenue = true, CurrentPeriodMovement = 100m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            // AssetTotal: Cash (1000) + Stock A (150) + Stock B (150) = 1300m
            Assert.Equal(1300m, report.AssetTotal);
            Assert.Equal("balanced", report.Status);
            Assert.Contains(report.Warnings, w => w.Contains("Some Stock-in-Hand closing values were not found"));
        }
    }
}
