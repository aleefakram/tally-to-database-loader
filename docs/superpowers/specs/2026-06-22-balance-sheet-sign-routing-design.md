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
- **Opening Balance Difference Computation**:
  Compute `totalOpening` by summing the opening balances of all processed ledgers. For the `"Stock-in-hand"` group, check if `HasOpeningStockValue` is true and use `OpeningStockValue` (since it is already normalized to a signed negative/debit value by the query adapter, do not negate it). Otherwise, use `OpeningBalance`.
  - **Inclusion Rules**: Include the reserved P&L ledger, revenue ledgers, and all recognized group ledgers.
  - **Exclusion Rules**: Exclude any ledgers whose primary groups fail to resolve or are unrecognized (unrecognized groups are excluded from the report).
- **Group Recognition & Routing**:
  Verify that the group is recognized by checking `LiabilityGroups.Contains(group.Key)` or `AssetGroups.Contains(group.Key)`.
  - If unrecognized, log a warning/fail as before.
  - If recognized, route to `LiabilitySide.Lines` if `signedBalance > 0` (Credit) or `AssetSide.Lines` if `signedBalance < 0` (Debit).
- **Inject the `"Difference in opening balances"` Line**:
  - If `totalOpening > 0` (Credit surplus), add to `AssetSide.Lines` with `Amount = totalOpening` and `Name = "Difference in opening balances"`.
  - If `totalOpening < 0` (Debit surplus), add to `LiabilitySide.Lines` with `Amount = -totalOpening` and `Name = "Difference in opening balances"`.

### 2. `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`
- Add unit tests validating:
  - Inverted/debit liability group routing.
  - Inverted/credit asset group routing.
  - Correct computation and routing of the opening balance difference on both sides.

### 3. `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`
- Add integration test covering the database query and calculation pipeline:
  - Seeds a company with a debit-balanced liability, an opening stock value, and a trial balance difference.
  - Runs `BalanceSheetVerificationService.VerifyAsync` and asserts the generated report contains the expected `"Difference in opening balances"` and dynamic routing.

## Verification Plan

### 1. Automated Verification
Run the dotnet test suite:
```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
```

### 2. Manual Verification in WPF UI
Run the WPF application and run the verification for the new company. Verify the report matches these expected values exactly:

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
