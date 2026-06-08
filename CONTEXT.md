# Tally-to-Database Sync Utility Context

This context describes the background synchronization system that extracts financial records (ledgers, vouchers, etc.) from a running Tally Prime instance and loads them into a target relational database.

## Language

**Sync Job**:
A configured rule linking a specific Tally company to a target database profile, specifying the recurrence interval/daily time and the synchronization mode.
_Avoid_: Task, batch, synchronization profile

**Database Profile**:
A saved set of connection credentials and network details (technology, host, port, credentials) used to target a database engine instance.
_Avoid_: Connection string, database target, connection profile

**Sync Mode**:
The strategy used to keep the target database up-to-date with Tally: either Full Sync or Incremental Sync.
_Avoid_: Sync type

**AlterID**:
An internal sequential number maintained by Tally Prime representing the modification state of a master or transaction, used during Incremental Sync to isolate changed records.
_Avoid_: Version ID, update sequence number, change ID

## Relationships

- A **Sync Job** targets a single database catalog utilizing one **Database Profile**
- A **Sync Job** executes in a specified **Sync Mode** (either Full or Incremental)
- An Incremental **Sync Mode** utilizes the **AlterID** to fetch changes from Tally

## .NET porting decisions

- Implementation scope is governed by `docs/superpowers/specs/2026-06-08-dotnet-port-phase-1-contract.md`: Phase 1 should implement the minimal safe parity contract and defer broader hardening decisions unless they protect immediate data integrity.
- Core functionality must maintain parity with the legacy Node.js loader for syncing, **Database Profiles**, Tally XML server communication, database writes, and incremental **AlterID** behavior.
- New .NET/WPF features may be added around the core workflow, but they must not silently change core sync semantics.
- `TallyDbLoader.Core` must remain UI-free and independently executable/testable. WPF owns tray behavior, windows, file pickers, user notifications, startup integration, and presentation state.
- Existing YAML configuration files are a migration/import source only. After import, SQLite is the source of truth for the WPF application. YAML and SQLite are not kept in bidirectional sync.
- **Database Profile** stores reusable database engine/server/credential settings. Target catalog, schema, and table prefix belong to the **Sync Job**.
- The current `CompanyProfile` class/table is a short-term implementation name for a **Sync Job Profile**, not a pure Tally company identity. The target model is to split this into `TallyCompany`, `DatabaseProfile`, and `SyncJob` after sync parity stabilizes.
- Runtime summary fields on `CompanyProfile` are only a latest-status dashboard cache. `SyncRun` is the durable execution ledger and must record every run, including failures and partial runs.
- Tally XML communication is serialized per Tally server/port. Database writes may run concurrently only when target catalog/schema/table-prefix isolation is guaranteed; otherwise they require a per-target lock. A `CompanyProfile` must not have overlapping active runs.
- Incremental sync watermarks are commit acknowledgments. They advance only after Tally extraction, parsing, and target database writes commit successfully. Failed runs are recorded in `SyncRun` but must not advance watermarks.
- Tally voucher cancellation is handled through normal incremental sync when it appears as an updated record. Actual deletion reconciliation is a separate explicit per-job policy, not implicit every-run behavior. Supported policy starts with `none` and `periodic_guid_sweep`; Edit Log based deletion sync is a later proven capability.
- Tally availability verification is mandatory for every run. Auto-launch is supported when configured, but `tally.ini` modification for company auto-load is explicit opt-in and must be logged. If Tally is already running, job execution must not rewrite `tally.ini` as a side effect.
- Database credential protection belongs behind a Core-facing abstraction such as `ICredentialProtector`. Sync execution must not require WPF or prompt the user mid-run. Tests can use a fake/no-op protector; persisted credentials should remain encrypted in SQLite.
- Initial .NET parity targets MSSQL, MySQL, and PostgreSQL. BigQuery, ADLS, CSV, JSON, and DuckDB are later explicit adapters, not part of the first parity gate unless separately prioritized.
- SQLite has two separate roles. The local SQLite config database is required internal app storage. SQLite as a synced target database is optional/test-focused unless promoted later and must be represented by an explicit target adapter/profile technology.
- Target table/schema management is governed by an explicit per-job schema management mode. Conservative modes may validate or create missing app-owned objects; destructive rebuild requires deliberate user action and must never occur silently.
- Full Sync defaults to a stage-validate-commit workflow. Direct truncate-live-then-load behavior is a low-safety explicit mode only; watermarks and run success advance only after the staged replacement commits.
- Sync Jobs may select a subset of entities, but dependency validation is required and non-selected entities must be left untouched. Incremental watermark tracking must be separated where Tally semantics differ by entity category.
- Incremental watermarks are keyed by Sync Job (`CompanyProfile` currently, future `SyncJob`) plus specific entity stream. Broad shared watermarks are only a legacy compatibility compromise and must not be used for selected-entity sync.
- Retry policy is bounded and transient-only. Tally/network startup failures and clearly transient database connection failures may retry with exponential backoff and jitter. Deterministic config/XML/permission errors are not retried. Ambiguous database commit outcomes must not be blindly retried.
- Sync run logging is structured around phases and entity streams. Each run records mode, selected entities, status, failure phase, retries, rows extracted/written by entity, target details, watermark before/after, and a UI log excerpt or full log reference.
- If target writes commit but local SQLite metadata/watermark update fails, the job enters `attention_required`/`unknown` rather than ordinary retry. Recovery requires reconciliation, target-side watermark readback, or manual repair.
- Normal synced operational target tables use idempotent upserts keyed by stable Tally identity, preferably GUID plus source/job context where needed. Append-only writes are reserved for explicit audit/history/raw snapshot tables.
- Each entity stream must have a documented identity strategy. GUID is preferred; stable natural keys are allowed only when proven. If no stable identity exists, incremental upsert fails closed for that stream and only explicit full snapshot replacement is allowed.
- Dynamic YAML/table configuration remains the core parity engine for TDL/XML generation, dynamic parsing, and configurable table writes. Strongly typed entity models are layered on top for common entities, UI ergonomics, diagnostics, and tests without reducing dynamic config expressiveness.
- YAML import is loss-preserving. Legacy and custom table definitions are imported faithfully where possible; unsupported or unknown features are flagged explicitly rather than silently discarded. Unknown custom tables require explicit identity strategy before incremental upsert is enabled.
- Imported configurations with unsupported or partially understood features are saved in a disabled `review_required` state. Automatic execution is blocked until the user reviews and approves or fixes the imported configuration.
- Imported table/entity configs have individual support/review status, rolled up to the parent Sync Job runnable state. Users may disable or fix problematic tables and run safe selected tables; the scheduler only runs jobs whose rollup state is runnable.
- Scheduled due ticks use `skip_if_running` by default. If the same `CompanyProfile` is already active, the scheduler records a skipped overlap event and computes the next due time from the scheduler clock without queueing catch-up runs.
- Manual `Sync Now` for an already running job is disabled/rejected with an `already_running` alert/log. During parity/stabilization there is no hidden queue or overlapping manual rerun.
- `Sync All Now` starts only enabled, runnable, idle jobs with complete configuration. Disabled, `review_required`, `attention_required`/`unknown`, already-running, or incomplete jobs are skipped and reported with reasons.
- Tray app exit performs bounded graceful shutdown: stop scheduling new work, request cancellation for active jobs, rollback/cancel before commit where possible, and mark ambiguous commit-phase exits as `attention_required`/`unknown`.
- Startup performs reconciliation before enabling scheduling. Stale running `SyncRun` rows from a previous process are marked interrupted/unknown as appropriate, and affected jobs enter `attention_required` when commit or watermark state is ambiguous.
- Single-instance behavior uses a named mutex plus local IPC restore command. A second launch sends `RestoreWindow` to the primary tray instance and exits; only if IPC fails should it show an already-running fallback message.
- Startup-on-login is an opt-in per-user interactive startup mechanism, such as Startup folder shortcut or per-user Run registry key. Phase 1 is not a Windows Service because Tally Prime is an interactive desktop application.
- Phase 1 uses one global Tally XML endpoint, defaulting to `localhost:9000`. Tally client instances and serialization locks are keyed by endpoint (`host:port`) to allow future multi-endpoint expansion.
- Before sync, the configured Tally company is verified by stable identity. GUID match is preferred; exact normalized name match is only a fallback. Ambiguous or changed identity fails closed and requires user review.
- If a Tally company is renamed but its GUID is unchanged, sync may continue, the local display metadata is updated, and the rename is logged. If the name matches but GUID differs, execution is blocked for manual review.
- Source company rename never automatically changes target catalog, schema, or table prefix. Target location is Sync Job configuration and any target rename/migration is explicit user action only.
- Target identity (`DatabaseProfile` plus catalog plus schema plus table prefix) is collision-checked on save and used for write locking. Duplicate target identity is blocked by default unless entity streams are explicitly non-overlapping and the user confirms the risk.
- Physical SQL identifiers are produced by a centralized engine-aware normalization policy. Logical names are preserved separately from physical identifiers; invalid names, reserved words, length limits, quoting, and normalization collisions are handled explicitly before DDL/DML execution.
- Executable SQL, connection management, transaction handling, identifier quoting, upsert/merge syntax, and bulk-load mechanics belong behind engine-specific writer/loader abstractions. Shared orchestrator code issues high-level logical operations only.
- Target database writes use per-target application locks plus engine-default transactions, typically Read Committed. Isolation is escalated only for specific writer operations that require it, and unsupported atomicity guarantees are logged.
- Full Sync should preserve the old readable target state until final commit wherever technically possible. Strategies that expose empty or partial live tables are non-atomic, require explicit opt-in, and are unacceptable as production defaults.
- Full Sync chunking is an extraction strategy inside one logical run. Chunks accumulate in staging with per-chunk diagnostics; validation and final live replacement occur only after all chunks succeed. Individual chunks never commit independently to live tables.
- Full Sync supports bounded within-run retries for failed chunks. If a chunk fails permanently or the process restarts, the logical run aborts; staging is discarded or quarantined and is never promoted. Cross-restart full-sync resume is a future-phase feature.
- Before Full Sync promotion, a mandatory validation gate runs after staging and before live transaction/swap. Phase 1 validation covers chunk/stream completeness, non-empty and unique identity keys, fatal parsing errors, and target schema compatibility. Accounting/statistical checks such as debit/credit balance, date-range completeness, and previous-run variance are deferred to future opt-in job policies.
- In Phase 1, every selected table/entity in a Sync Job is critical. A fatal parsing error in any selected table fails the entire logical run and prevents promotion for all selected tables.
- Lossy parsing or conversion fails by default, including string truncation, numeric overflow, invalid required dates, malformed identity fields, or required XML shape mismatches. Downgrading to warnings requires explicit per-table or per-field policy and must be logged with counts/samples.
- Raw Tally XML/full payload diagnostics are not persisted by default. Raw snippets or payloads are captured only on opt-in or bounded failure diagnostics, with size/retention limits and sensitivity warnings.
- Retention is tiered: structured `SyncRun` history is kept longer than technical logs, and raw XML/snippet diagnostics have short bounded retention. Evidence for `attention_required`/`unknown` runs is never deleted until explicitly resolved or confirmed by the user.
- Sync Job states are operational scheduler states: `disabled`, `review_required`, `idle`, `running`, `ok`, `warn`, `err`, `attention_required`, and `unknown`; `queued` is introduced only if visible queueing exists. `review_required`, `attention_required`, and `unknown` block automatic scheduling.
- Safety-blocked states (`review_required`, `attention_required`, `unknown`) are cleared only by explicit guided user or recovery workflows. Resolution is audited with action, timestamp, and outcome; timers and restarts never clear them automatically.
- Safety-relevant configuration changes are written to a dedicated read-only audit log in local SQLite, including Database Profile changes, credential/target changes, Sync Job changes, schema/destructive mode changes, entity selection, import approvals, recovery resolutions, Tally auto-launch settings, manual full reset/rebuild, and protected evidence deletion.
- Audit log entries are append-only. The application exposes no normal update/delete path for audit records. Any explicit retention purge must itself leave a permanent audit entry and must not remove unresolved safety-state evidence.
- Backup/export has two separate modes. Sanitized configuration export is live, human-readable JSON and omits passwords entirely while including Database Profiles, CompanyProfiles/Sync Jobs, schedules, policies, and dynamic table mappings. Full diagnostic backup is an explicit ZIP bundle using SQLite Online Backup/VACUUM INTO plus shared-read log copies; raw XML diagnostics are excluded by default and require an explicit sensitivity checkbox. Config import prompts for missing passwords, encrypts them locally, and audits the import.
- Configuration import never overwrites existing profiles/jobs silently. It scans for conflicts by profile identity, Sync Job name, target identity, and Tally company GUID; users must explicitly choose skip, copy/rename, or confirmed update before commit. Import summaries are audited.
- Sanitized configuration import is atomic all-or-nothing. Parsing, validation, conflict resolution, and credential entry occur before commit; final writes and audit entries happen in a single SQLite transaction and roll back completely on failure.
- Exported configuration JSON uses a versioned envelope with format, schema version, app version, export timestamp, and payload. Import rejects unknown formats, migrates older supported schema versions through explicit migrators, and rejects newer schemas by default.
- Local SQLite schema changes use explicit numbered migrations. App startup runs migrations before UI/scheduler availability, creates a safe backup before risky schema modifications, audits migrations, and rolls back/fails startup cleanly if migration fails.
- If local SQLite migration fails, the scheduler and normal dashboard remain blocked. WPF may show a dedicated Migration Recovery Window with failure details, backup/diagnostic options, retry, and restore actions; normal sync editing/execution is unavailable.
- SQLite migration execution, backup/restore APIs, rollback/failure handling, migration audit entries, and recovery business logic live in `TallyDbLoader.Core`. WPF is a thin Migration Recovery Window that presents choices and invokes Core services.
- Core parity stability requires automated coverage for Tally XML/UTF-16/parsing, loss-preserving YAML import and review states, watermark commit safety, no overlap/Tally serialization/target locks, Full Sync staging validation and rollback, MSSQL/MySQL/PostgreSQL writer behavior, identifier normalization, idempotent upserts, import/export conflicts, migrations, recovery, and append-only audit behavior.
- Default `dotnet test` remains fast/local/deterministic using SQLite, mocks, fakes, and dialect tests. Real MSSQL/MySQL/PostgreSQL integration tests are explicit opt-in via environment/config and are skipped cleanly when not configured.
- Live Tally Prime integration tests are manual/opt-in certification only. Default automation uses fake HTTP servers and static sanitized XML fixtures; live tests require explicit environment settings and a known loaded test company.

## Example dialogue

> **Dev**: "When the user triggers a manual sync from the tray icon, do we run every **Sync Job** immediately?"
> **Domain expert**: "Yes, the manual sync should execute all active **Sync Jobs** right away, bypassing their regular schedules."
> **Dev**: "And does the **Incremental Sync** mode compare the database's last recorded **AlterID** with Tally's current value?"
> **Domain expert**: "Exactly. If they match, Tally has no new modifications, and we skip syncing that job's data."

## Flagged ambiguities

- "Connection Profile" was used interchangeably with **Database Profile** — resolved: we use **Database Profile** consistently.
