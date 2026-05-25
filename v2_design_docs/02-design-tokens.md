# §02 — Design Tokens

All numeric values used in the UI. Every value here is referenced by name from [`03-components.md`](03-components.md) and [`04-screens.md`](04-screens.md). If a token name appears here, **do not hard-code its value elsewhere** — reference the token.

---

## Color tokens

WinUI-equivalent token names are noted in parentheses. The prototype implements these via CSS custom properties (`--w-bg`, etc.). In WPF, mirror them as `<SolidColorBrush x:Key="Bg" Color="..."/>` in `App.xaml` `<Application.Resources>`.

### Light theme (default)

| Token | Hex | Notes / WinUI equivalent |
|---|---|---|
| `bg`              | `#f3f3f3`         | Window background (`SolidBackgroundFillColorBase`) |
| `layer`           | `#fbfbfb`         | Card / page-content layer (`LayerFillColorDefault`) |
| `layer-2`         | `#f6f6f6`         | Alt layer — zebra striping, command bar background |
| `rail`            | `rgba(255,255,255,0.55)` | Translucent rail surface (Mica) |
| `titlebar`        | `rgba(243,243,243,0.85)` | Translucent caption bar |
| `stroke`          | `rgba(0,0,0,0.0578)` | Control stroke default (`ControlStrokeColorDefault`) |
| `stroke-strong`   | `rgba(0,0,0,0.1622)` | Control stroke strong / button bottom border |
| `divider`         | `rgba(0,0,0,0.0803)` | Card dividers, grid row borders |
| `text`            | `#1a1a1a`         | Primary text (`TextFillColorPrimary`) |
| `text-muted`      | `rgba(0,0,0,0.6063)` | Secondary text (`TextFillColorSecondary`) |
| `text-subtle`     | `rgba(0,0,0,0.4458)` | Tertiary text |
| `text-disabled`   | `rgba(0,0,0,0.3614)` | Disabled labels |
| `accent`          | `#0067c0`         | System accent (`SystemAccentColor`) |
| `accent-2`        | `#003e92`         | Pressed accent / "lit edge" |
| `accent-soft`     | `rgba(0,103,192,0.10)` | Selection background, info-pill background |
| `fill-default`    | `rgba(255,255,255,0.70)` | Button default fill |
| `fill-secondary`  | `rgba(249,249,249,0.50)` | Subtle surface fill (settings rail) |
| `fill-hover`      | `rgba(249,249,249,0.95)` | Hover fill |
| `fill-pressed`    | `rgba(249,249,249,0.40)` | Pressed fill |

### Dark theme

| Token | Hex |
|---|---|
| `bg`            | `#1f1f1f` |
| `layer`         | `#2c2c2c` |
| `layer-2`       | `#272727` |
| `rail`          | `rgba(43,43,43,0.55)` |
| `titlebar`      | `rgba(32,32,32,0.85)` |
| `stroke`        | `rgba(255,255,255,0.07)` |
| `stroke-strong` | `rgba(255,255,255,0.14)` |
| `divider`       | `rgba(255,255,255,0.08)` |
| `text`          | `#f1f1f1` |
| `text-muted`    | `rgba(255,255,255,0.78)` |
| `text-subtle`   | `rgba(255,255,255,0.54)` |
| `text-disabled` | `rgba(255,255,255,0.36)` |
| `accent`        | `#4cc2ff` |
| `accent-2`      | `#76d1ff` |
| `accent-soft`   | `rgba(76,194,255,0.14)` |
| `fill-default`  | `rgba(255,255,255,0.06)` |
| `fill-secondary`| `rgba(255,255,255,0.04)` |
| `fill-hover`    | `rgba(255,255,255,0.08)` |
| `fill-pressed`  | `rgba(255,255,255,0.03)` |

### Status colors (theme-invariant)

These are perceptually fixed across light/dark. The corresponding `Pill` background is `color-mix(currentColor 10–14%, transparent)`.

| Token | Hex | Used for |
|---|---|---|
| `status-ok`    | `#16a34a` | Healthy, success toast, "OK" connection state |
| `status-warn`  | `#d97706` | Stale, retried, warning toast |
| `status-err`   | `#dc2626` | Error, failed connection, danger button text |
| `status-info`  | `accent`  | Mode badges ("Full", "Incremental", "Consolidated") |

### Mica background

The window body uses a layered gradient. In WPF use Mica via `WindowChrome.GlassFrameThickness="-1"` on Windows 11; in the prototype this is a CSS approximation:

```
--w-mica:
  radial-gradient(1200px 600px at 100% -10%, rgba(0,103,192,0.06), transparent 60%),
  radial-gradient(900px 600px at 0% 100%, rgba(124,58,237,0.04), transparent 55%),
  linear-gradient(180deg, #f6f6f6 0%, #efefef 100%);
```

---

## Typography tokens

Use **Segoe UI Variable** family. Font features `ss01` and `cv01` enabled.

| Token | Font / Size / Weight | Use |
|---|---|---|
| `display`      | Segoe UI Variable Display, 22 px / 600 | Page title (`PageHeader heading`) |
| `subtitle`     | Segoe UI Variable Text,    13 px / 600 | Card titles, section heads |
| `body`         | Segoe UI Variable Text,    12.5 px / 400 | Body content, buttons, inputs |
| `body-strong`  | Segoe UI Variable Text,    12.5 px / 600 | Bold body, table cell highlights |
| `caption`      | Segoe UI Variable Text,    11.5 px / 400 | Helper text, status footer, command-group labels |
| `caption-mute` | Segoe UI Variable Text,    11 px / 400, `text-muted` | Field labels above inputs |
| `overline`     | Segoe UI Variable Text,    10 px / 500, uppercase, 0.04em tracking | DataGrid column headers |
| `mono`         | Cascadia Mono, 11.5 px | Identifiers, hex codes, log lines, connection strings |

WPF mapping: declare `<Style x:Key="DisplayTextStyle" TargetType="TextBlock"/>` etc. in `Themes/Typography.xaml`.

---

## Spacing scale

Use only these values. Spacing is multiples of 4. Composite paddings (card, page) are listed under [Layout patterns](#layout-patterns).

| Token | Value | Use |
|---|---|---|
| `space-1` | 4 px  | Tight inline gap (icon ↔ label inside a chip) |
| `space-2` | 6 px  | Group of inline buttons |
| `space-3` | 8 px  | Inside a card, between label and field |
| `space-4` | 10 px | Between fields in a 1-col stack |
| `space-5` | 12 px | Between cards |
| `space-6` | 14 px | Card internal padding (small) |
| `space-7` | 16 px | Card internal padding (default) |
| `space-8` | 18 px | Section spacing |
| `space-9` | 20 px | Page top/bottom padding |
| `space-10`| 24 px | Page left/right padding |

---

## Radii

| Token | Value | Use |
|---|---|---|
| `radius-sm` | 3 px | Inline keycap badge |
| `radius-md` | 4 px | Buttons, inputs |
| `radius-lg` | 5 px | Nav items |
| `radius-xl` | 6 px | Pills, secondary surfaces |
| `radius-2xl`| 8 px | Cards, modals, window itself |
| `radius-pill` | 999 px | Status pills, the engine dot |

---

## Elevation / shadow

WPF: use `DropShadowEffect` for the explicit shadows; everything else is "elevation by layer color" (Mica/layer/layer-2 stack), not shadows.

| Token | Value | Use |
|---|---|---|
| `shadow-card`   | none — just `1px stroke` | Cards never have a drop shadow. Elevation = lighter layer color. |
| `shadow-modal`  | `0 30px 80px rgba(0,0,0,0.35)` | Picker modal, future flyouts |
| `shadow-toast`  | `0 10px 30px rgba(0,0,0,0.18)` | Toast cards |
| `shadow-window` | `0 30px 60px rgba(0,0,0,0.35), 0 0 0 1px rgba(0,0,0,0.10)` | Outer window bezel (prototype only — WPF host provides this) |

---

## Motion

Keep motion minimal. Three named durations only.

| Token | Duration / Easing | Use |
|---|---|---|
| `motion-instant`  | 80 ms, linear | Button hover/press fills |
| `motion-quick`    | 140 ms, ease-out | Screen fade-in, focus underline grow |
| `motion-standard` | 180 ms, `cubic-bezier(.2,.7,.3,1)` | Toast in/out, modal in |

Named keyframes:

- `pulse` — engine indicator. 1.6s ease-out infinite. Goes from `0 0 0 0 rgba(22,163,74,.55)` to `0 0 0 6px rgba(22,163,74,0)`.
- `caret` — log stream cursor. 1s steps(1) infinite. 50% opacity toggle.

---

## Layout patterns

These compose tokens above into the standard page chrome.

### Application window
- Minimum window size: **1100 × 700**. Preferred: **1440 × 900**.
- Title bar: **32 px** tall, translucent (`titlebar`), 1 px bottom `stroke`.
- Rail: **220 px** wide, translucent (`rail`), 1 px right `stroke`.
- Content area: `bg` background, no border.

### CommandBar (ribbon-lite)
- Height: auto, ~**54 px** (icon + label + group label).
- Horizontal padding: `space-3` (8 px).
- Vertical separator between groups: 1 px wide, `divider`, with `space-1` margin on each side.
- Each command button: 54 px wide minimum, `radius-lg` (5 px), icon on top, 11 px label below, 10 px group label under that.

### PageHeader
- Padding: `14px 24px 10px` (top/horizontal/bottom).
- Breadcrumb: `caption` style, 4 px below = display title.
- Page title: `display` style (22 px / 600).
- Sub: `body` style at `text-muted`.

### StatusBar (footer)
- Height: **22 px**. Background `layer-2`. 1 px top `stroke`. Inset 12 px.

### Cards
- Background `layer`. 1 px `stroke` border. `radius-2xl` (8 px). No shadow.
- Internal padding: `space-7` (16 px) for forms, `space-6` (14 px) for compact cards.
- Section heading inside card: `subtitle` style (13 px / 600), margin-bottom 10 px.

### Form fields (vertical stack)
- Label `caption-mute` (11 px / muted), then **4 px gap**, then input.
- Input height **30 px**, `radius-md` (4 px).
- Field-to-field gap: `space-4` (10 px).
- Two-column form: `gap: 10 px 12 px` (row/col).
- Hint text below input: `caption` style (10 px / subtle), 4 px gap from input.

### DataGrid (rows)
- Row height: **36 px** (8 px padding × 2 + content). Use `40 px` if including a checkbox column.
- Column header: `overline` style. Background `layer-2`. 1 px bottom `divider`.
- Selected row: `accent-soft` background + 3 px `accent` left strip.
- Hover row: `fill-hover`.
- Border between rows: 1 px `divider`.
