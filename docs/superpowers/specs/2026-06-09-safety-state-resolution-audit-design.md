# Safety State Resolution & Audit Log Design

## Goal

Add a Phase 1 recovery foundation for blocked Sync Jobs by allowing an administrator to explicitly resolve safety states back to `idle`, while writing an immutable audit trail in the local SQLite configuration database.

This slice does not implement guided repair, retry, restore, diagnostic inspection, or target database mutation. It only records an authorized decision that the operator has reviewed the blocked condition and wants the Sync Job to become runnable again.

## Scope

Included:

- Add an append-only `config_audit_log` table to the local SQLite database.
- Add Core-owned APIs to resolve blocked `CompanyProfile.Status` values.
- Allow only these transitions:
  - `review_required -> idle`
  - `attention_required -> idle`
  - `unknown -> idle`
- Require a non-empty reason.
- Record actor, timestamp, entity identity, before/after state, action, and reason.
- Keep WPF as a thin presentation layer that collects a reason and calls Core.

Excluded:

- Retrying a failed sync run.
- Restoring backups.
- Editing configuration as part of resolution.
- Inspecting raw XML diagnostics.
- Repairing target databases.
- Automatically clearing safety states on timers, startup, or successful future events.

## Domain Rules

`CompanyProfile.Status` remains the source of truth for scheduler eligibility.

Blocked states:

- `review_required`
- `attention_required`
- `unknown`

Resolution target:

- All allowed resolutions transition the status to `idle`.

Invalid resolution attempts must fail closed:

- Missing profile.
- Empty or whitespace reason.
- Current status is `idle`, `completed`, `failed`, or `running`.
- Audit insert fails.
- Status update affects anything other than exactly one row.

## SQLite Schema

Add a migration that creates `config_audit_log`:

```sql
CREATE TABLE IF NOT EXISTS config_audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL,
    actor TEXT NOT NULL,
    action TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    entity_name TEXT NULL,
    before_json TEXT NOT NULL,
    after_json TEXT NOT NULL,
    reason TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_config_audit_log_created_at
ON config_audit_log(created_at DESC);

CREATE INDEX IF NOT EXISTS ix_config_audit_log_entity
ON config_audit_log(entity_type, entity_id, created_at DESC);
```

The table is append-only by application contract. Core must not expose update or delete methods for audit rows.

## Core API

Add repository support for one transactional operation:

```csharp
void ResolveCompanyProfileSafetyState(
    int companyProfileId,
    string actor,
    string reason,
    DateTime resolvedAt);
```

Behavior:

1. Validate `actor` and `reason` are non-empty after trimming.
2. Load the company profile inside the SQLite transaction.
3. Verify current status is one of `review_required`, `attention_required`, or `unknown`.
4. Build a compact `before_json` snapshot containing at least `id`, `name`, and `status`.
5. Update `company_profiles.status` to `idle`.
6. Verify exactly one row was updated.
7. Insert a `config_audit_log` row with action `resolve_safety_state`.
8. Commit the transaction.

If any step fails, roll back the transaction. The previous status must remain unchanged if the audit row is not written.

## WPF Flow

Add a simple selected-job action:

- Enable only when the selected company status is `review_required`, `attention_required`, or `unknown`.
- Prompt for a required reason.
- Use an actor string derived from the current Windows identity where available.
- Call the Core resolution API.
- Refresh company profiles after success.
- Show rejection/error feedback if Core rejects the operation.

The UI must not directly set `CompanyProfile.Status` for this workflow.

## Testing

Repository tests:

- Resolving `review_required`, `attention_required`, and `unknown` sets status to `idle`.
- Each successful resolution writes one audit row.
- Audit row contains action, actor, reason, entity id, entity name, before status, and after status.
- Resolving `idle`, `completed`, `failed`, or `running` throws.
- Empty reason throws.
- Missing profile throws.
- If the audit insert fails, the company status remains unchanged. This can be tested by forcing an audit failure inside a transaction, for example by temporarily renaming or dropping `config_audit_log` in a disposable test database before calling the resolution API.

WPF/ViewModel tests:

- Resolve command is disabled or rejected for non-blocked states.
- Resolve command calls Core only when a reason is supplied.
- After success, profiles are reloaded.

## Success Criteria

- `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes.
- There is no Core dependency on WPF.
- Safety states cannot be cleared without an audit row.
- Audit rows are append-only through the Core API surface.
