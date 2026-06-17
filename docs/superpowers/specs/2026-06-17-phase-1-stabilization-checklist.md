# Phase 1 Stabilization Checklist

## Purpose

Define the final hardening gate for the Phase 1 .NET beta before moving to new feature work.

This checklist verifies that the implemented Phase 1 slices form a coherent, safe beta: UI-free Core, guarded WPF operations, safe sync state handling, audited configuration mutations, sanitized config transfer, and diagnostic backup support.

## Scope

### Included

- Automated verification commands.
- Manual WPF smoke flows.
- Local SQLite safety checks.
- Documentation and release-readiness checks.
- Provider certification status review.
- Known v1 deferrals.

### Excluded

- New feature development.
- Conflict-resolution UI for imports.
- Audit viewer or retention management UI.
- Windows Service, CLI runner, or tray-only execution redesign.
- Live Tally certification beyond existing manual/opt-in policy.
- MSSQL/PostgreSQL/MySQL certification on this machine, because local database services are unavailable.

## Release-Blocking Criteria

Phase 1 beta is blocked if any of the following are true:

- Default `dotnet test` fails.
- `dotnet build src\TallyDbLoader.sln` fails.
- `git diff --check` reports whitespace or formatting errors in the active diff.
- WPF cannot start on a Windows desktop session.
- Configuration mutations are allowed while the sync engine is running.
- Safety states such as `review_required`, `attention_required`, or `unknown` can be cleared without an audited explicit action.
- Sanitized config export leaks passwords, DPAPI ciphertext, or raw XML payloads.
- Sanitized config import allows silent overwrite or conflict resolution in this create-new-only phase.
- Diagnostic backup fails to create a ZIP when logs/XML folders are missing and raw XML is not requested.

Non-blocking items must be documented as known limitations or deferred work.

## Automated Verification

Run from the repository root:

```powershell
dotnet test tests\TallyDbLoader.Tests\TallyDbLoader.Tests.csproj --no-restore
dotnet build src\TallyDbLoader.sln
git diff --check
git status --short
```

Expected result:

- Default tests pass.
- Opt-in provider tests are skipped unless explicitly configured.
- Build succeeds with zero errors.
- Active diff has no whitespace issues.
- Working tree is clean before tagging or handing off a beta candidate.

## Manual WPF Smoke Flows

Run the WPF shell:

```powershell
dotnet run --project src\TallyDbLoader.Wpf\TallyDbLoader.Wpf.csproj
```

Verify these workflows:

- App starts and shows dashboard without migration recovery mode.
- Tally settings can be edited while engine is stopped.
- Database profile can be created, edited, and deleted while engine is stopped.
- Sync job profile can be created and remains conceptually treated as a Sync Job, even though the class/table name is still `CompanyProfile`.
- Starting the sync engine blocks configuration mutations.
- Stopping the sync engine re-enables guarded configuration mutations.
- Safety-state resolution requires an explicit reason and writes an audit entry.
- Sanitized config export writes JSON and excludes passwords.
- Sanitized config import accepts create-new payloads, prompts for required passwords, and blocks conflicts.
- Diagnostic backup creates a ZIP without raw XML by default.
- Diagnostic backup handles missing raw XML/log folders without crashing when raw XML is not included.

## Data Integrity Smoke Checks

Use local SQLite/default tests as the Phase 1 automated baseline:

- Full sync staging validation must fail closed on missing IDs, duplicate primary identities for primary tables, parsing failures, or schema incompatibility.
- Derived/helper tables without `guid` must not fail blind GUID validation.
- Derived child tables may contain repeated parent GUID values when their table nature permits it.
- Watermarks advance only after successful commit.
- Startup reconciliation converts stale running jobs into `unknown`.
- Safety states are never cleared automatically.

## Configuration Transfer Checks

Sanitized export:

- Uses the versioned envelope format.
- Includes database profiles, sync job profiles, table/schema config, and schedule metadata.
- Omits password fields entirely or emits empty/null password placeholders only where required by the schema.
- Does not include DPAPI ciphertext.

Sanitized import:

- Is transactional.
- Requires passwords for exported profiles marked `has_password = true`.
- Imports new profiles as disabled/review-required where Core rules require review.
- Blocks existing database profile and company/sync job conflicts.
- Writes a single `import_sanitized_config` audit summary row.

## Provider Certification Status

Current local certification:

- SQLite: covered by default fast tests.
- MSSQL/PostgreSQL/MySQL: implementation exists with opt-in tests, but certification remains pending on a machine or CI runner with disposable database services.
- Live Tally Prime: manual/opt-in certification only. Default CI must rely on HTTP fakes and XML fixtures.

This status is acceptable for Phase 1 beta only if release notes clearly state provider parity is pending until opt-in tests pass.

## Known V1 Deferrals

Defer these until after Phase 1 stabilization:

- Import conflict-resolution UI.
- Audit log browser and retention controls.
- Diagnostic backup customization beyond raw XML include/exclude.
- Windows Service or CLI runner.
- Multi-endpoint Tally management UI.
- Cross-restart chunk recovery.
- Advanced accounting-level validation checks.
- Provider certification automation for external database services.

## Completion Criteria

The stabilization pass is complete when:

- Automated verification commands pass.
- Manual WPF smoke flows are checked or explicitly marked not run with reason.
- Release history reflects the current beta capabilities and provider certification status.
- Known deferrals are documented.
- No release-blocking issue remains open.
