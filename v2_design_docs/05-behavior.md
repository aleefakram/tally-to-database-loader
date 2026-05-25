# §05 — Behavior & State

State machines, guards, navigation rules, and event taxonomy. Everything written as `WHEN X, THEN Y` for deterministic translation.

---

## Index

1. [Routes & navigation](#routes--navigation)
2. [Engine state machine](#engine-state-machine)
3. [Engine mutation guard](#engine-mutation-guard)
4. [Per-company status](#per-company-status)
5. [Threading](#threading)
6. [Window lifecycle](#window-lifecycle)
7. [Modals](#modals)
8. [Toasts](#toasts)
9. [Connection-string auto-parse](#connection-string-auto-parse)
10. [Validation rules](#validation-rules)
11. [Keyboard shortcuts](#keyboard-shortcuts)

---

## Routes & navigation

The view-model holds a **route stack**: `Stack<Route>`. The current route is `Peek()`. Push to navigate, pop to go back.

```ts
type Route =
  | { screen: 'dashboard' }
  | { screen: 'companies' }
  | { screen: 'company', id: string }
  | { screen: 'databases', dbId?: string }
  | { screen: 'logs' }
  | { screen: 'history', filter?: { companyId?: string, runId?: string } }
  | { screen: 'settings', section?: string }
  | { screen: 'wizard', step?: number }
```

### Rules

1. WHEN the user clicks a rail item, THEN replace the stack with a single-frame `{ screen: <id> }`. Rail navigation is NOT a push — it resets the back history.
2. WHEN the user clicks a card on Dashboard or a row on Companies (double-click), THEN push `{ screen: 'company', id }`.
3. WHEN on a non-top-level route (currently just `company`), THEN the title-bar back arrow is enabled. Click pops one frame.
4. WHEN the user clicks a breadcrumb segment, THEN pop the stack until the matching segment is on top.
5. WHEN a modal is open, navigation actions are deferred until it closes.

### Rail highlight rules

The rail "active" item is computed from the **current route**:

| Route | Rail highlight |
|---|---|
| `dashboard` | Dashboard |
| `companies`, `company` | Companies |
| `databases` | Databases |
| `logs` | Sync log |
| `history` | History |
| `settings` | Settings |
| `wizard` | (rail hidden) |

---

## Engine state machine

The background sync engine has three states:

```
        ┌──────┐    Start    ┌─────────┐
        │ idle ├────────────►│ running │
        └──▲───┘             └────┬────┘
           │                 Pause│
           │                      ▼
           │              ┌──────────┐
           └──────Stop────┤  paused  │
                          └──────────┘
```

- **idle** — no worker. Cycles do not run. Manual `Run now` returns immediately with an info toast "Engine is idle — start it to sync."
- **running** — worker active. Cycles run every 60 s (configurable). Log streams.
- **paused** — worker exists but is suspended. Auto-cycles do not fire. Manual `Run now` IS allowed; it triggers a one-shot and returns to paused.

### Transitions

| From | Action | To | Effect |
|---|---|---|---|
| idle    | Start   | running | `_worker = new BackgroundSyncWorker(_repo, ...)`. Subscribe to `OnLogMessage`, `OnSyncCompleted`. `_worker.Start()`. Toast "Engine started". |
| running | Pause   | paused  | `_worker.Pause()`. Log "engine paused at HH:MM:SS". Toast "Engine paused". |
| paused  | Resume  | running | `_worker.Resume()`. Toast "Engine resumed". |
| running | Stop    | idle    | `_worker?.Dispose()`; `_worker = null`. Toast "Engine stopped". |
| paused  | Stop    | idle    | Same as above. |

### Engine state exposure

`MainViewModel.IsSyncRunning : bool` — true when state == running. Used by all mutation guards (see below).
`MainViewModel.EngineState : EngineState { Idle, Running, Paused }` — exposed for richer UI hints (Pause vs Stop labels, etc.).

### Auto-stop on shutdown

WHEN `SessionEnding` or `MainWindow.ExitApplication()` fires, THEN `_worker?.Dispose()` is called BEFORE the WPF application shuts down. This prevents a final SQLite write from racing with disposal.

---

## Engine mutation guard

When the engine is running, configuration writes are dangerous. The v1 spec already established this rule (§8.1). v2 keeps the rule and clarifies the UI signaling:

| Operation | Guard |
|---|---|
| `SaveTallySettings()`        | early-return + log warning |
| `SaveDatabaseProfile()`      | early-return + log warning |
| `DeleteDatabaseProfile()`    | early-return + log warning |
| `SaveCompanyProfile()`       | early-return + log warning |
| `DeleteCompanyProfile()`     | early-return + log warning |
| `TestDatabaseConnection()`   | early-return + log warning |
| `DetectActiveCompaniesAsync()` | early-return + log warning |

Each early-return writes a single `WARN` line to the log: `[guard] {operation} skipped — engine running`.

### UI signaling

WHEN `IsSyncRunning == true`, THEN on every screen with mutable controls:
1. The whole editor card opacity drops to **0.94** (subtle).
2. **Save** / **Delete** / **Test** buttons are visually enabled BUT clicking them triggers the toast: `"Stop the engine to save changes."` (kind: `warn`).
3. The PageHeader shows an extra `Engine running — edits locked` info Pill alongside the status pill.
4. CommandBar items `Delete` in the Profile group and similar destructive items render `dim: true`.

**Do not** disable the buttons with `IsEnabled="false"`. They must remain focusable and accessible — the toast is the rejection signal. This matches user expectation set by the v1 spec §8.2 but improves discoverability.

---

## Per-company status

`CompanyProfile.Status` is recomputed by `BackgroundSyncWorker` after each cycle for that company.

| Status | Rule |
|---|---|
| `ok`   | Last run succeeded AND `now - LastRunAt <= 2 × IntervalMinutes`. |
| `warn` | Last run succeeded OR partially succeeded BUT `now - LastRunAt > 2 × IntervalMinutes` (i.e. stale). |
| `err`  | Last run failed OR `ErrorCount24h >= 3`. |
| `idle` | `Enabled == false` AND `LastRunAt == null`. (Never synced.) |

WHEN the worker writes to a profile's status, it dispatches to UI via `Application.Current.Dispatcher.Invoke`.

---

## Threading

All worker callbacks that touch `MainViewModel` properties or `ObservableCollection` items MUST marshal:

```csharp
_worker.OnLogMessage += (msg) => {
    Application.Current.Dispatcher.Invoke(() => {
        LogOutput += $"{DateTime.Now:HH:mm:ss} {msg}\n";
        StatusText = msg;
    });
};
```

Direct writes from worker threads cause `InvalidOperationException: The calling thread cannot access this object because a different thread owns it.`

For high-frequency callbacks (`OnLogMessage` can fire many times per second), batch with a 100 ms tick: accumulate into a `ConcurrentQueue<string>`, flush in a `DispatcherTimer`.

---

## Window lifecycle

The v1 spec §8.3 established the lifecycle. v2 keeps it verbatim:

| Event | Handler | Behavior |
|---|---|---|
| User clicks `✕` on title bar | `MainWindow.OnClosing` | If `_isExiting == false`: `e.Cancel = true; Hide();` (minimize to tray). Else exit. |
| Tray menu → Exit | `MainWindow.ExitApplication()` | `_isExiting = true; Application.Current.Shutdown();` |
| Windows shutdown / sign-out | `Application.SessionEnding` | `_isExiting = true;` and call `_worker?.Dispose()`. Do not block — the OS will kill us otherwise. |
| Single-instance: second app launch | `App.OnStartup` mutex check | Show the existing window via inter-process signal (NamedPipe), then exit the new process. |

---

## Modals

| Modal | Trigger | Dismissal |
|---|---|---|
| Company picker | `DetectActiveCompaniesAsync` returns > 1 OR user clicks "Detect from Tally" / "Add company" / "Re-detect" / unlinked-hint | Esc / backdrop click / Cancel button → cancel; "Select company" → commit + toast |
| "Are you sure?" delete confirm | Click `Delete` on a Company profile OR DB connection (only when used by 0) | Cancel / Esc → no-op; Confirm → delete + toast |
| Connection failed details | Click "Show details" on a failed Test connection toast | Esc / Close button |

WHEN a modal is open, the rail and CommandBar are not interactive (overlay z-order traps clicks).

---

## Toasts

Triggered events and copy templates. Toasts auto-dismiss in **4.5 s**.

| Event | Kind | Title | Body |
|---|---|---|---|
| Engine start | info | "Engine started" | "Background worker spinning up…" |
| Engine pause | warn | "Engine paused" | (none) |
| Engine stop | warn | "Engine stopped" | "Background worker disposed." |
| `Run now` for company | info | "Sync queued" | "{name} will run on the next worker tick." |
| Test connection success | ok | "Connection OK" | "{dbName} responded in {ms}ms." |
| Test connection failure | err | "Connection failed" | "{dbName}: {exception.Message.Truncate(120)}." Action: "Show details" → opens modal. |
| Save settings success | ok | "Saved" | "Tally connection settings updated." |
| Save settings while engine runs | warn | "Engine is running" | "Stop the engine to save changes." |
| Detect — 0 companies | warn | "No active companies" | "Open a company in Tally Prime, then try again." |
| Detect — 1 company | ok | "Company linked" | "{name} is now linked." |
| Sync error (per company, ≥ 3 consecutive errors) | err | "Sync paused" | "{name} failed 3× — schedule paused." Action: "Open profile" → navigates. |

Stack origin bottom-right, 18 px from right and 32 px from bottom. Maximum **5 toasts** visible; older ones drop off the top of the stack.

---

## Connection-string auto-parse

WHEN the user pastes into the `DbServer` field on the Databases editor OR the wizard's database step, AND the input starts with `postgresql://` / `postgres://` OR contains `Server=` / `Host=` / `Database=` / `Initial Catalog=`, THEN run `TryParseConnectionString(input)`:

- `postgresql://user:pwd@host:port/db?params` → sets `DbTech = "postgres"`, `DbServer = host`, `DbPort = port`, `DbUsername = user`, `DbPassword = pwd`, and (if path present) `JobTargetCatalog = db.TrimStart('/')`.
- ADO.NET style → use `System.Data.Common.DbConnectionStringBuilder` to parse keys; map `Server` / `Host` → `DbServer`, `Database` / `Initial Catalog` → `JobTargetCatalog`, `User Id` / `User ID` / `Username` → `DbUsername`, `Password` → `DbPassword`.

On success, show toast `info` "Connection string detected" body "Filled {n} fields."

---

## Validation rules

Validated **at save time** by `MainViewModel`. Inline visual hints (red bottom border on the field) shown only if validation fails — the user is not slowed down while typing.

### TallySettings

- `TallyServer`: non-empty; must parse as a hostname OR IPv4. Inline message: "Enter an IP or hostname."
- `TallyPort`: integer 1–65535. Default 9000.
- `TallyExePath`: optional but if set must exist on disk. Inline: "File not found."

### DatabaseProfile

- `Name`: non-empty; unique across `DatabaseProfiles`. Inline: "Name already in use."
- `Server`: non-empty.
- `Port`: 1–65535. Default 5432 (postgres) / 1433 (mssql) based on `Tech`.
- `Username`: non-empty.
- `Password`: non-empty (warned, not blocking). Inline: "Empty passwords are accepted but not recommended."

### CompanyProfile

- `Name`: non-empty, must match a Tally-detected company name OR be flagged "Tally not currently open" (still allowed).
- `Db`: must reference an existing `DatabaseProfile`.
- `TargetCatalog`: non-empty; restricted to `[a-zA-Z0-9_\-]+` per database engine constraints.
- `IntervalMinutes`: 1–1440.
- `Mode`: must be `Full` or `Incremental`.
- `EntityFlags`: at least one entity selected.

---

## Keyboard shortcuts

| Key | Effect | Scope |
|---|---|---|
| `Ctrl+F` | Focus the search input in the CommandBar | Dashboard, Companies, Logs |
| `Ctrl+R` / `F5` | Refresh page data | All pages |
| `Ctrl+N` | New (Add company / New DB connection) | Dashboard, Companies, Databases |
| `Del` | Delete the selected item (with confirm) | Companies, Databases |
| `Enter` | Open the selected row (= double-click) | Companies, History |
| `Esc` | Close modal OR go back if back is enabled OR clear search | global |
| `Ctrl+,` | Open Settings | global |
| `Ctrl+L` | Switch to Sync log | global |
| `Ctrl+H` | Switch to History | global |
| `F1` | Open Docs (external link) | global |
