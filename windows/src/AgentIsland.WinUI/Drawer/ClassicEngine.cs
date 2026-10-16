using System.IO;
using AgentIsland.Model;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace AgentIsland.WinUI.Drawer;

/// The Classic bots in embedded V8: Characters/classic.mjs is the approved
/// round 3 engine from windows/notes/classic-grok-20261015 (sha256
/// 589fa571…), our own code, shipped with the app. Twelve shapes in eleven
/// colours, picked per bot in its menu; frames are vector SVG, drawn
/// antialiased.
internal sealed class ClassicEngine : ICharacterEngine
{
    public const string EngineFile = "classic.mjs";

    // One call per frame advances every due bot and returns all SVGs at once,
    // so the host/script boundary is crossed once per frame, not per bot.
    private const string HostModule = """
        import * as lib from './classic.mjs';
        const bots = [];
        globalThis.agentIslandClassic = {
            shapes: () => lib.SHAPE_NAMES.join(','),
            colors: () => lib.COLORS.join(','),
            shapeFor: name => lib.shapeFor(name) ?? '',
            fidgets: kind => lib.fidgetsOf(kind).join(','),
            add(kind, shape, color, seed, tempo) {
                bots.push(lib.createBot(kind, { seed, tempo, rim: true, shape, color }));
                return bots.length - 1;
            },
            setLook(i, shape, color) { bots[i].setLook(shape, color); },
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
    private readonly Dictionary<DisplayProvider, string[]> _fidgets = new();

    public ClassicEngine(string engineDirectory)
    {
        _engine = new V8ScriptEngine();
        try
        {
            _engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
            var hostUri = new Uri(Path.Combine(engineDirectory, "agent-island-classic.mjs"));
            _engine.Execute(new DocumentInfo(hostUri) { Category = ModuleCategory.Standard }, HostModule);
            _host = (ScriptObject)_engine.Script.agentIslandClassic;
            Shapes = ((string)_host.InvokeMethod("shapes")).Split(',');
            Colors = ((string)_host.InvokeMethod("colors")).Split(',');
            foreach (var provider in DisplayProviders.All)
            {
                var list = (string)_host.InvokeMethod("fidgets", DrawerLooks.Kind(provider));
                _fidgets[provider] = list.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        catch
        {
            _engine.Dispose();
            throw;
        }
    }

    public CharacterTheme Theme => CharacterTheme.Classic;

    /// The shapes and colours for the look picker, in menu order.
    public IReadOnlyList<string> Shapes { get; }

    public IReadOnlyList<string> Colors { get; }

    /// The shape drawn for a saved name, or null when none fits: the Grok Bot
    /// study copy's names map to the nearest (square to squircle, and so on).
    public string? ShapeFor(string name) =>
        (string)_host.InvokeMethod("shapeFor", name) is { Length: > 0 } shape ? shape : null;

    public int AddBot(DisplayProvider provider, string shape, string color, double tempo, int seed) =>
        Convert.ToInt32(_host.InvokeMethod("add", DrawerLooks.Kind(provider), shape, color, seed, tempo));

    /// Takes effect on the bot's next frame.
    public void SetLook(int bot, string shape, string color) => _host.InvokeMethod("setLook", bot, shape, color);

    public void SetState(int bot, string state, double phase) => _host.InvokeMethod("setState", bot, state, phase);

    /// The bots look along the pointer's offset from their centre.
    public void SetGaze(int bot, double left, double top, double width, double height, double x, double y) =>
        _host.InvokeMethod("setGaze", bot, x - (left + width / 2), y - (top + height / 2));

    public void ClearGaze(int bot) => _host.InvokeMethod("clearGaze", bot);

    public void Poke(int bot) => _host.InvokeMethod("poke", bot);

    public string[] Fidgets(DisplayProvider provider) =>
        _fidgets.TryGetValue(provider, out var fidgets) ? fidgets : Array.Empty<string>();

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
