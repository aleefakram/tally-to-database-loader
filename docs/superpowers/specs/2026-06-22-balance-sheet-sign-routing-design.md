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
- **Opening Balance Difference Computation**:
  Compute `totalOpening` immediately after the ledger resolution loop has completed (so that `PrimaryGroup` is fully populated for all ledgers).
  - Calculate `totalOpening` using the **Tally report-period opening basis** (i.e. using `OpeningStockValue` directly without negation for `Stock-in-hand` if `HasOpeningStockValue` is true; otherwise `OpeningBalance`).
  - **Inclusion Rules**: Include the reserved P&L ledger, revenue ledgers, and all recognized group ledgers.
  - **Exclusion Rules**: Exclude any ledgers whose primary groups fail to resolve or are unrecognized (which will fail the report anyway).
- **Group Recognition & Routing**:
  For each non-revenue group:
  - First verify that the group is recognized by checking `LiabilityGroups.Contains(group.Key)` or `AssetGroups.Contains(group.Key)`. If unrecognized, call `Fail(report, $"Unrecognized primary group '{group.Key}' was detected.")` to fail the report verification and populate `ErrorSummary`.
  - If recognized, calculate `signedBalance`.
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
      "Branch / Divisions",
      "Misc. Expenses (ASSET)",
      "Suspense A/c",
      request.Options.ProfitAndLossLedgerName,
      "Difference in opening balances"
  };
  ```

### 2. `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`
- Add unit tests validating:
  - Unrecognized group validation failure (asserting `Status == "failed"` and `ErrorSummary` is populated).
  - Debit-balanced liabilities correctly routed to Assets side.
  - Difference in opening balances computed and routed correctly on both sides.
  - Deterministic sorting order of report lines on both sides when dynamic routing occurs.

### 3. `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`
- Add integration test covering the database query and calculation pipeline:
  - Seeds a company with a positive raw `stock_value` in the `trn_closingstock_ledger` table, and verifies the query adapter correctly normalizes it to a negative `OpeningStockValue`.
  - Runs `BalanceSheetVerificationService.GenerateAsync` and asserts the generated report contains the expected `"Difference in opening balances"` and dynamic routing.

## Verification Plan

### 1. Automated Verification
Run the dotnet test suite:
```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
```

### 2. Manual Verification in WPF UI
Run the WPF application and verify the report for the company **AR Foods** using `FinancialYearStart = 2025-05-01` and `AsAtDate = 2025-06-01` matches these expected values from the Tally screen ("Balance Sheet as at 1-Jun-25") exactly:

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
