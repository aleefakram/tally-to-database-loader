# §04 — Screen Catalog

One section per screen. Each section lists: element names (for AI agents to wire), data bindings, interaction rules, validation rules.

Element names are formatted as `x:Name="PascalCase"`. Use exactly these names in the XAML so cross-references in the rest of the doc set resolve.

---

## Index

1. [Dashboard](#1-dashboard) — `DashboardPage.xaml`
2. [Companies list](#2-companies-list) — `CompaniesPage.xaml`
3. [Company profile](#3-company-profile) — `CompanyProfilePage.xaml`
4. [Database connections](#4-database-connections) — `DatabasesPage.xaml`
5. [Live sync log](#5-live-sync-log) — `LogPage.xaml`
6. [Sync history](#6-sync-history) — `HistoryPage.xaml`
7. [Settings](#7-settings) — `SettingsPage.xaml`
8. [First-run wizard](#8-first-run-wizard) — `SetupWizardWindow.xaml`
9. [Company picker modal](#9-company-picker-modal) — `CompanyPickerDialog.xaml`
10. [System tray + toast](#10-system-tray--toast) — `TrayController.cs`

---

## 1. Dashboard

**Route:** `dashboard`. **Rail highlight:** Dashboard.

Card grid view. One card per company. Primary entry point — opens after launch.

### Page chrome

| Element | Component | Notes |
|---|---|---|
| `CommandBarMain` | CommandBar | Groups: **Engine** (Start/Stop, Pause, Refresh), **Companies** (Add, Detect, Edit `dim`, Delete `dim`), **View** (Cards, List). Right slot: search. |
| `PageHeaderMain` | PageHeader | Heading "Companies". Sub: live counts (`{n} companies · {ok} healthy · {warn} stale · {err} error · {idle} not configured`). Inline actions: Refresh, Detect from Tally, Add company. |
| `CardGrid` | `ItemsControl` | 3-column `UniformGrid`, 12 px gap. Items bind to `MainViewModel.Companies`. |
| `StatusBarMain` | StatusBar | Left: `{engineStatus} · last cycle {ago}`. Right: `Tally {ip}:{port} · OK`. |

### Company card

| Element | Binding | Notes |
|---|---|---|
| `CompanyCard` | `CompanyProfile` (DataTemplate root) | Click → `Navigate('company', {id})`. |
| `NameText`    | `CompanyProfile.Name` | Bold, ellipsis on overflow. |
| `TargetText`  | `→ {Db.Name} · {TargetCatalog}` | 11 px / text-muted; catalog mono. |
| `StatusPill`  | `CompanyProfile.Status` | Tone via [STATUS_TONE](#status-tone-map). |
| `LastValue`   | `CompanyProfile.LastRunAt` | Formatted via `RelativeTimeConverter` ("2 min ago", "never"). |
| `NextValue`   | computed | If `Enabled`: `in {Max(1, IntervalMinutes - elapsed)} min`. Else `paused`. |
| `RowsValue`   | `CompanyProfile.LastRowsWritten` | Mono, formatted with thousands separators. Dash if null. |
| `ErrorsValue` | `CompanyProfile.ErrorCount24h` | Red text if > 0. |
| `RunNowBtn`   | command `RunCompanyCommand` | Manual run. Stop click bubbling. |
| `EditBtn`     | command `EditCompanyCommand` | Routes to profile screen. Stop click bubbling. |

### Empty state

WHEN `Companies.Count == 0`, THEN show a centered empty card:
- Heading: "No companies linked yet"
- Body: "Open a company in Tally Prime, then click **Detect** to link it."
- Primary button: "Detect from Tally" → `OpenCompanyPicker()`.

### Status tone map

| `Status` | Pill tone | Label |
|---|---|---|
| `ok`   | ok      | Healthy |
| `warn` | warn    | Stale |
| `err`  | err     | Error |
| `idle` | neutral | Not configured |

---

## 2. Companies list

**Route:** `companies`. **Rail highlight:** Companies.

Dense management grid. Use when there are many companies and the user wants to bulk-edit / detect / delete.

### Page chrome

| Element | Component | Notes |
|---|---|---|
| `CommandBarMain` | CommandBar | Groups: **Companies** (New, Detect, Edit `dim` when no selection, Delete `dim`), **Run** (Run now `dim`, Pause `dim`), **View** (Refresh, Filters). Right slot: search. |
| `PageHeaderMain` | PageHeader | Heading "Companies". Sub: "Each company has exactly one sync profile." |

### Grid

`x:Name="CompaniesGrid"`. Bind `ItemsSource` to `MainViewModel.Companies`, `SelectedItem` to `MainViewModel.SelectedCompany`.

Columns (left to right):

| Col | Width | Bind | Notes |
|---|---|---|---|
| `Select` | 40 px | (CheckBox in row) | Multi-select checkbox; clicking it stops propagation. |
| `CompanyCol` | 1.6× | `Name` + `TargetCatalog` | Two-line cell: name (500 weight) + mono catalog at 10 px. |
| `DbCol` | 1.2× | `Db.Name` | text-muted |
| `ModeCol` | 0.7× | `Mode` | "Full" / "Incremental" |
| `IntervalCol` | 0.7× | `IntervalMinutes` | Formatted "{n} min" |
| `StatusCol` | 0.9× | `Status` | `Pill` |
| `Actions` | 100 px | — | Edit / Delete icon buttons |

Single-click selects (`accent-soft` background + 3 px accent strip). Double-click navigates to Company profile.

### Unlinked companies hint

Below the grid, render a dashed-border card listing companies currently open in Tally that don't have a profile yet (`MainViewModel.UnlinkedCompanies`). Click "Set them up →" opens the [Company picker modal](#9-company-picker-modal).

---

## 3. Company profile

**Route:** `company/{id}`. **Rail highlight:** Companies. **Back arrow** in title bar enabled (pops to Companies list).

The one and only sync-profile editor. 2-column layout: left = configuration (4 cards), right = status (3 cards).

### Page chrome

| Element | Component | Notes |
|---|---|---|
| `CommandBarMain` | CommandBar | Groups: **Run** (Run now `pri`, Pause, Re-detect), **Profile** (Edit, Test conn., Delete `dim` while engine runs), **Navigate** (History, Docs). |
| `PageHeaderMain` | PageHeader | Breadcrumb "Companies > {Name}". Heading = company name. Sub = "Single sync profile · last edited {when} by {user}". Inline actions: status Pill + (if engine running) "Engine running — edits locked" info Pill. |

### Left column

1. **Source — Tally company** (`SourceCard`)
   - Header: "Source — Tally company" + `Consolidated` info pill if applicable. Right-aligned "Re-detect" subtle button → opens [Company picker modal](#9-company-picker-modal).
   - Fields (2-col grid): `Name`, `GUID` (mono), `BooksFrom`, `BooksTo`.

2. **Target — Database** (`TargetCard`)
   - Header: "Target — Database" + `Connection verified · {ago}` ok pill.
   - Fields: `DatabaseProfile` (combobox bound to `DatabaseProfiles`), `TargetCatalog` (mono), `Schema` (mono, default `public`), `TablePrefix` (mono, default `tally_`). Hint under `TablePrefix`: "Tables created as e.g. {prefix}vouchers, {prefix}ledgers."
   - Footer buttons: **Test connection** (runs `TestDatabaseConnection` against the selected profile), **Open in connections…** (navigates to Databases page with this profile pre-selected).

3. **Schedule** (`ScheduleCard`)
   - Fields: `SyncMode` combobox ("Full" / "Incremental"), `IntervalMinutes` combobox (presets: 15 / 30 / 60 / 120; "Custom…" opens a number input), `ActiveHours` (read-only "00:00 – 23:59 (always)" for now), `RetryPolicy` ("3 retries, exponential backoff" — read-only).
   - Checkboxes: `Enabled`, `NotifyOnError`, `PauseOnTallyClose`.

4. **Entities to sync** (`EntitiesCard`)
   - Header includes selected count ("4 of 6 selected").
   - 2-col checkbox grid: Vouchers, Ledgers, Stock items, Groups, Cost centres, Currencies.
   - Bind to `CompanyProfile.EntityFlags` (bit-flagged enum or a `string[]` of entity names).

### Right column

1. **Status card** (`StatusCard`)
   - Status pill + "Last run {ago} — {retries} retries".
   - 2×2 stat grid: Next run, Last duration, Rows / day, Errors 24h.
   - Full-width primary "Run now" button at bottom.

2. **Recent runs** (`RecentRunsCard`)
   - Header "Recent runs" + right-aligned "View all →" link → navigate to History (filtered to this company).
   - List: time / status pill / result text / duration. 6 rows max.

3. **Activity** (`ActivityCard`)
   - Audit log of changes to this profile (user + when + what).
   - 5 entries max, "View all →" to a future audit screen.

### Mutation guard

WHEN `MainViewModel.IsSyncRunning == true`, THEN:
- All fields render at 0.94 opacity.
- `Save profile` button shows tooltip "Stop the engine to save changes."
- Clicking `Save profile` calls `MainViewModel.SaveDatabaseProfile()` which returns early with a logged warning (see [§05](05-behavior.md#engine-mutation-guard)).

---

## 4. Database connections

**Route:** `databases`. **Rail highlight:** Databases.

List on the left, editor on the right. The selected list item drives the editor.

### Page chrome

| Element | Component | Notes |
|---|---|---|
| `CommandBarMain` | CommandBar | Groups: **Connection** (New, Edit, Delete — `dim` when selected has `UsedByCount > 0`), **Verify** (Test `pri`, Test all), **Tools** (Logs, Docs). |
| `PageHeaderMain` | PageHeader | Heading "Database connections". Sub: "Define the targets that company profiles point at." |

### Left — list

`x:Name="DbList"`. Items bind to `DatabaseProfiles`. Selection bound to `SelectedDatabaseProfile`.

Each list item:
- Name (bold) + last-test Pill (`OK · 2m` / `Untested` / `Failed · 10m`)
- `{Tech} · {Server}:{Port}` (caption, mono server)
- "Used by {n} companies" (subtle)

Selected: 3 px `accent` left strip + `accent-soft` background.

### Right — editor

`EditDbProfileCard` — fields: `Name` (focused on new), `Tech` (combobox: PostgreSQL / SQL Server), `Server`, `Port`, `Username`, `Password` (masked).

Below fields, a one-line "paste a connection string" input. WHEN pasted, parse via `MainViewModel.TryParseConnectionString` and populate the fields above. See [§05](05-behavior.md#connection-string-auto-parse).

Footer:
- **Test connection** (calls `TestDatabaseConnection()`) — result rendered inline as a Pill.
- **Discard** — `CancelDbEdit()`.
- **Save profile** (primary) — `SaveDatabaseProfile()`.

Below the editor card, a second card "Companies using this connection" lists profile names that point at this DB (cross-link to each company profile).

---

## 5. Live sync log

**Route:** `logs`. **Rail highlight:** Sync log.

Live streaming worker output. Pre-cycle KPIs at the top, scrolling terminal below.

### Page chrome

| Element | Component | Notes |
|---|---|---|
| `CommandBarMain` | CommandBar | Groups: **Stream** (Pause/Resume — toggles `EngineRunning`, Reconnect, Clear), **Filter** (Level, Company), **Export** (Save .log, Copy). Right slot: filter input. |
| `PageHeaderMain` | PageHeader | Heading "Live sync log". Sub: "Streaming from background worker." |

### KPI strip

Four chips (Pill-on-card combos): Engine state, Current cycle ({time} · {n} events), Throughput (rows/s), Open conns ({n}/5).

### Log surface

`x:Name="LogStream"`. A monospace `RichTextBox` (read-only) inside a card. Background `layer-2`. Auto-scroll to bottom unless the user has scrolled away from the bottom (then pause auto-scroll until they return).

Each line is `{HH:MM:SS} {LEVEL} {message}`:
- INFO  → text-muted
- WARN  → `#b45309`
- ERROR → `#dc2626`

A blinking caret renders at the end **when** `EngineRunning == true` AND the log is at the bottom.

### Behavior

- Buffer cap: **500 lines**. Older lines drop off the top.
- `Clear` empties the buffer; does not stop the engine.
- `Save .log` writes the current buffer to a user-selected `.log` file.

---

## 6. Sync history

**Route:** `history`. **Rail highlight:** History.

Past run records across all companies. 2-pane layout: grid on the left, run-detail panel on the right.

### Page chrome

| Element | Component | Notes |
|---|---|---|
| `CommandBarMain` | CommandBar | Groups: **Run** (Re-run, Refresh), **Filter** (Company, Range, Status), **Export** (CSV, Copy). |
| `PageHeaderMain` | PageHeader | Heading "Sync history". Sub: "All runs across all companies." |

### Grid (left)

`x:Name="RunsGrid"`. Columns:

| Col | Width | Bind |
|---|---|---|
| When     | 140 px | `StartedAt` (formatted "Today HH:MM" / "Yesterday HH:MM" / "DD-MMM HH:MM") |
| Company  | 1.4×   | `Company.Name` |
| Mode     | 0.9×   | `Mode` |
| Result   | 1×     | `ResultSummary` (e.g. "+1,204 rows" / "Auth failed") |
| Dur.     | 70 px  | `Duration` (mono) |
| Status   | 80 px  | `Status` Pill |

Click selects (accent-soft + strip). Selection drives the right panel.

### Detail panel (right) — 340 px

Three cards stacked:

1. **Header card** — Company name + time + tone-appropriate "Completed" / "Completed with warnings" / "Failed" pill + Mode pill.
2. **Stats card** — 2×3 grid: Started, Ended, Duration, Retries, Rows in, Rows written.
3. **Entity breakdown card** — Rows per entity type: Vouchers / Ledgers / Stock items / Groups, each with a count and a tone Pill.
4. **Log excerpt card** — last ~4 lines from the worker log for this run, mono.

Below: "View full log →" navigates to the Logs screen filtered to this run's timeframe.

---

## 7. Settings

**Route:** `settings`. **Rail highlight:** Settings.

Left sub-nav, right detail. No CommandBar on this screen (only a PageHeader).

### Page chrome

`PageHeaderMain` heading "Settings", sub "Application preferences · Tally connection".

### Sub-nav (left, 200 px)

Items: Tally connection (default), General, Notifications, Auto-start, Logging & retention, About.

### Right pane

#### Tally connection section

Fields: Tally server IP / host (mono), Port (mono), `tally.exe` path (mono), `tally.ini` path (mono).
Checkboxes: Auto-start Tally if not running, Launch this app at sign-in, Minimize to tray on close.

Footer: **Test connection** → toast "Tally reachable · 0.2s" on success.

Right of the heading: `Reachable · v3.0.1` ok pill (reflects most recent test).

#### Active companies in Tally subsection

Lists current open companies with `Linked` / `Not linked` pill. Refresh button at top.

---

## 8. First-run wizard

**Route:** `wizard`. **Rail hidden.**

A 6-step linear setup flow. Shown once on first launch; can be re-invoked from Help → "Re-run setup."

### Steps

1. **Welcome** — short intro, "Get started" button.
2. **Tally** — same fields as Settings → Tally connection. Test connection inline.
3. **Database** — pick or paste a connection string; same fields as Databases editor.
4. **Companies** — link first company (Company picker), set DB target.
5. **Schedule** — choose Mode + Interval defaults to apply to that company.
6. **Review** — read-only summary; "Finish" launches the engine.

### Step indicator

Top of every step: a horizontal step row.
- Completed: green circle with checkmark.
- Current: accent-filled circle with the index.
- Future: outlined circle with the index, text-muted.

### Footer

`< Back` (disabled on step 1) — `Skip` (subtle, jumps to dashboard with engine paused) — `Continue →` (primary).

---

## 9. Company picker modal

Triggered by: **Detect from Tally**, **Add company**, **Re-detect** (on Company profile), unlinked-companies hint.

### Content

| Element | Notes |
|---|---|
| Header | "Select a company" + sub "{n} companies are currently open in Tally Prime." Close X top-right. |
| List | One row per company. Row = avatar (2-letter initials) + name + optional Consolidated info Pill + check icon if selected. |
| Footer | "Tip: pin a default in Settings." subtle text + Cancel + **Select company** primary. |

### Behavior

- Click row to select; clicking again re-confirms but doesn't dismiss.
- Esc or backdrop click dismisses (= cancel).
- WHEN the active Tally returns 0 companies, do NOT open the modal; instead show toast "No active companies. Open one in Tally Prime."
- WHEN the active Tally returns 1 company, do NOT open the modal; auto-assign and toast "Linked {name}."
- WHEN > 1, open this modal.

This replaces v1's "Modal Company Selector Window" with the same behavior but native to WinUI.

---

## 10. System tray + toast

Implemented in `Services/TrayController.cs`. Wraps `Windows.UI.Notifications` (or `WinForms NotifyIcon` if WinUI tray support is unavailable on the host Windows version).

### Tray icon

- Color: accent when engine running, neutral when idle, red when ≥ 1 company has `Status == 'err'`.
- Tooltip: `Tally Sync · {engineState} · {ok}/{total} healthy`.

### Tray context menu

Single click → toast quick-status. Right click → menu:

| Item | Action |
|---|---|
| Engine: {running/idle} (header — non-clickable) | — |
| Open dashboard | Show + focus main window |
| Run all now | `BackgroundSyncWorker.TriggerManualSync(null)` |
| Pause / Resume | toggles engine |
| View live log | Show main window + nav to `logs` |
| Settings… | Show main window + nav to `settings` |
| ───── | (separator) |
| **Exit** | `MainWindow.ExitApplication()` (see [§05 window lifecycle](05-behavior.md#window-lifecycle)) |

### Toasts

Triggered by: engine state changes, sync errors, "Run now", connection tests, config saves. Specifics in [§05 toasts](05-behavior.md#toasts).
