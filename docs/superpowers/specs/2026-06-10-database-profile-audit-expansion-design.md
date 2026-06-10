# DatabaseProfile Audit Expansion Design

## Goal

Extend the proven `config_audit_log` mechanism to `DatabaseProfile`.

This slice audits user-visible Database Profile configuration changes while ensuring that sensitive credentials (passwords or encrypted DPAPI data) are never written to audit logs. It does not change public repository method signatures or WPF files.

## Scope

Included:

- Add audit rows for `SaveDatabaseProfile` create operations.
- Add audit rows for `SaveDatabaseProfile` update operations.
- Add audit rows for `DeleteDatabaseProfile` delete operations.
- Keep all audit writes in the same SQLite transaction as the corresponding mutation.
- Use compact, credential-safe JSON snapshots (with `has_password` boolean flag instead of actual password strings).
- Add focused repository tests in `ConfigRepositoryTests`.

Excluded:

- WPF changes.
- Public `IConfigRepository` signature changes.
- CompanyProfile or TallySettings changes.
- Audit viewer UI.
- Audit retention or purge logic.

## Audit Helper

Use the existing private `InsertConfigAuditLog` helper in `ConfigRepository`.

Do not introduce a new public audit service or interface in this slice.

## Snapshot Fields

DatabaseProfile audit snapshots must contain exactly these fields:

```text
id
name
technology
server
port
username
has_password
```

Snapshots must exclude:

```text
password
last_test_result
last_tested_at
used_by_count
```

Reason: Storing `password` (even encrypted with DPAPI) poses security risks and violates the zero-secret audit policy. `last_test_result` and `last_tested_at` are diagnostic/runtime fields, not durable configuration intent, and excluding them prevents log spam during connection testing. `used_by_count` is a transient derived count and is excluded.

Snapshot JSON must use lowercase snake_case property names matching the list above. Do not serialize the full `DatabaseProfile` object.

When a flow needs an empty side of the audit snapshot, use the literal string `"{}"`. Do not use `null` or an empty string because `InsertConfigAuditLog` rejects null or whitespace JSON values.

## has_password Credential Snapshot Rule

To avoid exposing credentials in the database:
- `before.has_password` is computed from the loaded database row's stored password column: `!string.IsNullOrWhiteSpace(loaded.Password)`.
- `after.has_password` is computed after applying the same encryption/normalization path used for saving: `!string.IsNullOrWhiteSpace(encryptedPassword)`.

Do not decrypt the password for audit logic. This keeps the audit semantics tied to what is actually persisted while keeping the audit log entirely secret-free.

## Actor and Reason

`SaveDatabaseProfile` and `DeleteDatabaseProfile` currently have no actor or reason parameters. To avoid public API and WPF changes in this slice, use fixed values:

```text
actor = "system"
reason = "Database profile created"
reason = "Database profile updated"
reason = "Database profile deleted"
```

Known debt: operator attribution for database profile changes requires a future repository/API signature change that passes actor context from the UI or caller into Core.

## Create Flow

When `SaveDatabaseProfile` receives `profile.Id == 0`:

1. Begin SQLite transaction.
2. Encrypt/normalize password first via `EncryptPassword`. Store this in `encryptedPassword`.
3. Insert the database profile row using the existing column set.
4. Read `SELECT last_insert_rowid()` on the same connection.
5. Use the generated ID for `entity_id` and for the `id` property in `after_json`.
   - *Note on entity_id width:* The generated row ID is a 64-bit integer, but `DatabaseProfile.Id` and the `entity_id` parameter of the audit helper are 32-bit `int` types. The ID is narrowed to `int` to match the model identity types. Widen the model and helper if IDs can exceed `int.MaxValue`.
6. Use the literal `before_json = "{}"`.
7. Build `after_json` from the submitted non-secret fields, setting `has_password = !string.IsNullOrWhiteSpace(encryptedPassword)`.
8. Insert audit row:
   - `action = "create_database_profile"`
   - `entity_type = "database_profile"`
   - `entity_id = (int)generated id`
   - `entity_name = profile.Name`
9. Commit.

If the audit insert fails, roll back the insert.

## Update Flow

When `SaveDatabaseProfile` receives `profile.Id != 0`:

1. Begin SQLite transaction.
2. Load the current database profile row by ID using a hand-rolled projection of only the snapshot fields and the password column.
3. If no row exists, throw `InvalidOperationException`.
4. Build `before_json` from the loaded row, setting `has_password = !string.IsNullOrWhiteSpace(loaded.Password)`.
5. Encrypt/normalize password first via `EncryptPassword`. Store this in `encryptedPassword`.
6. Update the database profile row using the existing column set.
7. Assert exactly one row was updated. If not, throw `InvalidOperationException`.
8. Build `after_json` from the submitted `DatabaseProfile` object (without querying database again), setting `has_password = !string.IsNullOrWhiteSpace(encryptedPassword)`.
9. Insert audit row:
   - `action = "update_database_profile"`
   - `entity_type = "database_profile"`
   - `entity_id = profile.Id`
   - `entity_name = profile.Name`
     - *Note on entity_name:* Storing the submitted name (`profile.Name`) ensures that if a database profile is renamed, the outer audit row links to the new identifier, while `before_json` captures the previous configuration name. This is consistent with `CompanyProfile` auditing. Do not change this to use `loaded.Name`.
10. Commit.

If the audit insert fails, roll back the update.

## Delete Flow

When `DeleteDatabaseProfile(id)` is called:

1. Begin SQLite transaction.
2. Load the current database profile row by ID using a hand-rolled projection of snapshot fields and the password column.
3. If no row exists, throw `InvalidOperationException`.
4. Build `before_json` from the loaded row, setting `has_password = !string.IsNullOrWhiteSpace(loaded.Password)`.
5. Delete the database profile row.
6. Assert exactly one row was deleted. If not, throw `InvalidOperationException`.
7. Use the literal `after_json = "{}"`.
8. Insert audit row:
   - `action = "delete_database_profile"`
   - `entity_type = "database_profile"`
   - `entity_id = id`
   - `entity_name = loaded.Name`
9. Commit.

If the audit insert fails, roll back the deletion.

## Snapshot Projection

Use this column projection when loading an existing database profile for update/delete audit snapshots:

```sql
SELECT
    id AS Id,
    name AS Name,
    technology AS Technology,
    server AS Server,
    port AS Port,
    username AS Username,
    password AS Password
FROM database_profiles
WHERE id = @Id;
```

This query intentionally omits `last_test_result` and `last_tested_at`.

The `AS PascalCase` aliases are for Dapper to populate `DatabaseProfile` properties. JSON serialization must then use explicit anonymous objects with snake_case keys. Do not pass the Dapper-mapped `DatabaseProfile` object directly to `JsonSerializer.Serialize`.

## Error Handling

Use `InvalidOperationException` for:

- updating a missing database profile;
- deleting a missing database profile;
- update/delete affected-row count not equal to one;
- audit insert failure, via the existing helper.

Preserve the existing rollback-on-exception behavior.

## Testing

Add tests in `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`.

Required coverage:

- Creating a database profile writes one `create_database_profile` audit row.
- Create audit uses the generated database profile ID in both `entity_id` and `after_json.id`.
- Updating a database profile writes one `update_database_profile` audit row with correct `before_json` and `after_json`.
- Updating a database profile proves `before_json` reflects the pre-mutation database state, not the submitted object.
- Deleting a database profile writes one `delete_database_profile` audit row and removes the row.
- Delete audit uses `after_json = "{}"`.
- If `config_audit_log` is missing, create rolls back.
- If `config_audit_log` is missing, update rolls back.
- If `config_audit_log` is missing, delete rolls back.
- Updating a missing database profile throws `InvalidOperationException`.
- Deleting a missing database profile throws `InvalidOperationException`.
- Snapshot JSON contains exactly the allowed fields.
- Snapshot JSON excludes: `password`, `last_test_result`, `last_tested_at`, and `used_by_count`.
- Asserting the `has_password` transition from true to false: Create a database profile with a password, then update that same profile with an empty password. Assert that `create.after_json.has_password == true`, `update.before_json.has_password == true`, and `update.after_json.has_password == false`. Also assert that neither the plaintext password nor any `dpapi:` encrypted string appears in the JSON payloads.
- Asserting all required audit row metadata fields for create, update, and delete actions: Verify that `actor`, `action`, `entity_type`, `entity_id`, `entity_name`, and `reason` match the specified designs exactly.

## Success Criteria

- `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` passes.
- No WPF files are changed.
- No public repository interfaces are changed.
- `SaveDatabaseProfile` and `DeleteDatabaseProfile` cannot commit without corresponding audit rows.
- DatabaseProfile audit snapshots include configuration fields only, replacing `password` with the boolean `has_password`.
