# §07 — Migration from v1 → v2

Use this when porting the existing codebase (the one described by `uploads/2026-05-20-tally-net-wpf-ui-ux-spec-484e1c5c.md`) to the v2 design.

---

## What changed in one paragraph

The v1 UI was a **single MainWindow with two tabs** ("Dashboard Monitor" and "Configuration & Profiles") containing two data grids each, against a dark neon-green-on-black terminal aesthetic. v2 is a **multi-page NavigationView app** with six top-level routes, a ribbon-style CommandBar on every page, native Windows 11 Fluent visuals (light by default + dark theme), and a 1:1 **Company → Profile** model that replaces the v1 `SyncJob` concept.

---

## Concept renames

| v1 | v2 | Reason |
|---|---|---|
| `SyncJob` (entity)         | `CompanyProfile` (entity)   | The user thinks in companies, not jobs. |
| `SyncJobs` (collection)    | `Companies`                  | — |
| `SelectedSyncJob`          | `SelectedCompany`            | — |
| "Dashboard Monitor" tab    | `dashboard` route + `companies` route | Tabs are wrong for IA this size. |
| "Configuration & Profiles" tab | `databases` route + `settings` route + the editor cards on `company` route | Splits config by intent. |
| Sync Orchestrator Log TextBox | `LogPage` (its own route) | Needs more real estate than a tab corner. |
| Status bar text only       | `StatusBar` (component, same purpose) | Same role, cleaner. |
| "Active Sync Schedules Grid" | Dashboard card grid + Companies list (2 surfaces, 1 model) | A grid is wrong for the overview; a card grid is better. |
| "Database Targets Grid"     | `databases` route (list + editor split) | Promoted from sub-grid to first-class screen. |

## Behavior changes

| v1 | v2 | Notes |
|---|---|---|
| Save / Delete buttons disabled (`IsEnabled=False`) when engine runs | Buttons remain visually enabled; clicking shows a `warn` toast "Stop the engine to save changes." Card opacity drops to 0.94. | Per [§05 mutation guard](05-behavior.md#engine-mutation-guard). Better discoverability. |
| Modal popups on success (`MessageBoxImage.Information`) | Toasts (auto-dismiss in 4.5 s) bottom-right. | No more "click OK" interruptions. |
| Modal popups on connection failure (`MessageBoxImage.Error`) | `err` toast with "Show details" action that opens a modal with the exception text. | Same info, less disruptive. |
| One job = one company (implicit) | One profile = one company (enforced at schema level: UNIQUE constraint on `CompanyProfile.Name`). | Schema migration below. |
| Tab switch via `TabControl.SelectedIndex = 1` after Edit | `NavigateCommand` + route stack | See [§05 routes](05-behavior.md#routes--navigation). |
| Engine indicator: text in status bar | Pulsing dot in rail footer card + label + still in status bar | More visible. |
| Per-job "Run now" not present | Per-company "Run now" on card + profile + history rows | New: see [§04 dashboard](04-screens.md#1-dashboard). |

## Removed (intentionally)

| v1 element | Removed because |
|---|---|
| Neon green `Consolas 11pt #00FF33` log style | Wrong aesthetic for a modern app. Log is now mono but `text-muted` with status colors for WARN/ERROR lines. |
| The two-tab `MainWindow` layout | Replaced by NavigationView. |
| "Set Window Background to `#121212`" hard dark style | Replaced by light-by-default theme with a Tweaks-equivalent toggle. The v1 dark palette is preserved as the v2 dark theme but is no longer the default. |
| Per-row "Edit Selected Job" / "Delete Selected Job" buttons under the grid | Moved into the CommandBar above the grid; double-click opens the profile. |

## Added (new in v2)

| Element | Where defined |
|---|---|
| `Companies` list route                       | [§04.2](04-screens.md#2-companies-list) |
| `Company profile` route (1:1 with company)   | [§04.3](04-screens.md#3-company-profile) — replaces v1 inline editor |
| `Databases` route (list + editor)            | [§04.4](04-screens.md#4-database-connections) |
| `Sync log` route (full-page)                 | [§04.5](04-screens.md#5-live-sync-log) |
| `History` route                              | [§04.6](04-screens.md#6-sync-history) — and `SyncRun` model |
| First-run wizard                             | [§04.8](04-screens.md#8-first-run-wizard) |
| Toast notification system                    | [§05 toasts](05-behavior.md#toasts) |
| CommandBar (ribbon-lite)                     | [§03.2](03-components.md#2-commandbar) |
| Tray context menu (Run all, Pause, View log) | [§04.10](04-screens.md#10-system-tray--toast) |
| Per-company `Status` field                   | [§05 status](05-behavior.md#per-company-status) |
| `EntityFlags` on `CompanyProfile`            | [§06 model](06-data-bindings.md#data-models) |
| `Schema` and `TablePrefix` on profile        | [§04.3](04-screens.md#3-company-profile) → `TargetCard` |
| Engine `paused` state                        | [§05 engine](05-behavior.md#engine-state-machine) |
| Theme toggle (light/dark)                    | App-wide in Settings → General |

## Preserved (don't touch)

These v1 rules carry forward unchanged. Re-implement exactly:

1. **Single-instance Mutex** at `App.OnStartup`.
2. **`SessionEnding` handler** that flags `_isExiting = true` and disposes the worker.
3. **DPAPI encryption** at the repository boundary. See [§06 secrets](06-data-bindings.md#secrets-handling).
4. **Thread-safe UI updates** via `Application.Current.Dispatcher.Invoke`. See [§05 threading](05-behavior.md#threading).
5. **Idempotent `BackgroundSyncWorker.Dispose()`**.
6. **Connection-string auto-parse** (`TryParseConnectionString`). See [§05](05-behavior.md#connection-string-auto-parse).
7. **Modal company selector behavior** (0 / 1 / >1 active companies). See [§05 modals](05-behavior.md#modals) and [§04.9](04-screens.md#9-company-picker-modal).

---

## SQLite schema migration

Provide a one-shot migration on first launch of v2. Run inside a single transaction.

### 1. Rename `SyncJobs` → `CompanyProfiles` (or create a view)

If the existing app has data, prefer ALTER + ADD COLUMN over a fresh table so user data survives.

```sql
-- 1. Add new columns to existing SyncJobs table
ALTER TABLE SyncJobs ADD COLUMN TallyGuid TEXT NULL;
ALTER TABLE SyncJobs ADD COLUMN Consolidated INTEGER NOT NULL DEFAULT 0;
ALTER TABLE SyncJobs ADD COLUMN BooksFrom TEXT NULL;
ALTER TABLE SyncJobs ADD COLUMN BooksTo TEXT NULL;
ALTER TABLE SyncJobs ADD COLUMN Schema TEXT NOT NULL DEFAULT 'public';
ALTER TABLE SyncJobs ADD COLUMN TablePrefix TEXT NOT NULL DEFAULT 'tally_';
ALTER TABLE SyncJobs ADD COLUMN Enabled INTEGER NOT NULL DEFAULT 1;
ALTER TABLE SyncJobs ADD COLUMN NotifyOnError INTEGER NOT NULL DEFAULT 1;
ALTER TABLE SyncJobs ADD COLUMN PauseOnTallyClose INTEGER NOT NULL DEFAULT 0;
ALTER TABLE SyncJobs ADD COLUMN EntityFlags INTEGER NOT NULL DEFAULT 15; -- Vouchers|Ledgers|StockItems|Groups
ALTER TABLE SyncJobs ADD COLUMN LastRunAt TEXT NULL;
ALTER TABLE SyncJobs ADD COLUMN LastDurationMs INTEGER NULL;
ALTER TABLE SyncJobs ADD COLUMN LastRowsWritten INTEGER NULL;
ALTER TABLE SyncJobs ADD COLUMN ErrorCount24h INTEGER NOT NULL DEFAULT 0;

-- 2. Add unique constraint (de-dupe before creating)
-- Pick the most recently edited row per CompanyName, delete the rest:
DELETE FROM SyncJobs
WHERE rowid NOT IN (
    SELECT MIN(rowid)
    FROM SyncJobs
    GROUP BY CompanyName
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_SyncJobs_CompanyName ON SyncJobs(CompanyName);

-- 3. Rename for clarity (optional, if downstream code can take it)
-- (SQLite ALTER RENAME requires 3.25+; gate on PRAGMA user_version)
ALTER TABLE SyncJobs RENAME TO CompanyProfiles;
ALTER TABLE CompanyProfiles RENAME COLUMN CompanyName TO Name;
ALTER TABLE CompanyProfiles RENAME COLUMN SyncMode TO Mode;
ALTER TABLE CompanyProfiles RENAME COLUMN SyncIntervalMinutes TO IntervalMinutes;
```

### 2. New `SyncRuns` table

```sql
CREATE TABLE IF NOT EXISTS SyncRuns (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyId INTEGER NOT NULL REFERENCES CompanyProfiles(Id),
    StartedAt TEXT NOT NULL,
    EndedAt TEXT NOT NULL,
    Mode TEXT NOT NULL,
    Status TEXT NOT NULL,
    Retries INTEGER NOT NULL DEFAULT 0,
    RowsIn INTEGER NOT NULL DEFAULT 0,
    RowsWritten INTEGER NOT NULL DEFAULT 0,
    ByEntityJson TEXT NOT NULL DEFAULT '{}',
    ResultSummary TEXT NULL,
    LogExcerpt TEXT NULL
);
CREATE INDEX ix_SyncRuns_CompanyId_StartedAt ON SyncRuns(CompanyId, StartedAt DESC);
```

### 3. Bump `PRAGMA user_version`

Increment from whatever v1 was using to `2`. Migrate idempotently — running this twice is a no-op.

---

## XAML file moves

```
src/TallyDbLoader.Wpf/
├── App.xaml                              (preserved — register theme resources)
├── App.xaml.cs                           (preserved — single-instance + tray)
├── MainWindow.xaml                       (REWRITE — host the NavigationView)
├── MainWindow.xaml.cs                    (slim — only chrome events)
├── MainViewModel.cs                      (expand — see §06)
├── Services/
│   └── TrayController.cs                 (preserved + extend menu items)
├── Views/                                (new folder — one page each)
│   ├── DashboardPage.xaml
│   ├── CompaniesPage.xaml
│   ├── CompanyProfilePage.xaml
│   ├── DatabasesPage.xaml
│   ├── LogPage.xaml
│   ├── HistoryPage.xaml
│   ├── SettingsPage.xaml
│   └── SetupWizardWindow.xaml
├── Dialogs/                              (new folder)
│   └── CompanyPickerDialog.xaml          (was CompanySelectionWindow.xaml)
├── Themes/                               (new folder — styles & tokens)
│   ├── Tokens.xaml                       (color + brush keys; per [§02](02-design-tokens.md))
│   ├── Typography.xaml                   (TextBlock styles)
│   ├── Buttons.xaml                      (3 button styles + danger variant)
│   ├── TextBoxes.xaml                    (focused-bottom-accent template)
│   ├── Card.xaml
│   ├── Pill.xaml
│   ├── CommandBar.xaml
│   └── NavigationView.xaml               (rail item + active indicator)
├── Converters/                           (new folder)
│   ├── RelativeTimeConverter.cs
│   ├── StatusToToneConverter.cs
│   ├── NumberConverter.cs
│   └── NextRunConverter.cs
└── Controls/                             (new — composite primitives)
    ├── PageHeader.xaml                   (UserControl)
    ├── StatusBar.xaml                    (UserControl)
    └── Toast.xaml                        (UserControl, hosted in a Popup overlay)
```

---

## Smoke-test checklist after porting

Run these manually before declaring v2 done. The interactive prototype matches each.

1. ☐ Launch app — Dashboard appears with light theme, no errors. Mutex prevents second launch.
2. ☐ Click each rail item — page swaps, rail highlight follows.
3. ☐ Open `dashboard` → click a card → company profile opens, back arrow appears, breadcrumb is clickable.
4. ☐ On Companies list → double-click row → opens same profile screen.
5. ☐ Start engine → engine dot pulses, log streams, status bar reflects running.
6. ☐ Stop engine → dot stops, log stops.
7. ☐ While engine runs, click "Save profile" → `warn` toast appears, no write occurs.
8. ☐ Open Settings → change Tally port → Save → `ok` toast.
9. ☐ Open Databases → select profile → Test connection → toast reflects outcome.
10. ☐ Open Picker via "Detect from Tally" → list reflects current Tally state.
11. ☐ Close window → app hides to tray. Tray icon still visible.
12. ☐ Tray right-click → Exit → app shuts down cleanly (no leftover process).
13. ☐ Sign out of Windows / restart — `SessionEnding` fires, app does not block shutdown.
14. ☐ Toggle theme to dark → all colors swap; no contrast-failed text.
15. ☐ Run history list populates after at least one cycle. Click row → run detail panel updates.
