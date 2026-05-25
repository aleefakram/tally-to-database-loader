# §01 — Overview & Information Architecture

## Product summary

A Windows desktop utility that runs on the same machine as Tally Prime, detects open Tally companies, and syncs each company's data into a target database (PostgreSQL or SQL Server) on a per-company schedule. Runs as a single-instance tray application with a WPF (WinUI 3 Fluent) front end.

The user's mental model is **"each company I manage in Tally has one place it syncs to."** The UI is built around this.

---

## Visual style

- **Windows 11 Fluent / WinUI 3.** NavigationView left rail, Mica-style layered surfaces, native caption buttons, 8 px radii.
- **Light by default**, dark theme available. Both shipped from day one.
- **Accent** used sparingly — primary action, active nav indicator, focused input underline.
- **Status colors** used only for run/health state: green (healthy), amber (stale), red (error), neutral (idle).
- **No emoji**, no marketing-style gradients, no novelty fonts. Segoe UI Variable throughout.

Full tokens in [`02-design-tokens.md`](02-design-tokens.md).

---

## Information architecture

```
+--- NavigationView Rail (always visible, 220 px) ---+
|  ☰  Search                                          |
|                                                     |
|  ●  Dashboard         <- card grid of companies     |
|     Companies         <- management list (1:1 jobs) |
|     Databases         <- connection profiles        |
|     Sync log          <- live streaming worker log  |
|     History           <- run history + run detail   |
|                                                     |
|  [Engine: ● running]                                |
|     Settings          <- Tally conn + general       |
+-----------------------------------------------------+
```

Six primary screens. Plus three system-surface screens (First-run wizard, Tray menu + toast, Company picker modal) accessible by context.

### Screen list

| Route | Screen | File reference (XAML) |
|---|---|---|
| `dashboard` | **Dashboard** — card grid of all companies, status at a glance | `Views/DashboardPage.xaml` |
| `companies` | **Companies list** — dense management grid | `Views/CompaniesPage.xaml` |
| `company`   | **Company profile** — the per-company sync profile (1:1 with company) | `Views/CompanyProfilePage.xaml` |
| `databases` | **Database connections** — list + editor split | `Views/DatabasesPage.xaml` |
| `logs`      | **Live sync log** — streaming worker output | `Views/LogPage.xaml` |
| `history`   | **Sync history** — runs grid + run-detail panel | `Views/HistoryPage.xaml` |
| `settings`  | **Settings** — Tally connection + general + about | `Views/SettingsPage.xaml` |

Plus:

| Route | Screen | File reference |
|---|---|---|
| `wizard`    | **First-run setup wizard** — 6 steps, no rail | `Views/SetupWizardWindow.xaml` |
| (overlay)   | **Company picker modal** — list of currently-open Tally companies | `Views/CompanyPickerDialog.xaml` |
| (system)    | **Tray icon + context menu + toast** — Engine status, Run all, Pause, Exit | `Services/TrayController.cs` |

Detailed element-by-element breakdown in [`04-screens.md`](04-screens.md).

---

## Headline change vs v1 spec

The v1 spec exposed a `SyncJob` as a first-class entity in a "Dashboard Monitor" tab and an editor in a "Configuration & Profiles" tab. **v2 collapses this:**

- The user does not create a sync job. They **link a Tally company to a database** and that link **is** the sync profile.
- One company = one profile. Period. No two profiles per company. Migration in [`07-migration.md`](07-migration.md).
- The Dashboard surfaces companies (not jobs). The Companies list surfaces companies (not jobs). The Company profile screen is the only editor.
- The `SyncJob` table can stay in SQLite; the UI just never shows the abstraction.

---

## Engine model

The background sync engine is a single global process, not per-job. It is started and stopped from any CommandBar (the buttons are shown on every page, mirrored). When running:

- It enumerates all enabled company profiles.
- For each profile that is due (`now >= lastRunAt + intervalMin`), it executes one sync.
- It writes to the Live sync log via `OnLogMessage`.
- It updates `CompanyProfile.LastRunAt` and `CompanyProfile.Status` via the repository, marshaled to the UI dispatcher.

Per-company **"Run now"** is a manual override — it queues a one-shot sync regardless of schedule, via `BackgroundSyncWorker.TriggerManualSync(companyId)`.

Behavior rules and state machine in [`05-behavior.md`](05-behavior.md).

---

## Navigation rules

1. **Left rail is always visible** except on the First-run wizard.
2. **Active item** is highlighted with the accent indicator bar (3 px, left edge).
3. Clicking a card on Dashboard or a row on Companies opens the **Company profile** screen. The rail highlights "Companies" (not Dashboard) while on the profile.
4. The title bar shows **Back arrow** when not on a top-level route. Back pops one frame.
5. Breadcrumb in the page header is clickable: clicking "Companies" returns to the Companies list.
6. Modals (Company picker) do not change the route stack.

---

## Density & responsiveness

- Target window size: **1280 × 800** minimum, **1440 × 900** preferred. Resizable.
- Minimum supported: 1100 × 700. Below that, rail collapses to icons-only (not implemented in prototype but accommodated in spec).
- All grids are **virtualized** when row count > 50.

---

## Accessibility

- Every interactive control has a visible focus indicator (accent 2 px underline for inputs, accent ring for buttons).
- Color is never the only signal — status pills carry text ("Healthy", "Stale", "Error", "Not configured") alongside color.
- Keyboard shortcuts: `Ctrl+F` (focus search), `Ctrl+R` (refresh page), `F5` (Run all on Dashboard), `Esc` (close modal / go back), `Ctrl+,` (Settings).
- Tab order follows visual order. All form controls reachable.
- Screen-reader names: every CommandBar item exposes `AutomationProperties.Name` = the button label.
