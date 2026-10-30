# Edgelings for Windows

The Windows app: a drawer of agent characters at the edge of your screen, a hover card per agent, a Settings window, a Usage & cost page and a tray icon. The app, its folders and its exe are still called Agent Island for now. For what it does, see the [root README](../README.md).

## Layout

| Folder | What it is |
|---|---|
| `src/AgentIsland.WinUI` | The app: WinUI 3 (Windows App SDK, unpackaged, self-contained). The drawer and its characters, hover card, menus, Settings, Usage & cost, tray. Assembly and exe: `AgentIsland.WinUI`. |
| `src/AgentIsland.Core` | Everything below the UI: session scanning and live phases, usage and cost, the settings stores, localisation. No UI framework. |
| `tests/AgentIsland.Tests` | A console test runner for Core and the shipped character engines. It ends with `ALL GREEN`. |
| `build.ps1` | Publishes the app and zips it under `dist\`. |

The characters are drawn by two JavaScript engines in `src/AgentIsland.WinUI/Drawer/Characters` (`characters.mjs` for Pixel, `classic.mjs` for Classic), run in V8. The design notes are in [docs/windows-drawer-themes.md](../docs/windows-drawer-themes.md).

## Build and test

From the repository root, with the .NET 10 SDK:

```powershell
dotnet build windows/src/AgentIsland.WinUI/AgentIsland.WinUI.csproj -c Debug
dotnet run --project windows/tests/AgentIsland.Tests -c Debug
```

`windows\build.ps1` builds the release zip, `windows\dist\AgentIsland-<version>-win-x64.zip`. `-Version` takes the version to stamp on the build; without it the csproj's version is used. See [CONTRIBUTING.md](../CONTRIBUTING.md#windows) for the rules the tests follow.

## Get it

There are no Edgelings releases yet. Build it from source as above, or take a test package: every push that changes `windows/` is built by GitHub Actions, and the workflow can be run by hand from the Actions tab. Open the run and download `AgentIsland-win-x64-test` under Artifacts (you need to be signed in to GitHub). Unzip it and run `AgentIsland.WinUI.exe`. The builds are not code-signed.
