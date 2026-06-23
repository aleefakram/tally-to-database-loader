using System;
using System.Collections.Generic;
using System.Linq;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public static class BalanceSheetCalculator
    {
        private static readonly HashSet<string> LiabilityGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Capital Account",
            "Loans (Liability)",
            "Current Liabilities"
        };

        private static readonly HashSet<string> AssetGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Fixed Assets",
            "Investments",
            "Current Assets",
            "Branch / Divisions",
            "Misc. Expenses (ASSET)",
            "Suspense A/c",
            "Stock-in-hand"
        };

        public static BalanceSheetReport Calculate(
            string companyName,
            BalanceSheetRawData raw,
            BalanceSheetVerificationRequest request)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (request == null) throw new ArgumentNullException(nameof(request));

            var report = new BalanceSheetReport
            {
                CompanyProfileId = request.CompanyProfileId,
                CompanyName = companyName,
                FinancialYearStart = request.FinancialYearStart,
                AsAtDate = request.AsAtDate,
                BalanceTolerance = request.Options.BalanceTolerance,
                Warnings = new List<string>(raw.Warnings)
            };

            var groupMap = raw.Groups
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var ledger in raw.Ledgers)
            {
                if (ledger.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                {
                    ledger.PrimaryGroup = request.Options.ProfitAndLossLedgerName;
                    continue;
                }

                if (!ledger.IsRevenue)
                {
                    ledger.IsRevenue = ResolveRevenueFlag(
                        ledger.ParentGroupName,
                        groupMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                if (!ledger.IsRevenue)
                {
                    if (string.IsNullOrWhiteSpace(ledger.PrimaryGroup) ||
                        (!LiabilityGroups.Contains(ledger.PrimaryGroup) && !AssetGroups.Contains(ledger.PrimaryGroup)))
                    {
                        bool hasCycle = false;
                        string resolvedPrimaryGroup = ResolvePrimaryGroup(
                            ledger.ParentGroupName,
                            groupMap,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            ref hasCycle);

                        if (hasCycle)
                        {
                            return Fail(report, $"Circular group hierarchy detected while resolving '{ledger.ParentGroupName}'.");
                        }

                        if (string.IsNullOrWhiteSpace(resolvedPrimaryGroup))
                        {
                            return Fail(report, $"Unable to resolve primary group for ledger '{ledger.LedgerName}' with parent group '{ledger.ParentGroupName}'.");
                        }

                        ledger.PrimaryGroup = resolvedPrimaryGroup;
                    }
                }
            }

            var reservedPnl = raw.Ledgers.FirstOrDefault(l =>
                l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase));

            if (reservedPnl == null)
            {
                report.Warnings.Add($"Reserved Profit & Loss ledger '{request.Options.ProfitAndLossLedgerName}' was not found.");
            }

            decimal pnlOpening = (reservedPnl?.OpeningBalance ?? 0m) + (reservedPnl?.PrePeriodMovement ?? 0m);
            decimal pnlLessTransferred = reservedPnl?.CurrentPeriodDebit ?? 0m;

            decimal revenueCurrent = raw.Ledgers
                .Where(l => l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.CurrentPeriodMovement);

            decimal revenuePrePeriod = raw.Ledgers
                .Where(l => l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.OpeningBalance + l.PrePeriodMovement);

            // 1. Calculate resolved ledgers opening sum (excluding revenue and P&L)
            decimal resolvedOpening = raw.Ledgers.Sum(l =>
            {
                if (l.IsRevenue || l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                {
                    return 0m;
                }

                bool hasCycle = false;
                string primaryGroup = l.PrimaryGroup;
                if (string.IsNullOrWhiteSpace(primaryGroup))
                {
                    primaryGroup = ResolvePrimaryGroup(l.ParentGroupName, groupMap, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref hasCycle);
                }
                if (hasCycle || string.IsNullOrWhiteSpace(primaryGroup) || 
                    (!LiabilityGroups.Contains(primaryGroup) && !AssetGroups.Contains(primaryGroup)))
                {
                    return 0m;
                }

                if (NormalizeGroup(primaryGroup).Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                {
                    return l.HasOpeningStockValue ? l.OpeningStockValue : l.OpeningBalance + l.PrePeriodMovement;
                }
                return l.OpeningBalance + l.PrePeriodMovement;
            });

            // 2. Compute stockOpening and stockClosing (signed DB basis)
            decimal stockOpening = raw.Ledgers
                .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.HasOpeningStockValue
                    ? l.OpeningStockValue
                    : l.OpeningBalance + l.PrePeriodMovement);

            var stockLedgers = raw.Ledgers
                .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                .ToList();

            decimal stockClosing = stockLedgers.Sum(l => l.HasClosingStockValue
                ? l.ClosingStockValue
                : l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement);

            if (stockLedgers.Any(l => !l.HasClosingStockValue))
            {
                report.Warnings.Add("Some Stock-in-Hand closing values were not found; ledger balances were used as fallback.");
            }

            // 3. Compute Profit & Loss Breakdown using consistent signs
            report.ProfitAndLoss.OpeningBalance = pnlOpening + revenuePrePeriod - stockOpening;
            report.ProfitAndLoss.CurrentPeriod = revenueCurrent - (stockClosing - stockOpening);
            report.ProfitAndLoss.LessTransferred = pnlLessTransferred;

            // 4. Calculate total opening (including P&L opening)
            decimal totalOpening = resolvedOpening + report.ProfitAndLoss.OpeningBalance;

            var grouped = raw.Ledgers
                .Where(l => !l.IsRevenue)
                .Where(l => !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(l => NormalizeGroup(l.PrimaryGroup));

            foreach (var group in grouped)
            {
                bool isLiability = LiabilityGroups.Contains(group.Key);
                bool isAsset = AssetGroups.Contains(group.Key);

                if (!isLiability && !isAsset)
                {
                    return Fail(report, $"Unrecognized primary group '{group.Key}' was detected.");
                }

                decimal signedBalance = group.Sum(l =>
                {
                    if (NormalizeGroup(group.Key).Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase) && l.HasClosingStockValue)
                    {
                        return l.ClosingStockValue; // already negative/debit
                    }
                    return l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement;
                });

                if (signedBalance == 0m) continue;

                // Credit balance (> 0) goes to Liabilities, Debit balance (< 0) goes to Assets
                if (signedBalance > 0m)
                {
                    report.LiabilitySide.Lines.Add(new BalanceSheetLine
                    {
                        Name = group.Key,
                        Amount = signedBalance,
                        IsEmphasis = true
                    });
                }
                else
                {
                    report.AssetSide.Lines.Add(new BalanceSheetLine
                    {
                        Name = group.Key,
                        Amount = -signedBalance,
                        IsEmphasis = true
                    });
                }
            }

            if (report.ProfitAndLoss.NetAmount > 0m)
            {
                report.LiabilitySide.Lines.Add(CreateProfitAndLossLine(
                    request.Options.ProfitAndLossLedgerName,
                    report.ProfitAndLoss.NetAmount,
                    report.ProfitAndLoss));
            }
            else if (report.ProfitAndLoss.NetAmount < 0m)
            {
                report.AssetSide.Lines.Add(CreateProfitAndLossLine(
                    request.Options.ProfitAndLossLedgerName,
                    Math.Abs(report.ProfitAndLoss.NetAmount),
                    report.ProfitAndLoss));
            }

            // Inject Difference in opening balances
            if (totalOpening > 0m)
            {
                report.AssetSide.Lines.Add(new BalanceSheetLine
                {
                    Name = "Difference in opening balances",
                    Amount = totalOpening,
                    IsEmphasis = true
                });
            }
            else if (totalOpening < 0m)
            {
                report.LiabilitySide.Lines.Add(new BalanceSheetLine
                {
                    Name = "Difference in opening balances",
                    Amount = -totalOpening,
                    IsEmphasis = true
                });
            }

            // Unified primary group display order for sorting
            var unifiedOrder = new List<string>
            {
                "Capital Account",
                "Loans (Liability)",
                "Current Liabilities",
                "Fixed Assets",
                "Investments",
                "Current Assets",
                "Stock-in-hand",
                "Branch / Divisions",
                "Misc. Expenses (ASSET)",
                "Suspense A/c",
                request.Options.ProfitAndLossLedgerName,
                "Difference in opening balances"
            };

            report.LiabilitySide.Lines = report.LiabilitySide.Lines
                .OrderBy(l =>
                {
                    int index = unifiedOrder.FindIndex(x => x.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
                    return index >= 0 ? index : int.MaxValue;
                  })
                .ToList();

            report.AssetSide.Lines = report.AssetSide.Lines
                .OrderBy(l =>
                {
                    int index = unifiedOrder.FindIndex(x => x.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
                    return index >= 0 ? index : int.MaxValue;
                })
                .ToList();

            report.Status = report.Difference <= request.Options.BalanceTolerance
                ? "balanced"
                : "out_of_balance";

            return report;
        }

        private static string NormalizeGroup(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(unresolved)" : value.Trim();
        }

        private static string ResolvePrimaryGroup(
            string groupName,
            Dictionary<string, BalanceSheetGroupRow> groupMap,
            HashSet<string> visited,
            ref bool hasCycle)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return string.Empty;
            if (!visited.Add(groupName))
            {
                hasCycle = true;
                return string.Empty;
            }

            if (LiabilityGroups.Contains(groupName) || AssetGroups.Contains(groupName))
            {
                return groupName;
            }

            if (!groupMap.TryGetValue(groupName, out var group))
            {
                return groupName;
            }

            if (!string.IsNullOrWhiteSpace(group.PrimaryGroup) &&
                (LiabilityGroups.Contains(group.PrimaryGroup) || AssetGroups.Contains(group.PrimaryGroup)))
            {
                return group.PrimaryGroup.Trim();
            }

            if (string.IsNullOrWhiteSpace(group.ParentName))
            {
                return group.Name;
            }

            return ResolvePrimaryGroup(group.ParentName, groupMap, visited, ref hasCycle);
        }

        private static bool ResolveRevenueFlag(
            string groupName,
            Dictionary<string, BalanceSheetGroupRow> groupMap,
            HashSet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return false;
            if (!visited.Add(groupName)) return false;
            if (!groupMap.TryGetValue(groupName, out var group)) return false;
            if (group.IsRevenue) return true;
            return ResolveRevenueFlag(group.ParentName, groupMap, visited);
        }

        private static BalanceSheetReport Fail(BalanceSheetReport report, string errorSummary)
        {
            report.Status = "failed";
            report.ErrorSummary = errorSummary;
            report.LiabilitySide.Lines.Clear();
            report.AssetSide.Lines.Clear();
            return report;
        }

        private static BalanceSheetLine CreateProfitAndLossLine(
            string ledgerName,
            decimal amount,
            ProfitAndLossBreakdown breakdown)
        {
            var line = new BalanceSheetLine
            {
                Name = ledgerName,
                Amount = Math.Abs(amount),
                IsEmphasis = true,
                Kind = "profit_and_loss"
            };
            line.BreakdownLines.Add(new BalanceSheetBreakdownLine { Name = "Opening Balance", Amount = breakdown.OpeningBalance });
            line.BreakdownLines.Add(new BalanceSheetBreakdownLine { Name = "Current Period", Amount = breakdown.CurrentPeriod });
            line.BreakdownLines.Add(new BalanceSheetBreakdownLine { Name = "Less: Transferred", Amount = breakdown.LessTransferred });
            return line;
        }
    }
}
