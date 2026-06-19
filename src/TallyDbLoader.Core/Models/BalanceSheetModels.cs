using System;
using System.Collections.Generic;

namespace TallyDbLoader.Core.Models
{
    public class BalanceSheetVerificationOptions
    {
        public decimal BalanceTolerance { get; set; } = 0.05m;
        public string ProfitAndLossLedgerName { get; set; } = "Profit & Loss A/c";
    }

    public class BalanceSheetVerificationRequest
    {
        public int CompanyProfileId { get; set; }
        public DateTime FinancialYearStart { get; set; }
        public DateTime AsAtDate { get; set; }
        public BalanceSheetVerificationOptions Options { get; set; } = new BalanceSheetVerificationOptions();
    }

    public class BalanceSheetReport
    {
        public int CompanyProfileId { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public DateTime FinancialYearStart { get; set; }
        public DateTime AsAtDate { get; set; }
        public BalanceSheetSide LiabilitySide { get; set; } = new BalanceSheetSide { Title = "Liabilities" };
        public BalanceSheetSide AssetSide { get; set; } = new BalanceSheetSide { Title = "Assets" };
        public ProfitAndLossBreakdown ProfitAndLoss { get; set; } = new ProfitAndLossBreakdown();
        public string Status { get; set; } = "failed";
        public decimal BalanceTolerance { get; set; } = 0.05m;
        public List<string> Warnings { get; set; } = new List<string>();
        public string? ErrorSummary { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        public decimal LiabilityTotal => LiabilitySide.Total;
        public decimal AssetTotal => AssetSide.Total;
        public decimal Difference => Math.Abs(LiabilityTotal - AssetTotal);
    }

    public class BalanceSheetSide
    {
        public string Title { get; set; } = string.Empty;
        public List<BalanceSheetLine> Lines { get; set; } = new List<BalanceSheetLine>();
        public decimal Total
        {
            get
            {
                decimal total = 0m;
                foreach (var line in Lines)
                {
                    total += line.Amount;
                }
                return total;
            }
        }
    }

    public class BalanceSheetLine
    {
        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public bool IsEmphasis { get; set; }
        public string Kind { get; set; } = string.Empty;
        public List<BalanceSheetBreakdownLine> BreakdownLines { get; set; } = new List<BalanceSheetBreakdownLine>();
    }

    public class BalanceSheetBreakdownLine
    {
        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class ProfitAndLossBreakdown
    {
        public decimal OpeningBalance { get; set; }
        public decimal CurrentPeriod { get; set; }
        public decimal LessTransferred { get; set; }
        public decimal NetAmount => OpeningBalance + CurrentPeriod - LessTransferred;
    }

    public class BalanceSheetLedgerRow
    {
        public string LedgerName { get; set; } = string.Empty;
        public string ParentGroupName { get; set; } = string.Empty;
        public string PrimaryGroup { get; set; } = string.Empty;
        public bool IsRevenue { get; set; }
        public decimal OpeningBalance { get; set; }
        public decimal PrePeriodMovement { get; set; }
        public decimal CurrentPeriodMovement { get; set; }
        public decimal ClosingStockValue { get; set; }
        public bool HasClosingStockValue { get; set; }
    }

    public class BalanceSheetGroupRow
    {
        public string Name { get; set; } = string.Empty;
        public string ParentName { get; set; } = string.Empty;
        public string PrimaryGroup { get; set; } = string.Empty;
        public bool IsRevenue { get; set; }
    }

    public class BalanceSheetRawData
    {
        public List<BalanceSheetLedgerRow> Ledgers { get; set; } = new List<BalanceSheetLedgerRow>();
        public List<BalanceSheetGroupRow> Groups { get; set; } = new List<BalanceSheetGroupRow>();
        public bool HasClosingStockTable { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class BalanceSheetVerificationRun
    {
        public long Id { get; set; }
        public int CompanyProfileId { get; set; }
        public string TargetIdentity { get; set; } = string.Empty;
        public DateTime FinancialYearStart { get; set; }
        public DateTime AsAtDate { get; set; }
        public DateTime GeneratedAt { get; set; }
        public decimal LiabilityTotal { get; set; }
        public decimal AssetTotal { get; set; }
        public decimal Difference { get; set; }
        public decimal BalanceTolerance { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? WarningSummary { get; set; }
        public string? ErrorSummary { get; set; }
    }
}
