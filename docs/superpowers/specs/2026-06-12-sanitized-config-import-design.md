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
- Always default imported company profiles to `enabled = false` and `status = "review_required"`.
- Write a single `import_sanitized_config` summary row to `config_audit_log` inside the SQLite transaction.
- Fast local unit and integration tests.

Excluded:

- WPF UI integration (file pickers, import wizard screens).
- Importing password ciphertexts or migrating DPAPI secrets from external systems.
- Legacy configuration migrations beyond schema version 1.
- Automatic password generation or scanning.
- Runnable state preservation (all imported company profiles are disabled + review_required).

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

To resolve conflicts and supply passwords, callers pass an `ImportDecision` object. Dictionaries are keyed by transient source IDs (`int`) from the exported payload to ensure stable and unambiguous matching:

```csharp
public class ImportDecision
{
    // Key is the exported Database Profile ID (from payload.database_profiles[].id)
    public Dictionary<int, string> DatabasePasswords { get; set; } = new();
    
    // Key is the exported Database Profile ID (from payload.database_profiles[].id)
    public Dictionary<int, ConflictResolutionStrategy> DatabaseConflicts { get; set; } = new();
    
    // Key is the exported Company Profile ID (from payload.company_profiles[].id)
    public Dictionary<int, ConflictResolutionStrategy> CompanyConflicts { get; set; } = new();
}

public enum ConflictResolutionStrategy
{
    Skip,
    Overwrite
}
```

## Validation (Pre-Transaction)

The import service must validate the entire payload and decision object before opening any transaction or performing any writes. If validation fails, it throws a custom `ConfigImportValidationException` holding all aggregated errors in an `Errors` collection:

1. **Envelope Validation:**
   - Verify `format` is exactly `"tally-db-loader.config-export"`.
   - Verify `schema_version` is supported (exactly `1` in this slice).
   - Verify `application_version` is a non-empty string.
2. **Structural Validation:**
   - Verify JSON is well-formed.
   - Detect duplicate source IDs within the export payload.
   - Verify all `db_profile_id` references in company profiles map *only* to database profiles present in the export payload. A company profile cannot bind directly to an existing local database ID without matching a profile in the payload.
3. **Decision & Password Validation:**
   - For every database profile in the payload where `has_password = true` and the profile is new or being overwritten, a password must be provided in `DatabasePasswords`.
   - For an overwritten database profile:
     - If the source profile has `has_password = true`, the supplied password replaces the stored credential.
     - If the source profile has `has_password = false`, the existing local password is preserved on overwrite (the sanitized export cannot prove intent to delete credentials).
   - All detected conflicts must have a matching resolution strategy in `DatabaseConflicts` or `CompanyConflicts`.
   - Any ambiguous conflicts must fail validation.
   - If an imported company profile references an exported database profile that is skipped via `DatabaseConflicts`, the company profile itself must also be marked as `Skip` in `CompanyConflicts`. If the company is not skipped while its database profile is skipped, validation fails.

## Data Persistence & Repository Contract

To support transactional integrity and safe reference remapping, the repository contract is expanded using dedicated resolution models.

### Interface Changes

Add these resolved import models and repository method to the codebase:

```csharp
public enum ImportAction
{
    Create,
    Overwrite
}

public class ResolvedDatabaseProfileImport
{
    public int SourceId { get; set; }
    public int? ExistingLocalId { get; set; }
    public ImportAction Action { get; set; }
    public DatabaseProfile Profile { get; set; } = null!;
    public string? Password { get; set; }
    public bool PreserveExistingPassword { get; set; }
}

public class ResolvedCompanyProfileImport
{
    public int SourceId { get; set; }
    public int? ExistingLocalId { get; set; }
    public int SourceDbProfileId { get; set; }
    public ImportAction Action { get; set; }
    public CompanyProfile Profile { get; set; } = null!;
}
```

Add to `IConfigRepository`:

```csharp
void ImportSanitizedConfig(
    List<ResolvedDatabaseProfileImport> databaseProfiles,
    List<ResolvedCompanyProfileImport> companyProfiles,
    string actor,
    string reason,
    string beforeJson,
    string afterJson);
```

### Encapsulation of DPAPI
`ConfigRepository` implements `ImportSanitizedConfig` inside a single SQLite transaction. The method must assert that all resolved records are internally valid (e.g., matching database references, presence of required passwords unless preserved) and fail closed if called incorrectly.

1. For each database profile to import (Create or Overwrite):
   - If `Action == ImportAction.Create` or `Action == ImportAction.Overwrite` and `PreserveExistingPassword == false`, encrypt the supplied password using the existing private `EncryptPassword` helper.
   - If `Action == ImportAction.Overwrite` and `PreserveExistingPassword == true`, retrieve the password from the existing database record and preserve it.
2. Remap transient database profile IDs:
   - For `Create` database profiles, insert them, retrieve the new autoincremented ID, and map the temporary `SourceId` to this new ID.
   - For `Overwrite` database profiles, update the existing row and map the temporary `SourceId` to `ExistingLocalId`.
3. Map company profiles' `DbProfileId` using the remapped IDs.
4. For company profiles:
   - Always force `Enabled = false` and `Status = "review_required"`.
   - For `Create` company profiles, insert them.
   - For `Overwrite` company profiles, update the existing row using `ExistingLocalId`.
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

- **Envelope Validation Tests:** Reject invalid formats, newer schemas, and corrupt JSON. Treat `application_version` as an opaque string.
- **Conflict Resolution Tests:**
  - Test name-matching conflict behavior for database profiles.
  - Test GUID/name-matching conflict behavior for company profiles.
  - Verify ambiguous conflicts fail before transaction starts.
- **Credential Safety Tests:**
  - Verify missing passwords for profiles with `has_password = true` fail validation.
  - Verify imported passwords are encrypted with DPAPI and saved, and never leak into audit logs.
  - Verify that overwriting a database profile where `has_password = false` preserves the existing local password.
- **Safety State Tests:** Verify company profiles default to `enabled = false` and `status = "review_required"`.
- **SQLite Transaction and Remapping Tests:**
  - Verify that a validation failure or payload exception leaves profile counts and audit counts unchanged.
  - Verify successful import writes exactly one audit row.
  - Verify that a broken source `db_profile_id` cannot bind to a coincidentally matching local SQLite ID.
  - Verify that overwrite correctly remaps the source DB profile ID to the existing local DB profile ID for imported company profiles.
  - Verify that validation fails when an imported company profile references an exported database profile that is skipped, and the company profile itself is not skipped.
  - Verify a failure during company profile import rolls back database profile insertions.
  - Verify repository `ImportSanitizedConfig` asserts internal validity of its resolved records and fails closed if violated.

## Success Criteria

1. `ConfigImportService` is implemented in `TallyDbLoader.Core.Data`.
2. DPAPI password encryption remains private within `ConfigRepository`.
3. Importing a config with a database profile and company profile is atomic.
4. The entire import writes exactly one audit log row with `action = "import_sanitized_config"`.
5. No passwords or DPAPI ciphertexts are serialized to the audit log.
6. All tests pass successfully.
