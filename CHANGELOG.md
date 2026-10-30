# Changelog

User-facing changes. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Edgelings has no
releases yet, so its changes are collected under Unreleased. Below them is
Agent Island's own release history, kept as it was.

## [Unreleased] (Edgelings)

### Added
- A rebuilt Windows app: a drawer of bots at the screen edge, one per agent, in two styles (Pixel and Classic) with several looks per agent. The bots act out what each agent is doing and rest when the agent's app is closed.
- Per-session speech bubbles with fixed lines (finished, needs approval, has a question, hit an error, new Kimi mail), and a hover card per agent with its quotas, token use and the sessions waiting on you.
- Support for Claude Code and Claude Desktop, Codex, Kimi Code, Antigravity, Cursor, Grok and Grok Bot, and DeepSeek Harness.
- A Usage & cost page in Settings: tokens and estimated cost at API list prices from the agents' local logs, Kimi Code's other providers priced by their own price lists, and a separate "Memory (background)" row for the Kimi memory plugin.
- Kimi Code mailbox mail shown as a "Mail" row on the card; only the number of unread files is counted.
- Optional, observe-only hooks for Claude and Grok that let Edgelings tell a permission prompt is open.

### Changed
- The macOS app is still Agent Island 2.1.2 with a few small fixes. None of the above is on macOS yet.

## Agent Island release history

Everything below is the release history of Agent Island, the upstream project Edgelings grew out of. The versions are Agent Island's.

## [2.1.2] - 2026-08-09

### Added
- Cursor becomes a full citizen: live session state and your-turn alarms driven by Cursor's own completion signal instead of a timing guess, on top of the billing-cycle usage and plan it already reported. Both platforms.
- Real-time file watching for Grok, Antigravity, and Cursor — their sessions update on write rather than waiting for the next scan tick.
- Grok, Antigravity, and Cursor join the cost ledger and both report cards.
- Alarm sounds gain an Apple ringtone tier; Radar is the new default.
- The five-blade mark ships across the app icon, the DMG, and the Windows `.ico`.

### Changed
- **Antigravity replaces Gemini as the fifth provider.** Google retired Gemini Code Assist for individual accounts on 2026-06-18 and pointed those users at Antigravity; only Standard/Enterprise organisation licences survived, so the slot follows the users. Stored selections carrying `gemini` migrate to `antigravity` on read — nobody silently loses the island they chose. CLI is `agy`; the display name and Google's own gradient come with it.
- Antigravity quota comes from the **local language server** only (the cloud endpoints are a verified dead end): port discovery walks the `agy`/`antigravity` processes' listening sockets, loopback-scoped HTTPS, CSRF lifted from the IDE command line only after a 401. Resume uses `agy --conversation <id>` for an exact-thread append.
- Windows reaches 2.1.2 parity: the same five providers, the same live session state, released from the same tag as macOS.
- Report cards: the rank block is retired, the monthly table caps to the top three models like the weekly one, and `claude-opus-5` is priced (it was silently counted at $0).
- Claude in-app sign-in requests the exact six scopes the CLI uses, fixing an `Invalid request format` failure.

### Fixed
- The island could vanish and stay gone. Three separate disappearance paths are closed — a missed unlock notification, a full-screen Space transition, and a hover race that killed the window — with a self-healing check behind them.
- Cursor no longer spins forever without raising your turn; its turn status no longer freezes on a stale cache snapshot (the scan cache key ignored the SQLite WAL).
- Antigravity stopped firing mid-run: its tool steps report `source: MODEL` like the model itself, so a turn only counts as finished on a planner response with content and no pending tool calls.
- Guest logins are re-detected after sign-in, and every alarm click now lands on a real surface.

### Removed
- **Auto-resume is deleted outright.** Visibility and execution authority stay separate: Agent Island reports that a turn ended and takes you back to the session, but never continues a run on your behalf.

## [2.1.1] - 2026-08-08

### Added
- macOS: Cursor joins the island — billing-cycle usage and plan read from the local Cursor install, alongside Claude, Codex, Gemini, and Grok. Five providers, two configurable top-bar slots; every selected provider keeps a full data row.
- macOS: session monitoring goes island-wide — live working / stalled / your-turn state for Grok (from its native turn events) and Gemini (recency-based), joining Claude and Codex. Turn alarms and the island logo now speak each provider's own identity.
- macOS: Codex account switching — park each login under a name and swap from the provider menu; optional auto-switch rotates to the next parked account when the active quota reads exhausted, driven by real usage numbers.
- macOS: a paste-code fallback for Claude sign-in, shown only after a failed browser round-trip; the primary flow now mirrors the official CLI's authorize request exactly.
- macOS: weekly and monthly reports accept any start date via a calendar picker, and list every model that ran.

### Changed
- macOS: the settings panel is rewritten — sidebar navigation, teal-for-data / gold-for-selection color language, and micro-motion on every control; the Mac-type row is gone (notch detection is automatic).
- macOS: the About page states that every number is computed on the user's own computer (Mac or Windows) and asks for a GitHub star instead of listing feature marketing.
- macOS: Codex re-authentication now appears only when credentials actually fail, not whenever the CLI exists.

### Removed
- macOS: the subagent turn-alarm toggle and its scanning path — child threads never alarm.

## [1.7.1] - 2026-07-16

First pass on the external design review (2026-07-15): craft and restraint.

### Added
- The DMG now opens as a styled installer window — dark backdrop with the mark and wordmark, the app on the left, a drag arrow, Applications on the right, volume icon in the title bar (was: a bare default Finder window).
- Glow color choice in Settings → Top bar: Teal (the new default), Cobalt, Violet, Silver. Styles the ambient glow and loading sweep only — warning amber and critical red still override. The neighboring row is now titled "Visual mode" (same Calm/Vivid choice).
- Update prompt: the app checks the newest GitHub release at launch and every six hours and raises a two-button alert — "Update" opens the release page, "I know" snoozes that version for seven days. Settings → "Check now" answers from the same lookup. (The Sparkle feed URL has always been empty, so its update checks silently found nothing — which is why old installs never heard about new versions.) Windows follows the same 20s + 6-hour cadence and 7-day snooze; its Update button keeps the in-app download-and-relaunch flow it already had.

### Fixed (pre-existing)
- The expanded panel can no longer open as an empty black slab: a state-driven watchdog forces the content visible whenever the panel has been expanded for 300ms — the fade-in choreography could lose a timing race against fast hover flicks and strand the panel black.
- Classic mouse wheels now flip pages in the expanded panel (one notch = one page, debounced). Wheel mice have no horizontal swipe, and dragging out of the panel collapsed it — Shift+wheel and trackpad swipes are unchanged.
- Single-subscription machines get the solo split layout automatically: a provider with no CLI footprint (no `~/.claude` / `~/.config/claude`, or no `~/.codex`) yields its half of the island — live charts on the subscribed side, per-model breakdown filling the other — without hunting for the Settings toggle. Flipping a provider toggle still wins, and a machine with neither footprint keeps showing both.
- Launch at Login no longer dead-ends at "Operation not permitted": the toggle clears any stale Background Task Management record before registering (ad-hoc builds change signature every update, which strands the old record), detects Gatekeeper app translocation and says to move the app into Applications, and on macOS pushback opens System Settings → Login Items with a plain instruction instead of relaying the errno string.
- The turn alarm no longer pops over the very session you're looking at: when the app hosting that session's CLI (matched by process working directory, then up the parent chain to Terminal/iTerm/VS Code/Claude/ChatGPT…) is frontmost, the alarm holds — and fires the moment you switch away with the turn still open (community report). Sessions whose host can't be resolved (tmux, daemons) keep the old always-pop behavior.
- Codex token accounting reconciled (the "+19% vs ccusage" report): two real defects fixed. ① Replayed `last_token_usage` events — the runtime re-reports a turn's delta without advancing the cumulative counter; every replay was counted again (one marathon file carried +331M phantom tokens). A guard now skips deltas whose cumulative pair is exactly unchanged. ② `~/.codex/archived_sessions` was never scanned — archiving moves rollout files out of the live tree (2.5B July tokens lived only there). Remaining difference to ccusage is deliberate: usage is attributed to when it happened (event timestamp, local time), not to the session's start day, which misplaces multi-day auto-resumed sessions wholesale.

### Changed
- Visual effects are now a two-mode choice in Settings — **Vivid** (the default: continuous halo + orbit sweep in your chosen glow color) and **Calm** (fully clean: no ambient light at all; only approaching-limit amber/red and the attention pulse remain). The system low-power / battery-saver mode still forces Calm either way, and the Glow color row lives under Vivid only.
- macOS: the island silhouette now matches the physical notch height exactly. It was sized to the menu bar, which macOS can draw taller than the housing — on those machines the island hung below the real notch as a grey chin.
- macOS: the silhouette's top corners flare outward into the screen edge the way the hardware housing does, instead of meeting the menu bar as bare 90° corners.
- macOS: the turn alarm and the report cards now share one card style — same 26pt continuous corners, same near-black base — instead of two different corner radii and two different blacks.
- A provider that needs a login shows a steady red logo (the usage page still offers one-click re-authentication). The endless red blink is reserved for stalls and rate limits.
- Report cards v2: the weekly and monthly cards share one skeleton (identical header/hero/split/footer rhythm), drop the QR tile and repo URL (share-clean for social feeds), keep exactly one logo (the brand's, centered in the footer — provider legends use color dots), and follow the brand-teal palette (title wordmark, heat ramp, peak bar, value line).
- Product name is written "Agent Island" everywhere user-facing.
- Settings: "Visual effects" now sits with the island-appearance controls (Top bar group, next to Cost display) as title + picker only — the explanatory sentence is gone.
- New "Interface scale" dial (100–150%) magnifies the island on screens without a notch — for 4K externals running large scaled or native modes where a point shrinks well below MacBook size. Notched MacBooks stay 1:1 so the silhouette keeps matching the housing.
- Trailing full stops removed from every caption and subtitle, both languages (59 strings each).
- With a single subscription, the freed half of the panel now shows the provider's bare mark and name instead of the per-model breakdown table — the table's "5h" legend read as a quota window Codex no longer has (community report). Per-model data returns with the report-card redesign.
- With a single subscription, the collapsed island splits its two flanks — logo on the provider's side, usage number on the other — instead of crowding one end and leaving the other half empty. (Windows also retires its "center the island when solo" toggle; the split is simply how a solo island lays out now.)
- The Settings status guide demos speak one glyph language — the symmetric mark spinning (working), steady with a small bell badge (your turn), pulsing red (needs attention) — instead of two logo rows plus a bell-in-a-box.
- Report cards v3, both platforms: flat near-black base with no gradients, the app mark on the wordmark line, the "≈ API value" line sharing the hero number's baseline, a Claude-vs-Codex faction duel — official marks at the beam ends, a white spark riding the true usage split, and the chibi duel art above it (the leading side wins the crown; 48–52% is a back-to-back draw) — weekly 7-day bars with the peak day highlighted in brand teal and captioned with its number, TOP-3 (weekly) / TOP-5 (monthly) model pies — the monthly heatmap retires — and a bare rank footer: lifetime total plus the gold congratulations line.
- Internal preference keys migrated from the inherited "MacIsland." prefix to "AgentIsland." in one shot, every setting preserved; the CODEXISLAND_DEMO / CODEXISLAND_DEBUG env fallbacks are removed (AGENTISLAND_* only).

### Fixed
- macOS: the expanded panel's hairline border no longer traces the top edge, which read as a light-leak seam against the bezel.
- A pathological multi-MB line in a Claude session log can no longer buffer unbounded during cost scans (macOS: 64 MiB backstop; Windows: oversized lines skipped before parsing — totals unchanged on both).
- macOS: a single wedged cost scan can no longer freeze cost data for the rest of the process lifetime — the per-provider scan gate gains a 10-minute wedge escape (the "panel stuck on stale numbers, sync spinner forever" report).

## [1.6.1] - 2026-07-14

macOS and Windows ship together.

### Added
- Weekly & monthly report cards (macOS + Windows): shareable cards with your total tokens and an "≈ API value" line, the Claude/Codex split, 7-day bars (weekly) or a 24-week activity heatmap with your current streak (monthly), and a TOP-5 model donut where every row carries tokens, dollars, and share — rows sum to the headline number. Copy image or share via the system sheet; the weekly card also greets you once per ISO week. Entries live in the panel footer, the menu-bar/tray menu, and Settings.
- Island ranks: seven lifetime-token tiers, from 🌊 Drifter (100M) to 👑 Legendary Navigator (100B), printed on both cards.
- Codex reset cards: an ×N chip shows your banked resets; clicking it lists each card with its expiry.
- Usage fetches now retry transient network failures (SSL hiccups, timeouts) with backoff before showing anything; if the network stays down, the panel keeps your last data with a short "network drop" caption instead of a raw system error.

### Changed
- Codex moved to a single weekly quota in July 2026 (the 5-hour window is gone from its API). The usage tiles label themselves from the window length the provider actually reports — Codex shows one "week" tile with the true reset countdown — and the out-of-quota alarm names the real window. Claude is unchanged (still 5-hour + weekly).
- Model pricing snapshot refreshed (2026-07-13): Claude Fable 5 / Mythos 5 / Sonnet 5 / Opus 4.5–4.8 / Haiku 4.5, GPT-5.6 sol·terra·luna, GPT-5.5/5.4 and pro tiers — cost figures track current list prices.
- The cost page is the panel's default page; the weekly model breakdown counts all tokens (cache included) over the calendar week, so what you share adds up.
- Settings: share entries for the weekly/monthly cards sit bottom-right, Quit moved next to GitHub, the License link is gone, and the "Open threads via" picker is removed — threads always open in the desktop app.

### Fixed
- macOS: background timers now survive the locked screen (App Nap opt-out) — quota tracking and alarms stay live while you're away, without keeping the Mac awake — and usage refreshes the instant you unlock. Opening the island also refreshes stale numbers, capped by your refresh interval.
- Report windows rebuild their content on every open, so a language switch or fresh data is always reflected immediately.
- Codex's single-window world no longer renders a dead second tile or a permanent "no data" caption.
- Settings footer no longer overflows in English (the Quit button was being crushed into a vertical letter-stack).
- Windows: reset-card popup no longer snaps the panel shut; report-card layout is elastic with a visible close control; idle animations no longer peg the CPU; session scans cache turn-parses on an (mtime, size) fingerprint.

### Removed
- Auto-resume is retired on both platforms. OpenAI's move to weekly quotas removed the 5-hour reset cycle it was built around, and its real-world reliability never met the bar. All trigger surfaces are hidden; the code remains for a possible future return.
- The Codex 5-hour quota alarm and Codex credits displays.

## [1.5.7] - 2026-07-12

### Added
- Windows: a switch to turn the out-of-quota alarm off (Settings → Alarms), matching macOS — for people who only want auto-resume and not the "you're out" panel.

### Fixed
- Both platforms: the out-of-quota alarm no longer re-fires every few minutes while a window stays maxed out. Anthropic's rolling 5-hour reset time drifts by seconds on each refresh; the alarm's dedup was keyed on the exact reset timestamp, so the drift made it forget it had already fired. It now re-arms only when the reset boundary jumps to a genuinely new cycle. (1.5.6 only stopped the same-refresh duplicates, not this cross-refresh repeat.)
- macOS: much lower idle CPU. The island's rotating glow was re-shading its conic gradient on the CPU 30 times a second whenever the island was visible — the app's dominant background cost. It's now shaded once and spun on the GPU: identical look, a large drop in CPU and energy use.
- macOS: the every-few-seconds session scan stopped re-reading every file's metadata O(n·log n) times while sorting — it now reads each modification time once, via a direct syscall — trimming a periodic CPU spike.
- Windows: softer, cleaner island glow, and the Settings copy was trimmed to match the leaner macOS layout.

## [1.5.6] - 2026-07-11

### Added
- Windows: one-click auto-update. When a new release ships, "Update & Relaunch" downloads the zip in the background, swaps the running app, and reopens it on the new version — no more hunting the releases page and reinstalling by hand. macOS has always had this through Sparkle. (Note: this build must be installed manually one last time; every version after it updates itself.)
- macOS: a switch to turn the out-of-quota alarm off (Settings → Alarms) for people who only want auto-resume and don't want the "you're out" panel.
- macOS: each provider chooses how "Open thread" lands — Claude and Codex can independently open the desktop app or resume in the CLI (Settings → Providers).

### Fixed
- The quota alarm no longer fires two or three times for one limit (it was most visible in Claude Code) — one alarm per provider per blocked stretch, and the redundant system banner is gone. Both platforms.
- Auto-resume actually runs again. A session whose working directory couldn't be read was silently refused authorization, so "Continue after reset" never fired for it; triggers are now trusted per-session and trusted automatically when you create them. macOS.
- The auto-resume picker no longer lists every thread twice. Both platforms.
- When a 5-hour or weekly window resets, the display refreshes and auto-resume fires on its own — including right after the Mac or PC wakes from sleep. Before this, the reset time could pass with nothing updating until you quit and relaunched. Both platforms.
- Windows: re-authenticating Claude no longer fails with "Authorization failed / Invalid request format."
- Windows: two copies of the app open at once no longer overwrite each other's settings, and update prompts follow the Sparkle wording macOS uses.
- macOS: Settings drops the grey explainer subtitles, and the expanded carousel pages with a left-click drag.

## [1.5.5] - 2026-07-10

### Added
- Windows: real update checks — a daily poll of GitHub Releases (honoring the auto-check toggle) surfaces a Download dialog when a new version ships, and "Check now" genuinely reports latest / newer / unreachable. macOS already gets this from Sparkle.

### Fixed
- Windows: "Open thread" on a Codex session now lands on the exact thread in the Codex desktop app first (`codex://threads/…`), with the terminal resume only as fallback — the priority was inverted, so anyone with the codex CLI installed got a terminal instead of their chat window.
- Both platforms: the quota display setting is now an explicit Used | Remaining choice instead of a confusingly-worded toggle.

## [1.5.4] - 2026-07-10

### Added
- macOS: opt-in "Hide in Mission Control" (Settings → Display). The island ships pinned through Exposé — right on notched MacBooks where the Spaces bar drops below the housing, wrong on external displays where the bar hugs the top edge and the island covered it. Off by default.
- Windows: when only one provider is switched on, the island folds the hidden side away and centers the visible logo, animated. On by default; Settings → Display to keep the symmetric layout instead.

## [1.5.3] - 2026-07-10

### Added
- Settings → Display: "Show remaining instead of used" — every percent readout (usage tiles, peek pills) counts down what's left of a window instead of up what's spent. Both platforms.
- "Open thread" on a Claude Desktop session now copies the session title to the clipboard (macOS adds a quiet notification), so locating the conversation is one paste in Claude's search — the platform still offers no conversation-level deep link.

### Fixed
- A provider switched OFF in Settings no longer drives the island's red attention pulse or the out-of-quota alarm. Someone who only runs Claude keeps Codex hidden — its missing login used to pulse red forever. Both platforms.

## [1.5.2] - 2026-07-10

### Fixed
- "Open thread" on a Claude Desktop session brings Claude Desktop forward again instead of popping a Terminal (both platforms). CLI sessions still resume for real via `claude --resume` from the session's own directory — there is no external deep link that lands on an existing Desktop conversation, so app-level focus is the honest ceiling there.
- App icon rebuilt on Apple's icon grid with transparent margins: no more white corners on macOS 15, no more system backing plate on macOS 26. (Windows .ico was already clean.)
- Release automation: notes are now reliably taken from this CHANGELOG — the publish step had silently no-op'd since 1.2.3 — and a missing section now fails the release instead of shipping a stub.

### Added
- Demo mode can render both alarm cards headlessly to PNG (release screenshots without screen-recording permission).

## [1.5.1] - 2026-07-10

First version-aligned macOS + Windows release: one tag, both platforms, same detection-engine behavior.

### Added
- Out-of-quota alarm: a distinct full-screen alarm the moment a 5-hour or weekly window hits 100%, with the reset time on it. Fires once per reset cycle, warms up on launch, respects the master alarm switch.
- In-app browser re-auth for Claude: the Re-authenticate button opens the claude.com authorize page in your default browser and finishes via a local loopback callback — no Terminal, no code pasting. The CLI flow remains as fallback.
- Settings toggle "Alarm on subagent threads" (off by default) on both platforms.

### Fixed
- Rate-limit / API-error lines in Claude transcripts (`isApiErrorMessage`) no longer fire a false "It's your turn" alarm.
- Codex subagent (child) threads no longer raise turn alarms or drive the logo by default.
- "Open thread" reliability: the `codex://` deep link is delivered to the running Codex app instead of a stale duplicate handler; Claude sessions resume via `claude --resume` in a terminal (the `claude://resume` deep link only ever started a new session and is gone).
- Usage sync can no longer freeze on a wedged fetch — 25s request timeouts plus a loading watchdog.
- Auto-resume safety: sessions with no recorded project path fail closed instead of being auto-trusted; blocked interval triggers back off a full cycle instead of retrying every minute.

### Changed
- Cost page hidden by default on both platforms (Settings → Display to re-enable).
- Windows: brand tray icon with usage ring and state color; simplified placement (top bar or floating widget).

## [1.4.1] - 2026-07-05

### Fixed
- Codex automation rollouts (probes, orchestrator subagents, `codex exec`) no longer raise turn alarms or drive the logo.
- Forced language now applies everywhere immediately (date formatters no longer freeze the old locale); the misleading "restart required" alert is gone.
- Display tab: the two "Top bar" sections are merged into one.
- Alarm sounds preview on click, not on hover; resume commands run off the main thread (first-time Terminal permission no longer freezes the app).

### Changed
- Native English copy: "Got it", unified session terminology, "Show on" for screen choice.
- Localization tables cleaned (59 dead keys removed); release CI now runs the test suites before building.

## [1.3.2] - 2026-07-04

### Fixed
- Subagent storm: dozens of orchestrated child sessions finishing no longer queue dozens of alarm popups — bursts collapse to one alarm for the newest turn, the rest are recorded silently.

### Changed
- Faster alerts: FSEvents latency 0.2s → 0.05s, scan throttle 1s → 0.5s, confirm buffer 2.5s → 1s. A finished turn now pops its alarm in ~1.2-1.7s.

## [1.3.1] - 2026-07-03

### Fixed
- Turn alarms are reliable: per-turn delivery keys (no more repeat pop-ups from metadata writes), Claude Desktop's post-turn bookkeeping no longer randomly swallows "your turn", confirm re-checks against a fresh scan, and alarms auto-dismiss once you reply in the thread.
- Alarm window: first click acts even when unfocused, Esc closes, minimize removed (no stranded ringing panel), fixed size.
- A stalled session no longer masks a finished turn; acknowledged turns stop pinning the logo so a running sibling spins again.
- Usage-API errors (rate limit/offline) no longer silently swallow turn alarms.

### Added
- Event-driven scanning (FSEvents): the logo starts/stops with the run within ~1s; alarms land ~3s after a turn finishes.
- Two alarms queue instead of silently replacing each other; dismissing one recalls the next.
- "Open thread" on Claude Desktop sessions deep-links via claude://resume; several finished turns each get their own reminder.

### Changed
- Settings wording: width is now explained by Mac type (Notch / No notch); auto-resume naming unified (自动续跑) to match the README.
- verify.sh smoke launch runs in demo mode so it can never fire a real resume or touch the keychain.

## [0.1.4] - 2026-05-09

A polish + hardening release. One user-visible fix in Settings; the rest
is interior work — perf, refactor, and three release-pipeline guardrails
that exist so a botched future release doesn't silently brick auto-update.

### Fixed

- **Settings → Providers now shows auth errors instead of `0%`.** When
  Claude or Codex can't be reached (auth missing, expired, rate-limited),
  the row used to render `synced 2m ago · 0% / 0%` — the most authoritative
  diagnostic surface in the app silently masked the real reason. It now
  shows `⚠ auth required — run claude` (or whichever error fired) in place
  of the `0%`, per window.

### Internal

- **`IslandRootView` decomposed.** The root view used to observe seven
  stores; any `@Published` emission re-evaluated the whole tree, including
  every overlay and gesture closure. Split into `GlowLayer`, `LogoOverlay`,
  and `PeekPillOverlay` children, each subscribed to only what they read.
  Up to 8 redundant body re-evals per poll cycle eliminated.
- **`AppEnvironment` centralizes mode flags.** `CODEXISLAND_DEMO` /
  `CODEXISLAND_DEBUG` were checked across eight files via raw
  `ProcessInfo.processInfo.environment["..."]` lookups. Resolved once at
  launch into a typed enum (`AppEnvironment.isDemo`, `.isDebug`); a typo in
  any one literal can no longer silently miss the mode.
- **Generic `LogParseCache<Event>` shared by both log readers.**
  `ClaudeLogReader` and `CodexLogReader` previously duplicated ~70-80% of
  their cache + file-walk scaffolding. Extracted to one generic. Net
  −218 LOC across the two reader files. As a behavioral side effect, the
  Codex reader now uses the same 64 KB chunked streaming reader as Claude,
  closing a peak-RSS spike during 30-day rollout scans. Cache JSON shape
  is byte-identical, so existing caches survive the upgrade.

### Release pipeline

These all guard against silent bricks of Sparkle auto-update or the
Homebrew cask. None affect the running app — but if any one of them ever
fires, you'll get a loud failure at release time instead of a silently
broken update channel weeks later.

- **`build.sh` and `release.sh` reject non-semver `VERSION`.** A
  `VERSION` of `1` or `1.0` parses as `[1]` under Apple's component-wise
  comparator, which is *larger* than `0.0.99` — Sparkle would never offer
  any update to the affected installs. Tagging now fails loud at
  `error: VERSION must be X.Y.Z`.
- **`release.sh` aborts on empty EdDSA signature.** `set -euo pipefail`
  doesn't catch a zero-exit with malformed `sign_update` output. An
  appcast with `sparkle:edSignature=""` is rejected silently by every
  Sparkle client. The release now fails before the appcast is written.
- **CI uses an explicit DMG path for SHA-256.** A glob that matched no
  files would silently produce an empty SHA, which then `sed`'d into the
  Homebrew cask without changing it — `brew install` mismatched on every
  user. The path is now derived from the tag and existence-checked.
- **`build.sh` propagates Sparkle XPC codesign failures.** Previously
  swallowed via `2>/dev/null || true`, surfacing only at the user's first
  Check Now click as "The updater failed to start." The path-existence
  guard kept the original "tolerate missing helpers" behavior; real
  signing errors now fail the build.

## [0.1.0] - 2026-05-05

Three changes on top of the 0.0.10 baseline. The minor-version bump signals
that the 0.0.x bootstrap series is over — not that this single release is
big. Per-tag detail for the 0.0.x series lives on the
[GitHub Releases page](https://github.com/ericjypark/codex-island/releases).

### Added

- **Token counting toggle.** Settings → Providers → Tokens picks between
  *All tokens* (input + output + cache_creation + cache_read — ccusage
  parity, the prior default and the only mode in 0.0.x) and *Input + output*
  (matches Anthropic's claude.ai stats panel, which excludes cache reads).
  Both totals are computed every scan and cached, so flipping the segment
  is instant — no rescan.
- **`CHANGELOG.md`.** Going forward, each release ships with a curated
  user-facing changelog in this file.

### Changed

- **Continuous (squircle) corners on the island silhouette.** Replaces the
  hand-rolled circular-arc + straight-line path with
  `UnevenRoundedRectangle(style: .continuous)`, eliminating the small kink
  at the tangent point that was visible against the hardware notch.
- **Peek pill always shows window context.** When a provider didn't return
  an active `resetAt`, the pill used to drop the separator and render bare
  percentage — making the layout shift between hovers. It now always renders
  `<percent> · <label>`. With an active countdown the label is the live time
  remaining at full opacity; otherwise it falls back to the window length
  (`5h`) at reduced opacity, so countdown vs. passive label stays visually
  distinct without changing geometry.

### Internal

- `MacIsland.costCache.v2` → `v3`. First launch on 0.1.0 backfills the
  billable-tokens column with one fresh local-log scan; existing dollar +
  total-tokens rollups remain valid.

## [1.2.x] - 2026-06-28 → 2026-07-02

Reliability series for the turn alarm and quota display: stale-alarm suppression (metadata writes no longer resurrect old turns), immediate turn-state transitions, alarm control polish, quota display fixes, localization and peek-pill layout polish, Sparkle key rotation (1.2.2) and default feed URL fix.

## [1.1.0] - 2026-06-24

Visible Claude re-auth flow, security policy, Product Hunt / awesome-list badges, install-path docs for Chinese users.

## [1.0.0] - 2026-06-21

First public release: usage island (Claude + Codex 5h/weekly usage, cost, reset countdowns in the notch), live logo states (working / your turn / stalled), auto-resume triggers after quota reset, demo mode for filming, bilingual UI, launch video and screenshots.
