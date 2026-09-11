---
version: alpha
name: "DANBOT Control Deck"
description: "A dark, instrument-panel interface that makes live combat automation behavior explicit before configuration detail."
colors:
  primary: "#E2554A"
  background: "#0C0C0F"
  sidebar: "#0E0E12"
  elevated: "#15151A"
  text: "#F3F3F5"
  text-muted: "#8A8A94"
  accent: "#E2554A"
  accent-hover: "#EF6B60"
  success: "#3DCF8E"
  warning: "#F0B35A"
typography:
  sans:
    fontFamily: "Segoe UI, system-ui, -apple-system, BlinkMacSystemFont, sans-serif"
  mono:
    fontFamily: "ui-monospace, SFMono-Regular, Consolas, monospace"
rounded:
  DEFAULT: "1rem"
  sm: "0.75rem"
  control: "0.5625rem"
spacing:
  page-max: "70rem"
  card-gap: "0.875rem"
  page-inline: "2.125rem"
components:
  behavior-summary: {}
  settings-bar: {}
  button: {}
  card: {}
  field: {}
  dialog: {}
---

# DANBOT Control Deck Design System

## Overview

### Creative North Star

The interface is a compact combat telemetry console: the current decision is the primary instrument, configuration is secondary, and diagnostic detail stays quiet until requested.

### Product context and register

- **Audience and primary job:** A For Honor player configuring screen-driven controller reactions who must understand the bot's next action without consulting a guide.
- **Target market and evidence:** Desktop users running the local Windows application; the repository's WinForms/WebView2 shell and controller bridge are the source of truth.
- **Locales:** English UI. User-created profile names are displayed verbatim and safely as text.
- **Usage scene:** A small desktop window used immediately before or alongside gameplay, often under time pressure.
- **Register:** Product interface. State clarity and compact density lead.
- **Memorable signature:** The persistent Active Behavior instrument pairs F/LT and E decisions with their actual gate, delay, priority, and fallback explanation.
- **Restraint:** Existing dark theme, coral accent, sidebar, typography, cards, and control geometry remain stable.
- **Anti-references:** Avoid game-guide prose, decorative HUD clutter, and settings screens that expose flags without explaining precedence.
- **Token ownership/runtime mapping:** This file documents the canonical tokens implemented in `ui/styles.css`; the stylesheet remains the runtime source. Any durable token change updates both files together.

## Colors

Near-black surfaces separate the titlebar, sidebar, scroll region, and cards through tone and restrained borders. Coral is reserved for selection, focus, and the product signature. Green means live or successfully committed state. Amber means a recoverable pending state such as unapplied timings or an unsaved profile. Text, icons, and wording accompany every semantic color.

## Typography

Segoe UI and system fallbacks carry controls and explanations. Monospace is reserved for keys, timings, controller labels, and compact machine state. Uppercase with tracking is limited to short eyebrows and status labels; instructions use sentence case.

## Layout

The 202px sidebar and 1120px content maximum are canonical at desktop sizes. Cards use a 14px gap and compact internal rhythm. The Active Behavior instrument is sticky on usable desktop widths and becomes part of document flow below 680px so it cannot obscure content at 200% scaling. At reduced logical widths, the summary stacks before timing cards collapse to one column. Controls wrap instead of truncating essential state.

## Elevation & Depth

Hierarchy comes from translucent tonal layers, hairline borders, and one restrained inset highlight. Blur supports separation but never carries meaning. The behavior summary may use a soft coral wash; ordinary settings cards remain neutral.

## Shapes

Cards use a 16px radius, primary controls 12px, and compact fields roughly 9px. Pills are reserved for status. Square key chips and compact rounded indicators distinguish input vocabulary from actions.

## Components

### Foundational visual states

All controls provide default, hover, focus-visible, pressed, and disabled states. Focus uses a two-pixel coral ring with offset. Success and pending state use icon/dot plus text. Reduced-motion preferences collapse decorative transitions and animations.

### Buttons and actions

Coral solid buttons commit durable profile saves. Neutral outlined buttons apply timings or invoke secondary actions. Busy Apply retains its dimensions, disables repeat submission, and changes its label until the bot acknowledges success or failure.

### Navigation and data display

The left sidebar remains the primary navigation. Active Behavior stays above route content and reports hero, profile, effective F/LT action, effective E action, requirements, priorities, and Legit fallback behavior. Overview is reserved for start/stop, profile, readiness, and active reactions; scans, overlays, input tests, telemetry, and the persistent last-reaction explanation live in Diagnostics.

### Forms and overlays

Switches apply immediately and say so near configuration groups. Numeric settings remain pending until Apply. Saving a profile is a separate persistence operation. Timing inputs are grouped by reaction family, show units beside the value, validate inline, and state when their delay begins and whether the active reaction uses them. Calibration values stay separate from reaction timings. The profile and hero pickers use the authored listbox primitive where popup geometry is part of the contract; the fixed telemetry session label remains native because its platform popup is acceptable. Both variants keep visible focus, readable labels, and keyboard selection. Dialogs trap focus, restore focus, close on Escape, and expose labelled title and description. Profile replacement warns before discarding edits.

### Iconography

The existing text-symbol icon system remains canonical. Icons supplement labels and never replace an action's accessible name.

### Motion

Motion is short and state-oriented: view entry, dialog entry, toast entry, and a pending-status pulse. The existing soft-out easing is canonical; all motion respects reduced-motion preference.

### Content and data visualization

Copy states what the bot sends, what must be enabled, and why an option is inactive. “Input sent” never claims a confirmed in-game result. Timings use milliseconds and describe the event from which the delay begins.

## Do's and Don'ts

- **Do:** Derive behavior explanations from the same C# policy that selects runtime reactions.
- **Do:** Keep “Timings not applied” independent from “Profile not saved” on every configuration page.
- **Don't:** Reintroduce multiple hero checkboxes or the paired “Your hero” and “No hero” controls.
- **Don't:** Use color alone for readiness, failure, pending state, or priority.
