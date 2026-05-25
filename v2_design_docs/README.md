# Tally Sync — UI/UX Spec v2

**Status:** Active. Supersedes `tally-net-wpf-ui-ux-spec.md` (2026-05-20).
**Audience:** AI coding agents and senior WPF developers building the .NET port.
**Target stack:** .NET 8 + WPF (WinUI 3 Fluent visual style), SQLite repository, single-instance tray app.

---

## How to read this doc set

Read in order if you are unfamiliar with the project; otherwise jump to the section you need.

| # | File | Purpose |
|---|---|---|
| 1 | [`01-overview.md`](01-overview.md) | Product overview, information architecture, navigation model, headline behavioral changes |
| 2 | [`02-design-tokens.md`](02-design-tokens.md) | Color, typography, spacing, radius, elevation, motion — all numeric, all named |
| 3 | [`03-components.md`](03-components.md) | Component catalog — `AppFrame`, `CommandBar`, `PageHeader`, `Pill`, `w-btn`, `w-input`, `w-card`, `w-nav-item`, plus WPF style mapping |
| 4 | [`04-screens.md`](04-screens.md) | Full screen catalog — element names, controls, data bindings, interactions, validation |
| 5 | [`05-behavior.md`](05-behavior.md) | State machines, engine guards, navigation rules, modal triggers, toast events |
| 6 | [`06-data-bindings.md`](06-data-bindings.md) | View-model properties, repository methods, SQLite models, encryption rules |
| 7 | [`07-migration.md`](07-migration.md) | v1 → v2 deltas: what was removed, renamed, added, deprecated |

The **interactive prototype** lives at `Tally Sync Hi-fi.html` (in the project root). It is the canonical reference for visual style and interaction. The **wireframes** at `Tally Sync Wireframes.html` show the full screen catalog laid out side-by-side.

---

## Conventions used across this doc set

1. **Code identifiers** are fenced: `MainViewModel.StartSyncEngine()`, `SyncJob.Status`.
2. **File paths** are relative to the WPF project root: `src/TallyDbLoader.Wpf/MainWindow.xaml`.
3. **Rules** are written as `WHEN X, THEN Y` so they survive translation by AI agents.
4. **Tokens** are referenced by name (`accent`, `layer-2`, `text-muted`) — see [`02-design-tokens.md`](02-design-tokens.md) for resolved values per theme.
5. **Cross-refs** use Markdown links — always relative.

---

## Quick links

- Interactive prototype → [`../Tally Sync Hi-fi.html`](../Tally%20Sync%20Hi-fi.html)
- Wireframes catalog → [`../Tally Sync Wireframes.html`](../Tally%20Sync%20Wireframes.html)
- Browsable doc index (with embedded screens) → [`index.html`](index.html)
- v1 spec (deprecated) → [`../uploads/2026-05-20-tally-net-wpf-ui-ux-spec-484e1c5c.md`](../uploads/2026-05-20-tally-net-wpf-ui-ux-spec-484e1c5c.md)

---

## Non-negotiable rules (read first)

These five rules sit above everything. If you only read this README, read these.

1. **Companies-first IA.** A "sync job" is not a first-class entity in the UI. Each company has **exactly one** `CompanyProfile` (1:1) that bundles target DB + schedule + mode + entities. See [§01](01-overview.md#information-architecture).
2. **Engine mutation guard.** WHEN `IsSyncRunning == true`, THEN all writes through `MainViewModel` short-circuit and the corresponding UI controls render in a locked visual state. No modal alerts. See [§05](05-behavior.md#engine-mutation-guard).
3. **DPAPI at rest.** Database passwords are encrypted with `System.Security.Cryptography.ProtectedData` at the repository boundary. Plaintext exists only in memory and in UI fields. See [§06](06-data-bindings.md#secrets-handling).
4. **Single instance + tray lifecycle.** Mutex-guarded single instance. Closing the main window hides to tray; explicit "Exit" in tray context menu or `SessionEnding` shuts down. See [§05](05-behavior.md#window-lifecycle).
5. **Thread-safe UI updates.** All callbacks from `BackgroundSyncWorker` mutating observable state must marshal to the UI dispatcher: `Application.Current.Dispatcher.Invoke(...)`. See [§05](05-behavior.md#threading).
