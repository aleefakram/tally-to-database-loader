# Audit Infrastructure Tracer Design

## Goal

Strengthen the local configuration audit foundation without broadening the Phase 1 scope into a full configuration-audit rollout.

This slice extracts the already-working `config_audit_log` write behavior into a small Core-owned helper and proves it by adding exactly one additional audited mutation: `SaveTallySettings`.

## Scope

Included:

- Keep using the existing `config_audit_log` table from schema version 4.
- Add a small internal audit insertion helper in `ConfigRepository`.
- Refactor `ResolveCompanyProfileSafetyState` to use the helper without changing behavior.
- Make `SaveTallySettings` write one audit row in the same SQLite transaction as the settings update.
- Add focused tests for transaction rollback, compact snapshots, and secret-free audit payloads.

Excluded:

- Audit viewer UI.
- Audit retention or purge behavior.
- Configuration import/export.
- Auditing database profile mutations.
- Auditing company profile mutations.
- Auditing deletes.
- Any target database writes.
- Any new public audit service abstraction.

## Design Constraints

Core remains UI-free. Repository methods that already receive an actor string must use that caller-provided actor. Existing methods that do not receive actor context must not introduce WPF dependencies in this slice.

Audit writes are fail-closed. If the audited mutation succeeds but the audit insert fails, the entire SQLite transaction rolls back.

Audit payloads must be compact and intentional. Do not serialize whole model objects by default.

Audit payloads must never contain credentials, encrypted credential blobs, or other secrets. This slice only audits Tally XML endpoint settings, which do not contain database passwords.

## Internal Audit Helper

Add a private helper in `ConfigRepository`, not a public interface:

```csharp
private static long InsertConfigAuditLog(
    SqliteConnection conn,
    SqliteTransaction transaction,
    DateTime createdAt,
    string actor,
    string action,
    string entityType,
    int entityId,
    string? entityName,
    string beforeJson,
    string afterJson,
    string reason)
```

Behavior:

1. Trim and validate `actor`, `action`, `entityType`, and `reason`.
2. Validate `beforeJson` and `afterJson` are non-empty.
3. Insert one row into `config_audit_log`.
4. Read `last_insert_rowid()` on the same connection.
5. Return the audit id.
6. Wrap insert/identity-read failures in `InvalidOperationException`, preserving the original exception as `InnerException`.

The helper does not open connections, begin transactions, commit, or roll back. Callers own the transaction boundary.

Callers are responsible for serializing compact snapshots before calling the helper. This avoids accidental double serialization, such as passing an already serialized JSON string through an `object` parameter and storing `"\"{}\""` instead of `{}`.

Snapshot serialization in this slice must use anonymous objects with lowercase property names. Do not pass named model objects to `JsonSerializer.Serialize` for audit snapshots in this slice.

`entityId` is `int` to match the current local configuration entity ID types. If audited entity IDs ever exceed `int.MaxValue`, this helper and the repository call sites must be widened to `long`.

## Safety State Resolution Refactor

`ResolveCompanyProfileSafetyState` will keep its public signature and behavior:

```csharp
long ResolveCompanyProfileSafetyState(
    int companyProfileId,
    string actor,
    string reason,
    DateTime resolvedAt);
```

It will continue to:

- allow only `review_required`, `attention_required`, and `unknown`;
- update status to `idle`;
- write `action = "resolve_safety_state"`;
- use compact snapshots containing exactly `id`, `name`, and `status`;
- return the generated audit id.

The only intended implementation change is replacing inline audit insertion with the helper.

## Tally Settings Audit Tracer

`SaveTallySettings` will become the first ordinary configuration mutation to use the audit helper.

Action:

```text
update_tally_settings
```

Entity:

```text
entity_type = "tally_settings"
entity_id = 1
entity_name = null
```

`entity_name = null` is intentional. `tally_settings` is a singleton row with no user-visible name, and the schema explicitly allows a null entity name.

Actor and reason:

- `SaveTallySettings` currently has no actor/reason parameters.
- To avoid changing method signatures or WPF flows in this slice, the repository uses:
  - `actor = "system"`
  - `reason = "Tally settings updated"`

This is known debt. Operator attribution for settings changes requires a future signature change that passes actor context from the UI or caller into Core. This slice deliberately does not make that API change.

Snapshots:

```json
{
  "server": "localhost",
  "port": 9000,
  "auto_start_tally": false
}
```

Rules:

- Include only `server`, `port`, and `auto_start_tally`.
- Exclude `tally_exe_path` and `tally_ini_path` in this tracer slice.
- Exclude any values unrelated to Tally XML server communication.
- For every save, `before_json` represents the current database row loaded before the update.
- If the singleton row is unexpectedly missing, throw `InvalidOperationException` with the message: `tally_settings singleton row (id=1) is missing. Database may be corrupt.`
- Do not invent a synthetic before-state and do not write `before_json = null`.
- `after_json` is built from the submitted `TallySettings` parameter after validation, not by re-reading the database. This matches the existing safety-state pattern: audit after-state records the intended committed state without a redundant read.

## Transaction Flow

`SaveTallySettings` will use one SQLite transaction:

1. Load the current settings row for `before_json`.
2. If the current row is missing, throw `InvalidOperationException`.
3. Build `before_json` from the loaded row.
4. Upsert the new settings.
5. Build `after_json` from the submitted settings values.
6. Insert the audit row.
7. Commit.

If any step fails, roll back the whole transaction.

## Error Handling

The audit helper throws:

- `ArgumentException` for empty required audit fields.
- `InvalidOperationException` for database insert or audit id retrieval failures, preserving the original exception as `InnerException`.

`SaveTallySettings` propagates these exceptions. It must not silently save settings without an audit row.

## Testing

Add or update tests to verify:

- `ResolveCompanyProfileSafetyState` still writes the same audit row after the helper refactor.
- `SaveTallySettings` writes one `update_tally_settings` audit row.
- The Tally settings audit row contains compact `before_json` and `after_json` with only:
  - `server`
  - `port`
  - `auto_start_tally`
- `tally_exe_path` and `tally_ini_path` are not present in the audit JSON.
- If `config_audit_log` is missing, `SaveTallySettings` rolls back and the prior settings remain unchanged.
- If the `tally_settings` singleton row is missing, `SaveTallySettings` throws `InvalidOperationException` and writes no audit row.

`SaveTallySettings` audit tests belong in `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`, following the existing isolated temporary database pattern.

## Success Criteria

- `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes.
- `ResolveCompanyProfileSafetyState` behavior remains unchanged.
- `SaveTallySettings` cannot commit without an audit row.
- The slice does not touch WPF.
- The slice does not audit database profiles or company profiles yet.
- No credentials or filesystem paths are written to audit JSON.
