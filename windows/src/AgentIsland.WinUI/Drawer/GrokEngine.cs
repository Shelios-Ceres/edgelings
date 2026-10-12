using System.IO;

using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace AgentIsland.WinUI.Drawer;

/// Runs the Grok Bot character engine in embedded V8 and hands back one SVG
/// frame per bot. The engine is a local study copy of the installed Grok Bot
/// app: it is read at runtime and never shipped (see GrokEngineLocator).
public sealed class GrokEngine : IDisposable
{
    public const string EngineFile = "study-engine.mjs";

    // The engine's scheduler helpers reference browser globals a bare V8
    // lacks; manual advance/frame calls never schedule work, so inert
    // stand-ins are enough.
    private const string Shims = """
        globalThis.performance ??= { now: () => Date.now() };
        globalThis.queueMicrotask ??= f => { Promise.resolve().then(f); };
        globalThis.setTimeout ??= () => 0;
        globalThis.clearTimeout ??= () => {};
        """;

    // One call per frame advances every due bot and returns all SVGs at once,
    // so the host/script boundary is crossed once per frame, not per bot.
    private const string HostModule = """
        import * as lib from './study-engine.mjs';
        const bots = [];
        const playlist = (engine, state, seed) =>
            lib.A(engine, state, { seed, firstPhase: 0, isCompact: true });
        globalThis.agentIslandDrawer = {
            shapes: () => lib.O.join(','),
            colors: () => lib.I.map(c => c.id).join(','),
            add(shape, color, state) {
                const engine = lib.o({ body: lib.h(shape), isGazeAlways: true });
                const seed = bots.length + 1;
                bots.push({ engine, seed, palette: lib.p(lib.S(color), '#fff'), playlist: playlist(engine, state, seed) });
                return bots.length - 1;
            },
            setState(i, state) {
                const b = bots[i];
                b.playlist = playlist(b.engine, state, b.seed);
            },
            setLook(i, shape, color) {
                const b = bots[i];
                b.engine.setBody(lib.h(shape));
                b.palette = lib.p(lib.S(color), '#fff');
            },
            setGaze(i, left, top, width, height, x, y) {
                const rect = { left, top, width, height, x: left, y: top, right: left + width, bottom: top + height };
                bots[i].engine.setGaze(lib.s(rect, { x, y }));
            },
            clearGaze(i) { bots[i].engine.setGaze(null); },
            poke(i, kind) { bots[i].engine.poke(kind); },
            tick(steps, size) {
                const dts = steps.split(',');
                const parts = [];
                for (let i = 0; i < bots.length; i++) {
                    if (dts[i] === '') { parts.push(''); continue; }
                    const b = bots[i], dt = +dts[i];
                    b.playlist.advance(dt);
                    b.engine.advance(dt);
                    parts.push(b.playlist.state + '\u0002' + lib.C(b.engine.frame(), b.palette, size, 'b' + i));
                }
                return parts.join('\u0001');
            },
        };
        """;

    private readonly V8ScriptEngine _engine;
    private readonly ScriptObject _host;

    public GrokEngine(string engineDirectory)
    {
        _engine = new V8ScriptEngine();
        try
        {
            _engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
            _engine.Execute(Shims);
            var hostUri = new Uri(Path.Combine(engineDirectory, "agent-island-drawer.mjs"));
            _engine.Execute(new DocumentInfo(hostUri) { Category = ModuleCategory.Standard }, HostModule);
            _host = (ScriptObject)_engine.Script.agentIslandDrawer;
            Shapes = ((string)_host.InvokeMethod("shapes")).Split(',');
            Colors = ((string)_host.InvokeMethod("colors")).Split(',');
        }
        catch
        {
            _engine.Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> Shapes { get; }

    public IReadOnlyList<string> Colors { get; }

    public int AddBot(string shape, string color, string state) =>
        Convert.ToInt32(_host.InvokeMethod("add", shape, color, state));

    public void SetState(int bot, string state) => _host.InvokeMethod("setState", bot, state);

    public void SetLook(int bot, string shape, string color) => _host.InvokeMethod("setLook", bot, shape, color);

    public void SetGaze(int bot, double left, double top, double width, double height, double x, double y) =>
        _host.InvokeMethod("setGaze", bot, left, top, width, height, x, y);

    public void ClearGaze(int bot) => _host.InvokeMethod("clearGaze", bot);

    public void Poke(int bot, string kind) => _host.InvokeMethod("poke", bot, kind);

    /// Advances each bot whose dt is a number (NaN skips it) and returns its
    /// (engine state, SVG); skipped bots come back null.
    public (string State, string Svg)?[] Tick(IReadOnlyList<double> dts, double size)
    {
        var steps = string.Join(',', dts.Select(dt =>
            double.IsNaN(dt) ? "" : dt.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var raw = (string)_host.InvokeMethod("tick", steps, size);
        return raw.Split('\u0001')
            .Select(part =>
            {
                if (part.Length == 0) return ((string, string)?)null;
                var split = part.IndexOf('\u0002');
                return (part[..split], part[(split + 1)..]);
            })
            .ToArray();
    }

    public void Dispose() => _engine.Dispose();
}

/// Where the local engine copy lives. Development checkouts keep it in the
/// ignored windows/notes study folder; other machines may drop a copy into
/// %LOCALAPPDATA%\AgentIsland\grok-engine or point AGENTISLAND_GROK_ENGINE at
/// one. Absent everywhere, the drawer falls back to simple native characters.
public static class GrokEngineLocator
{
    public static string? Find()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("AGENTISLAND_GROK_ENGINE") is { Length: > 0 } custom)
        {
            candidates.Add(custom);
        }
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentIsland", "grok-engine"));
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "windows", "notes", "grok-installed", "0.66.0"));
        }
        return candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, GrokEngine.EngineFile)));
    }
}
