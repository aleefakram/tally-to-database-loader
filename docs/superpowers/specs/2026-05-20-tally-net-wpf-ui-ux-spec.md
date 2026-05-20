# UI/UX Design & Data-Binding Specification: .NET Port WPF Desktop Front End (AI Agent Compliant)

This document details the UI/UX design, visual system, data bindings, window lifecycles, and form-validation rules for the .NET port WPF desktop application. It maps visual components directly to C# files, namespaces, classes, and SQLite database models. This specification acts as a direct reference for senior UI/UX developers to redesign the application and is formatted for seamless consumption by AI coding agents.

---

## 1. Directory Structure & Key Files

AI agents can locate the relevant UI and view-model codebases at the following relative paths:
- **Main App Entry & System Tray Bootstrapper:** `src/TallyDbLoader.Wpf/App.xaml.cs` (Handles application startup and Single-Instance Mutex)
- **Main Dashboard Window (Markup):** `src/TallyDbLoader.Wpf/MainWindow.xaml` (Contains style resources, tab controls, grids, and event bindings)
- **Main Dashboard Window (Code-behind):** `src/TallyDbLoader.Wpf/MainWindow.xaml.cs` (Pipes events to the view-model, intercepts window close/minimize events)
- **Dashboard View-Model:** `src/TallyDbLoader.Wpf/MainViewModel.cs` (Orchestrates UI properties, SQLite data transfers, Tally API detection, and background sync worker hooks)
- **System Tray Controller:** `src/TallyDbLoader.Wpf/TrayController.cs` (Wraps WinForms `NotifyIcon`, notification tip triggers, and tray context menu)
- **Modal Company Selector Window (Markup):** `src/TallyDbLoader.Wpf/CompanySelectionWindow.xaml` (Simple list dialog with badges)
- **Modal Company Selector Window (Code-behind):** `src/TallyDbLoader.Wpf/CompanySelectionWindow.xaml.cs` (Assigns active company list bindings)

---

## 2. Visual Styling & Theme Reference

The application overrides default Windows OS control templates using styles declared in the local `<Window.Resources>` block in `MainWindow.xaml`.

### 2.1 Window Configurations
- **Typography:**
  - Base Font: `Segoe UI` (default fallback is system sans-serif)
  - Main Log Monospace Font: `Consolas` (`11pt`, `#00FF33` neon green)
- **MainWindow Dimensions:**
  - Startup Location: `CenterScreen`
  - Dimensions: `980px` width x `700px` height
  - ResizeMode: Resizable
- **CompanySelectionWindow Dimensions:**
  - Startup Location: `CenterOwner` (locked modal)
  - Dimensions: `400px` width x `350px` height
  - ResizeMode: `NoResize`

### 2.2 Core Theme Palette (Hex Codes)
| Color Token | Hex Code | Applied Controls |
| :--- | :--- | :--- |
| **Window Background** | `#121212` | Main Window (`Background`), Company Selector Window |
| **Card Background** | `#1A1A1A` | Group card border panels, TabControl workspace backing |
| **Control Default Gray**| `#222222` | Unselected TabItem backgrounds |
| **Zebra Striping Odd** | `#252525` | Alternate DataGrid row backgrounds |
| **Zebra Striping Even**| `#1E1E1E` | Standard DataGrid row backgrounds |
| **Input Fields Backing**| `#2D2D2D` | TextBox, ComboBox background |
| **Input Field Borders** | `#444444` | Control outlines for form inputs |
| **Terminal Background** | `#0E0E0E` | Log terminal TextBox background |
| **Terminal Text** | `#00FF33` | Neon green monospace text stream |
| **Standard Text** | `#FFFFFF` | Core header text, button labels, selected tab items |
| **Muted Text / Gray** | `#CCCCCC` / `#888888`| Secondary labels, subtitles, status-bar footer details |
| **Primary Accent** | `#4A90E2` | Titles, selected TabItem background, standard action buttons |
| **Accent Hover** | `#5AA0F2` / `#357ABD`| Primary button hover style overrides |
| **Cell Selection Blue** | `#3A78C4` | Active cell focus on DataGrid grids |
| **Active / Green** | `#4CAF50` | "Start Sync" button background |
| **Danger / Red** | `#F44336` / `#C62828`| "Stop Sync" and Delete action buttons |
| **Special Action Purple**| `#7B1FA2` | "Test Connection" button, consolidated company badge |
| **Cancel Control Gray** | `#555555` | Neutral cancel edit buttons |

---

## 3. Screen Layouts & Controls Reference

The application UI is structured as a header-monitor-footer grid with tabs.

```
+-----------------------------------------------------------------------------+
| Tally-to-Database Sync Utility  [Subtitle Details]   [Start Sync] [Stop Sync]|
+-----------------------------------------------------------------------------+
|  [ Dashboard Monitor ]                      [ Configuration & Profiles ]    |
| +-----------------------------------------+ +-----------------------------+ |
| |                                         | |                             | |
| |        TAB CONTENT AREA                 | |        TAB CONTENT AREA     | |
| |                                         | |                             | |
| +-----------------------------------------+ +-----------------------------+ |
+-----------------------------------------------------------------------------+
| (O) Status: Sync engine is idle.                                            |
+-----------------------------------------------------------------------------+
```

### 3.1 Global Header Control Deck
- **Controls:** Title TextBlock, Subtitle TextBlock.
- **Start Sync Button:** Calls `StartButton_Click` (`MainWindow.xaml.cs`). Runs `vm.StartSyncEngine()` which initializes `BackgroundSyncWorker` and pipes sync updates to `vm.LogOutput`.
- **Stop Sync Button:** Calls `StopButton_Click` (`MainWindow.xaml.cs`). Runs `vm.StopSyncEngine()` to terminate active sync loops.

### 3.2 Tab 1: "Dashboard Monitor" (TabControl Index 0)
- **Active Sync Schedules Grid (`x:Name="SyncJobsGrid"`):**
  - Binds `ItemsSource` to `SyncJobs` collection.
  - Binds `SelectedItem` to `SelectedSyncJob` (Two-way).
  - Columns: Company Name (`CompanyName`), Catalog Name (`TargetCatalog`), Sync Mode (`SyncMode`), Sync Interval (`SyncIntervalMinutes`), Status (`Status`).
  - Context buttons below grid:
    - **"Edit Selected Job"** (Calls `EditJobButton_Click`): Sets `vm.SelectedSyncJob`, runs `vm.StartEditingSyncJob()`, and switches `TabControl.SelectedIndex = 1` to open the editor.
    - **"Delete Selected Job"** (Calls `DeleteJobButton_Click`): Runs `vm.DeleteSyncJob()`.
- **Database Targets Grid (`x:Name="DbProfilesGrid"`):**
  - Binds `ItemsSource` to `DatabaseProfiles` collection.
  - Binds `SelectedItem` to `SelectedDatabaseProfile` (Two-way).
  - Columns: Name (`Name`), Technology (`Technology`), Server Address (`Server`).
  - Context buttons below grid:
    - **"Edit Selected Target"** (Calls `EditDbProfileButton_Click`): Sets `vm.SelectedDatabaseProfile`, runs `vm.StartEditingDbProfile()`, and switches `TabControl.SelectedIndex = 1`.
    - **"Delete Selected Target"** (Calls `DeleteDbProfileButton_Click`): Runs `vm.DeleteDatabaseProfile()`.
- **Sync Orchestrator Log Output TextBox:**
  - Binds `Text` to `LogOutput` (One-way). Read-only Console text area.

### 3.3 Tab 2: "Configuration & Profiles" (TabControl Index 1)
- **Global Tally Configuration Panel:**
  - Form Fields: Tally Server IP (`TallyServer`), Port (`TallyPort`), `tally.exe` Path (`TallyExePath`), `tally.ini` Path (`TallyIniPath`), Auto-start Checkbox (`AutoStartTally`).
  - Save Button (Calls `SaveTallyButton_Click`): Runs `vm.SaveTallySettings()`.
- **Database Target Profile Editor:**
  - Header: Text block binding to `DbFormHeader`.
  - Form Fields: Profile Name (`DbName`), Database Technology ComboBox (`DbTech`), Host Server (`DbServer`), Connection Port (`DbPort`), Username (`DbUsername`), Password (`DbPassword`).
  - Form Actions:
    - **"Test Connection"** (Calls `TestConnectionButton_Click`): Runs `vm.TestDatabaseConnection()`.
    - **"Cancel"** (Calls `CancelDbEditButton_Click`): Visible if `IsEditingDbProfileVisibility == Visible`. Runs `vm.CancelDbEdit()`.
    - **"Save Database Profile" / "Update Profile"** (Calls `SaveDbProfileButton_Click`): Binds button text to `DbSaveButtonText`. Runs `vm.SaveDatabaseProfile()`.
- **Sync Job Editor Form:**
  - Header: Text block binding to `JobFormHeader`.
  - Form Fields: 
    - Company name (`JobCompany`) with adjacent **"Detect"** button.
    - Target Catalog / Database (`JobTargetCatalog`).
    - Recurrence Timer (`JobInterval`).
    - Connection Selector ComboBox: `ItemsSource` binds to `DatabaseProfiles`, selected item binds to `JobSelectedProfile`. Displays profile name.
    - Sync Mode ComboBox: Selected value binds to `JobSyncMode` (`full` or `incremental`).
  - Form Actions:
    - **"Cancel"** (Calls `CancelJobEditButton_Click`): Visible if `IsEditingJobVisibility == Visible`. Runs `vm.CancelJobEdit()`.
    - **"Save Sync Job" / "Update Sync Job"** (Calls `AddSyncJobButton_Click`): Binds button text to `JobSaveButtonText`. Runs `vm.AddSyncJob()`.

---

## 4. Class-Level Data Bindings & Properties

The central namespace is `TallyDbLoader.Wpf`. The view-model binds directly to data entities declared in `TallyDbLoader.Core.Models`.

```
                +------------------------------+
                |    TallyDbLoader.Wpf         |
                |        MainViewModel         |
                +--------------+---------------+
                               |
       +-----------------------+-----------------------+
       |                                               |
       ▼                                               ▼
ObservableCollection<DatabaseProfile>        ObservableCollection<SyncJob>
- Id (int)                                   - Id (int)
- Name (string)                              - CompanyName (string)
- Technology (string)                        - DbProfileId (int)
- Server (string)                            - TargetCatalog (string)
- Port (int)                                 - SyncIntervalMinutes (int?)
- Username (string)                          - Status (string)
- Password (string) - Plaintext in memory/UI (encrypted only at repository persistence) - SyncMode (string)
```

### 4.1 System Settings Mappings
- **TallySettings Entity (`TallyDbLoader.Core.Models.TallySettings`):**
  - Map variables: `settings.Server` -> `TallyServer`, `settings.Port` -> `TallyPort`, `settings.TallyExePath` -> `TallyExePath`, `settings.TallyIniPath` -> `TallyIniPath`, `settings.AutoStartTally` -> `AutoStartTally` (maps `1` to `true`, `0` to `false`).

---

## 5. UI Event Logic & Form Helper Functions

### 5.1 Connection String Auto-Parsing (`TryParseConnectionString`)
- **Trigger:** Property Setter for `DbServer` in `MainViewModel.cs`.
- **Parsing Rules:**
  - If input starts with `postgresql://` or `postgres://`, parses as `Uri`:
    - Sets `DbTech = "postgres"`
    - Updates Host address (`DbServer`), Port (`DbPort`), Username (`DbUsername`), Password (`DbPassword`).
    - Strips the path parameters and sets `JobTargetCatalog`.
  - If standard ADO.NET connection strings are parsed (containing `Server=`, `Host=`, `Database=`, or `Initial Catalog=`):
    - Instantiates a generic `DbConnectionStringBuilder` to extract and assign individual properties.

### 5.2 Dynamic Tally Company Detection (`DetectActiveCompaniesAsync`)
- **Action:** Issues HTTP request using `TallyClient` to `http://{TallyServer}:{TallyPort}`.
- **Workflow:**
  1. Calls `client.FetchActiveCompaniesDetailedAsync()`.
  2. If collection count is `0`, alerts: `"No active companies found. Please ensure a company is open in Tally Prime."`
  3. If collection count is `1`, sets `JobCompany = companies[0].Name`.
  4. If collection count is `> 1`, triggers the modal window selector:
     - Instantiates `var dialog = new CompanySelectionWindow(companies)` (defined in `src/TallyDbLoader.Wpf/CompanySelectionWindow.xaml.cs`).
     - ListBox `CompaniesListBox.ItemsSource` binds to the `List<TallyCompanyInfo>` collection.
     - Group companies with `IsGroup == true` render a consolidated purple badge (`#7B1FA2`) marked `"Consolidated"`.
     - Clicking "Select Company" maps `dialog.SelectedCompany` and closes with `DialogResult = true`.
     - `MainViewModel` assigns `JobCompany = selectedCompany.Name`.

### 5.3 Database Connection Verification (`TestDatabaseConnection`)
- **Action:** Executes transient DB open query:
  - If `DbTech == "postgres"`, builds Npgsql connection:
    - `"Host={DbServer};Port={DbPort};Username={DbUsername};Password={DbPassword};Database=postgres;Timeout=5;"`
    - Adds `SslMode=Require;TrustServerCertificate=True;` if server is not localhost (`127.0.0.1`).
  - If `DbTech == "mssql"`, builds SQL connection:
    - `"Server={DbServer},{DbPort};User Id={DbUsername};Password={DbPassword};TrustServerCertificate=True;Connection Timeout=5"`
  - Catches connection exceptions, logs output to Console terminal, and displays popups:
    - On success: `MessageBoxImage.Information`
    - On error: `MessageBoxImage.Error`

---

## 6. Background Engine Control (`StartSyncEngine` / `StopSyncEngine`)
- **Start Execution:**
  - Instantiates `_worker = new BackgroundSyncWorker(_repo, TallyServer, TallyPort)`.
  - Binds logging handler `_worker.OnLogMessage += (msg) => { ... }` which prefixes timestamps and appends messages to `LogOutput`, and updates the status bar `StatusText`.
  - Binds completed handler `_worker.OnSyncCompleted += () => { ... }` which invokes `LoadConfiguration()` on the UI dispatcher thread to refresh schedules and statuses.
  - Calls `_worker.Start()`.
- **Stop Execution:**
  - Calls `_worker?.Dispose()` (which stops the task and disposes the cancellation token sources) and sets `_worker = null`.

---

## 7. AI Agent Guidelines for Code Modifications

When editing the UI, AI agents must adhere to the following constraints:
1. **Thread-Safe UI Updates:** Any callback from `BackgroundSyncWorker` or asynchronous tasks that modifies observable collections must invoke the main UI dispatcher thread:
   ```csharp
   System.Windows.Application.Current.Dispatcher.Invoke(() => LoadConfiguration());
   ```
2. **DPAPI Data Encryption:** DB passwords must be encrypted before database persistence using the Windows Data Protection API (`ProtectedData`), wrapped inside core repository methods.
3. **No Code-Behind Business Logic:** Event handlers in `MainWindow.xaml.cs` must strictly act as event-routing bridges, passing control directly to `MainViewModel`. All connection tests, validations, and database writes must occur in `MainViewModel` or business layers.
4. **Command Execution Safety:** Confirm that background schedulers are stopped before deleting database profiles or jobs to prevent SQLite transaction locking errors.
