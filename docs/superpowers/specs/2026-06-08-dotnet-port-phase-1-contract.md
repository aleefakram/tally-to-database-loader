# .NET Port Phase 1 Contract

This document trims the grilling-session decisions into an implementation contract for stabilizing the .NET port. It separates Phase 1 requirements from future hardening so the port does not stall on speculative edge cases.

## Goal

Phase 1 proves that the .NET application can safely replace the Node.js loader for core sync workflows while keeping the WPF app thin and the sync engine independently testable.

## Phase 1 Success Criteria

1. Core sync parity is proven for the supported relational targets: MSSQL, MySQL, and PostgreSQL.
2. `TallyDbLoader.Core` remains UI-free and can run/test sync behavior without WPF.
3. Existing YAML table configuration is importable without silently dropping unknown/custom definitions.
4. Unsupported or partially understood imported configuration is saved as `review_required` and cannot run automatically.
5. `CompanyProfile` remains the short-term implementation name for a Sync Job Profile.
6. Database Profile stores reusable server/credential settings; target catalog, schema, and table prefix belong to the Sync Job Profile.
7. Database credentials are stored encrypted in the local SQLite configuration database through a Core-facing credential protection boundary; sync execution must not prompt for credentials mid-run.
8. Tally XML communication uses UTF-16 and is serialized per Tally endpoint.
9. Before sync, Tally availability and configured company identity are verified. GUID matching is preferred when available; normalized name matching is only a fallback. Mismatches fail closed.
10. The same Sync Job Profile cannot run concurrently.
11. Writes to the same target identity are locked.
12. Incremental sync uses Tally AlterID watermarks, and those watermarks advance only after extraction, parsing, and target writes commit successfully.
13. Failed runs are recorded in `SyncRun` and do not advance watermarks.
14. Startup performs minimal reconciliation before scheduling: stale `running` jobs from a previous process are marked interrupted/unknown, and ambiguous states block automatic scheduling.
15. App shutdown stops scheduling new work, requests cancellation of active runs, and marks ambiguous interrupted runs as `unknown` or `attention_required`.
16. Fresh local SQLite configuration storage is initialized from a baseline schema before the scheduler starts.
17. Full Sync loads into staging, validates structurally, and promotes only after the staged data passes validation.
18. Full Sync must not expose empty or partial live target tables as the default production behavior.
19. Normal operational target writes are idempotent by stable Tally identity where available.
20. If no stable identity exists for an entity stream, incremental upsert fails closed for that stream.
21. WPF can configure Database Profiles and Sync Job Profiles, run sync manually, and display run status/history.
22. Safety-blocked states such as `review_required`, `attention_required`, and `unknown` block automatic scheduling.

## Required Phase 1 Validation

The Full Sync validation gate runs after staging and before promotion. It must fail closed on:

- missing selected entity stream/chunk completion
- missing or duplicate required identity keys
- fatal parser errors
- schema incompatibility that would make promotion fail
- lossy conversion unless explicitly allowed by a table/field policy

Accounting-level checks are not required in Phase 1:

- debit/credit balance validation
- previous-run variance thresholds
- mandatory non-empty date range checks
- referential completeness beyond required structural keys

These may become opt-in policies later.

## Required Phase 1 Tests

Default `dotnet test` should remain fast, local, and deterministic. It should cover:

- Tally XML generation and UTF-16 request content
- parser behavior using sanitized XML fixtures
- YAML import preservation and `review_required` blocking
- watermark advancement only after commit success
- failed runs not advancing watermarks
- credential protection round-trip and encrypted-at-rest storage
- startup reconciliation of stale running jobs
- bounded shutdown/cancellation state handling
- Tally availability and company identity mismatch blocking
- no overlapping runs for the same Sync Job Profile
- Tally request serialization
- target identity write locking
- Full Sync staging validation and failure rollback/cleanup
- idempotent upsert behavior using fakes or local test doubles
- identifier normalization and collision detection
- baseline SQLite schema initialization
- local SQLite config/repository behavior

Real MSSQL/MySQL/PostgreSQL tests are opt-in integration tests. Live Tally Prime tests are manual/opt-in certification tests.

## Phase 1 Non-Goals

These are intentionally outside the first stabilization pass:

- physical split of `CompanyProfile` into `TallyCompany` and `SyncJob`
- BigQuery, ADLS, CSV, JSON, and DuckDB target adapters
- cross-restart Full Sync resume
- Edit Log based deletion reconciliation
- advanced accounting/statistical validation
- complex queued rerun policies
- Windows Service mode
- multi-endpoint Tally UI
- full diagnostic backup system
- migration recovery window polish
- long-term audit retention/purge UX
- raw XML diagnostic capture beyond bounded failure snippets

## Future Hardening Backlog

- Split the domain model into `TallyCompany`, `DatabaseProfile`, and `SyncJob`.
- Add explicit deletion reconciliation policies, starting with periodic GUID sweep and later Edit Log support.
- Add target-side watermark/readback recovery for post-commit local metadata failures.
- Build sanitized JSON config export/import with versioned schema migrators.
- Add full diagnostic ZIP backup with explicit raw XML opt-in.
- Add local SQLite numbered migrations and a limited recovery UI.
- Add append-only audit log for safety-relevant configuration changes.
- Add retention policies for run history, logs, and diagnostic artifacts.
- Add advanced validation policies as per-job opt-ins.
- Add live Tally certification fixtures and documentation.

## Implementation Rule

When a decision is not needed to satisfy Phase 1 success criteria, implement the simpler safe behavior and record the broader version as backlog. Do not add configurability unless it protects Phase 1 data integrity or is already required by the existing app surface.
