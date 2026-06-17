# Design: Balance Sheet Verification

## Purpose

Add a manual Balance Sheet Verification feature that lets users visually confirm that synced target database data produces a Tally-style Balance Sheet for the same period they inspect in Tally.

This is a business-level validation report, not a cell-level reconciliation engine. It is intended to catch practical data quality issues such as missing vouchers, wrong signs, wrong ledger balances, broken group mappings, or incomplete accounting sync before the synced database is trusted by a BI platform.

## Scope

Version 1 computes a Balance Sheet from the synced target database only. It does not fetch Tally's own Balance Sheet, does not compare against a Tally-exported statement, and does not change Sync Job success or failure state.

The user manually runs the report for a selected Sync Job and chooses:

- Financial Year Start
- As At Date

The output mirrors the top-level Tally Balance Sheet view:

- Left side: Liabilities
- Right side: Assets
- Top-level primary group totals
- Profit & Loss A/c line
- Profit & Loss A/c breakdown:
  - Opening Balance
  - Current Period
  - Less: Transferred
- Grand totals on both sides

## Non-Goals

- No automatic post-sync verification gate.
- No cell-level Tally-vs-database diff.
- No automated side-by-side comparison with Tally's own Balance Sheet.
- No drilldown below top-level Balance Sheet groups in version 1.
- No target database writes.
- No scheduler blocking based on Balance Sheet report outcome.

## User Flow

1. User opens the Balance Sheet Verification view in the WPF app.
2. User selects a Sync Job.
3. App defaults Financial Year Start to `CompanyProfile.BooksFrom` when present; otherwise it uses April 1 for the fiscal year containing the As At Date.
4. App defaults As At Date to `CompanyProfile.BooksTo` when present; otherwise it uses the current local date.
5. User adjusts both dates as needed.
6. User clicks Run.
7. Core reads the configured target database and computes the report.
8. WPF renders a two-column Balance Sheet:
   - Liabilities on the left
   - Assets on the right
   - grand totals at the bottom
9. WPF shows report status:
   - `balanced` when liabilities total equals assets total within decimal precision
   - `out_of_balance` when totals differ
   - `failed` when required data could not be read or calculated

## Architecture

Keep calculation logic in `TallyDbLoader.Core`. WPF owns only input collection, command wiring, and report rendering.

### Core Service

Add `BalanceSheetVerificationService`.

Responsibilities:

- Validate selected Sync Job and target database profile.
- Open/read the target database connection.
- Run provider-specific Balance Sheet queries.
- Convert query rows into a structured report model.
- Calculate side totals and difference.
- Save a lightweight local SQLite history row.
- Return the report to WPF.

### Query Adapter Boundary

Add `IBalanceSheetQueryAdapter`.

Provider adapters:

- SQLite
- MSSQL
- PostgreSQL
- MySQL

Adapters keep provider-specific SQL details out of the service:

- identifier quoting
- date parameter syntax
- `COALESCE` and null handling
- decimal casting
- parameter marker conventions

Default automation tests provider SQL generation and adapter selection. Live MSSQL/PostgreSQL/MySQL execution remains opt-in.

### Result Models

Add Core models:

- `BalanceSheetReport`
- `BalanceSheetSide`
- `BalanceSheetLine`
- `ProfitAndLossBreakdown`
- `BalanceSheetVerificationRun`

The report model stores numeric values as decimals. Formatting, alignment, and Indian number grouping belong to WPF or export formatting, not Core.

## Calculation Rules

Required synced tables:

- `mst_group`
- `mst_ledger`
- `trn_voucher`
- `trn_accounting`

Conditionally used table:

- `trn_closingstock_ledger`, when present, for Stock-in-Hand closing value.

### Ledger Movement

For each ledger:

- Opening balance comes from `mst_ledger.opening_balance`.
- Accounting movement comes from `trn_accounting.amount`.
- `trn_accounting` is joined to `trn_voucher` by `guid`.
- Exclude vouchers where `trn_voucher.is_order_voucher = 1`.
- Exclude vouchers where `trn_voucher.is_inventory_voucher = 1` for accounting Balance Sheet movement.
- Include movement through `As At Date`.
- For Stock-in-Hand ledgers, use the latest `trn_closingstock_ledger.stock_value` on or before As At Date when that table exists. If it does not exist, fall back to ledger/accounting balance and add a report warning.

Version 1 report period uses:

- Financial Year Start as the start of current-period P&L movement.
- As At Date as the inclusive report date.

### Balance Sheet Groups

Use `mst_group.primary_group` and group flags to classify balances.

Liabilities:

- Capital Account
- Loans (Liability)
- Current Liabilities
- Profit & Loss A/c when net P&L is positive

Assets:

- Fixed Assets
- Current Assets
- Branch / Divisions
- Misc. Expenses (ASSET)
- Suspense A/c
- Profit & Loss A/c when net P&L is negative

Version 1 groups rows by top-level primary group only. It does not render subgroup or ledger drilldown.

### Profit & Loss A/c

Revenue ledgers are not listed directly under assets or liabilities. They feed Profit & Loss A/c.

The service calculates:

- Opening Balance: net revenue movement in synced rows dated before Financial Year Start. If the synced database does not contain pre-period accounting history, the value is `0.00` and the report includes a warning that P&L opening may not mirror Tally.
- Current Period: net revenue movement from Financial Year Start through As At Date.
- Less: Transferred: `0.00` in version 1. Detecting Tally's nonzero transfer line requires an explicit transfer-ledger policy and is outside this first slice.

Profit & Loss A/c amount:

```text
Opening Balance + Current Period - Less Transferred
```

If the result is positive, show it on the liabilities side. If negative, show its absolute value on the assets side.

## Local History

Add a lightweight local SQLite history table for generated Balance Sheet verification runs.

Store:

- Sync Job ID
- target database identity
- Financial Year Start
- As At Date
- generated timestamp
- liability total
- asset total
- difference
- status: `balanced`, `out_of_balance`, or `failed`
- warning summary
- optional error summary

The history table is an app-local evidence index. It does not store every rendered line in version 1.

## Error Handling

Missing required table:

- Return `failed`.
- Include the table name in the error summary.

Missing required column:

- Return `failed`.
- Include table and column in the error summary.

Database query or connection failure:

- Return `failed`.
- Keep the message actionable without exposing passwords.

Out-of-balance report:

- Return a rendered report and status `out_of_balance`.
- Do not block future syncs.

Partial calculation warning:

- Return the rendered report with warning text.
- Keep status based on totals unless calculation cannot proceed.

Unexpected exception:

- Return `failed`.
- Save a failed history row when the local history database is available.

## WPF Presentation

Add a manual Balance Sheet Verification view or panel.

Controls:

- Sync Job selector
- Financial Year Start date picker
- As At Date date picker
- Run button

Report layout:

- Two-column Balance Sheet
- Liabilities on the left
- Assets on the right
- Group names left-aligned
- Amounts right-aligned
- Profit & Loss A/c breakdown indented below its line
- Grand totals fixed at the bottom of each side

Status display:

- Balanced
- Out of balance with difference amount
- Failed with concise failure reason

## Tests

Add focused Core tests using SQLite fixtures.

Required tests:

- Simple asset/liability balances produce equal totals.
- Revenue movement creates Profit & Loss A/c.
- Negative Profit & Loss A/c appears on the asset side.
- Date filtering respects Financial Year Start and As At Date.
- Order/inventory vouchers are excluded from accounting movement.
- Missing required table returns failed report.
- Missing required column returns failed report.
- Local history row is saved with totals and status.

Provider tests:

- Adapter selection resolves SQLite, MSSQL, PostgreSQL, and MySQL.
- Provider SQL generation includes expected date parameters and required table names.

Default test command:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
```

## Success Criteria

- Users can manually generate a top-level Balance Sheet from synced database data.
- Users can choose Financial Year Start and As At Date.
- The report visually resembles the Tally Balance Sheet level shown in the approved screenshot.
- Core calculation is testable without WPF.
- SQLite fixture tests prove the major accounting paths.
- Provider adapters exist for SQLite, MSSQL, PostgreSQL, and MySQL.
- The feature does not modify target synced tables or Sync Job execution status.
