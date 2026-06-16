# WPF Sanitized Config Import UI Design

## Purpose

Add a thin WPF entry point for importing sanitized configuration JSON files produced by the existing export tool.

This slice is create-new only. If the import payload conflicts with existing database profiles or company profiles, WPF blocks the import and tells the user conflict resolution is not supported in this version.

## Scope

### Included

- Add `Import Sanitized Config` to the existing Settings page export/support card.
- Add a Core preview method so WPF can inspect import contents without duplicating private JSON envelope classes.
- Prompt for required database passwords using WPF `PasswordBox` controls.
- Call the existing Core import service with a local `ImportDecision`.
- Block import while the sync engine is running.
- Show success, cancellation, and validation/error feedback through existing toast patterns.

### Excluded

- Conflict resolution UI.
- Overwrite, skip, rename, or merge actions.
- Importing DPAPI ciphertext.
- Legacy schema migrators beyond the existing schema version 1 behavior.
- Tray menu import actions.
- Audit viewer or retention UI.

## UI Placement

The Settings page already has a `Configuration & Support Exports` card containing:

- `Export Sanitized Config`
- `Create Diagnostic Backup`

Add `Import Sanitized Config` to the same horizontal button row. The import button is a configuration mutation and must be blocked while `IsSyncRunning` is true. The command handler must also call the existing engine guard helper so the rule is enforced even if the button state is missed.

The button tooltip should make the safety rule explicit:

```text
Import sanitized configuration. Stop the engine before importing.
```

## Core Preview API

`ConfigImportService` currently exposes only:

```csharp
void ImportJson(string json, ImportDecision decision, string actor, string reason)
```

WPF must not duplicate the private export envelope classes inside `ConfigImportService`. Add a public preview method:

```csharp
ConfigImportPreview PreviewJson(string json)
```

Preview must parse and validate enough of the JSON to drive the UI, but it must not write anything.

Suggested models:

```csharp
public sealed class ConfigImportPreview
{
    public IReadOnlyList<ConfigImportPreviewDatabaseProfile> DatabaseProfiles { get; init; } = Array.Empty<ConfigImportPreviewDatabaseProfile>();
    public IReadOnlyList<ConfigImportPreviewCompanyProfile> CompanyProfiles { get; init; } = Array.Empty<ConfigImportPreviewCompanyProfile>();
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    public bool HasConflicts { get; init; }
    public bool IsValid => ValidationErrors.Count == 0;
}

public sealed class ConfigImportPreviewDatabaseProfile
{
    public int SourceId { get; init; }
    public string Name { get; init; } = "";
    public bool HasPassword { get; init; }
    public bool HasConflict { get; init; }
}

public sealed class ConfigImportPreviewCompanyProfile
{
    public int SourceId { get; init; }
    public string Name { get; init; } = "";
    public bool HasConflict { get; init; }
}
```

Conflict detection must use the same identity rules as `ImportJson`:

- database profiles by normalized name
- company profiles by Tally GUID when present, otherwise normalized name
- ambiguous company profile matches count as conflicts/errors

Preview errors should be user-displayable, but `ImportJson` remains the final authority. The command still handles `ConfigImportValidationException` from import execution.

## WPF Command Flow

Add `ImportSanitizedConfigCommand` to `MainViewModel`.

Add these injectable delegates:

```csharp
public Func<string, string?>? OpenFileDialogHandler { get; set; }
public Func<ConfigImportPreview, Dictionary<int, string>?>? PasswordPromptHandler { get; set; }
```

`OpenFileDialogHandler` returns the selected JSON file path or `null` on cancellation.

`PasswordPromptHandler` receives the Core preview and returns a dictionary keyed by exported database profile source ID. It returns `null` when the user cancels.

Flow:

1. User clicks `Import Sanitized Config`.
2. Command calls `GuardEngineRunning("ImportSanitizedConfig")`; if blocked, exit.
3. Open file dialog selects a JSON file.
4. If cancelled, exit silently with no success toast.
5. Read the file text.
6. Call `ConfigImportService.PreviewJson(json)`.
7. If preview has validation errors, show an error toast and do not import.
8. If preview has conflicts, show a warning/error toast explaining create-new-only import blocks conflicts.
9. Prompt for passwords for preview database profiles with `HasPassword = true`.
10. If the password prompt is cancelled, exit silently with no success toast.
11. If any required password is missing or empty, show a validation toast and do not call import.
12. Create a local `ImportDecision` with only `DatabasePasswords` populated.
13. Call `ConfigImportService.ImportJson(json, decision, GetActorName(), reason)`.
14. Reload configuration through `LoadConfiguration()`.
15. Show a success toast summarizing imported profile counts.

Reason:

```text
User imported sanitized configuration from WPF settings
```

## Password Prompt Window

Add a small modal WPF window for password collection.

Rules:

- Use `PasswordBox`, not plain `TextBox`.
- Display only database profile names and password fields.
- Require non-empty passwords for all rows shown.
- Return a `Dictionary<int, string>` only when the user submits a complete form.
- Return `null` on cancellation.
- Do not store passwords in `MainViewModel` fields or bind them into long-lived view-model state.

Implementation may use a simple code-behind window rather than a new full MVVM flow. The password dictionary and `ImportDecision` must be local variables inside command execution.

## Error Handling

Handle `ConfigImportValidationException` separately from generic exceptions.

For conflicts, show a user-facing message like:

```text
Import blocked: this version only supports new profiles. Rename or remove conflicting profiles before importing.
```

For validation errors, show the first error in the toast and log or summarize the remaining count if needed.

Generic exceptions show an error toast and do not claim success.

## Safety Rules

- Import mutates local SQLite configuration, so it is blocked while the sync engine is running.
- WPF does not provide conflict strategies in this slice.
- Imported company profiles remain disabled and `review_required` through existing Core behavior.
- Cleartext passwords exist only in the password prompt result dictionary and local import decision during command execution.
- No password should be written to logs, toasts, audit JSON, or long-lived view-model properties.

## Tests

Add Core tests for `PreviewJson`:

- invalid JSON returns validation errors or throws the same validation exception style chosen for preview
- valid create-new payload returns profile names, source IDs, and `HasPassword`
- database profile name conflict sets `HasConflicts`
- company profile conflict sets `HasConflicts`
- missing `has_password` is reported as validation error

Add WPF tests in `MainViewModelTests`:

- file dialog cancellation exits without parsing/importing and without success toast
- invalid JSON shows an error toast
- conflict payload shows create-new-only conflict warning and does not import
- missing required password blocks import before calling `ImportJson`
- password prompt cancellation exits without import and without success toast
- successful create-new import writes profiles, reloads collections, and shows success toast
- command is blocked while `State = EngineState.Running`

Tests should inject `OpenFileDialogHandler` and `PasswordPromptHandler`. They must not open real dialogs.

## Verification

Run:

```powershell
dotnet test tests\TallyDbLoader.Tests\TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigImportServiceTests|FullyQualifiedName~MainViewModelTests"
dotnet test tests\TallyDbLoader.Tests\TallyDbLoader.Tests.csproj --no-restore
git diff --check
```

## Open Decisions

None. Conflict resolution is explicitly deferred.
