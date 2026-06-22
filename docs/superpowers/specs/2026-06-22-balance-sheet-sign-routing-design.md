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
- Compute `totalOpening` by summing the opening balances of all ledgers. For the `"Stock-in-hand"` group, check if `HasOpeningStockValue` is true and use `-OpeningStockValue`, otherwise fall back to `OpeningBalance`.
- Update the loop over the non-revenue, non-P&L grouped ledgers to route them dynamically to `LiabilitySide.Lines` (if the signed balance is positive) or `AssetSide.Lines` (if the signed balance is negative).
- Inject the `"Difference in opening balances"` line:
  - If `totalOpening > 0`, add to `AssetSide.Lines` with `Amount = totalOpening`.
  - If `totalOpening < 0`, add to `LiabilitySide.Lines` with `Amount = -totalOpening`.

### 2. `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`
- Add unit tests validating:
  - Inverted/debit liability group routing.
  - Inverted/credit asset group routing.
  - Correct computation and routing of the opening balance difference on both sides.

## Verification Plan
1. Run `dotnet test` to verify the new dynamic routing logic and test suite correctness.
2. Re-run verification in the WPF app UI to ensure the balance sheet of the new company balances perfectly (Difference = 0.00).
