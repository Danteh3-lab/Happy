---
version: beta
name: "DANBOT Control Deck"
description: "A dark gaming dashboard that makes live combat automation state explicit first and configuration second."
colors:
  surface-0: "#0A0B0E"
  surface-1: "#101217"
  surface-2: "#151820"
  surface-3: "#1D212B"
  surface-inset: "#07080B"
  text-1: "#F2F3F6"
  text-2: "#A7ACB8"
  text-3: "#767D8B"
  accent: "#E2554A"
  accent-hover: "#F06A5F"
  signal: "#4ADAE6"
  signal-dim: "#5A949D"
  ok: "#3DCF8E"
  warn: "#F0B35A"
  danger: "#FF5C7A"
typography:
  ui:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
  display:
    fontFamily: "Bahnschrift, Segoe UI Variable Display, Segoe UI, sans-serif"
  mono:
    fontFamily: "Cascadia Mono, Cascadia Code, Consolas, monospace"
  scale: [11, 12, 13, 15, 18, 24, 32, 44]
rounded:
  xs: "6px"
  sm: "10px"
  md: "14px"
  lg: "18px"
spacing:
  grid: "4px"
  page-max: "1180px"
  card-gap: "16px"
components:
  run-status: {}
  readiness: {}
  active-behavior: {}
  switch-tile: {}
  stat-tile: {}
  commit-bar: {}
  listbox: {}
  menu-button: {}
  help-panel: {}
  dialog: {}
  overlay-hud: {}
---

# DANBOT Control Deck Design System

## Overview

### Creative North Star

A combat HUD on a dark console: whether the bot is live is the primary instrument, what it will do next is second, configuration is third, and diagnostics stay one page away.

### Product context and register

- **Audience and primary job:** A For Honor player configuring screen-driven controller reactions who must understand the bot's next action without consulting a guide.
- **Target market and evidence:** Desktop users running the local Windows application; the WinForms/WebView2 shell and controller bridge are the source of truth.
- **Locales:** English UI. User-created profile names are displayed verbatim and safely as text.
- **Usage scene:** A small desktop window used immediately before or alongside gameplay, often under time pressure.
- **Register:** Gaming dashboard. State clarity and compact density lead; decoration (grid, glow, HUD brackets) stays behind content.
- **Memorable signature:** The Dashboard run orb and status word, paired with the Active Behavior card that states the effective F/LT and E action.
- **Anti-references:** Game-guide prose, decorative HUD clutter that competes with state, and settings screens that expose flags without explaining precedence.
- **Token ownership/runtime mapping:** `ui/styles/tokens.css` is the runtime source of truth and this file documents it. `Overlay/OverlayTheme.cs` mirrors the shared colors for the F7 overlay; `OverlayThemeTests` fails when they drift. Durable token changes update all three together.

## Colors

Five near-black surfaces separate app background, chrome (titlebar, sidebar, commit bar), cards, raised/hover states and inset wells. Coral (`--accent`) is reserved for selection, focus, the primary action and the product signature. Cyan (`--signal`) marks live readouts, counters and HUD geometry. State colors carry meaning: green `--ok` is live or committed, amber `--warn` is a recoverable pending state (unapplied timings, unsaved profile), rose `--danger` is failure. Danger is deliberately rose so it never reads as the coral accent. Every state color is paired with an icon shape and text.

## Typography

Segoe UI Variable carries controls and explanations at a 13px base. Bahnschrift, a condensed DIN-style face, is the display voice for page titles, the run status word, action names and numeric readouts (with tabular figures). Cascadia Mono is reserved for keys, values, controller labels and compact machine state. Uppercase with tracking is limited to eyebrows, stat labels and status pills; instructions use sentence case.

## Layout

- Titlebar 48px, sidebar 212px, commit bar 60px; page content max 1180px.
- Five pages, chosen from the sidebar or Ctrl+1–5: **Dashboard**, **Combat**, **Heroes & Timing**, **Controller & Binds**, **Diagnostics**. The last page is remembered per viewer.
- The **commit bar** is fixed below every page, so "Timings not applied" and "Profile not saved" are always visible together with Apply and Save.
- Dashboard uses a 12-column grid: run status (8) + readiness (4), active behavior (7) + quick toggles (5), live signal (7) + session (5). Configuration pages use two columns.
- Breakpoints: 1040px (dashboard cards go full width), 900px (single-column pages, 176px sidebar), 680px (icon-only 60px sidebar). `max-height: 620px` (200% scaling) compacts the chrome so **Start bot** stays above the fold.
- Controls wrap instead of truncating essential state.

## Elevation & Depth

Hierarchy comes from tonal surfaces, hairline borders and one inset top highlight. Glow is reserved for live state (run orb, active nav icon, enabled tiles) and never carries meaning on its own. The page background has a faint 32px grid; the run card adds HUD corner brackets.

## Shapes

Cards use a 14px radius, buttons and fields 10px, keys and small chips 6px. Pills are reserved for status. Square key chips (`F / LT`, `E`, `kbd`) distinguish input vocabulary from actions.

## Components

### Foundational visual states

All controls provide default, hover, focus-visible, pressed and disabled states. Focus uses a two-pixel coral ring with offset. Elements with a `data-state` (`ok`, `warn`, `bad`, `busy`, `idle`) show the icon whose `data-for` matches, so every state has its own shape as well as color.

### Run status

A large orb with a state glyph (▶ live, ❚❚ paused, ■ standby, ! error), the status word in the display face, a sentence of explanation, **Start bot** and **Pause** (F6). The orb ring spins only while live and only when motion is allowed. The titlebar run pill repeats the state on every page.

### Switches and tiles

Switch rows (`role="switch"` checkboxes) are the configuration primitive. Dashboard quick toggles are switch tiles bound to the same settings, so changing either updates both. Host-owned toggles (orange parry, vision overlay, anchor scan) are `aria-pressed` buttons with an ON/OFF label and their hotkey.

### Stat tiles and counters

Stat tiles show a label, a value in the display face, and a left state bar. Counters show large cyan numbers. Values update in place at the 5 Hz status rate; only changed text is written.

### Commit bar

Profile listbox, the two independent state chips, **Apply timings**, **Save profile**, and an overflow menu with Save as…, Reload saved and Delete profile. Apply keeps its size while busy, disables repeat submission and changes its label until the bot acknowledges success or failure.

### Forms

Switches apply immediately and say so near each group. Numeric settings stay pending until Apply. Saving a profile is a separate persistence operation. Timing inputs are grouped by reaction family, show units, validate inline, and state when their delay begins and whether the active reaction uses them. Calibration lives in Diagnostics, apart from reaction timings. Each numeric setting appears in exactly one place.

### Pickers, menus and dialogs

The hero and profile pickers share one listbox component (arrow keys, Home/End, type-ahead, Escape). The overflow menu uses `role="menu"`. The telemetry label stays a native select. Dialogs trap focus, restore focus, close on Escape and expose a labelled title and description. Destructive confirmations use the danger button. Profile replacement warns before discarding edits.

### Help panel

A slide-over from the titlebar **?** holds the setup guide, all hotkeys (including F1) and feature notes. It closes on Escape and returns focus.

### Iconography

A single inline SVG sprite of 1.8px-stroke line icons. Icons supplement labels and never replace an action's accessible name; icon-only buttons carry `aria-label`.

### Motion

Short and state-oriented: page entry, dialog and toast entry, the live orb ring, the busy spinner and the bind-capture pulse. Durations are 120/200/320ms on one soft-out curve, and all motion respects reduced-motion.

### Overlay HUD

The F7 overlay uses the same palette in GDI+. Chips are opaque `--surface-1` panels with a hairline border, a 3px bar in the state color and white text; the header reads `DANBOT // VISION` with a filled LIVE or outlined IDLE pill; the diagnostics line is muted mono. The active guard zone is a solid 2px line in the decision color; inactive zones are dashed `--signal-dim`. The window's transparency key is near-black (1, 2, 3) rather than magenta, every fill is opaque, and text uses grayscale anti-aliasing, so edges blend toward dark instead of fringing pink.

### Content

Copy states what the bot sends, what must be enabled, and why an option is inactive. "RT sent" never claims a confirmed in-game result. Timings use milliseconds and describe the event from which the delay begins.

## Do's and Don'ts

- **Do:** Derive behavior explanations from the same C# policy that selects runtime reactions.
- **Do:** Keep "Timings not applied" independent from "Profile not saved", visible on every page.
- **Do:** Put each numeric setting in exactly one place (`ia-coverage.test.js` enforces it).
- **Don't:** Reintroduce multiple hero checkboxes or the paired "Your hero" and "No hero" controls.
- **Don't:** Use color alone for readiness, failure, pending state or priority.
- **Don't:** Write host-provided text with `innerHTML`; the page's CSP blocks inline script and style.
