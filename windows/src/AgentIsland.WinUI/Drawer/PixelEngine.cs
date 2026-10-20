using System.IO;
using AgentIsland.Model;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace AgentIsland.WinUI.Drawer;

/// The pixel bots in embedded V8: Characters/characters.mjs is the approved
/// v5.2 engine from windows/notes/characters-v5.2-20261020 (sha256 0d154175…),
/// our own code, shipped with the app. Frames are SVG rectangles on a grid of
/// 2-DIP cells, so they stay crisp at 100, 150 and 200 % scaling.
internal sealed class PixelEngine : ICharacterEngine
{
    public const string EngineFile = "characters.mjs";

    // One call per frame advances every due bot and returns all SVGs at once,
    // so the host/script boundary is crossed once per frame, not per bot.
    private const string HostModule = """
        import * as lib from './characters.mjs';
        const bots = [], makes = [];
        globalThis.agentIslandPixels = {
            phases: () => lib.PHASES.join(','),
            fidgets: (kind, look) => lib.fidgetsOf(kind, look || undefined).join(','),
            add(kind, seed, tempo, look) {
                const make = { kind, seed, tempo };
                makes.push(make);
                bots.push(lib.createBot(kind, { seed, tempo, rim: true, look: look || undefined }));
                return bots.length - 1;
            },
            setLook(i, look) {
                const { kind, seed, tempo } = makes[i], old = bots[i];
                bots[i] = lib.createBot(kind, { seed, tempo, rim: true, look: look || undefined });
                bots[i].setState(old.state, -1);
            },
            setState(i, state, phase) { bots[i].setState(state, phase); },
            setGaze(i, dx, dy) { bots[i].setGaze(dx, dy); },
            clearGaze(i) { bots[i].clearGaze(); },
            poke(i) { bots[i].poke(); },
            tick(steps, size) {
                const dts = steps.split(','), parts = [];
                for (let i = 0; i < bots.length; i++) {
                    if (dts[i] === '') { parts.push(''); continue; }
                    bots[i].advance(+dts[i]);
                    parts.push(bots[i].state + '\u0002' + bots[i].svg(size, 'b' + i));
                }
                return parts.join('\u0001');
            },
        };
        """;

    private readonly V8ScriptEngine _engine;
    private readonly ScriptObject _host;
    private readonly Dictionary<(DisplayProvider, string?), string[]> _fidgets = new();
    private readonly HashSet<string> _phases;

    /// Where the build copies the engine.
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Drawer", "Characters");

    public PixelEngine(string engineDirectory)
    {
        _engine = new V8ScriptEngine();
        try
        {
            _engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
            var hostUri = new Uri(Path.Combine(engineDirectory, "agent-island-pixels.mjs"));
            _engine.Execute(new DocumentInfo(hostUri) { Category = ModuleCategory.Standard }, HostModule);
            _host = (ScriptObject)_engine.Script.agentIslandPixels;
            _phases = ((string)_host.InvokeMethod("phases")).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        }
        catch
        {
            _engine.Dispose();
            throw;
        }
    }

    public CharacterTheme Theme => CharacterTheme.Pixel;

    public int AddBot(DisplayProvider provider, double tempo, int seed, string? look) =>
        Convert.ToInt32(_host.InvokeMethod("add", DrawerLooks.Kind(provider), seed, tempo, look ?? ""));

    public void SetLook(int bot, string? look) => _host.InvokeMethod("setLook", bot, look ?? "");

    public void SetState(int bot, string state, double phase) => _host.InvokeMethod("setState", bot, state, phase);

    /// The pixel bots look along the pointer's offset from their centre.
    public void SetGaze(int bot, double left, double top, double width, double height, double x, double y) =>
        _host.InvokeMethod("setGaze", bot, x - (left + width / 2), y - (top + height / 2));

    public void ClearGaze(int bot) => _host.InvokeMethod("clearGaze", bot);

    public void Poke(int bot) => _host.InvokeMethod("poke", bot);

    /// Pixel's answer to every event is its poke hop; the host decides when.
    public void Cue(int bot, string kind) => Poke(bot);

    public string[] Fidgets(DisplayProvider provider, string? look)
    {
        if (_fidgets.TryGetValue((provider, look), out var fidgets)) return fidgets;
        var list = (string)_host.InvokeMethod("fidgets", DrawerLooks.Kind(provider), look ?? "");
        return _fidgets[(provider, look)] = list.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool Draws(string state) => _phases.Contains(state);

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
