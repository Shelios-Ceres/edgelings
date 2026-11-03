# macOS port (work in progress)

The Mac app will be a native Swift UI over the shared C# Core (`windows/src/AgentIsland.Core`). Nothing here is the shipped Mac app yet: that is still `Sources/` and `build.sh`, which this folder doesn't touch.

## Stage 0: the spike

| Folder | What it settles |
|---|---|
| `bridge/` | How Swift calls Core. **A1** is a NativeAOT dylib (`Aot/`, C exports `ai_call` and `ai_free`); **A2** is a self-contained helper process (`Helper/`, one JSON request per line on stdin). Both answer the same requests through `Shared/BridgeApi.cs`. `swift/bench.swift` calls each and prints size, cold start, scan times and memory; `make-fixture.py` writes the synthetic Kimi Code data they scan. |
| `golden/` | The character engines on JavaScriptCore (`JSCEngine.swift`), drawn with Core Graphics (`SvgCanvas.swift`, a port of `SvgCanvas.cs`), compared with frames Windows made with V8 and Win2D (`frames/`). |

CI (all on `macos-15`, on `mac/**` pushes): `macOS Core` runs the Core test suite (Windows-only suites are skipped by name and listed in the log), `macOS bridge` builds and measures A1 and A2, `macOS golden frames` runs the drawing check.

## Regenerating the golden frames

On Windows, with a Debug build of the app (it writes into the folder you name; back up the app's settings first if you run it through the harness):

```powershell
$env:AGENTISLAND_DEBUG_GOLDEN = "<repo>\macos\golden\frames"
.\windows\src\AgentIsland.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-x64\AgentIsland.WinUI.exe
```

It writes one `.svg` (the engine's output) and one `.png` (Win2D's drawing) per frame, and `frames.tsv`. Regenerate them whenever an engine or `SvgCanvas.cs` changes. Every frame is a fresh bot with seed 7, moved on by three 0.4 s steps; the PNG is 44 DIP at 192 dpi (Pixel unantialiased, as in the drawer).

## Stage 1: the macOS platform layer

Core now reads real state on a Mac. Swift only supplies what AppKit owns: window state, launch at login, and the sleep, wake and unlock notifications (`platform/`).

### Folders

One place resolves them all: `IslandPaths`. `EDGELINGS_ROOT` (or the bridge's `configure` call with `root`) moves the home and everything under it to `<root>/home`, so tests and benches never touch real folders. The test runner and the bench set it. The agents' own overrides (`CODEX_HOME`, `GROK_HOME`, `KIMI_CODE_HOME`, `CLAUDE_CONFIG_DIR`) still win when set. The diagnostic keeps only the caches elsewhere (`configure` with `cache`).

| What | macOS path | Where it comes from |
|---|---|---|
| Edgelings settings, crash log | `~/Library/Application Support/Edgelings` | new for the Mac port |
| Edgelings caches | `~/Library/Caches/Edgelings` | new |
| Hooks' signal folders (`claude-hooks`, `grok-hooks`) | `~/Library/Application Support/Edgelings/` | Windows keeps its Agent Island folder; a Mac install never had one |
| Claude Code sessions | `~/.claude/projects`, `~/.config/claude/projects` (or `CLAUDE_CONFIG_DIR`) | upstream `Sources/`, Claude Code |
| Claude Code login | Keychain item `Claude Code-credentials`, or `Claude Code-credentials-<8 hex>` | upstream `ClaudeCredentials.swift` |
| Claude Desktop sessions | `~/Library/Application Support/Claude/claude-code-sessions` | upstream `SessionScanner.swift` (Windows' `Claude-Data` move does not apply) |
| Codex | `~/.codex` (`CODEX_HOME`): `sessions`, `archived_sessions`, `session_index.jsonl`, `auth.json` | same layout as upstream |
| Grok CLI | `~/.grok` (`GROK_HOME`): `sessions`, `auth.json`, `hooks` | upstream |
| Kimi Code | `~/.kimi-code` (`KIMI_CODE_HOME`) | Windows layout; **not verified on a Mac** |
| DeepSeek Harness | `~/.dsh/sessions` | Windows layout; **not verified on a Mac** |
| Cursor | `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb` | upstream |
| Antigravity | `~/.gemini/<antigravity, antigravity-ide, antigravity-cli>/brain/…` | Windows layout; upstream reads an older `antigravity-ide/<project>/chats` layout; **not verified on a Mac** |
| Grok Bot app | `/Applications/Grok Bot.app` (or `~/Applications`), process name `Grok Bot` | a guess, **not verified** |

Credentials other than Claude's: Codex `auth.json` and Grok `auth.json` are files under those homes; Cursor's token is in `state.vscdb`. Whether any of them also keeps a copy in the Keychain on a Mac is **not verified**; the diagnostic reports which files exist.

### Processes and presence: where the code lives

- **Processes are read in Core** (`MacProcesses`): libproc (`proc_listallpids`, `proc_pidinfo`, `proc_pidpath`) and `sysctl(KERN_PROCARGS2)` through P/Invoke, all in libSystem. They need no UI framework, the session scanner calls them from Core's own loop, and the diagnostic and tests exercise the same code. `KERN_PROCARGS2` is parsed up to `argc` arguments and never past them, so a process's environment is never read. An agent's process is named by libproc's name, else its image's file name, and the official installer's versioned `…/claude/versions/<n>` binary counts as `claude`.
- **Windows are Swift's** (`platform/WindowSnapshot.swift`): `CGWindowListCopyWindowInfo` owner pid, layer 0, size and on-screen state, plus `NSRunningApplication.isHidden`. Reaching CoreGraphics' CF types from C# would be a few hundred lines of P/Invoke under AOT, and Swift also has the hidden flag. Titles are never read, so no Screen Recording permission is needed. Swift sends one entry per app (`{"pid":…, "offscreen":…}`) with the bridge's `windows` call on its loop. Core uses the latest report for 20 seconds; with no fresh report a running agent counts as open, never closed on a guess.
- **A limit of the signal:** a window on another Space also reads as off screen, so its app reads as `minimized`. The friend's check says whether that matters.

### Launch at login, sleep, wake and unlock

Swift side: `LaunchAtLogin` wraps `SMAppService.mainApp` (it works from a signed `.app` only, so CI only compiles it). `PlatformEvents` forwards `NSWorkspace` sleep and wake, session-active, and the distributed screen-locked/unlocked notifications to Core as `systemEvent`, which calls `UsageStore.HandleWake` and `HandleUnlock`, as the Windows power and session events do.

### Claude's Keychain login

`ClaudeKeychain` runs `/usr/bin/security` (each call has a deadline and is killed at it, because `security` can block for ever behind an authorization dialog). It finds the freshest Claude Code login among `Claude Code-credentials` and the suffixed items (an item counts only when it holds a token pair), and after a refresh writes the rotated pair **back into the item it came from**, only while that item still holds the refresh token that was sent, keeping every other field (the MCP logins beside it). The token URL, client id, `claude-code/X.Y.Z` User-Agent, beta header and the 403 handling are the Windows code's, unchanged. Nothing prints or logs a token. The JSON goes to `security add-generic-password -U -w` as an argument, as the upstream app does: for a moment it is visible to the same user's processes, behind the same trust boundary as the Keychain. The first time Edgelings touches Claude Code's item, macOS asks to allow `security`; "Always Allow" is expected.

Tests: the logic against an in-memory stand-in for `security` (everywhere), and on a Mac the same calls against a throwaway keychain file (`ClaudeKeychainTests`). The real login Keychain is never touched by a test or a bench.

### AOT

Core runs the trim and AOT analyzers in every build, and the NativeAOT dylib build fails on any `IL` or `CA1416` warning. `Preferences` serialises through a source-generated context (`PreferencesJson`, which lists every stored type; a test stores each), the Claude and Grok hook JSON avoid the reflective `JsonArray.Add<T>`, and `InternetStatus` keeps its COM lookup to Windows. CA1416 is back on for Core; Windows-only members carry `[SupportedOSPlatform("windows")]`.

### Hooks on macOS (what is needed; nothing is built or installed)

Upstream's Mac app has no hooks. The Windows ones are PowerShell commands:

- **Claude Code:** a `Notification` entry in `~/.claude/settings.json` whose command reads the hook's JSON from stdin and writes `{session_id, transcript_path, type, at}` to `<signal folder>/<session_id>.json`. On a Mac it needs a `sh` command (drop the `"shell": "powershell"` field), the signal folder `~/Library/Application Support/Edgelings/claude-hooks`, and a JSON reader that ships with macOS: `plutil -extract session_id raw -o - -` reads JSON from stdin. `date +%s` gives the stamp. `ClaudeHooks.Install` and its `Command` are the places to branch.
- **Grok:** `~/.grok/hooks/…json` pointing at a script that does the same into `…/Edgelings/grok-hooks`. The script is PowerShell today; the `sh` version is the same few lines.
- The scanners that read the signal folders (`ClaudeHooks`, `GrokHooks`) are file readers and already work on a Mac.
- Installing either edits another agent's config, so it waits for the user's decision.

### The diagnostic tool

`macos/diagnose` builds `edgelings-diagnose` (Swift, universal) beside the universal `AgentIslandBridge.dylib` (workflow `macOS diagnostic`, artifact `Edgelings-diagnose`, with `HOW-TO-RUN.md` for the friend). It prints each agent's presence, process, session count and phases, which logins exist, 30-day token totals, and with `--fetch` Claude's and Codex's quota windows. Ids, counts and states only; the CI run also executes it on the runner, so the whole path (dylib, libproc, windows, scan) is exercised.

### Not verified (needs the friend's Mac)

Everything that needs a real desktop and real agents: that each agent's process is recognised by name, that `minimized`/`hidden` match what the user sees, the Keychain prompt and write-back against the real Claude Code item, `SMAppService`, the wake/unlock notifications, and the "not verified" paths above.

## Stages 2-4: the Edgelings app (`macos/app`)

The Mac app: an accessory app (menu-bar icon, no Dock icon), bundle id `dev.edgelings.Edgelings`, universal, macOS 13+, ad-hoc signed. `macos/app/build-app.sh <arm64-aot> <x64-aot> <out>` assembles `Edgelings.app` (the Core dylib and `libe_sqlite3.dylib` in `Frameworks`, the two character engines in `Resources/Characters`, Ark Pixel and its OFL licence in `Resources/Fonts`). CI workflow `macOS app` builds it, starts it for 20 s, runs `--diagnose` from the bundle (SQLite must load), renders every screen offscreen and uploads the friend's zip.

| Part | Where |
|---|---|
| Core's side: the drawer's poll, dwell, rest and speech rules; hover cards; Settings and Usage & cost data; hooks and Codex accounts | `bridge/Shared/AppModel.cs`, `SettingsApi.cs` (calls `appStart`, `frame`, `card`, `looks`, `setDrawer`, `settings`, `set`, `agent`, `usage`, `tr`, `format`, `rescan`, `log`) |
| Engines on JavaScriptCore | `engine/CharacterEngine.swift` (the same host module as `V8CharacterEngine.cs`) |
| SVG drawing | `golden/SvgCanvas.swift` (compiled into the app too) |
| Drawer panel, game loop, pointer, bubbles, hover card | `app/Sources/DrawerController.swift`, `Presenters.swift`, `CardView.swift` |
| Kits and tokens | `Theme.swift`, `SettingsKit.swift` |
| Settings and Usage & cost | `SettingsStore.swift`, `SettingsViews.swift`, `UsagePage.swift` |
| Offscreen renders | `RenderMode.swift`, `RenderSettings.swift` (`Edgelings --render <folder>`) |
| Diagnostic | `Diagnostic.swift` (`Edgelings --diagnose`, and Settings > General > Copy diagnostic) |

Text is chosen in Core (`L10n`), so the Mac shows the Windows wording in English and Simplified Chinese; the few Mac-only strings are in `MacStrings`. Both skins follow the app language; see "Pixel in Chinese" below.

### Hooks

`ClaudeHooks.UseShell` and `GrokHooks.UseShell` (off by default, turned on only by `appStart`) make the optional permission-prompt hooks `sh` commands that read the event with `plutil` and keep ids and a type, never text. Nothing is installed until the user flips the switch on the agent's card (a backup of `~/.claude/settings.json` is made first). `ShellHooksTests` runs the generated command on a Mac with temp folders only.

### Fixes 1 (after the first friend report)

- **Presence by bundle:** a process counts as an agent by its libproc name, its image's file name, the `.app` bundle its main executable sits in (`MacAgentNames`), or Kimi Code's folder. The diagnostic lists what matched and who started it.
- **Save battery** follows the power source (`platform/Power.swift`, IOKit) as well as Low Power Mode, and caps at 60 fps as Windows does.
- **Loop cost:** `Edgelings --bench-loop --scale 2` times seven characters stage by stage (JavaScriptCore step, SVG parse, Core Graphics, blit) and turns it into a share of one core. CI uses it as a regression guard (busy 35 %, calm 25 %, any of three runs). `golden/SvgCanvas.swift` has a byte scanner for the engines' SVG, a path cache and a colour cache.
- **Pixel engine:** `windows/tests/engine-diff` holds the frozen engine and a differential test (Node, and JavaScriptCore in `macos-jsc.yml`) behind a 2.5-4x faster `characters.mjs` with byte-identical output.
- **Sign in** writes a `.command` file under Application Support and opens it in Terminal (`TerminalLauncher`).
- **Friend kit:** `app/HOW-TO-TEST.md`, `PROBE.md` (read-only commands) and `CHECKLIST.md` (two minutes for a person) are packed with the zip.

### Fixes 2 (after the second friend report)

- **Where the time goes:** `CoreProfile` (Core: each agent's scan, the cost scans, the process table, presence, every bridge call) and `Perf` (the app: loop tick, JavaScriptCore step, SVG raster, drawer draw, hover poll, polls) are printed by the diagnostic ("CPU:" and "Where the time goes"). `bridge/make-big-home.py` writes a home with the data sizes of a real developer Mac (15,000 Antigravity files, 80 Kimi sessions, a Cursor ledger), and the real-screen step measures the app on it (`--big-root`, `EDGELINGS_DIAG_FILE` / `EDGELINGS_DIAG_AFTER` write the diagnostic to a file, `EDGELINGS_IGNORE_REDUCE_MOTION=1` keeps the runner's VM from holding the loop at 6 fps). The drawer view redraws only the characters that changed.
- **Kimi on a Mac:** `/Applications/Kimi.app` (the desktop client) counts for Kimi's presence and the Kimi Work sessions follow it; Kimi Code's own app (`kimi-code-app`) stays apart. The ChatGPT app's windows decide Codex's presence, as its embedded Codex is real Codex.
- **In full screen** (Drawer page, both platforms): the panels join full-screen Spaces (`fullScreenAuxiliary`) only when the setting is on (`DrawerController.collectionBehavior`); off is the default. `edgelings-uitest` puts a window of its own into full screen and checks that the drawer stays out of that Space with the setting off and joins it with the setting on (it skips, with a note, on a runner that cannot enter full screen).

### Pixel in Chinese

The Pixel style follows the app language, as Classic does. Core no longer sets Pixel text in English (`L10n.English()` is gone from `AppModel.cs`, and the Settings page note is gone).

- **The faces:** Ark Pixel's Latin face for English; while Core says the app is Chinese (`FrameModel.chinese`), every pixel line sets in Fusion Pixel 10px zh_hans (`fusion-pixel-10px-proportional-zh_hans.ttf`, SIL OFL, full file, by TakWolf), whose Latin and digits are Ark's, with PingFang SC as the cascade fallback. Both files and their licences live in `windows/src/AgentIsland.WinUI/Assets/Fonts`, shared with the Windows app; `PixelFace` registers the Chinese one the first time the app speaks Chinese. Sizes, line heights and layouts are the English ones (the spec is `windows/notes/pixel-zh-20261102/SPEC.md`, option F).
- **Line breaks:** `PixelBreaks` offers a break between Han characters, never before a closing mark or after an opening one, never inside a Latin word, a number with its unit or a command. A line without Han characters keeps Core Text's breaks. `--self-test` checks the rules and that the face draws the app's hard characters.
- **Menus:** the drawer's right-click menu, the tray menu and the locked bubble read their words from Core's table (`AppStrings`), with the few Mac-only ones translated there.
- **Renders:** `--render` writes the cards, bubbles and Settings pages in both languages for both skins (`-zh` in the name).

### Not ported yet

M5's ghost-plate pop, the "Right now" row, the Agents expander and grip drag, Classic's segmented tabs (B8) and the "classic shapes you can recolour" copy (A8) wait for the design chats. The drawn Classic window frame is not ported. Sparkle and the release are stage 5. The upstream Mac-only features (turn alarm, reminders, jump to terminal, report cards) and the notch island stay in `Sources/` for the user to decide after parity.
