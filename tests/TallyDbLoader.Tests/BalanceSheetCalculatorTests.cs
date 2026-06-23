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
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -1200m },
                    new() { LedgerName = "Stock", PrimaryGroup = "Stock-in-hand", OpeningBalance = -200m, ClosingStockValue = -300m, HasClosingStockValue = true },
                    new() { LedgerName = "Sales", PrimaryGroup = "Sales Accounts", IsRevenue = true, CurrentPeriodMovement = 400m },
                    new() { LedgerName = "Purchase", PrimaryGroup = "Purchase Accounts", IsRevenue = true, CurrentPeriodMovement = -200m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(300m, report.ProfitAndLoss.CurrentPeriod);
            Assert.Contains(report.LiabilitySide.Lines, l => l.Name == "Profit & Loss A/c" && l.Amount == 500m);
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
                    new() { LedgerName = "Profit & Loss A/c", PrimaryGroup = "Capital Account", OpeningBalance = 250m, PrePeriodMovement = 50m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -300m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(300m, report.ProfitAndLoss.OpeningBalance);
        }

        [Fact]
        public void Calculate_LessTransferred_UsesDirectDebitPostingToProfitAndLossLedger()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    // Even if net current movement is positive (credit adjustment), direct debits are still retrieved
                    new() { LedgerName = "Profit & Loss A/c", PrimaryGroup = "Capital Account", CurrentPeriodMovement = 50m, CurrentPeriodDebit = 150m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -1050m }
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
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -100m, CurrentPeriodMovement = 0.02m }
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
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -1000m, CurrentPeriodMovement = -100m },
                    // Stock A has closing stock value -150m (negative) which translates to 150m asset/debit
                    new() { LedgerName = "Stock A", PrimaryGroup = "Stock-in-hand", OpeningBalance = -100m, ClosingStockValue = -150m, HasClosingStockValue = true },
                    // Stock B has no closing stock value, falls back to its ledger balance of -150m
                    new() { LedgerName = "Stock B", PrimaryGroup = "Stock-in-hand", OpeningBalance = -100m, CurrentPeriodMovement = -50m, HasClosingStockValue = false },
                    // Revenue to offset the stock increase of 100m (Stock A +50m, Stock B +50m)
                    new() { LedgerName = "Sales", PrimaryGroup = "Sales Accounts", IsRevenue = true, CurrentPeriodMovement = 100m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            // AssetTotal: Cash (1100) + Stock A (150) + Stock B (150) = 1400m
            Assert.Equal(1400m, report.AssetTotal);
            Assert.Equal("balanced", report.Status);
            Assert.Contains(report.Warnings, w => w.Contains("Some Stock-in-Hand closing values were not found"));
        }

        [Fact]
        public void Calculate_ReservedProfitAndLossLedgerWithEmptyPrimaryAndParent_Succeeds()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 900m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -1000m },
                    new() { LedgerName = "Profit & Loss A/c", PrimaryGroup = "", ParentGroupName = "", OpeningBalance = 100m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal("balanced", report.Status);
            var plLine = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name.Equals("Profit & Loss A/c", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(plLine);
            Assert.Equal(100m, plLine.Amount);
        }

        [Fact]
        public void Calculate_UnrecognizedGroup_FailsVerificationAndPopulatesErrorSummary()
        {
            var request = new BalanceSheetVerificationRequest
            {
                CompanyProfileId = 1,
                FinancialYearStart = new DateTime(2025, 4, 1),
                AsAtDate = new DateTime(2025, 6, 1)
            };

            var raw = new BalanceSheetRawData
            {
                Groups = new List<BalanceSheetGroupRow>(),
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Capital Ledger",
                        ParentGroupName = "Capital Account",
                        PrimaryGroup = "Capital Account",
                        OpeningBalance = 2000m
                    },
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Cash",
                        ParentGroupName = "Current Assets",
                        PrimaryGroup = "Current Assets",
                        OpeningBalance = -1500m
                    },
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Unknown Ledger",
                        ParentGroupName = "Some Group",
                        PrimaryGroup = "Not A Valid Group",
                        OpeningBalance = -1000m
                    }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

            Assert.Equal("failed", report.Status);
            Assert.NotNull(report.ErrorSummary);
            Assert.Contains("Unrecognized primary group", report.ErrorSummary);
            Assert.Empty(report.AssetSide.Lines);
            Assert.Empty(report.LiabilitySide.Lines);
        }

        [Fact]
        public void Calculate_DebitBalancedLiability_RoutesToAssets()
        {
            var request = new BalanceSheetVerificationRequest
            {
                CompanyProfileId = 1,
                FinancialYearStart = new DateTime(2025, 4, 1),
                AsAtDate = new DateTime(2025, 6, 1)
            };

            var raw = new BalanceSheetRawData
            {
                Groups = new List<BalanceSheetGroupRow>(),
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Overdraft Liab",
                        ParentGroupName = "Current Liabilities",
                        PrimaryGroup = "Current Liabilities",
                        OpeningBalance = -5000m // Debit (Asset-like)
                    }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

            var assetLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Current Liabilities");
            var liabilityLine = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name == "Current Liabilities");

            Assert.NotNull(assetLine);
            Assert.Null(liabilityLine);
            Assert.Equal(5000m, assetLine.Amount);
        }

        [Fact]
        public void Calculate_CreditBalancedAsset_RoutesToLiabilities()
        {
            var request = new BalanceSheetVerificationRequest
            {
                CompanyProfileId = 1,
                FinancialYearStart = new DateTime(2025, 4, 1),
                AsAtDate = new DateTime(2025, 6, 1)
            };

            var raw = new BalanceSheetRawData
            {
                Groups = new List<BalanceSheetGroupRow>(),
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Bank Overdraft",
                        ParentGroupName = "Current Assets",
                        PrimaryGroup = "Current Assets",
                        OpeningBalance = 3000m // Credit (Liability-like)
                    }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

            var assetLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Current Assets");
            var liabilityLine = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name == "Current Assets");

            Assert.Null(assetLine);
            Assert.NotNull(liabilityLine);
            Assert.Equal(3000m, liabilityLine.Amount);
        }

        [Fact]
        public void Calculate_OpeningBalanceDifference_WithPrePeriod_InjectsCorrectSide()
        {
            var request = new BalanceSheetVerificationRequest
            {
                CompanyProfileId = 1,
                FinancialYearStart = new DateTime(2025, 4, 1),
                AsAtDate = new DateTime(2025, 6, 1)
            };

            // Trial balance difference with pre-period movements:
            // Capital Ledger: Opening = 1000m, PrePeriod = 200m -> Total Opening credit = 1200m.
            var raw = new BalanceSheetRawData
            {
                Groups = new List<BalanceSheetGroupRow>(),
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Capital Ledger",
                        ParentGroupName = "Capital Account",
                        PrimaryGroup = "Capital Account",
                        OpeningBalance = 1000m,
                        PrePeriodMovement = 200m
                    }
                }
            };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

            var diffAssetLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
            Assert.NotNull(diffAssetLine);
            Assert.Equal(1200m, diffAssetLine.Amount);
        }

        [Fact]
        public void Calculate_OpeningBalanceDifference_DebitSurplus_InjectsLiabilities()
        {
            var request = new BalanceSheetVerificationRequest
            {
                CompanyProfileId = 1,
                FinancialYearStart = new DateTime(2025, 4, 1),
                AsAtDate = new DateTime(2025, 6, 1)
            };

            // Trial balance difference: Cash = -1000m (Debit) -> totalOpening = -1000m (Debit surplus)
            var raw = new BalanceSheetRawData
            {
                Groups = new List<BalanceSheetGroupRow>(),
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Cash",
                        ParentGroupName = "Current Assets",
                        PrimaryGroup = "Current Assets",
                        OpeningBalance = -1000m
                    }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

            var diffLiabilityLine = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
            Assert.NotNull(diffLiabilityLine);
            Assert.Equal(1000m, diffLiabilityLine.Amount);
        }

        [Fact]
        public void Calculate_PnLBreakdownWithStock_ComputesCorrectly()
        {
            var request = new BalanceSheetVerificationRequest
            {
                CompanyProfileId = 1,
                FinancialYearStart = new DateTime(2025, 5, 1),
                AsAtDate = new DateTime(2025, 6, 1)
            };

            var raw = new BalanceSheetRawData
            {
                Groups = new List<BalanceSheetGroupRow>(),
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Profit & Loss A/c",
                        ParentGroupName = "",
                        PrimaryGroup = "",
                        OpeningBalance = 5000m // Credit
                    },
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Inventory Ledger",
                        ParentGroupName = "Stock-in-hand",
                        PrimaryGroup = "Stock-in-hand",
                        HasOpeningStockValue = true,
                        OpeningStockValue = -2000m, // Debit (Opening Stock)
                        HasClosingStockValue = true,
                        ClosingStockValue = -3000m // Debit (Closing Stock)
                    },
                    new BalanceSheetLedgerRow
                    {
                        LedgerName = "Sales",
                        ParentGroupName = "Sales Accounts",
                        PrimaryGroup = "Sales Accounts",
                        IsRevenue = true,
                        CurrentPeriodMovement = 10000m // Credit (Revenue)
                    }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

            // Opening P&L: 5000 (pnlOpening) - (-2000 stockOpening) = 7000 (credit)
            Assert.Equal(7000m, report.ProfitAndLoss.OpeningBalance);

            // Current P&L: 10000 (revenueCurrent) - (-3000 stockClosing - (-2000 stockOpening)) = 10000 - (-1000) = 11000 (credit)
            Assert.Equal(11000m, report.ProfitAndLoss.CurrentPeriod);
        }

        [Fact]
        public void Calculate_UnifiedOrdering_SortsDeterministically()
        {
            var request = new BalanceSheetVerificationRequest
            {
                CompanyProfileId = 1,
                FinancialYearStart = new DateTime(2025, 4, 1),
                AsAtDate = new DateTime(2025, 6, 1)
            };

            var raw = new BalanceSheetRawData
            {
                Groups = new List<BalanceSheetGroupRow>(),
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    // Mix groups to test sorting on both sides
                    new BalanceSheetLedgerRow { LedgerName = "Suspense", ParentGroupName = "Suspense A/c", PrimaryGroup = "Suspense A/c", OpeningBalance = -100m },
                    new BalanceSheetLedgerRow { LedgerName = "Capital", ParentGroupName = "Capital Account", PrimaryGroup = "Capital Account", OpeningBalance = 500m },
                    new BalanceSheetLedgerRow { LedgerName = "Asset", ParentGroupName = "Current Assets", PrimaryGroup = "Current Assets", OpeningBalance = -300m },
                    new BalanceSheetLedgerRow { LedgerName = "Loans", ParentGroupName = "Loans (Liability)", PrimaryGroup = "Loans (Liability)", OpeningBalance = 400m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

            // Assets Side Order should be: Current Assets, then Suspense A/c, then Difference in opening balances
            var assetNames = report.AssetSide.Lines.Select(l => l.Name).ToList();
            Assert.Equal("Current Assets", assetNames[0]);
            Assert.Equal("Suspense A/c", assetNames[1]);
            Assert.Equal("Difference in opening balances", assetNames[2]);

            // Liabilities Side Order should be: Capital Account, then Loans (Liability)
            var liabilityNames = report.LiabilitySide.Lines.Select(l => l.Name).ToList();
            Assert.Equal("Capital Account", liabilityNames[0]);
            Assert.Equal("Loans (Liability)", liabilityNames[1]);
        }
    }
}
