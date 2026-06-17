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
- Indian digit grouping, for example `42,00,000.00`
- zero-balance primary group rows hidden by default, matching Tally's top-level Balance Sheet behavior

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
   - `balanced` when liabilities and assets differ by no more than the configured decimal tolerance
   - `out_of_balance` when totals differ
   - `failed` when required data could not be read or calculated

## Architecture

Keep calculation logic in `TallyDbLoader.Core`. WPF owns only input collection, command wiring, and report rendering.

### Core Service

Add `BalanceSheetVerificationService`.

Responsibilities:

- Validate selected Sync Job and target database profile.
- Validate target schema and table prefix using the same identifier policy as sync writers before building SQL.
- Open/read the target database connection asynchronously.
- Run provider-specific Balance Sheet queries.
- Convert query rows into a structured report model.
- Calculate side totals and difference.
- Save a lightweight local history row through `IConfigRepository`.
- Return the report to WPF as `Task<BalanceSheetReport>`.
- Accept a `CancellationToken`.

The service must not open a separate local SQLite configuration connection for history writes. Local app persistence stays behind `IConfigRepository`.

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
- schema-qualified and table-prefix-qualified table names

Identifiers cannot be passed as SQL parameters. Before any adapter concatenates a schema, table prefix, or table name into SQL, it must validate:

- schema is a valid provider identifier
- table prefix is either empty or a valid identifier fragment
- final physical table names pass `DbIdentifierPolicy.Validate`
- reserved keyword and provider length rules are enforced

Dynamic values such as dates, Sync Job IDs, and ledger names must remain parameterized.

All query APIs must be async and must dispose connections, commands, and readers with `using` or `await using`.

Default automation tests provider SQL generation and adapter selection. Live MSSQL/PostgreSQL/MySQL execution remains opt-in.

### Result Models

Add Core models:

- `BalanceSheetReport`
- `BalanceSheetSide`
- `BalanceSheetLine`
- `ProfitAndLossBreakdown`
- `BalanceSheetVerificationRun`

The report model stores numeric values as decimals. Formatting, alignment, and Indian number grouping belong to WPF or export formatting, not Core.

Add `BalanceSheetVerificationOptions`:

- `BalanceTolerance`, default `0.05m`
- `ProfitAndLossLedgerName`, default `Profit & Loss A/c`

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
- Database sign convention is Tally-oriented: credits are positive and debits are negative.
- `trn_accounting` is joined to `trn_voucher` by `guid`.
- Exclude vouchers where `trn_voucher.is_order_voucher = 1`.
- Exclude vouchers where `trn_voucher.is_inventory_voucher = 1` for accounting Balance Sheet movement.
- Include movement through `As At Date`.
- For Stock-in-Hand ledgers, use the latest `trn_closingstock_ledger.stock_value` on or before As At Date when that table exists. If it does not exist, fall back to ledger/accounting balance and add a report warning.

Version 1 report period uses:

- Financial Year Start as the start of current-period P&L movement.
- As At Date as the inclusive report date.

### Balance Sheet Groups

Use `mst_group.primary_group` and group flags to classify balances. If `primary_group` is blank for a custom subgroup, recursively walk `mst_group.parent` until a known primary group is found. Detect cycles or unresolved primary groups and return `failed` with the affected group name.

Liabilities:

- Capital Account
- Loans (Liability)
- Current Liabilities
- Profit & Loss A/c when net P&L is positive

Assets:

- Fixed Assets
- Investments
- Current Assets
- Branch / Divisions
- Misc. Expenses (ASSET)
- Suspense A/c
- Profit & Loss A/c when net P&L is negative

Version 1 groups rows by top-level primary group only. It does not render subgroup or ledger drilldown. Primary groups with a zero balance are omitted from display, but recognized groups such as `Investments` must appear when their balance is nonzero.

### Profit & Loss A/c

Revenue ledgers are not listed directly under assets or liabilities. They feed Profit & Loss A/c.

The service calculates:

- Opening Balance: opening balance of the reserved Profit & Loss ledger plus direct Profit & Loss ledger movement before Financial Year Start plus any pre-period revenue movement available in synced rows.
- Current Period: net revenue movement from Financial Year Start through As At Date, excluding the reserved Profit & Loss ledger itself, plus Stock-in-Hand adjustment.
- Less: Transferred: direct debit postings to the reserved Profit & Loss ledger from Financial Year Start through As At Date, reported as a positive deduction.

If the reserved Profit & Loss ledger is not found by `ProfitAndLossLedgerName`, use `0.00` for the reserved-ledger portion and add a warning. The default name is `Profit & Loss A/c`.

Stock-in-Hand adjustment for Current Period:

```text
Closing Stock as at As At Date - Opening Stock at Financial Year Start
```

Closing Stock uses `trn_closingstock_ledger` when present. Opening Stock uses Stock-in-Hand ledger opening balances plus pre-period stock movement available in synced rows. If stock valuation inputs are missing, calculate from available ledger balances and add a warning.

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
- balance tolerance used
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
- Use configured decimal tolerance when evaluating the difference.

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

The Run command must be async from WPF through Core. While the report is running, disable the Run button and keep the UI responsive.

Report layout:

- Two-column Balance Sheet
- Liabilities on the left
- Assets on the right
- Group names left-aligned
- Amounts right-aligned
- Profit & Loss A/c breakdown indented below its line
- Grand totals fixed at the bottom of each side
- Amounts formatted with Indian grouping using `CultureInfo("en-IN")` or a dedicated converter with equivalent output

Status display:

- Balanced
- Out of balance with difference amount
- Failed with concise failure reason
- Warnings for partial calculation assumptions, such as missing Stock-in-Hand closing values or missing reserved Profit & Loss ledger

## Tests

Add focused Core tests using SQLite fixtures.

Required tests:

- Simple asset/liability balances produce equal totals.
- Revenue movement creates Profit & Loss A/c.
- Negative Profit & Loss A/c appears on the asset side.
- Date filtering respects Financial Year Start and As At Date.
- Order/inventory vouchers are excluded from accounting movement.
- Credits are treated as positive and debits as negative.
- Investments primary group appears on the asset side.
- Stock-in-Hand closing value contributes to both assets and current-period P&L.
- Stock-in-Hand fallback emits a warning when `trn_closingstock_ledger` is absent.
- Profit & Loss ledger opening balance contributes to P&L opening balance.
- Direct debit postings to Profit & Loss ledger appear as `Less: Transferred`.
- Custom subgroup with blank `primary_group` resolves through recursive parent traversal.
- Recursive group cycle returns failed report.
- Balance tolerance controls whether a small difference is `balanced` or `out_of_balance`.
- Invalid schema or table prefix is rejected before SQL execution.
- Missing required table returns failed report.
- Missing required column returns failed report.
- Local history row is saved with totals and status.

Provider tests:

- Adapter selection resolves SQLite, MSSQL, PostgreSQL, and MySQL.
- Provider SQL generation includes expected date parameters, schema-qualified table names, table prefixes, and required table names.
- Provider SQL generation keeps dynamic values parameterized.
- Provider SQL generation uses valid parameter syntax for SQLite, MSSQL, PostgreSQL, and MySQL.
- Async service path disposes target database connections after report generation.

Presentation tests:

- WPF amount formatting produces Indian grouped values such as `65,04,742.51`.

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
