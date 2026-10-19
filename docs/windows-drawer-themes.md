# Drawer themes (Windows)

A theme decides how the Windows drawer looks: the character that stands in
for each agent, the panels it shows (the hover card and the speech bubbles),
and the colour that identifies each agent across the app. Two themes ship,
chosen in **Settings → Drawer → Character style** and switched live:

| | Pixel (default) | Classic |
|---|---|---|
| Characters | Seven pixel bots, one per agent, from a bundled engine | Six desk companions from a bundled engine (Cursor shares Grok's planet, in its own colour); flat vector, no look picker |
| Panels | Opaque pixel panels, English, in the Ark Pixel font | Paper with a soft tail and shadow, in the app language |
| Settings and menus | The pixel kit (`Shell/PixelTheme.xaml`) | The Classic kit (`Shell/ClassicTheme.xaml`): rounder Fluent with the Sky accent |
| Agent colour | Each bot's own colour | Each companion's own colour |

This document is the contract for adding a theme: what it **must** provide,
what it **may** change, what it **must not** touch, how to wire it in and how
to check it. "Must", "should" and "may" carry their RFC 2119 meanings.

## 1. Anatomy

A theme has three layers. A new theme must supply the first, should supply
the third, and may reuse an existing style for the second.

1. **Character set**: a JavaScript engine that draws one animated bot per
   agent as SVG frames.
2. **Surfaces**: the chrome of the hover card and the speech bubbles (plate,
   outline, corners, pointer, quota bars, type).
3. **Identity**: one colour per agent (its *swatch*), which tints the card,
   the bubbles, and the usage charts and the meters in Settings.

## 2. Workflow

1. Design and review the theme outside the repo; the maintainer keeps design
   work in the git-ignored `notes/`.
2. Freeze the approved engine: record its SHA-256 and a hash of every frame
   it can draw (§8).
3. Wire it in with the checklist in §7.
4. Verify with §8, then commit. The engine file ships byte-identical.

## 3. Requirements (must)

### 3.1 One bot per agent

The engine must provide a bot for each agent, under these kind names:

| Agent | `DisplayProvider` | Kind |
|---|---|---|
| Claude | `Claude` | `claude` |
| Codex | `Codex` | `codex` |
| Antigravity | `Antigravity` | `antigravity` |
| Grok | `Grok` | `grok` |
| Cursor | `Cursor` | `cursor` |
| Kimi Code | `Kimi` | `kimi` |
| DeepSeek | `DeepSeek` | `deepseek` |

Each bot should be recognisable at 44 DIP, and tell apart from the others by
silhouette, not colour alone: Codex, Kimi Code and DeepSeek are all blue in
some themes.

While Grok and Cursor share one X Subscription (their usage APIs report the
same pool), the drawer shows Grok's bot for both apps and hides Cursor's.
The `cursor` bot must still exist for users whose plans are billed apart.

### 3.2 A state for every phase

The drawer reports one of 21 phases per agent, and each must map to a state
the engine draws. Both themes use the same state names, and a new theme
should too: the mapping, tests and pacing then apply unchanged.

| Phase | Card label | State |
|---|---|---|
| `Idle` | Idle | `idle` |
| `Thinking` | Thinking | `thinking` |
| `Streaming` | Replying | `streaming` |
| `Reading` | Reading files | `reading` |
| `Searching` | Searching code | `searching` |
| `WebSearching` | Searching the web | `web` |
| `Writing` | Editing code | `writing` |
| `Running` | Running commands | `running` |
| `Controlling` | Operating apps | `controlling` |
| `Spawning` | Starting a subagent | `spawning` |
| `WaitingOnAgents` | Waiting on subagents | `orbit` |
| `Sending` | Sending a message | `sending` |
| `Asking` | Asking you | `asking` |
| `Approving` | Waiting for your approval | `approving` |
| `Compacting` | Compacting context | `compacting` |
| `Working` | Working | `working` |
| `Finished` | Waiting for your reply | `finished` → rests in `listening` |
| `Failed` | Something went wrong | `failed` → rests in `broken` |
| `NetworkTrouble` | Network trouble | `offline` |
| `RateLimited` | Out of quota | `sleeping` |
| `AuthRequired` | Sign-in needed | `locked` |
| `Idle`, while its app is away | Resting | `resting` |

How states behave:

- **One-shot states** play once from their start, then must settle on their
  own into a resting state and report it through `state` (Pixel: `finished`
  becomes `listening` after 1.7 s, `failed` becomes `broken` after 1.5 s).
  The drawer holds a phase in its resting state, and re-enters that state if
  the engine falls back to `idle`.
- **Looping states** must loop seamlessly and accept a start phase in
  [0, 1). A negative phase lets the engine pick, so bots entering a state
  together never move in step.
- **Calm states** (the engine's `CALM` list) must look right at 30 fps: when
  every visible bot is calm, the drawer drops to that rate. Both themes' calm
  states are `idle`, `listening`, `broken`, `offline`, `sleeping` and `locked`.
- **Fidgets**: `fidgetsOf(kind)` lists one to three short, idle-only states
  per bot (Pixel Claude: `wave`, `hop`, `stroll`). They must loop; the drawer
  starts one every 25 to 75 s, ends it after 2.5 to 4.5 s, and never fidgets
  under reduced motion or while the bot rests. A fidget is idle play: no work
  pose, prop or attention sign.
- **Resting**: while an agent's app is closed, or every window it shows is
  minimized or hidden in the tray (its own, or its host's: the Claude app
  around Claude Code, the Codex app around its CLI, the terminal a CLI runs
  in; a covered window still counts as open), its bot plays `resting` instead
  of `idle`. Any other phase still shows, so a minimized terminal mid-task
  shows its work. The card reads "Resting", with "App closed", "Minimized"
  or "In the tray". Pixel's words follow its looks, since its `resting`
  draws the z's and its `sleeping` the doze: away reads "Sleeping", and out
  of quota "Resting". `resting` is a calm loop, set apart from `idle` and
  from the quota's `sleeping`; it belongs in `PHASES` and `CALM`, and its
  start (phase 0) is the settled pose, which reduced motion holds still
  (Pixel's `sleeping` too). A set that doesn't draw it yet shows `idle`
  without fidgets instead.

### 3.3 Engine module

A theme's engine is a single ES module, run inside the app in embedded V8.

It must be pure: no `import`, DOM, timers, network, file or process access,
and no `Math.random`. All randomness comes from the bot's `seed`, so the same
seed and calls always draw the same frames.

It must export:

| Export | Meaning |
|---|---|
| `KINDS` | The seven kind names (§3.1) |
| `PHASES` | Every state name it draws, fidgets aside |
| `CALM` | The calm states |
| `createBot(kind, { seed, tempo, rim })` | A new bot. `tempo` scales its clock (the drawer gives each agent 0.90 to 1.12); `rim` draws an outline that keeps it visible on any wallpaper |
| `fidgetsOf(kind)` | The bot's fidget states |
| `swatchOf(kind)` | The bot's colour, as `0xRRGGBB` |
| `settleOf(state)` | A one-shot state's resting state, else `null` |

And a bot must offer:

| Member | Meaning |
|---|---|
| `setState(state, phase = -1)` | Enter a state, at a point in its cycle (§3.2) |
| `setGaze(dx, dy)` | Look toward the pointer: its offset from the bot's centre in DIPs, sent while it is within 480 DIP |
| `clearGaze()` | Look ahead again |
| `poke()` | A click: a short hop or nod |
| `advance(dt)` | Move time on by `dt` seconds |
| `svg(size, id)` | The current frame (§3.4) |
| `state` | The state it is in now, after any settling |

The engine must blend state changes itself (Pixel blends poses over
0.32 s); the drawer never animates transitions. A frame should stay under
about 2 KB of SVG (Pixel's idle frames are about 1 KB): the drawer asks for
every due bot's frame in one call, and rasterises a frame only when its text
changes.

### 3.4 Frame format

Frames are drawn by the app's own SVG renderer (`SvgCanvas`), not a browser.
A frame must:

- be `<svg viewBox="0 0 44 44">`, a 44 × 44 DIP box (the `size` argument
  scales it);
- use only the elements `svg`, `defs`, `g`, `path` (commands M L H V C A Z),
  `circle`, `use`, `clipPath` and `mask`;
- use only the attributes `transform` (`translate`, `scale`, `rotate`,
  `matrix`), `opacity`, `fill`, `fill-rule`, `stroke`, `stroke-width`,
  `stroke-dasharray`, `stroke-linecap` and `stroke-linejoin`;
- write colours as `#RGB`, `#RRGGBB`, `white`, `black` or `transparent`;
- prefix any `id` with the `id` argument, since bots share one canvas;
- draw within 22 DIP of the box's centre. The drawer's window region is each
  bot's inscribed circle: it takes the bot's clicks and clips its drawing,
  so anything painted past it is cut off. (Open point: Pixel's v3 set paints
  up to 31 DIP out in some states, and those corners are cut.)

Anything else is ignored or drawn black: gradients, filters, `text`, images,
`rgb()` and named colours, `fill-opacity`, CSS.

### 3.5 Identity colours

- One swatch per agent, from `swatchOf`, mirrored in
  `DrawerLooks` and checked against the engine by a test.
- Swatches must read on both light and dark panels. On dark surfaces, a
  swatch with luma under 48 (near-black, like Pixel Grok's `#0B0B0C`) is
  lifted to `#D6D6D6` automatically (`Brand.Readable`); check how yours fare.
- A nearly used-up quota window (90 % or more) turns to the system's
  critical red. Swatches should stay distinguishable from it.

### 3.6 Light and dark

Every surface colour must have a light and a dark value: panel fill,
outlines, bar track. Text uses the system's Fluent text brushes for the mode.

### 3.7 Licences and originality

- Art, code and fonts must be original, or under a licence that allows
  bundling in a public app (OFL-1.1, MIT, Apache-2.0, CC0…), with the
  licence file shipped beside the asset, like `Assets/Fonts/ark-pixel-OFL.txt`.
- Characters must not copy third-party logos or mascots in a public release
  without permission. (Open point: Pixel's Claude bot recreates Clawd,
  Claude Code's own mascot.)
- A downloaded asset must be checked against its publisher's checksum, and
  its source, version and checksum recorded in the commit message.

## 4. Free to change (may)

- Shapes, palettes, animation, fidgets, gaze and poke reactions.
- Surface chrome: plate fill, outline, corner shape, pointer, padding, quota
  bar style, dividers, type and the language of the text (§6).
- Card width: 264 DIP for paper and 272 for pixel today. Keep it under about
  300, since the card sits beside the drawer and must fit on screen.
- A frame-rate ceiling: stepped pixel motion needs no more than 30 fps;
  smooth vector motion may follow the display.
- The numbers of the drawer's moments: how bots slide in and out (reveal,
  hide, a bot shown or hidden, an edge switch), lift and land when the
  drawer is dragged, and refuse a locked drag. The moments
  themselves are the app's (`Drawer/DrawerMoment.cs`, on the clock and curves
  in `Shell/Motion.cs`): Pixel steps whole 2-DIP cells on a 30-tick clock,
  and Classic eases on cubic Béziers over Fluent's 83 to 500 ms durations.
  While one plays, the window region opens to the whole window.
- The numbers of the popups' moments: the card, the speech bubbles and the
  drawer menu (`Drawer/HoverCard.cs`, `Drawer/SpeechBubble.cs`,
  `Shell/MenuMotion.cs`), and the kits' tooltips, dialogs and drop-downs
  (`Shell/ControlMotion.cs`, switched on by the kits' styles). They play on
  the UI thread through `Shell/UiMoment.cs`, on the same clock and curves.
- The numbers of Settings' moments: the page switch, the Agents page's
  cards gliding as an agent is hidden, shown or moved (by its arrows or its
  grip), and an agent opened from the drawer
  (`Settings/SettingsWindow.Motion.cs`); expanders (`Shell/ExpanderMotion.cs`,
  with `Shell/RevealPanel.cs`); quota bars counting (`Shell/QuotaBar.cs`,
  `PixelBar`); the busy signs (`FaceSpinner`, `PixelSpinner`); a button's
  press (`Shell/ControlMotion.cs`); the Usage & cost page's period tabs,
  charts and numbers (`Cost/CostPage.Motion.cs`). A switch's flip is
  keyframes in its kit's template.

## 5. Fixed (must not change)

- **What the card says.** A theme restyles what `StatusCards.For` returns
  and never drops or invents data. A card shows, wherever the data exists,
  the agent's name and phase, its detail, its running tasks, each quota
  window (label, bar and value), today's and this month's tokens, then its
  notes: warnings first, then the quota's age, reset cards and agent notes.
- **Interaction.** A pointer resting on a bot for 200 ms opens its card; a
  click pokes the bot and opens its agent in Settings; a drag moves the
  drawer, and a locked drawer says so instead; right-click opens the menu;
  and only the bots' circles take clicks.
- **Geometry.** A 44-DIP box per bot, 10-DIP gaps, the drawer's edge layout.
- **Reduced motion.** With animations off in Windows, bots step at 12 fps
  (6 when calm) and never fidget.
- **Saved settings.** The theme is saved by name under
  `AgentIsland.drawer.theme`. Add `CharacterTheme` values; never rename or
  reorder existing ones, or users' choices are lost.

## 6. Crisp pixels (grid-based themes)

- Draw on a grid of 2-DIP cells: that is 2, 3 and 4 screen pixels at 100,
  150 and 200 % scaling, so every edge lands on a pixel. At 125 % and 175 %
  no grid is exact; the drawer draws grid themes without antialiasing and
  blits them nearest-neighbour, so edges stay hard.
- Keep panel geometry in whole cells too: outlines, corner steps, bar
  blocks and gaps.
- Size a pixel font so each of its pixels covers whole screen pixels:
  `design px × n ÷ scale`, with `n = round(13 × scale ÷ design px)`
  (`PixelPlate.FontSize`). The Ark Pixel 10 px design comes out at 13⅓ DIP
  at 150 %. Never use bold: a pixel face has none, and a synthetic one smears
  its pixels.
- A Latin-only face means English text. Build the card, bubble and detail
  text inside `L10n.English()`; a caption a store translated when it was set
  comes back to English through `L10n.Relocalize`.

## 7. Wiring checklist

Paths are under `windows/src/AgentIsland.WinUI` unless noted.

1. **Engine.** Add `Drawer/Characters/<name>.mjs` as Content with
   `CopyToOutputDirectory`, and list it in `Drawer/Characters/.gitattributes`
   with `-text` so line-ending conversion never changes its bytes.
2. **Host.** Add a class implementing `ICharacterEngine`: copy `PixelEngine`
   or `ClassicEngine`, which keep one host module, cross into V8 once per
   frame for every bot and turn the drawer's gaze box into a pointer offset.
3. **Theme value.** Append to `CharacterTheme` in
   `AgentIsland.Core/Model/DrawerStore.cs`.
4. **Rules.** In `AgentIsland.Core/Model/DrawerLooks.cs`: each agent's kind,
   `Entry` and `Hold` for every phase, `IsCalm`, `IsOneShot`, the swatches
   and `Swatch(provider, theme)`.
5. **Engine choice and pacing.** Create the host in
   `DrawerWindow.CreateEngine`; give the theme its frame-rate rule in `Step`
   and `Pace`, and aliased drawing in `OnDraw` if it is grid-based.
6. **Surfaces.** Reuse the paper or pixel style, or add one in `HoverCard`
   and `SpeechBubble`, with helpers like `Shell/PaperPlate.cs` and
   `Shell/PixelPlate.cs`. Both take a `pixel` flag today; a third style turns
   that flag into the theme. Settings and the drawer menu take their
   controls from the theme's kit through `Shell/Looks.cs`.
7. **Settings.** Add the option to Character style in
   `Settings/SettingsWindow.cs`, and its strings to `L10n` (English key and
   Chinese).
8. **Assets.** Put fonts and their licences in `Assets/Fonts`, with Content
   entries in the project file.
9. **Tests.** Add a test like `tests/AgentIsland.Tests/PixelThemeTests.cs`
   that reads the shipped engine and checks that every phase maps to a state
   it has, and that the calm and one-shot sets and the swatches match.

## 8. Verifying a theme

- **Engine.** Hash every frame the engine can draw (each kind, state and
  fidget, with gaze and poke) with a node script, and record the hash when
  the design is approved. Re-run it after any change: an unchanged hash
  proves the pixels are unchanged. Review frames rendered headlessly from a
  dev page, never by capturing your desktop.
- **Unit tests.** `dotnet run` in `windows/tests/AgentIsland.Tests`.
- **In the app.** Launch `AgentIsland.WinUI.exe` with:

  | Variable | Effect |
  |---|---|
  | `AGENTISLAND_DEBUG_BUBBLE=<n>` or `all` | Opens the card of visible bot `n` (counting from 0), or every agent's card |
  | `AGENTISLAND_DEBUG_SNAPSHOT=<dir>` | With the above, saves that card as a PNG rendered off-screen; with `AGENTISLAND_DEBUG_OPEN_SETTINGS=1`, saves every settings page; with `AGENTISLAND_DEBUG_OPEN_COST=1`, the Usage & cost page in each period |
  | `AGENTISLAND_DEBUG_CARD_TEXT=long` | Fills every card with the longest it says (a long phase and detail, several notes, a tip that can't be split), to check how its phrases flow from line to line |
  | `AGENTISLAND_DEBUG_WORK_HEIGHT=<DIP>` | Gives the card only that much of the screen's height, to check how every agent's card takes columns on a shorter screen (690 is a 1080p screen at 150 %) |
  | `AGENTISLAND_DEBUG_SAY=<Phase>` or `Locked` | Makes the first bot say that phase's line, or the line a locked drawer answers a drag with |
  | `AGENTISLAND_DEBUG_STATES=<dir>` | Saves the kit's controls at rest, hovered, pressed and focused |
  | `AGENTISLAND_DEBUG_HOVER_PREVIEW=1` | Opens a test window whose card and bubble stay inside it, so a print of the window shows the paper's shadow |
  | `AGENTISLAND_DEBUG_DRAWER_STATS=<file>` | Logs the frame rate and CPU once a second |
  | `AGENTISLAND_DEBUG_DRAWER_FILM=<dir>` | Renders the drawer off-screen at 150 %, on light and dark ground: each drawer moment frame by frame, and a sheet of every bot in each state a phase plays (resting too, where its set draws it), in each of its fidgets, and poked while it rests |
  | `AGENTISLAND_DEBUG_POPUP_FILM=<dir>` | With `AGENTISLAND_DEBUG_HOVER_PREVIEW=1`, steps the card's and the bubbles' moments by hand and saves each as a strip of its frames, printed from the test window so the paper's shadow shows; every agent's card among them, in two columns there |
  | `AGENTISLAND_DEBUG_MENU_FILM=<dir>` | The same for the drawer menu: its open, a submenu's and, in Classic, its close |
  | `AGENTISLAND_DEBUG_CONTROL_FILM=<dir>` | The same for a tooltip, a drop-down and a dialog in Settings; with `AGENTISLAND_DEBUG_TRACE`, it logs how many tooltip popups are still open after the tooltip closes, which must be 0 (a template's `Closed` state that never completes leaves them on screen) |
  | `AGENTISLAND_DEBUG_SETTINGS_FILM=<dir>` | The same for Settings' own moments: a page switch, expanders, agents hidden and shown, an agent opened from the drawer, a meter counting, an agent card moved by its arrows and one dragged by its grip (taken, held past the next, let go), a switch flipping and a button pressed. It hides, moves and flips for real: restore the settings afterwards. `AGENTISLAND_DEBUG_FILM_ONLY=<names>` films only the strips starting with those comma-separated names |
  | `AGENTISLAND_DEBUG_COST_SAMPLE=1` | With `AGENTISLAND_DEBUG_OPEN_COST=1`, gives Kimi Code and Grok a sample day (Kimi Code's K3 at noon yesterday and its K2.8 Preview, which has no price, at 1:00 and 3:00; Grok at 2:00) to check a chart of tokens, a day used without a price and a quiet last five hours; render after 8:00 |
  | `AGENTISLAND_DEBUG_COST_FILM=<dir>` | The same for the usage numbers on Settings' Usage & cost page: two period switches and a scan counting |
  | `AGENTISLAND_DEBUG_DRAWER_MOMENTS=1` | Plays every drawer moment live, one after another; with `AGENTISLAND_DEBUG_TRACE=<file>`, each start and end is logged |
  | `AGENTISLAND_DEBUG_REDUCED_MOTION=1` | Acts as if Windows' animation effects were off |
  | `AGENTISLAND_DEBUG_REST=1` | Rests every idle bot, as if its agent's app were minimized |

  The live drawer is drawn by Win2D and cannot be snapshotted; the film
  draws the same bots with the same painter. To see the live drawer,
  capture it over a plain window of your own, and check the capture's
  corner pixels before looking at it.
- **Before release**, check every phase on every bot; light and dark mode;
  100, 150 and 200 % scaling (and 125 % for grid themes); reduced motion;
  switching to and from the theme live; and idle CPU in line with Pixel.

## 9. Reference: the Pixel theme

- **Engine**: `Drawer/Characters/characters.mjs`, approved v5.1, SHA-256
  `b95350269ba74e61ab8c69e543ed8a75267e036c4cb9ebe546d34796217e567f`; a
  22 × 22 grid of 2-DIP cells, a one-cell white rim, 0.32 s pose blends;
  every frame within 22 DIP of the box centre, so nothing meets the
  drawer's circle.
- **Swatches**:

  | Claude | Codex | Antigravity | Grok | Cursor | Kimi Code | DeepSeek |
  |---|---|---|---|---|---|---|
  | `#D77757` | `#9EA1AB` | `#3D7BF5` | `#0B0B0C` | `#45454F` | `#2B8BFF` | `#4D6BFE` |

- **Panel colours**:

  | Token | Light | Dark |
  |---|---|---|
  | Panel fill | `#FFFDF7` | `#1F2027` |
  | Outline, one agent's card or bubble | that agent's colour | that agent's colour, lifted if near-black |
  | Outline, all-agents card | `#2B2B33` | `#5A5B66` |
  | Bar track | the bar's colour at 18 % | at 25 % |
  | Warning | system critical red | system critical red |

- **Geometry**: 2-DIP cells; a one-cell outline whose corners step in two
  cells outside and one inside; a pointer of three cells against the edge
  and one beyond, aimed at the bot (4 DIP reach). The card is 272 DIP wide
  with 16/14 DIP padding, a 52-DIP label column and a 72-DIP value column.
  Bars are 14 blocks of 4 × 6 DIP with 2-DIP gaps; any non-zero share fills
  at least one block, and a window that failed to load is an empty bar at
  half opacity. Bubbles are at most 240 DIP wide, with 14/9/14/10 DIP text
  padding, and linger 4.5 s.
- **Type**: Ark Pixel 10px Prop latin (OFL-1.1), 13⅓ DIP at 150 %, regular
  weight, English.

## 10. Reference: the Classic theme

- **Engine**: `Drawer/Characters/classic.mjs`, the desk companions approved
  2026-10-17, SHA-256
  `937db3ce938a446390f861aeb41ab4f9f42d3a418fc2aa84d6e0fcf7c700edc5`; vector
  frames in a 44-DIP box with a 1.3-DIP white rim, 0.32 s pose blends and
  frames up to 3.2 KB. Each agent is a desk object with a face: Claude a
  succulent, Codex a mug, Antigravity a paper plane, Grok a planet (Cursor
  shares it, in its own colour), Kimi Code a moon lamp and DeepSeek a whale
  stapler. Each works its own mechanism through the work states; the only
  badges are asking's "?" and approving's "!", each made of its companion's
  world. Every frame stays within the circle (22 DIP of the centre).
- **Swatches**: each companion's own colour (`DrawerLooks.ClassicSwatch`,
  the engine's `swatchOf`); on dark surfaces a near-black one is lifted to
  `#D6D6D6`.

  | Claude | Codex | Antigravity | Grok | Cursor | Kimi Code | DeepSeek |
  |---|---|---|---|---|---|---|
  | `#D97757` | `#2D8CFF` | `#8E63F5` | `#6E7480` | `#24252C` | `#1D4E9E` | `#4D6BFE` |

- **Panel colours**:

  | Token | Light | Dark |
  |---|---|---|
  | Paper (card and bubble fill) | `#FFFFFF` | `#2A2B31` |
  | Hairline (edge) | ink `#17191F` at 8 % | white at 7 % |
  | Text | `#17191F` | `#F3F4F6` |
  | Secondary and tertiary text | `#50545E`, `#62666F` | `#C4C7CE`, `#A9ADB5` |
  | Divider, all-agents card | `#E4E6EB` | `#393A41` |
  | Bar track and plan chip | the agent's colour at 14 % over paper | at 24 % |
  | Warning, 90 % and over | `#C42B1C` | `#FF99A4` |
  | Shadow | 0/8 blur 14, ink at 13 %; 0/1 blur 2 at 10 % | 0/8 blur 16, black at 45 %; 0/1 blur 2 at 35 % |

- **Geometry**: one path, a rounded rectangle with a soft tail toward the
  bot (`Shell/PaperPlate.cs`), its shadow following the tail. The card is
  264 DIP wide, rounded 16, with 16/13/16/14 DIP padding; its tail is
  centred on the bot (half-base 10, reach 9) and kept clear of the corners,
  and the all-agents card has none. Rows indent 24 DIP past a 16-DIP face,
  in 40 · * · 58 DIP columns 8 apart; bars are 6-DIP pills with a 6-DIP
  minimum fill. Bubbles are at most 240 DIP wide, rounded 12 (less on a
  one-line bubble, so the tail fits), with a tail of half-base 6 and reach
  7 and 15/9/15/10 DIP text padding; they linger 4.5 s.
- **Type**: Segoe UI Variable in the app language: the agent's name 13/18
  semibold, rows 12/16, bubbles 13/18.
