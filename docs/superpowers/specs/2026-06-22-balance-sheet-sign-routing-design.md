# Balance Sheet Dynamic Sign Routing and Opening Balance Difference Design

## Context
When loading data from Tally, the Balance Sheet presents group balances based on their net debit/credit sign:
1. **Dynamic Group Routing**: 
   - Debit-balanced groups (including liability groups with a net debit balance) are displayed on the **Assets** side as positive values.
   - Credit-balanced groups (including asset groups with a net credit balance, e.g. Bank Overdraft) are displayed on the **Liabilities** side as positive values.
2. **Difference in Opening Balances**:
   - If the trial balance of all ledger opening balances is not equal to zero, Tally displays the difference under the name `"Difference in opening balances"`.
   - A credit surplus (more credits than debits) displays on the **Assets** side.
   - A debit surplus (more debits than credits) displays on the **Liabilities** side.

The current implementation of `BalanceSheetCalculator` hardcodes groups into either Assets or Liabilities based on predefined category lists and does not compute the opening balance difference, leading to large discrepancies (such as `20,17,356.26` in new companies with debit-balanced liabilities and opening balance differences).

## Proposed Changes

### 1. `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs`
- **Resolution and Validation Step**:
  Keep the existing ledger loop that resolves `PrimaryGroup` and `IsRevenue` for all ledgers.
- **Fail Method Update**:
  Modify `Fail` to clear both sides to prevent partial/invalid lines from being returned:
  ```csharp
  private static BalanceSheetReport Fail(BalanceSheetReport report, string error)
  {
      report.Status = "failed";
      report.ErrorSummary = error;
      report.LiabilitySide.Lines.Clear();
      report.AssetSide.Lines.Clear();
      return report;
  }
  ```
- **Opening Balance Difference Computation**:
  Compute `totalOpening` immediately after the ledger resolution loop has completed (so that `PrimaryGroup` is fully resolved for all ledgers).
  - Calculate `totalOpening` using the **Tally report-period opening basis** (to match the P&L stock opening value at `FinancialYearStart`):
    - For `"Stock-in-hand"` group ledgers: use `OpeningStockValue` directly without negation if `HasOpeningStockValue` is true; otherwise use `OpeningBalance + PrePeriodMovement`.
    - For all other ledgers: use `OpeningBalance + PrePeriodMovement`.
  - **Inclusion/Exclusion Rules**: Include the reserved P&L ledger, revenue ledgers, and all recognized group ledgers. Exclude unrecognized groups (which will fail the report anyway).
- **P&L Stock-in-Hand Sign Alignment**:
  Align `stockOpening` and `stockClosing` to use the signed DB basis:
  - `stockOpening = raw.Ledgers.Where(...).Sum(l => l.HasOpeningStockValue ? l.OpeningStockValue : l.OpeningBalance + l.PrePeriodMovement);`
  - `stockClosing = raw.Ledgers.Where(...).Sum(l => l.HasClosingStockValue ? l.ClosingStockValue : l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement);`
  Adjust P&L formulas to consume these signed (negative) values:
  - `report.ProfitAndLoss.OpeningBalance = pnlOpening + revenuePrePeriod + stockOpening;`
  - `report.ProfitAndLoss.CurrentPeriod = revenueCurrent - (stockClosing - stockOpening);`
- **Group Recognition & Routing**:
  For each non-revenue group:
  - First verify that the group is recognized by checking `LiabilityGroups.Contains(group.Key)` or `AssetGroups.Contains(group.Key)`. If unrecognized, call `Fail(report, ...)` to set `Status = "failed"`, populate `ErrorSummary`, clear lines, and **return immediately**.
  - If recognized, calculate `signedBalance`. Do not negate `ClosingStockValue` in the sum:
    ```csharp
    decimal signedBalance = group.Sum(l =>
    {
        if (NormalizeGroup(group.Key).Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase) && l.HasClosingStockValue)
        {
            return l.ClosingStockValue; // already negative/debit
        }
        return l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement;
    });
    ```
  - Route to `LiabilitySide.Lines` if `signedBalance > 0` (Credit) or `AssetSide.Lines` if `signedBalance < 0` (Debit).
- **Inject the `"Difference in opening balances"` Line**:
  - If `totalOpening > 0` (Credit surplus), add to `AssetSide.Lines` with `Amount = totalOpening` and `Name = "Difference in opening balances"`.
  - If `totalOpening < 0` (Debit surplus), add to `LiabilitySide.Lines` with `Amount = -totalOpening` and `Name = "Difference in opening balances"`.
- **Deterministic Unified Sorting**:
  Sort the final report lines on both sides using a single, unified group display order, with `"Difference in opening balances"` placed at the end:
  ```csharp
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
  ```

### 2. `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`
- Add unit tests validating:
  - Unrecognized group validation failure (asserting `Status == "failed"`, `ErrorSummary` populated, and sides cleared).
  - Debit-balanced liabilities correctly routed to Assets side.
  - Credit-balanced assets (e.g. Bank Overdraft) correctly routed to Liabilities side.
  - Difference in opening balances computed using `OpeningBalance + PrePeriodMovement` and routed correctly on both sides.
  - Deterministic sorting order of report lines on both sides when dynamic routing occurs.

### 3. `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`
- Add integration tests covering the database query and calculation pipeline:
  - Proves that raw positive `stock_value` from `trn_closingstock_ledger` is correctly mapped to a negative `OpeningStockValue`.
  - Proves that a balanced trial balance (with pre-period stock movements and a balancing counterparty movement) results in `totalOpening == 0` (no difference line).
  - Proves that an unbalanced trial balance correctly computes and includes the `"Difference in opening balances"`.

## Verification Plan

### 1. Automated Verification
Run the dotnet test suite:
```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
```

### 2. Manual Verification in WPF UI
Run the WPF application and verify the report for the company **AR Foods** using `FinancialYearStart = 2025-05-01` and `AsAtDate = 2025-06-01` matches these expected values from the Tally screen ("Balance Sheet as at 1-Jun-25") exactly (Note: Stock-in-hand has a balance of 0.00 in AR Foods for this period, so it is not displayed):

| Report Section / Group | Expected Side | Expected Amount |
| :--- | :--- | :--- |
| **Capital Account** | Liabilities | `1,00,000.00` |
| **Profit & Loss A/c (Net)** | Liabilities | `1,61,56,220.53` |
| **Current Liabilities** | Assets | `9,10,983.13` |
| **Fixed Assets** | Assets | `69,070.00` |
| **Current Assets** | Assets | `45,92,779.85` |
| **Branch / Divisions** | Assets | `23,16,595.49` |
| **Suspense A/c** | Assets | `81,71,402.06` |
| **Difference in opening balances** | Assets | `1,95,390.00` |
| **Total Assets / Liabilities** | Both Sides | `1,62,56,220.53` |
| **Difference** | balanced / 0.00 | `0.00` |
