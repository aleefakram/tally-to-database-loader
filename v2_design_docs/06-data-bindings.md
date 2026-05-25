# §06 — Data Bindings

Maps every UI control on every screen to its view-model property, command, or repository method. This section is the canonical reference for AI agents wiring up XAML to C#.

Conventions used:

- **VM** = `MainViewModel` unless otherwise noted.
- **Repo** = `TallyDbLoader.Core.Data.ConfigRepository`.
- **Models** = `TallyDbLoader.Core.Models.*`.
- Two-way bindings noted explicitly. Default is one-way.

---

## Index

1. [View-model surface](#view-model-surface)
2. [Data models](#data-models)
3. [Repository methods](#repository-methods)
4. [Secrets handling](#secrets-handling)
5. [Bindings — per screen](#bindings--per-screen)

---

## View-model surface

`MainViewModel` exposes:

### Collections (`ObservableCollection<T>`)

| Property | Element type | Source |
|---|---|---|
| `Companies` | `CompanyProfile` | `Repo.GetAllCompanyProfiles()` |
| `DatabaseProfiles` | `DatabaseProfile` | `Repo.GetAllDatabaseProfiles()` |
| `RunHistory` | `SyncRun` | `Repo.GetRecentSyncRuns(50)` |
| `UnlinkedTallyCompanies` | `TallyCompanyInfo` | derived from `TallyClient.FetchActiveCompaniesDetailedAsync()` minus current `Companies` |

### Selections (two-way)

| Property | Type |
|---|---|
| `SelectedCompany` | `CompanyProfile?` |
| `SelectedDatabaseProfile` | `DatabaseProfile?` |
| `SelectedRun` | `SyncRun?` |

### Engine

| Property | Type | Notes |
|---|---|---|
| `EngineState` | enum `Idle \| Running \| Paused` | See [§05 engine](05-behavior.md#engine-state-machine). |
| `IsSyncRunning` | bool | Computed: `EngineState == Running`. Drives mutation guard. |
| `LogOutput` | string | Rolling buffer (last 500 lines). |
| `StatusText` | string | Single-line status for `StatusBar`. |

### Editor scratch state

The editor screens (Company profile, Databases) bind to scratch fields on the VM, not directly to the model — this lets the user discard pending edits.

| Property | Source model field | Two-way |
|---|---|---|
| `DbName`, `DbTech`, `DbServer`, `DbPort`, `DbUsername`, `DbPassword` | `DatabaseProfile.*` | yes |
| `JobCompany` (string), `JobSelectedProfile` (DatabaseProfile), `JobTargetCatalog`, `JobInterval`, `JobSyncMode`, `JobEntities` | `CompanyProfile.*` | yes |
| `TallyServer`, `TallyPort`, `TallyExePath`, `TallyIniPath`, `AutoStartTally` | `TallySettings.*` | yes |

### Form chrome

| Property | Used by |
|---|---|
| `DbFormHeader` (string) | DB editor card heading ("New connection" / "Edit prod-pg-01") |
| `DbSaveButtonText` | "Save profile" / "Update profile" |
| `JobFormHeader` | "New profile" / "Edit Citrine Foods" |
| `JobSaveButtonText` | "Save profile" / "Update profile" |
| `IsEditingDbProfileVisibility` | `Visibility` enum — controls visibility of Cancel button |
| `IsEditingJobVisibility` | same |
| `IsSyncNotRunning` | `!IsSyncRunning` — for `IsEnabled` bindings on form groups |

### Routing

| Property | Type | Notes |
|---|---|---|
| `Route` | `Route` | Current route. |
| `RouteStack` | `Stack<Route>` | History. |
| `CanGoBack` | bool | `RouteStack.Count > 1`. |

### Commands (`ICommand`)

| Command | Calls |
|---|---|
| `NavigateCommand(string id)`        | replaces route to `{ screen: id }` |
| `BackCommand`                       | pops one frame |
| `StartSyncEngineCommand`            | engine: idle → running |
| `PauseSyncEngineCommand`            | engine: running → paused |
| `ResumeSyncEngineCommand`           | engine: paused → running |
| `StopSyncEngineCommand`             | engine: running/paused → idle |
| `RunCompanyCommand(string id)`      | one-shot sync for company |
| `OpenCompanyPickerCommand`          | sets `IsCompanyPickerOpen = true` |
| `StartEditingCompanyCommand(id)`    | sets scratch fields + navigates to `company/{id}` |
| `SaveCompanyProfileCommand`         | persists scratch → repo (guarded) |
| `DeleteCompanyProfileCommand(id)`   | confirm → delete (guarded) |
| `StartEditingDbProfileCommand(id)`  | sets scratch + opens editor |
| `SaveDatabaseProfileCommand`        | persists (guarded) |
| `DeleteDatabaseProfileCommand(id)`  | confirm → delete (guarded, blocked if used) |
| `TestDatabaseConnectionCommand`     | runs transient open against scratch (guarded) |
| `SaveTallySettingsCommand`          | persists Tally settings (guarded) |
| `DetectActiveCompaniesCommand`      | calls `TallyClient.FetchActiveCompaniesDetailedAsync` and routes per [§04 picker rules](04-screens.md#9-company-picker-modal) |
| `RefreshCommand`                    | re-fetches from repo for current route |
| `ExportLogCommand`                  | save current `LogOutput` to file |
| `ClearLogCommand`                   | empties `LogOutput` |

---

## Data models

Location: `TallyDbLoader.Core.Models`.

### `CompanyProfile`

Replaces v1's `SyncJob`. 1:1 with a Tally company.

```csharp
public class CompanyProfile {
    public int Id { get; set; }
    public string Name { get; set; }          // matches Tally company name
    public string? TallyGuid { get; set; }    // GUID from Tally if known
    public int Consolidated { get; set; }
    public DateTime? BooksFrom { get; set; }
    public DateTime? BooksTo { get; set; }

    public int DbProfileId { get; set; }
    public DatabaseProfile? Db { get; set; }  // navigation

    public string TargetCatalog { get; set; }
    public string Schema { get; set; } = "public";
    public string TablePrefix { get; set; } = "tally_";

    public string Mode { get; set; }          // "Full" | "Incremental"
    public int IntervalMinutes { get; set; }
    public int Enabled { get; set; } = 1;
    public int NotifyOnError { get; set; } = 1;
    public int PauseOnTallyClose { get; set; } = 0;

    public int EntityFlags { get; set; }      // bitmask flags

    public string Status { get; set; }        // "ok" | "warn" | "err" | "idle"
    public DateTime? LastRunAt { get; set; }
    public int? LastDurationMs { get; set; }
    public long? LastRowsWritten { get; set; }
    public int ErrorCount24h { get; set; }
}

[Flags]
public enum EntityFlags {
    None = 0,
    Vouchers    = 1 << 0,
    Ledgers     = 1 << 1,
    StockItems  = 1 << 2,
    Groups      = 1 << 3,
    CostCentres = 1 << 4,
    Currencies  = 1 << 5,
}
```

### `DatabaseProfile`

```csharp
public class DatabaseProfile {
    public int Id { get; set; }
    public string Name { get; set; }
    public string Technology { get; set; }   // "postgres" | "mssql"
    public string Server { get; set; }
    public int Port { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }      // DPAPI-encrypted string in db, decrypted plaintext in memory
    public string LastTestResult { get; set; }     // e.g. "OK · 2m" or "Untested"
    public DateTime? LastTestedAt { get; set; }
    public int UsedByCount { get; set; }           // count of CompanyProfile referencing this Id
}
```

### `TallySettings`

```csharp
public class TallySettings {
    public string Server { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
    public string? TallyExePath { get; set; }
    public string? TallyIniPath { get; set; }
    public int AutoStartTally { get; set; }
}
```

### `SyncRun` (history)

```csharp
public class SyncRun {
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public TimeSpan Duration => EndedAt - StartedAt;
    public string Mode { get; set; }
    public string Status { get; set; }    // ok | warn | err
    public int Retries { get; set; }
    public long RowsIn { get; set; }
    public long RowsWritten { get; set; }
    public Dictionary<string, long> ByEntity { get; set; }  // serialized
    public string? ResultSummary { get; set; }              // "+1,204 rows" / "Auth failed"
    public string? LogExcerpt { get; set; }                 // last ~10 log lines
}
```

### `TallyCompanyInfo`

```csharp
public class TallyCompanyInfo {
    public string Name { get; set; }
    public string? Guid { get; set; }
    public bool IsGroup { get; set; }       // Consolidated
    public DateTime? BooksFrom { get; set; }
    public DateTime? BooksTo { get; set; }
}
```

---

## Repository methods

`ConfigRepository` (in `TallyDbLoader.Core.Data`):

```csharp
List<CompanyProfile> GetAllCompanyProfiles();
void SaveCompanyProfile(CompanyProfile company);
void DeleteCompanyProfile(int id);

List<DatabaseProfile> GetAllDatabaseProfiles();
DatabaseProfile? GetDatabaseProfileById(int id);
DatabaseProfile? GetDatabaseProfileByName(string name);
void SaveDatabaseProfile(DatabaseProfile profile);
void DeleteDatabaseProfile(int id);

TallySettings GetTallySettings();
void SaveTallySettings(TallySettings settings);

List<SyncRun> GetRecentSyncRuns(int limit = 50);
List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50);
void AddSyncRun(SyncRun run);
```

All writes are transactional.

---

## Secrets handling

**Rule:** `DatabaseProfile.Password` is plaintext in memory and in the editor's bound TextBox. It is encrypted to a base64 string prefixed with `dpapi:` **at the repository boundary** (in `SaveDatabaseProfile`) using:

```csharp
byte[] plainBytes = Encoding.UTF8.GetBytes(password);
byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
string encryptedString = "dpapi:" + Convert.ToBase64String(encryptedBytes);
```

Decryption happens transparently in `GetDatabaseProfileById` / `GetDatabaseProfileByName` / `GetAllDatabaseProfiles`. The view-model only ever sees plaintext.

WHEN the user views a profile they didn't create on this machine, DPAPI decryption will throw — surface a warning log "This password was encrypted on another machine. Re-enter it." and leave the field empty.

---

## Bindings — per screen

### Dashboard

| XAML | Binding | Mode |
|---|---|---|
| `CompanyCard.DataContext` | `CompanyProfile` (DataTemplate) | — |
| `CompanyCard.MouseLeftButtonUp` | `Navigate('company', Id)` | — |
| `NameText.Text`           | `Name` | — |
| `TargetText.Text`         | `"→ " + Db.Name + " · " + TargetCatalog` (MultiBinding) | — |
| `StatusPill.Tone`         | `Status` via `StatusToToneConverter` | — |
| `LastValue.Text`          | `LastRunAt` via `RelativeTimeConverter` | — |
| `NextValue.Text`          | computed via `NextRunConverter(LastRunAt, IntervalMinutes, Enabled)` | — |
| `RowsValue.Text`          | `LastRowsWritten` via `NumberConverter` | — |
| `ErrorsValue.Text`        | `ErrorCount24h` (red if > 0) | — |
| `RunNowBtn.Command`       | `vm.RunCompanyCommand`, parameter = `Id` | — |
| `EditBtn.Command`         | `vm.StartEditingCompanyCommand`, parameter = `Id` | — |
| `CommandBarMain.Engine.Start` | `vm.StartSyncEngineCommand`. WHEN running, swap label to "Stop" and bind to `StopSyncEngineCommand`. | — |

### Companies list

| XAML | Binding |
|---|---|
| `CompaniesGrid.ItemsSource` | `vm.Companies` |
| `CompaniesGrid.SelectedItem` | `vm.SelectedCompany` (two-way) |
| `CompaniesGrid.MouseDoubleClick` | `Navigate('company', SelectedCompany.Id)` |
| `CommandBarMain.Companies.Edit.IsEnabled` | `SelectedCompany != null` |
| `UnlinkedHint.ItemsSource` | `vm.UnlinkedTallyCompanies` |

### Company profile

For brevity, only non-obvious bindings:

| XAML | Binding |
|---|---|
| `PageHeaderMain.Heading` | `vm.SelectedCompany.Name` |
| `PageHeaderMain.Breadcrumb["Companies"].Command` | `vm.NavigateCommand('companies')` |
| `PageHeaderMain.EngineLockPill.Visibility` | `vm.IsSyncRunning ? Visible : Collapsed` |
| `SourceCard.NameField.Text` | `vm.JobCompany` (two-way) |
| `SourceCard.GuidField.Text` | `vm.SelectedCompany.TallyGuid` |
| `SourceCard.ReDetectBtn.Command` | `vm.OpenCompanyPickerCommand` |
| `TargetCard.DbCombo.ItemsSource` | `vm.DatabaseProfiles` |
| `TargetCard.DbCombo.SelectedItem` | `vm.JobSelectedProfile` (two-way) |
| `TargetCard.TargetCatalogField.Text` | `vm.JobTargetCatalog` (two-way) |
| `TargetCard.TestBtn.Command` | `vm.TestDatabaseConnectionCommand` |
| `ScheduleCard.ModeCombo.SelectedValue` | `vm.JobSyncMode` (two-way) |
| `ScheduleCard.IntervalCombo.SelectedValue` | `vm.JobInterval` (two-way) |
| `EntitiesCard.<each checkbox>.IsChecked` | `vm.JobEntities` flag-bound (two-way) |
| `StatusCard.RunNowBtn.Command` | `vm.RunCompanyCommand`, parameter = `Id` |
| `RecentRunsCard.ItemsSource` | `vm.SelectedCompanyRecentRuns` (last 6) |

### Databases

| XAML | Binding |
|---|---|
| `DbList.ItemsSource` | `vm.DatabaseProfiles` |
| `DbList.SelectedItem` | `vm.SelectedDatabaseProfile` (two-way, triggers `StartEditingDbProfile`) |
| `EditDbProfileCard.NameField.Text` | `vm.DbName` (two-way) |
| `EditDbProfileCard.TechCombo.SelectedValue` | `vm.DbTech` (two-way) |
| `EditDbProfileCard.ServerField.Text` | `vm.DbServer` (two-way; setter triggers `TryParseConnectionString`) |
| `EditDbProfileCard.PortField.Text` | `vm.DbPort` (two-way) |
| `EditDbProfileCard.UsernameField.Text` | `vm.DbUsername` (two-way) |
| `EditDbProfileCard.PasswordField.Password` | `vm.DbPassword` (PasswordBox helper, two-way) |
| `EditDbProfileCard.TestBtn.Command` | `vm.TestDatabaseConnectionCommand` |
| `EditDbProfileCard.SaveBtn.Command` | `vm.SaveDatabaseProfileCommand` |
| `EditDbProfileCard.SaveBtn.Content` | `vm.DbSaveButtonText` |

### Live sync log

| XAML | Binding |
|---|---|
| `LogStream.Document` | `vm.LogOutput` via `FlowDocumentConverter` (color per level) |
| `EngineKpi.Pill.Tone` | `vm.IsSyncRunning ? ok : neutral` |
| `EngineKpi.Pill.Text` | `vm.EngineState.ToString().ToLower()` |
| `CommandBarMain.Stream.Pause.Command` | `vm.PauseSyncEngineCommand` (or `ResumeSyncEngineCommand` based on state) |
| `CommandBarMain.Stream.Clear.Command` | `vm.ClearLogCommand` |
| `CommandBarMain.Export.SaveLog.Command` | `vm.ExportLogCommand` |

### History

| XAML | Binding |
|---|---|
| `RunsGrid.ItemsSource` | `vm.RunHistory` |
| `RunsGrid.SelectedItem` | `vm.SelectedRun` (two-way) |
| `RunDetail.DataContext` | `vm.SelectedRun` |

### Settings

| XAML | Binding |
|---|---|
| `TallyServerField.Text` | `vm.TallyServer` (two-way) |
| `TallyPortField.Text` | `vm.TallyPort` (two-way) |
| `TallyExePathField.Text` | `vm.TallyExePath` (two-way) |
| `TallyIniPathField.Text` | `vm.TallyIniPath` (two-way) |
| `AutoStartCheck.IsChecked` | `vm.AutoStartTally` (two-way) |
| `TestBtn.Command` | new `TestTallyConnectionCommand` (not in v1) |
| `SaveBtn.Command` | `vm.SaveTallySettingsCommand` |

### Company picker modal

| XAML | Binding |
|---|---|
| `CompaniesListBox.ItemsSource` | `List<TallyCompanyInfo>` (passed in dialog ctor) |
| `CompaniesListBox.SelectedItem` | `SelectedCompany` (two-way; local to dialog) |
| `SelectBtn.Command` | dialog command — sets `DialogResult = true` and closes |
