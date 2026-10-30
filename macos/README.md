# macOS port (work in progress)

The Mac app will be a native Swift UI over the shared C# Core (`windows/src/AgentIsland.Core`). Nothing here is the shipped Mac app yet: that is still `Sources/` and `build.sh`, which this folder doesn't touch.

## Stage 0: the spike

| Folder | What it settles |
|---|---|
| `bridge/` | How Swift calls Core. **A1** is a NativeAOT dylib (`Aot/`, C exports `ai_call` and `ai_free`); **A2** is a self-contained helper process (`Helper/`, one JSON request per line on stdin). Both answer the same requests through `Shared/BridgeApi.cs`. `swift/bench.swift` calls each and prints size, cold start, scan times and memory; `make-fixture.py` writes the synthetic Kimi Code data they scan. |
| `golden/` | The character engines on JavaScriptCore (`JSCEngine.swift`), drawn with Core Graphics (`SvgCanvas.swift`, a port of `SvgCanvas.cs`), compared with frames Windows made with V8 and Win2D (`frames/`). |

CI (all on `macos-14`, on `mac/**` pushes): `macOS Core` runs the Core test suite (Windows-only suites are skipped by name and listed in the log), `macOS bridge` builds and measures A1 and A2, `macOS golden frames` runs the drawing check.

## Regenerating the golden frames

On Windows, with a Debug build of the app (it writes into the folder you name; back up the app's settings first if you run it through the harness):

```powershell
$env:AGENTISLAND_DEBUG_GOLDEN = "<repo>\macos\golden\frames"
.\windows\src\AgentIsland.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-x64\AgentIsland.WinUI.exe
```

It writes one `.svg` (the engine's output) and one `.png` (Win2D's drawing) per frame, and `frames.tsv`. Regenerate them whenever an engine or `SvgCanvas.cs` changes. Every frame is a fresh bot with seed 7, moved on by three 0.4 s steps; the PNG is 44 DIP at 192 dpi (Pixel unantialiased, as in the drawer).
