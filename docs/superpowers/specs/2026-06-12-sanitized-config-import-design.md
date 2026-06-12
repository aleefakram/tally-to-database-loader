# Sanitized Configuration Import Design

## Goal

Add a Core-owned sanitized configuration import service that parses a versioned configuration JSON envelope (produced by the export service), validates it, resolves conflicts, prompts for or merges passwords, and transactionally commits changes to the local SQLite database.

This slice establishes the import validation, conflict resolution, transactional persistence, and import auditing. It does not implement a WPF UI, a file picker, or migrators for legacy configuration schemas.

## Scope

Included:

- Add a `ConfigImportService` in `TallyDbLoader.Core.Data`.
- Accept a JSON string and an explicit `ImportDecision` object.
- Perform pre-transaction validation: format check, schema version check, JSON validation, duplicate source ID check, and database reference validation.
- Detect conflicts by:
  - Database Profile: normalized name.
  - Company Profile: `tally_guid` (when present) or normalized name.
- Accept caller-supplied passwords for imported database profiles that require them.
- Expose a repository-level transactional import method in `IConfigRepository`: `ImportSanitizedConfig`.
- Encapsulate DPAPI password encryption inside `ConfigRepository`.
- Remap temporary source database profile IDs to newly created local IDs during import.
- Default imported company profiles to `enabled = false` and `status = "review_required"`, unless the import decision explicitly preserves runnable state.
- Write a single `import_sanitized_config` summary row to `config_audit_log` inside the SQLite transaction.
- Fast local unit and integration tests.

Excluded:

- WPF UI integration (file pickers, import wizard screens).
- Importing password ciphertexts or migrating DPAPI secrets from external systems.
- Legacy configuration migrations beyond schema version 1.
- Automatic password generation or scanning.

## Conflict Resolution & Identity Rules

Conflicts are identified before writing to the database using the following keys:

### 1. Database Profiles
- **Matching Key:** Normalized name (trimmed, case-insensitive).
- **Conflict Handling:**
  - If a name matches an existing profile, it is a conflict. The import decision must specify whether to `Overwrite` the existing database profile or `Skip` it.
  - If it is a new profile, it is created.

### 2. Company Profiles (Sync Jobs)
- **Matching Key:** `tally_guid` (if present in both); otherwise, normalized name (trimmed, case-insensitive).
- **Conflict Handling:**
  - If both `tally_guid` and normalized name are present and contradict an existing record (e.g., name matches but GUID differs, or GUID matches but name differs), the import must be blocked as ambiguous.
  - For simple conflicts (GUID match or name match), the import decision must specify `Overwrite` or `Skip`.

## Import Decision Model

To resolve conflicts and supply passwords, callers pass an `ImportDecision` object:

```csharp
public class ImportDecision
{
    public Dictionary<string, string> DatabasePasswords { get; set; } = new();
    public Dictionary<string, ConflictResolutionStrategy> DatabaseConflicts { get; set; } = new();
    public Dictionary<string, ConflictResolutionStrategy> CompanyConflicts { get; set; } = new();
    public bool PreserveRunnableState { get; set; } = false;
}

public enum ConflictResolutionStrategy
{
    Skip,
    Overwrite
}
```

## Validation (Pre-Transaction)

The import service must validate the entire payload and decision object before opening any transaction or performing any writes. If validation fails, it throws a `ValidationException` detailing all errors:

1. **Envelope Validation:**
   - Verify `format` is exactly `"tally-db-loader.config-export"`.
   - Verify `schema_version` is supported (exactly `1` in this slice).
   - Verify `application_version` is parseable.
2. **Structural Validation:**
   - Verify JSON is well-formed.
   - Detect duplicate source IDs within the export payload.
   - Verify all `db_profile_id` references in company profiles map to valid database profiles present in the export payload or existing in the database.
3. **Decision & Password Validation:**
   - For every database profile in the payload where `has_password = true` and the profile is new or being overwritten, a password must be provided in `DatabasePasswords`.
   - All detected conflicts must have a matching resolution strategy in `DatabaseConflicts` or `CompanyConflicts`.
   - Any ambiguous conflicts must fail validation.

## Data Persistence & Repository Contract

To support transactional integrity, the repository contract is expanded.

### Interface Changes

Add to `IConfigRepository`:

```csharp
void ImportSanitizedConfig(
    List<DatabaseProfile> databaseProfiles,
    List<CompanyProfile> companyProfiles,
    string actor,
    string reason,
    string beforeJson,
    string afterJson);
```

### Encapsulation of DPAPI
`ConfigRepository` implements `ImportSanitizedConfig` inside a single SQLite transaction:

1. For each new or overwritten database profile, encrypt the caller-supplied password using the existing private `EncryptPassword` helper.
2. Remap transient database profile IDs:
   - For new database profiles, insert them, retrieve the new autoincremented ID, and map the temporary source ID to this new ID.
   - For existing/overwritten database profiles, update them and map their temporary ID to the existing ID.
3. Map company profiles' `DbProfileId` using the remapped IDs.
4. For company profiles:
   - If `PreserveRunnableState` is `false`, set `Enabled = false` and `Status = "review_required"`.
   - If `PreserveRunnableState` is `true`, map `Enabled` and `Status` from the export payload.
   - Insert new company profiles or update existing ones.
5. Write a single `import_sanitized_config` audit row using `InsertConfigAuditLog`.
6. Commit the transaction. If any step fails, roll back everything.

## Auditing

A single audit log entry is written for the entire import operation:

- **Entity Type:** `"config"`
- **Entity ID:** `0`
- **Entity Name:** `"sanitized_import"`
- **Action:** `"import_sanitized_config"`
- **Actor:** Caller-provided actor (defaults to `"system"` if not provided by UI).
- **Audit Payloads:**
  - `before_json` and `after_json` must contain a compact, secret-free summary of the imported configuration state. Do not serialize full profiles or credentials.
  - Payload shape:
    ```json
    {
      "database_profiles": [
        { "name": "DB1", "action": "created" },
        { "name": "DB2", "action": "overwritten" }
      ],
      "company_profiles": [
        { "name": "Company A", "action": "created", "enabled": false, "status": "review_required" }
      ]
    }
    ```

## Testing

Add unit and integration tests in `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`:

- **Envelope Validation Tests:** Reject invalid formats, newer schemas, and corrupt JSON.
- **Conflict Resolution Tests:**
  - Test name-matching conflict behavior for database profiles.
  - Test GUID/name-matching conflict behavior for company profiles.
  - Verify ambiguous conflicts fail before transaction starts.
- **Credential Safety Tests:**
  - Verify missing passwords for profiles with `has_password = true` fail validation.
  - Verify imported passwords are encrypted with DPAPI and saved, and never leak into audit logs.
- **Safety State Tests:** Verify company profiles default to `enabled = false` and `status = "review_required"` unless explicitly overridden.
- **SQLite Transaction Test:** Verify a failure during company profile import rolls back database profile insertions.

## Success Criteria

1. `ConfigImportService` is implemented in `TallyDbLoader.Core.Data`.
2. DPAPI password encryption remains private within `ConfigRepository`.
3. Importing a config with a database profile and company profile is atomic.
4. The entire import writes exactly one audit log row with `action = "import_sanitized_config"`.
5. No passwords or DPAPI ciphertexts are serialized to the audit log.
6. All tests pass successfully.
