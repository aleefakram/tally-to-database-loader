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
                if (string.IsNullOrWhiteSpace(ledger.PrimaryGroup))
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

                if (!ledger.IsRevenue)
                {
                    ledger.IsRevenue = ResolveRevenueFlag(
                        ledger.ParentGroupName,
                        groupMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
            }

            var reservedPnl = raw.Ledgers.FirstOrDefault(l =>
                l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase));

            if (reservedPnl == null)
            {
                report.Warnings.Add($"Reserved Profit & Loss ledger '{request.Options.ProfitAndLossLedgerName}' was not found.");
            }

            decimal pnlOpening = reservedPnl?.OpeningBalance ?? 0m;
            decimal pnlLessTransferred = (reservedPnl?.CurrentPeriodMovement ?? 0m) < 0m
                ? Math.Abs(reservedPnl.CurrentPeriodMovement)
                : 0m;

            decimal revenueCurrent = raw.Ledgers
                .Where(l => l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.CurrentPeriodMovement);

            decimal revenuePrePeriod = raw.Ledgers
                .Where(l => l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.PrePeriodMovement);

            decimal stockOpening = raw.Ledgers
                .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.OpeningBalance + l.PrePeriodMovement);

            var stockLedgers = raw.Ledgers
                .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                .ToList();

            decimal stockClosing = stockLedgers.Any(l => l.HasClosingStockValue)
                ? stockLedgers.Sum(l => l.ClosingStockValue)
                : stockLedgers.Sum(l => l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement);

            if (stockLedgers.Any() && !stockLedgers.Any(l => l.HasClosingStockValue))
            {
                report.Warnings.Add("Stock-in-Hand closing values were not found; ledger balances were used.");
            }

            report.ProfitAndLoss.OpeningBalance = pnlOpening + revenuePrePeriod;
            report.ProfitAndLoss.CurrentPeriod = revenueCurrent + (stockClosing - stockOpening);
            report.ProfitAndLoss.LessTransferred = pnlLessTransferred;

            var grouped = raw.Ledgers
                .Where(l => !l.IsRevenue)
                .Where(l => !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(l => NormalizeGroup(l.PrimaryGroup));

            foreach (var group in grouped)
            {
                decimal signedBalance = group.Sum(l =>
                {
                    if (group.Key.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase) && l.HasClosingStockValue)
                    {
                        return l.ClosingStockValue;
                    }
                    return l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement;
                });

                if (signedBalance == 0m) continue;

                if (LiabilityGroups.Contains(group.Key))
                {
                    report.LiabilitySide.Lines.Add(new BalanceSheetLine
                    {
                        Name = group.Key,
                        Amount = Math.Abs(signedBalance),
                        IsEmphasis = true
                    });
                }
                else if (AssetGroups.Contains(group.Key))
                {
                    report.AssetSide.Lines.Add(new BalanceSheetLine
                    {
                        Name = group.Key,
                        Amount = Math.Abs(signedBalance),
                        IsEmphasis = true
                    });
                }
                else
                {
                    report.Warnings.Add($"Unrecognized primary group '{group.Key}' was excluded.");
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
            if (!groupMap.TryGetValue(groupName, out var group)) return string.Empty;
            if (!string.IsNullOrWhiteSpace(group.PrimaryGroup)) return group.PrimaryGroup.Trim();
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
