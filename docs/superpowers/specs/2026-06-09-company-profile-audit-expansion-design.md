# CompanyProfile Audit Expansion Design

## Goal

Extend the proven `config_audit_log` mechanism to `CompanyProfile`, which is the current implementation name for a Sync Job Profile.

This slice audits user-visible Sync Job configuration changes while avoiding runtime lifecycle noise. It does not rename `CompanyProfile`, split the domain model, or change public repository method signatures.

## Scope

Included:

- Add audit rows for `SaveCompanyProfile` create operations.
- Add audit rows for `SaveCompanyProfile` update operations.
- Add audit rows for `DeleteCompanyProfile` delete operations.
- Keep all audit writes in the same SQLite transaction as the corresponding mutation.
- Use compact, configuration-only JSON snapshots.
- Add focused repository tests in `ConfigRepositoryTests`.

Excluded:

- WPF changes.
- Public `IConfigRepository` signature changes.
- DatabaseProfile auditing.
- TallySettings changes.
- Audit viewer UI.
- Audit retention or purge logic.
- Import/export audit behavior.
- Auditing cascaded child rows such as `sync_runs`.
- Auditing runtime lifecycle methods such as `TryStartCompanyProfile`, `MarkCompanyProfileUnknown`, or `CompleteCompanyProfileRun`.

## Audit Helper

Use the existing private `InsertConfigAuditLog` helper in `ConfigRepository`.

Do not introduce a new public audit service or interface in this slice.

## Snapshot Fields

CompanyProfile audit snapshots must contain exactly these fields:

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

Snapshots must exclude:

```text
status
last_run_at
last_duration_ms
last_rows_written
error_count_24h
db
```

Reason: `status` and the `last_*`/error fields are runtime lifecycle state. They are already controlled by dedicated sync lifecycle methods and safety-state resolution. This slice audits configuration changes only.

Snapshot JSON must use lowercase snake_case property names matching the list above. Do not serialize the full `CompanyProfile` object.

## Actor and Reason

`SaveCompanyProfile` and `DeleteCompanyProfile` currently have no actor or reason parameters. To avoid public API and WPF changes in this slice, use fixed values:

```text
actor = "system"
reason = "Company profile created"
reason = "Company profile updated"
reason = "Company profile deleted"
```

Known debt: operator attribution for company profile changes requires a future repository/API signature change that passes actor context from the UI or caller into Core.

## Create Flow

When `SaveCompanyProfile` receives `company.Id == 0`:

1. Begin SQLite transaction.
2. Normalize the persisted `status` exactly as the current method does.
3. Insert the company profile row using the existing column set.
4. Read `SELECT last_insert_rowid()` on the same connection.
5. Use the generated ID for `entity_id` and for the `id` property in `after_json`.
6. Use `before_json = "{}"`.
7. Build `after_json` from the inserted configuration values only.
8. Insert audit row:
   - `action = "create_company_profile"`
   - `entity_type = "company_profile"`
   - `entity_id = generated id`
   - `entity_name = company.Name`
9. Commit.

Do not change the public `SaveCompanyProfile` return type. The generated ID is needed only inside the transaction for audit logging.

If the audit insert fails, roll back the company row insert.

## Update Flow

When `SaveCompanyProfile` receives `company.Id != 0`:

1. Begin SQLite transaction.
2. Load the current company profile row by ID using only the snapshot fields.
3. If no row exists, throw `InvalidOperationException`.
4. Build `before_json` from the loaded row.
5. Update the company profile using the existing column set.
6. Assert exactly one row was updated. If not, throw `InvalidOperationException`.
7. Build `after_json` from the submitted configuration values only.
8. Insert audit row:
   - `action = "update_company_profile"`
   - `entity_type = "company_profile"`
   - `entity_id = company.Id`
   - `entity_name = company.Name`
9. Commit.

If the audit insert fails, roll back the update.

## Delete Flow

When `DeleteCompanyProfile(id)` is called:

1. Begin SQLite transaction.
2. Load the current company profile row by ID using only the snapshot fields.
3. If no row exists, throw `InvalidOperationException`.
4. Build `before_json` from the loaded row.
5. Delete the company profile row.
6. Assert exactly one row was deleted. If not, throw `InvalidOperationException`.
7. Use `after_json = "{}"`.
8. Insert audit row:
   - `action = "delete_company_profile"`
   - `entity_type = "company_profile"`
   - `entity_id = id`
   - `entity_name = loaded.Name`
9. Commit.

If the audit insert fails, roll back the deletion.

Do not audit cascaded child rows. If SQLite cascade behavior deletes related rows, this slice records only the explicit `company_profiles` deletion.

## Error Handling

Use `InvalidOperationException` for:

- updating a missing company profile;
- deleting a missing company profile;
- update/delete affected-row count not equal to one;
- audit insert failure, via the existing helper.

Preserve the existing rollback-on-exception behavior.

## Testing

Add tests in `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`.

Required coverage:

- Creating a company profile writes one `create_company_profile` audit row.
- Create audit uses the generated company profile ID in both `entity_id` and `after_json.id`.
- Updating a company profile writes one `update_company_profile` audit row with correct `before_json` and `after_json`.
- Deleting a company profile writes one `delete_company_profile` audit row and removes the row.
- Delete audit uses `after_json = "{}"`.
- If `config_audit_log` is missing, create rolls back.
- If `config_audit_log` is missing, update rolls back.
- If `config_audit_log` is missing, delete rolls back.
- Updating a missing company profile throws `InvalidOperationException`.
- Deleting a missing company profile throws `InvalidOperationException`.
- Snapshot JSON contains exactly the allowed fields.
- Snapshot JSON excludes runtime fields: `status`, `last_run_at`, `last_duration_ms`, `last_rows_written`, `error_count_24h`, and `db`.

## Success Criteria

- `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes.
- No WPF files are changed.
- No public repository interfaces are changed.
- `SaveCompanyProfile` and `DeleteCompanyProfile` cannot commit without corresponding audit rows.
- CompanyProfile audit snapshots include configuration fields only.
- Runtime lifecycle fields are not written to CompanyProfile audit JSON.
