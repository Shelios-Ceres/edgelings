# Contributing to Edgelings

Thanks for being here. The project is small, so a good bug report or a small fix helps a lot.

## Reporting bugs

Open an issue at <https://github.com/Shelios-Ceres/edgelings/issues>. Useful things to include:

- Your OS and build: the Windows version (`winver`), or the macOS version (`sw_vers`).
- Which build you ran: the commit, or the Actions run a test package came from.
- Which agent is involved (Claude Code, Codex, Kimi Code, and so on), and whether its bot, quota or Usage & cost page shows data at all. An "auth required" or sign-in message usually means the agent's own sign-in isn't where Edgelings looks for it.
- A short description of what you expected and what happened. A screenshot helps; blur project paths and session titles first.
- On macOS, for a settings bug: the output of `defaults read dev.agentisland.AgentIsland`.

Some of the usage endpoints Edgelings reads are undocumented (Claude's `/api/oauth/usage`, Codex's `/wham/usage`, and others) and can change without notice. If one starts returning something unexpected, a `curl` of the response with the token removed lets us update the parser.

## Building locally

### Windows

Install [Git](https://git-scm.com/download/win) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run these from the repository root in PowerShell:

```powershell
dotnet build windows/src/AgentIsland.WinUI/AgentIsland.WinUI.csproj -c Release -p:Platform=x64
dotnet run --project windows/tests/AgentIsland.Tests -c Release
```

The test runner ends with `ALL GREEN`. `windows\build.ps1` publishes a self-contained build and zips it as `windows\dist\Edgelings-<version>-win-x64.zip`; unzip it and run `Edgelings.exe`. The app is still called Agent Island inside the code. [windows/README.md](windows/README.md) has the layout.

Tests must pass on any machine. The CI runner is en-US and UTC, and yours probably isn't:

- Sort and compare strings with `StringComparer.Ordinal`. A bare `.Order()` follows the machine's culture.
- Pass a time zone (for example `CostSummarizer.Summarize(…, zone)`) instead of relying on `TimeZoneInfo.Local`.
- Use temp folders; never read or write your real agent folders or settings.
- A quick local check: run the suite once more with the environment variable `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

### macOS

`Sources/` is Agent Island 2.1.2 plus small fixes (see the [README](README.md#macos)).

```sh
./build.sh
open build/AgentIsland.app
```

`./scripts/verify.sh` runs the unit suites, builds, and smoke-launches the binary for one second. The app runs forever, so a plain `./build.sh && ./build/…/AgentIsland` would block. There is no Xcode project and no SwiftPM; `build.sh` calls `swiftc` on `Sources/`.

## Code style

- **Lowercase Conventional Commits.** `feat(scope): summary`, `fix(scope): summary`, `docs: summary`. The body explains the why; the diff is the what. See `git log` for examples.
- **No `Co-Authored-By` lines**, including tags for Claude, Copilot and the like.
- **Atomic commits.** One logical change per commit. Each commit must build, and the Windows tests (or `./scripts/verify.sh` for `Sources/`) must pass.
- **No AI vocabulary.** Words like *comprehensive*, *delve*, *crucial*, *robust* and *seamless* stay out of commit messages, comments and docs. Direct words are better.
- **Comments only when the why is non-obvious:** a hidden constraint, a workaround for a specific bug, behaviour that would surprise a reader.
- **Match the style of the file you are editing**, even if you would do it differently.

## Things that need work

- The macOS port of the Windows features: the drawer and its characters, per-session bubbles, Kimi, DeepSeek and Grok Bot support, and the Usage & cost page.
- Accessibility: a keyboard and screen-reader pass, and a high-contrast variant.
- Code signing. Windows and macOS builds are unsigned today.

## Code of conduct

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Short version: be kind.
