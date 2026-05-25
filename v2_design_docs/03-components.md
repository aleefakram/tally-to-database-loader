# §03 — Component Catalog

The visual primitives used everywhere. Names match the prototype's React components and CSS classes. WPF mapping below each.

The prototype implementation is in `primitives.jsx`. WPF style resource files referenced below should live under `Themes/`.

---

## Index

1. [AppFrame](#1-appframe)
2. [CommandBar](#2-commandbar)
3. [PageHeader](#3-pageheader)
4. [StatusBar](#4-statusbar)
5. [Card](#5-card)
6. [Button](#6-button)
7. [Input / TextBox](#7-input--textbox)
8. [Field (form field group)](#8-field-form-field-group)
9. [Pill (status badge)](#9-pill-status-badge)
10. [DataGrid row / header](#10-datagrid-row--header)
11. [NavItem (NavigationView item)](#11-navitem)
12. [Toast](#12-toast)
13. [Modal](#13-modal)
14. [Icon set](#14-icon-set)

---

## 1. AppFrame

The outer window shell. Title bar + NavigationView rail + content slot.

**Props**

| Prop | Type | Notes |
|---|---|---|
| `title` | string | App name. Default "Tally Sync". |
| `subtitle` | string | Current screen, shown after em-dash. |
| `nav` | route id | Highlights the active rail item. |
| `onNavigate(id)` | function | Called when a rail item is clicked. |
| `onBack` | function or null | If provided, back arrow in title bar is enabled. |
| `syncRunning` | boolean | Controls engine indicator (pulsing dot when true). |
| `hideRail` | boolean | Used only on the First-run wizard. |

**Structure**

```
AppFrame
├── TitleBar (32 px)
│   ├── BackButton + ForwardButton (forward always disabled in v2)
│   ├── AppIcon + Title + " — " + Subtitle (drag region)
│   └── CaptionButtons: minimize ─ / maximize ▢ / close ✕
└── Body (flex row)
    ├── Rail (220 px) — if !hideRail
    │   ├── Hamburger toggle
    │   ├── Search input (28 px)
    │   ├── NavItem[]: Dashboard, Companies, Databases, Sync log, History
    │   ├── (spacer)
    │   ├── EngineStatusCard (dot + label + …)
    │   └── NavItem[]: Settings
    └── Content slot
```

**WPF mapping**

- Host inside a `Window` with `WindowChrome.GlassFrameThickness="-1"` for Mica.
- Title bar implemented via `WindowChrome.CaptionHeight="32"` + custom `Border` with the back/forward buttons and drag region.
- Rail implemented as `muxc:NavigationView PaneDisplayMode="Left" OpenPaneLength="220" IsBackButtonVisible="Collapsed"`.
- Use `NavigationViewItem` for each rail item. Bind `Tag` to the route id, handle `ItemInvoked` to call `MainViewModel.Navigate(id)`.

---

## 2. CommandBar

Ribbon-style toolbar at the top of every primary page. Icon-over-label buttons grouped by intent, separated by vertical dividers.

**Props**

| Prop | Type | Notes |
|---|---|---|
| `groups` | `CommandGroup[]` | See shape below. |
| `right` | ReactNode | Right-aligned slot — usually a search input. |

```ts
type CommandItem = {
  icon: ReactNode,    // Icon (16 px)
  label: string,      // 11 px label
  kind?: 'pri',       // accent variant
  dim?: boolean,      // disabled-looking
  onClick?: () => void,
}
type CommandGroup = { label: string, items: CommandItem[] }
```

**Behavior rules**

- `dim: true` items render at 0.45 opacity, are not clickable, and the cursor stays default. Used for context-sensitive buttons when no row is selected.
- The group label appears under the row of items as a 10 px caption.
- A 1 px vertical `divider` separates groups, with `space-1` margin on each side.
- Maximum 5 items per group recommended; max 4 groups per command bar.

**WPF mapping**

- Use `muxc:CommandBar` (WinUI) or hand-rolled `ItemsControl` with a horizontal `WrapPanel`.
- Each item is an `AppBarButton` with `LabelPosition="Default"` (label below icon).
- Disabled state via `IsEnabled` binding (not opacity).

---

## 3. PageHeader

Title + subtitle + breadcrumb + inline actions, between the CommandBar and the body.

**Props**: `heading`, `sub?`, `breadcrumb?`, `actions?`.

**Rules**

- Page title uses `display` style (22 px / 600).
- Breadcrumb (if any) sits **above** the title at 11.5 px / subtle. The last segment is the current page (non-clickable). Earlier segments are clickable and call `navigate(...)`.
- `actions` is a horizontal flex of `Button`s right-aligned to the title row.

---

## 4. StatusBar

Footer bar. 22 px tall, `layer-2` background. Left/right text slots.

**Use for**: ambient status info (`Engine running · last cycle 2 min ago`), counts (`6 companies · 1 selected`), persistent connection hints (`Tally 192.168.1.40:9000 · OK`).

**Never use for**: actionable controls. There are no buttons in the status bar.

---

## 5. Card

Generic content surface.

- Background `layer`. 1 px `stroke`. `radius-2xl` (8 px). No drop shadow.
- Internal padding `space-7` (16 px) for forms; `space-6` (14 px) for compact info cards.

WPF: `<Border Background="{StaticResource LayerBrush}" BorderBrush="{StaticResource StrokeBrush}" BorderThickness="1" CornerRadius="8" Padding="16"/>` — define `<Style x:Key="Card" TargetType="Border"/>`.

---

## 6. Button

Three variants. All 30 px tall, `radius-md` (4 px), 12 px horizontal padding.

| Class | Use | Style |
|---|---|---|
| `.w-btn`        | Default secondary | `fill-default` bg, `stroke` top/sides, `stroke-strong` bottom (WinUI "lit edge") |
| `.w-btn.pri`    | Primary action — one per region max | `accent` bg, white text (black in dark mode), 1 px darker bottom border |
| `.w-btn.subtle` | Tertiary / icon-only | transparent, hover shows `fill-hover` |
| `.w-btn.danger` | Destructive verb | inherits `.w-btn`, color = `status-err` text |

Modifiers:

- `.w-btn.icon` — 30×30 square, icon only. Use in dense toolbars.
- Inline kbd hint: `<span class="kbd">Ctrl+F</span>` (11 px monospace, `stroke` border).

WPF: `<Style x:Key="ButtonDefault" TargetType="Button"/>`, `<Style x:Key="ButtonPrimary" TargetType="Button" BasedOn="{StaticResource ButtonDefault}"/>`, `<Style x:Key="ButtonSubtle"/>`, `<Style x:Key="ButtonDanger"/>`.

---

## 7. Input / TextBox

WinUI 3 TextBox styling.

- 30 px tall, 10 px horizontal padding, `radius-md`.
- Background `fill-default`. 1 px `stroke` on top/sides. 1 px `stroke-strong` on the bottom (heavier bottom border is the WinUI signature).
- **On focus**: bottom border becomes 2 px `accent`. The container compensates by setting `padding-bottom: 0` so total height stays constant.

Class `.w-input.focus` is the focused state in the prototype (static — for showing the visual in mockups).

WPF: `<Style x:Key="TextBoxStyle" TargetType="TextBox"/>` overriding the template. The focused-bottom-accent effect uses a `<VisualStateManager>` with a `Storyboard` animating the `BorderThickness` and `BorderBrush`.

---

## 8. Field (form field group)

Vertical group: **label / input / hint?**.

```
┌─ label (11 px / text-muted)
│
└─ input (30 px)
   └─ hint (10 px / text-subtle)
```

Always paired. Never put a bare TextBox without its label above.

---

## 9. Pill (status badge)

Compact status badge with a colored dot + text.

| Tone | Color (text) | Background |
|---|---|---|
| `ok`      | `#0a7d3b` | `rgba(22,163,74,0.10)` |
| `warn`    | `#8a5300` | `rgba(217,119,6,0.14)` |
| `err`     | `#a2231d` | `rgba(220,38,38,0.10)` |
| `info`    | `accent`  | `accent-soft` |
| `neutral` | `text-muted` | `fill-secondary` |

20 px tall, `radius-xl` (6 px), 11 px label, 6 px circular dot, 1 px border `color-mix(currentColor 22%, transparent)`.

**Label conventions**

- Healthy / Stale / Error / Not configured / Paused — these specific strings.
- Boolean things: capitalize first letter. Don't yell.

WPF: `<Style x:Key="Pill" TargetType="Border"/>` + content presenter.

---

## 10. DataGrid row / header

Used on Companies, History, and the Database list editor.

- Use **CSS grid** (or WPF `Grid` with `ColumnDefinition` widths) — never a `DataGrid` with auto-sized columns for these layouts.
- Header row: `overline` style on a `layer-2` background, 6 px vertical padding.
- Body row: 36 px tall, 12 px horizontal padding. 1 px `divider` between rows.
- Selected row: `accent-soft` background + 3 px `accent` left strip (achieved via `box-shadow: inset 3px 0 0 0 accent`).
- Hover row: `fill-hover` background.
- Click selects. Double-click opens detail (Companies → CompanyProfile).

---

## 11. NavItem

A row in the NavigationView rail.

- 36 px tall, 12 px horizontal padding, `radius-lg` (5 px), 1 px horizontal margin.
- Icon (16 px, `text-muted`) + 12 px gap + label.
- **Active state**: `fill-hover` background + 3 px `accent` strip on the left (positioned absolutely, 8 px top/bottom inset).
- Hover state: `fill-hover` background only.

WPF: customize `NavigationViewItem` via `<Style x:Key="NavigationViewItemRevealStyle" TargetType="NavigationViewItem"/>`.

---

## 12. Toast

A floating notification card, stacked bottom-right.

- Stack origin: `bottom: 32 px, right: 18 px`. Gap between cards: `space-3`.
- Card: 260–360 px wide, `space-7` padding, `shadow-toast`, animated in via `motion-standard`.
- Auto-dismiss: **4.5 s**. User can hover to pause (not implemented yet — flag in [§05](05-behavior.md)).
- Icon in `kind` color: `check` (ok), `warn` (warn), `err` (err), `link` (info).
- Title `body-strong`, body `body / text-muted`.

WPF: implement via a `Popup` or a custom `NotificationManager` (or use `Sergey Borodin's WPFNotification` style). Toasts are managed by `MainViewModel.ToastService`.

---

## 13. Modal

Full-window overlay with a centered card.

- Backdrop: `rgba(0,0,0,0.35)`, clicking dismisses unless explicitly disabled.
- Card: `layer` background, `shadow-modal`, `radius-2xl`, max width per use (Picker = 440 px).
- Animates in via `motion-quick` fade + 4 px translate.
- Esc key dismisses.

Modal triggers in [§05](05-behavior.md#modals).

---

## 14. Icon set

The prototype uses 24 hand-tuned SVG icons (1.5 px stroke, currentColor) listed below. For production, **swap to Fluent System Icons** (Microsoft's free icon set, available as a NuGet package: `Fluent.Icons.WinForms.Glyphs` or via `Segoe Fluent Icons` font).

| Prototype key | Fluent System Icon equivalent | Used by |
|---|---|---|
| `home`       | `Home16Regular`         | rail · Dashboard |
| `companies`  | `Building16Regular`     | rail · Companies; "Detect", company avatar fallback |
| `db`         | `Database16Regular`     | rail · Databases |
| `log`        | `DocumentText16Regular` | rail · Sync log; "Save .log" |
| `history`    | `History16Regular`      | rail · History |
| `settings`   | `Settings16Regular`     | rail · Settings |
| `search`     | `Search16Regular`       | search inputs |
| `play`       | `Play16Filled`          | "Run now" |
| `stop`       | `Stop16Filled`          | "Stop" |
| `pause`      | `Pause16Filled`         | "Pause" |
| `plus`       | `Add16Regular`          | "New" / "Add" |
| `edit`       | `Edit16Regular`         | "Edit" |
| `trash`      | `Delete16Regular`       | "Delete" |
| `refresh`    | `ArrowSync16Regular`    | "Refresh" |
| `link`       | `Link16Regular`         | "Test connection" |
| `back`       | `ArrowLeft16Regular`    | title-bar back |
| `fwd`        | `ArrowRight16Regular`   | title-bar forward (always disabled) |
| `chevDown`   | `ChevronDown16Regular`  | combobox indicator |
| `chevRight`  | `ChevronRight16Regular` | breadcrumb separator |
| `check`      | `Checkmark16Regular`    | selected item, ok toast |
| `warn`       | `Warning16Regular`      | stale, warn toast |
| `err`        | `ErrorCircle16Regular`  | error toast |
| `x`          | `Dismiss16Regular`      | modal close |
| `hamburger`  | `Navigation16Regular`   | rail toggle |
| `more`       | `MoreHorizontal16Regular` | overflow menu trigger |

Do not invent new icons. If a screen needs an icon not on this list, request approval first.
