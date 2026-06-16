# WPF Export Tools Wiring Design

## Purpose

Expose the already-implemented Core export tools from the WPF Settings page:

- Sanitized configuration export.
- Diagnostic backup ZIP export.

This slice is intentionally UI wiring only. It does not implement sanitized import UI, audit viewing, retention policies, tray menu actions, or new Core backup/export behavior.

## Scope

### Included

- Add Settings page controls for sanitized config export and diagnostic backup export.
- Add `MainViewModel` commands:
  - `ExportSanitizedConfigCommand`
  - `CreateDiagnosticBackupCommand`
- Use `ConfigExportService` for JSON generation.
- Use `DiagnosticBackupService` for ZIP creation and audit logging.
- Add small injectable dialog/prompt delegates so command paths can be unit tested.
- Run diagnostic backup creation off the UI thread and marshal feedback back through the existing dispatcher helper.

### Excluded

- Sanitized config import UI.
- Tray menu entries for backup/export.
- New navigation pages.
- New Core service behavior.
- Formatting-baseline cleanup.

## UI Placement

Both actions live in `SettingsPage.xaml` under a new card titled `Configuration & Support Exports`.

The card contains:

- `Export Sanitized Config` button.
- `Create Diagnostic Backup` button.

The UI copy must make the boundary clear:

- Sanitized config export omits database passwords.
- Diagnostic backup may include encrypted local configuration and can optionally include raw XML diagnostics.

Raw XML inclusion must remain opt-in. The first implementation may use a confirmation prompt rather than a dedicated checkbox dialog.

## View-Model Design

`MainViewModel` remains the command owner. It will expose these injectable delegates:

```csharp
public Func<string, string, string?>? SaveFileDialogHandler { get; set; }
public Func<string?>? FolderBrowserDialogHandler { get; set; }
public Func<string, string, bool>? ConfirmationPromptHandler { get; set; }
```

The delegates represent:

- Save file path selection for sanitized config export.
- Output folder selection for diagnostic backup export.
- Explicit yes/no prompt for including raw XML diagnostics.

Production handlers are assigned from `MainWindow`, where WPF and WinForms dialog dependencies already belong. Unit tests can set the delegates directly and run with `DisableDispatcher = true`.

`ExportLog` may keep its existing direct dialog usage for now unless the implementation naturally reuses `SaveFileDialogHandler` without broad refactoring. This slice must not turn into a full dialog abstraction cleanup.

## Actor Resolution

`ResolveSafetyBlock` currently resolves the audit actor inline. Extract that logic into a private `GetActorName()` helper on `MainViewModel`.

The helper order is:

1. `WindowsIdentity.GetCurrent()?.Name`
2. `Environment.UserName`
3. `"unknown"`

Both `ResolveSafetyBlock` and diagnostic backup export use this helper. Exceptions from actor lookup are swallowed and fall through to the next source.

## Sanitized Config Export Flow

1. User clicks `Export Sanitized Config`.
2. View model asks `SaveFileDialogHandler` for a path.
3. If the handler returns `null` or whitespace, the command exits silently with no success toast.
4. View model creates `ConfigExportService(_repo, applicationVersion)`.
5. It calls `ExportJson(DateTimeOffset.Now)`.
6. It writes the JSON to the selected path.
7. It shows a success toast with the target file name.
8. Exceptions show an error toast and do not claim success.

Default filename:

```text
tally-sync-config.json
```

Default filter:

```text
JSON Files (*.json)|*.json|All Files (*.*)|*.*
```

The application version should come from the WPF assembly informational version when available, falling back to `"dev"`.

## Diagnostic Backup Export Flow

1. User clicks `Create Diagnostic Backup`.
2. View model asks `FolderBrowserDialogHandler` for an output directory.
3. If the handler returns `null` or whitespace, the command exits silently with no success toast.
4. View model asks `ConfirmationPromptHandler` whether to include raw XML diagnostics.
5. View model builds a `DiagnosticBackupRequest`.
6. It runs `DiagnosticBackupService.CreateBackup(request)` inside `Task.Run`.
7. Completion feedback is marshalled through `InvokeOnDispatcher`.
8. Success toast includes the generated ZIP file name.
9. Exceptions show an error toast and do not claim success.

Request fields:

- `ConfigDatabasePath`: the database path passed to `MainViewModel` constructor.
- `OutputDirectoryPath`: selected folder.
- `ApplicationVersion`: same helper used by sanitized config export.
- `Actor`: `GetActorName()`.
- `Reason`: `"User requested diagnostic backup from WPF settings"`.
- `CreatedAt`: `DateTimeOffset.Now`.
- `LogDirectoryPath`: app-relative log directory if present; otherwise empty string.
- `RawXmlDirectoryPath`: app-relative raw XML diagnostics directory if present.
- `IncludeRawXml`: user confirmation result, but only true if the raw XML directory exists.

If the user asks to include raw XML but no raw XML directory exists, the command shows a warning toast and proceeds with `IncludeRawXml = false`. It must not call `DiagnosticBackupService` with `IncludeRawXml = true` and a missing raw XML directory.

The log directory may be missing. `DiagnosticBackupService` already tolerates a missing log directory when passed an empty or non-existent path, so the view model should not fail preemptively.

## Path Resolution

The view model stores the constructor `dbPath` in a private field for later backup requests.

App-relative paths are resolved from:

```csharp
AppDomain.CurrentDomain.BaseDirectory
```

Phase 1 defaults:

- logs: `<baseDir>/logs`
- raw XML diagnostics: `<baseDir>/raw_xml`

If future configuration introduces explicit paths, this slice can be extended without changing the Core service contract.

## Threading

Sanitized config export is expected to be small and may run synchronously.

Diagnostic backup creation must run on a background thread because it can copy the SQLite database and package log files. Use `Task.Run(..., _asyncOpsCts.Token)` following existing async command patterns in `MainViewModel`.

All toast updates and observable collection changes must go through `InvokeOnDispatcher`.

## Safety Rules

These export actions are read/export operations, so they are allowed while the sync engine is running.

Do not call `GuardEngineRunning` from either command.

Sanitized config export must never include database password plaintext or DPAPI ciphertext. That remains enforced by `ConfigExportService`.

Diagnostic backup intentionally includes a live SQLite database copy. Credentials remain DPAPI-encrypted in that database copy by Core design.

## Testing Requirements

Add focused tests in `MainViewModelTests.cs`.

Required cases:

- Sanitized config export writes JSON to the selected path.
- Sanitized config export cancellation produces no file and no success assertion.
- Sanitized config export failure shows an error toast.
- Diagnostic backup cancellation does not create a ZIP and does not call success feedback.
- Diagnostic backup command creates a ZIP when folder selection succeeds.
- Diagnostic backup command uses `DisableDispatcher = true` safely in tests.
- Diagnostic backup command does not request raw XML from Core when the user confirms raw XML but the raw XML directory is missing.

Tests should inject dialog delegates rather than opening real dialogs. They should use temporary database and output directories, then call `SqliteConnection.ClearAllPools()` before cleanup where SQLite files are created.

## Verification

Run:

```powershell
dotnet test tests\TallyDbLoader.Tests\TallyDbLoader.Tests.csproj --no-restore
```

Run targeted tests if new filters are useful:

```powershell
dotnet test tests\TallyDbLoader.Tests\TallyDbLoader.Tests.csproj --no-build --filter "FullyQualifiedName~MainViewModelTests"
```

Do not use repo-wide `dotnet format --verify-no-changes` as a blocking gate for this slice until the formatting baseline cleanup is completed.

## Open Decisions

None. Sanitized import UI, tray shortcuts, and dedicated support pages are explicitly deferred.
