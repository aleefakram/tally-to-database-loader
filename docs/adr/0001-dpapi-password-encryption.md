# DPAPI Password Encryption and Migration Bridge

We decided to encrypt all database profile passwords persisted in SQLite using the Windows Data Protection API (DPAPI). Encrypted values are tagged with a `dpapi:` prefix, allowing legacy plain-text credentials to be loaded seamlessly and automatically migrated to encrypted format on the next save.

## Context

To secure stored database passwords in SQLite, the application must encrypt credentials before database persistence. However, existing installations might contain legacy database profiles with plain-text passwords.

We wanted to:
1. Enforce strong, user-level DPAPI encryption on Windows.
2. Avoid a breaking database migration or manual reconfiguration step.
3. Decouple password encryption details from the UI/ViewModel layer.

## Decision

1. **DPAPI Encryption**: We will encrypt passwords in `ConfigRepository.SaveDatabaseProfile` using `ProtectedData.Protect` under `DataProtectionScope.CurrentUser`.
2. **Encrypted Value Tagging**: The base64-encoded encrypted bytes will be stored in the database prefixed with `dpapi:`.
3. **Migration Bridge**: When reading database profiles, any password string starting with `dpapi:` is automatically decrypted. Any password without the prefix is returned as legacy plain-text.
4. **On-Save Migration**: Since the repository read methods return plain-text, saving or updating the profile automatically writes it back encrypted with the `dpapi:` prefix.

## Consequences

- Stored passwords are safe from raw inspection of the SQLite file.
- The UI and View-Model layer remain unaware of the encryption format.
- Backward compatibility is maintained for existing plain-text profiles.
