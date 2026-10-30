# Approaching-limit alerts — design

Date: 2026-05-06
Status: Approved (pending implementation plan)

_Upstream history: an Agent Island design note from May 2026. It is not on the Edgelings roadmap._

## Goal

Notify the user inside the island when Claude 5h or Codex 5h usage is
approaching its limit. No native macOS banners. The notification surfaces
through the existing peek-pill and silhouette-glow primitives so the
feature stays inside the app's "quiet ambient" aesthetic.

## Scope

In scope
- Threshold detection on Claude 5h and Codex 5h windows.
- Two-tier severity (warning, critical) with user-configurable percentages.
- Visual treatment: silhouette glow tint while above threshold, peek pill
  content swap on hover, one-shot peek-pulse on first crossing per window
  per reset cycle.
- Settings UI: master toggle + warning/critical number fields.

Out of scope
- 7d window alerts.
- Per-provider threshold overrides.
- Persisting crossing memory across restarts.
- Sound, badge dot, Dock bounce, native UNUserNotification banners.
- "Burn-rate" anomaly detection (faster-than-usual without crossing a %).
- Shareable usage card (separate spec).
- Contribution chart / Stats view (separate spec).

## Behavior

### Trigger model
- Two thresholds: warning (default 80%), critical (default 95%), both
  user-overridable in Settings.
- Eligible windows: **Claude 5h**, **Codex 5h** only. 7d windows ignored.
- Hidden providers (existing `ProviderVisibilityStore`) skipped: when
  `claudeVisible == false`, Claude 5h cannot trigger any alert state.
- Master toggle gates everything. **Default: off.** Existing users should
  not get a surprise visual change on update; alerts are opt-in via
  Settings.

### Visual treatment

**Glow tint, sustained while above threshold**
- Below warning → existing glow behavior unchanged (cobalt, with whatever
  refresh-pulse / Low Power Mode interaction it has today).
- At/above warning → silhouette glow color shifts to **amber** via the
  same shadow primitive used for the cobalt glow.
- At/above critical → glow shifts to **red**.
- When both windows are above threshold, highest severity wins (red beats
  amber).
- Refresh pulse continues to ride on top: a fetch in progress still
  flashes the cobalt-pulse over the underlying alert tint.
- **Low Power Mode interaction.** Today, LPM hides the steady-state glow
  and lets it pulse only during refresh. For alerts, the amber/red tint
  **overrides LPM steady-state hiding** — if the user opted into alerts,
  the tint is always visible while above threshold. Rationale: LPM is an
  idle-aesthetic preference; alerts are an explicit safety preference and
  win when both apply.

**Peek pill content swap (hover)**
- Default content remains for windows below threshold.
- For a tracked window above threshold:
  - Warning: `⚠ Claude 5h · 87% · 1h 12m` (amber accent on the percent + countdown).
  - Critical: `⚠ Claude 5h · 96% · 1h 12m` (red accent).
- Both providers can be in different severity states simultaneously; each
  side renders its own content independently.

**Pulse on crossing (one-shot attention nudge)**
- The first time a window's percent crosses warning OR critical inside its
  current 5h reset, the peek pill auto-extends for **~4 seconds** with the
  alert content, then collapses back to silhouette.
- One pulse per `(provider, threshold, reset-window)` triple. Crossing
  warning then later crossing critical produces two separate pulses.
- Window reset (new `resetAt`) clears the memory for that window so the
  next cycle can pulse again.
- A pulse uses the same surface as a normal hover-peek; the differences
  are content + accent color.

### Edge cases
- Panel already expanded when the crossing fires → suppress pulse (user is
  already looking at the data).
- First refresh after app launch → suppress pulse. Initial discovery is
  not a "crossing"; the first usage update is a warmup tick that
  populates crossing memory but emits no pulse.
- Both providers cross in the same refresh tick → single coalesced pulse
  with both lines stacked.
- Master toggle off → no tint, no content change, no pulse — exactly like
  today.
- Value bounces (87 → 88 → 87) → only the first crossing pulses; bouncing
  back and forth across the threshold inside one reset window does not
  re-pulse.
- Value drops below threshold mid-window (rare, possible after a
  rate-limit-induced lower reading) → glow returns to normal, peek
  content returns to normal. If it later crosses up again inside the same
  reset window, no re-pulse (already pulsed for this window).
- `CODEXISLAND_DEMO=1` → tint is allowed (so screen recordings can
  showcase the look) but pulses are suppressed.

### Settings UI
New "Alerts" section in `SettingsView`, three rows using existing
`SettingsRow` / `SettingsToggle`:
1. Toggle: **Approaching-limit alerts** (default off).
2. Stepper + label: **Warning at __ %** (default 80, allowed 50–98).
3. Stepper + label: **Critical at __ %** (default 95, allowed 51–99).

Rows 2 and 3 are disabled (greyed) when row 1 is off.

Inline validation: if `warning >= critical`, the lower stepper shows a red
border + helper text "Warning must be below critical." Steppers clamp to
allowed ranges, so the invalid state is hard to hit without explicit user
input; helper text is the recovery path when it does happen.

## Architecture

### New files

`Sources/Model/AlertThresholdStore.swift`
- `@MainActor final class AlertThresholdStore: ObservableObject`,
  singleton via `static let shared`.
- Mirrors `RefreshIntervalStore` / `ProviderVisibilityStore` shape.
- Published state: `enabled: Bool` (default `false`),
  `warningPercent: Int` (default 80), `criticalPercent: Int` (default 95).
- UserDefaults keys:
  - `MacIsland.alertsEnabled`
  - `MacIsland.alertWarning`
  - `MacIsland.alertCritical`
- First-run seeding uses the `defaults.object(forKey:) == nil` idiom from
  `ProviderVisibilityStore`.

`Sources/Model/AlertEngine.swift`
- `@MainActor final class AlertEngine: ObservableObject`, singleton.
- Combine subscriptions to `UsageStore.shared.$claude`, `$codex`,
  `AlertThresholdStore.shared.$enabled` / `$warningPercent` /
  `$criticalPercent`, and `ProviderVisibilityStore.shared.$claudeVisible`
  / `$codexVisible`.
- Internal types:
  - `enum Severity { case none, warning, critical }`.
  - `struct PulseEvent: Identifiable` — `id: UUID`, `severity: Severity`,
    `lines: [PulseLine]` where each line carries provider, percent,
    reset-countdown.
  - `struct CrossingKey: Hashable` — `(provider: Provider, threshold:
    Threshold, resetAt: Date)`.
- Published outputs:
  - `severity: Severity` — worst-of across visible 5h windows currently at
    or above their respective threshold.
  - `pulseEvent: PulseEvent?` — UI observes; consumer sets back to `nil`
    after rendering.
- Internal state:
  - `crossings: Set<CrossingKey>` — not persisted.
  - `warmedUp: Bool` — `false` until first usage update consumed; pulses
    suppressed during warmup but `crossings` are still populated so we
    don't pulse retroactively when warmup ends.
- `start()` wires the Combine subscriptions. Called once from `App.swift`
  after `UsageStore.shared.startAutoRefresh()`.
- Suppression rules:
  - `enabled == false` → `severity = .none`, no pulses.
  - `CODEXISLAND_DEMO=1` → tint allowed, pulses suppressed.
  - `IslandModel.state == .expanded` at pulse-emission time → pulse
    coalesced (event not emitted).

### Modified files

`Sources/App.swift`
- After `UsageStore.shared.startAutoRefresh()`, call
  `AlertEngine.shared.start()`.

`Sources/Views/IslandRootView.swift`
- Observe `AlertEngine.shared`. The existing shadow color
  (`IslandColor.cobalt.opacity(0.35)` at line 48) becomes a function of
  severity:
  - `.none` → cobalt + existing LPM behavior (no change).
  - `.warning` → amber tint, sustained even when LPM is on; cobalt
    pulse overlays during refresh.
  - `.critical` → red tint, sustained even when LPM is on; cobalt pulse
    overlays during refresh.
- The chart-decoration cobalt stops at lines 313–316 (inside the expanded
  panel) are unaffected — those are decoration inside the expanded
  surface, not the silhouette glow.
- React to `pulseEvent`:
  - If `model.state == .compact`, call `model.setState(.peek)`, render
    pulse content in the pill, then `model.setState(.compact)` after 4s.
  - If `model.state == .peek`, render alert content for 4s overlay then
    return to default peek content.
  - If `model.state == .expanded`, drop event (suppression already
    handled in engine, but defensive in case of timing race).

`Sources/Views/NotchPeekPill.swift`
- Read `AlertEngine.shared.severity` (or per-side severity if engine
  exposes that). When a tracked window is above threshold, swap pill
  content for that side: `⚠ ` glyph + accent color (amber for warning,
  red for critical). Default formatting otherwise.
- During a pulse, render `PulseEvent.lines` (1–2 lines covering both
  providers when both crossed) on the same surface for 4s.

`Sources/Views/SettingsView.swift`
- Insert "Alerts" section. Use existing `SettingsRow` / `SettingsToggle`
  components. Stepper component: prefer SwiftUI `Stepper(value:in:)` with
  a label; if the existing settings row pattern doesn't accommodate it,
  add a thin wrapper alongside `SettingsToggle`.

### Theme additions

`Sources/Theme/...` (existing `IslandColor` namespace)
- Add `IslandColor.amber` and `IslandColor.alertRed`. Choose saturation
  that reads on the black silhouette without clashing against existing
  cobalt; sample from cobalt's luminance ladder.

## Tests

### Logic tests (`Tests/AlertEngineTests.swift`, or project's existing
test target — confirmed during implementation)

- First usage update at 50% → `severity == .none`, no pulse, warmup
  consumed.
- First usage update at 85% (already-above-threshold on launch) → no
  pulse (warmup path), `crossings` populated, `severity == .warning`.
- Second update 85 → 86 → no new pulse.
- Subsequent update 86 → 96 → `severity == .critical`, exactly one new
  pulse for `(claude, critical, resetAt)`.
- `resetAt` advances → matching `crossings` keys pruned, the next
  above-threshold percent re-pulses.
- `claudeVisible = false` while Claude crosses 80% → no pulse from Claude
  windows; Codex windows still pulse normally.
- Both providers cross within the same usage update → single coalesced
  `PulseEvent` carrying two lines.
- `enabled = false` → `severity == .none` regardless of usage; flipping
  back to `true` does not retroactively pulse for already-crossed
  windows.
- Value drops below threshold within a reset window then crosses back up
  → no re-pulse for the same `(provider, threshold, resetAt)` triple.

### Manual / visual

- Run with `CODEXISLAND_DEMO=1` and a hand-tuned 85%/96% scenario;
  verify amber tint, red tint, and that pulses are suppressed in demo
  mode.
- `./scripts/verify.sh` smoke test continues to pass (one-second launch).

## Risks / open questions

- **Color choice on silhouette.** Amber and red on a black silhouette
  read very differently than cobalt. Saturation needs a design pass
  after first build. Approach 2 chosen specifically to keep the surface
  the same; only color differs.
- **Pulse coordination with hover.** If user is mid-hover when a pulse
  fires, the hover content briefly becomes pulse content. Acceptable —
  the user is already looking. Confirm during build.
- **Stepper UX.** Existing settings rows are toggle-heavy. Adding a
  stepper-with-numeric-label may need a new component variant. Defer
  the call to implementation; reuse `SettingsRow` if it can carry an
  arbitrary trailing view.
- **Combine retain cycles in `AlertEngine`.** Singleton + multiple
  publishers — straightforward, but use `[weak self]` in sinks.

## Implementation notes (non-binding)

- The crossing memory key uses `resetAt: Date`, but `resetAt` can be
  `nil` (per `WindowUsage`). Treat a nil `resetAt` as "no usable
  reset boundary" → skip alerting on that update; the next update with
  a real `resetAt` is the one we react to.
- Engine subscribes to `$claude` / `$codex` (which fire on every
  `refresh()`), so the engine reacts at the same cadence as `UsageStore`.
  No additional timer needed.
