# Sanitized Configuration Export Design

## Goal

Add a Core-owned, read-only sanitized configuration export that writes a human-readable JSON envelope for environment migration and review.

This slice establishes the export contract only. It does not implement import, diagnostic ZIP backup, UI file pickers, or conflict resolution.

## Scope

Included:

- Add a `ConfigExportService` in `TallyDbLoader.Core`.
- Export a versioned JSON envelope.
- Include database profiles without passwords.
- Include company profiles, currently representing Sync Job Profiles.
- Exclude runtime state, credentials, audit rows, sync runs, and raw diagnostics.
- Add fast local unit tests for JSON shape and secret exclusion.

Excluded:

- Sanitized config import.
- Diagnostic backup ZIP.
- WPF UI integration.
- Password prompting or credential migration.
- Conflict resolution.
- YAML-to-SQLite import.
- Dynamic table configuration export if no persisted table-config repository exists yet.

## Export Service

Create a small Core service:

```csharp
public sealed class ConfigExportService
{
    public ConfigExportService(IConfigRepository repository, string applicationVersion);

    public string ExportJson(DateTimeOffset exportedAt);
}
```

The service returns JSON as a string. File selection and writing are UI/application concerns and are outside this slice.
The application version is passed as a constructor string to avoid adding a single-use version-provider abstraction.

## JSON Envelope

The exported JSON must be indented and deterministic enough for tests.

Top-level shape:

```json
{
  "format": "tally-db-loader.config-export",
  "schema_version": 1,
  "application_version": "2.0.0-beta",
  "exported_at": "2026-06-11T10:15:30.0000000+05:30",
  "payload": {
    "database_profiles": [],
    "company_profiles": []
  }
}
```

Rules:

- `format` is a stable string used by future import routing.
- `schema_version` starts at `1`.
- `application_version` comes from the constructor-supplied string.
- `exported_at` uses the caller-supplied `DateTimeOffset` in round-trip format.
- JSON property names use lowercase snake_case.
- The exporter must not serialize repository model objects directly.

## Database Profile Payload

Each database profile includes only configuration fields:

```text
id
name
technology
server
port
username
has_password
```

Rules:

- `password` is omitted entirely.
- DPAPI ciphertext is omitted entirely.
- `has_password` is `true` when the repository model has a non-empty password after normal repository loading.
- Phase 1 accepts that `GetAllDatabaseProfiles()` returns decrypted password values. The exporter must use the value only to compute `has_password`, must not serialize it, and must not retain it outside the local projection step.
- If DPAPI decryption fails and the repository returns an empty password, export will report `has_password = false`; this is an accepted Phase 1 compromise to avoid adding a new credential-presence query in this slice.
- `last_test_result`, `last_tested_at`, and `used_by_count` are excluded.
- IDs are included for intra-export references only. Future import may remap them.

## Company Profile Payload

Each company profile includes only Sync Job configuration fields:

```text
id
name
tally_guid
consolidated
books_from
books_to
db_profile_id
target_catalog
schema
table_prefix
mode
interval_minutes
enabled
notify_on_error
pause_on_tally_close
entity_flags
```

Rules:

- Runtime fields are excluded: `status`, `last_run_at`, `last_duration_ms`, `last_rows_written`, and `error_count_24h`.
- Joined `DatabaseProfile` objects are excluded.
- Date values use round-trip ISO strings or `null`.
- `db_profile_id` preserves the local reference for future import mapping.
- The current `CompanyProfile` name is accepted as the implementation name for Sync Job Profile.

## Auditing

This slice does not write an audit row. Export is strictly read-only and must not mutate local SQLite state.

Export auditing is a future slice after a Core-owned audit append service exists. The export JSON itself must never include audit rows.

## Error Handling

- Export should fail if repository reads fail.
- Export should not mutate local SQLite state.
- Export should not require a scheduler pause.
- Empty profile lists are valid and produce empty arrays.

## Testing

Add tests for:

- Envelope contains `format`, `schema_version`, `application_version`, `exported_at`, and `payload`.
- JSON is indented and parseable.
- Database profiles include exactly allowed fields.
- Database profile password material is absent, including plaintext and `dpapi:`.
- Database profile `has_password` is present.
- Company profiles include exactly allowed configuration fields.
- Company profile runtime fields are absent.
- Empty repository exports valid empty arrays at `payload.database_profiles` and `payload.company_profiles`.
- Export does not change profile counts or write sync runs.

Default `dotnet test` must remain fast and local.

## Import Design Considerations

Future import must treat this envelope as an input contract, not as a database backup.

Future import should:

- Reject unknown `format` values.
- Reject newer `schema_version` values by default.
- Route older versions through explicit migrators.
- Treat exported IDs as temporary source IDs and remap them locally.
- Prompt for missing database passwords before commit.
- Detect conflicts before writing anything.
- Commit all accepted changes in one SQLite transaction.
- Save imported unsafe or unsupported jobs as disabled `review_required`.
- Audit the final import summary.

These constraints are documented now so the export shape remains importable later without forcing an immediate schema version bump.

## Success Criteria

- Export service lives in Core.
- No WPF files are changed.
- No import behavior is added.
- No passwords or DPAPI ciphertext appear in exported JSON.
- `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes.
