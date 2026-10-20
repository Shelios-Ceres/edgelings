# Agent Island for Windows

The Windows app: a drawer of agent characters at the edge of your screen, a hover card per agent, a Settings window, a Usage & cost page and a tray icon. It ships from the same tag as the macOS app (see the repository's [CONTRIBUTING.md](../CONTRIBUTING.md#windows) for setup).

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

`windows\build.ps1` builds the release zip, `windows\dist\AgentIsland-<version>-win-x64.zip`. `-Version` takes the shared `VERSION` file's value at release time; the csproj version is only a fallback for local builds.

## Download

https://github.com/tristan666666/agent-island/releases/latest (`AgentIsland-<version>-win-x64.zip`). Unzip it and run `AgentIsland.WinUI.exe`.
