using AgentIsland.Model;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace AgentIsland.WinUI.Drawer;

/// A character set's bots in embedded V8. Both sets ship as a JavaScript
/// engine (Characters/characters.mjs for Pixel, Characters/classic.mjs for
/// Classic), our own code, run through one small host module that is the same
/// for both. Each agent wears one of its looks, in its colour; the frames
/// come back as SVG.
internal sealed class V8CharacterEngine : ICharacterEngine
{
    // One call per frame advances every due bot and returns all SVGs at once,
    // so the host/script boundary is crossed once per frame, not per bot.
    // The engine file, the global the host defines and the cue line are filled in per set.
    private const string HostTemplate = """
        import * as lib from '@ENGINE@';
        const bots = [], makes = [];
        globalThis.@GLOBAL@ = {
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
            /*CUE*/
            tick(steps, size) {
                const dts = steps.split(','), parts = [];
                for (let i = 0; i < bots.length; i++) {
                    // A bot past a short step list (a picker's previews) stays as it is:
                    // advancing it by +undefined would turn its clock to NaN for good.
                    if (!dts[i]) { parts.push(''); continue; }
                    bots[i].advance(+dts[i]);
                    parts.push(bots[i].state + '\u0002' + bots[i].svg(size, 'b' + i));
                }
                return parts.join('\u0001');
            },
        };
        """;

    private const string CueLine = "            cue(i, kind) { bots[i].cue?.(kind); },\n";

    private readonly V8ScriptEngine _engine;
    private readonly ScriptObject _host;
    private readonly bool _ownCues;
    private readonly Dictionary<(DisplayProvider, string?), string[]> _fidgets = new();
    private readonly HashSet<string> _phases;

    /// Where the build copies the engines.
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Drawer", "Characters");

    /// The pixel bots come from the approved v5.2 engine
    /// (windows/notes/characters-v5.2-20261020, sha256 0d154175…): frames are
    /// SVG rectangles on a grid of 2-DIP cells, so they stay crisp at 100,
    /// 150 and 200 % scaling. The Classic bots come from the desk companions
    /// engine with looks from windows/notes/classic-grok-20261015/looks
    /// (sha256 a63f3495…): vector SVG, drawn antialiased.
    public static V8CharacterEngine Create(CharacterTheme theme, string engineDirectory) => theme == CharacterTheme.Pixel
        ? new(theme, engineDirectory, "characters.mjs", "agentIslandPixels", "agent-island-pixels.mjs", ownCues: false)
        : new(theme, engineDirectory, "classic.mjs", "agentIslandClassic", "agent-island-classic.mjs", ownCues: true);

    private V8CharacterEngine(
        CharacterTheme theme, string engineDirectory, string engineFile, string global, string hostFile, bool ownCues)
    {
        Theme = theme;
        _ownCues = ownCues;
        var host = HostTemplate
            .Replace("@ENGINE@", "./" + engineFile)
            .Replace("@GLOBAL@", global)
            .Replace("            /*CUE*/\n", ownCues ? CueLine : "");
        _engine = new V8ScriptEngine();
        try
        {
            _engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
            var hostUri = new Uri(Path.Combine(engineDirectory, hostFile));
            _engine.Execute(new DocumentInfo(hostUri) { Category = ModuleCategory.Standard }, host);
            _host = (ScriptObject)_engine.Script[global];
            _phases = ((string)_host.InvokeMethod("phases")).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        }
        catch
        {
            _engine.Dispose();
            throw;
        }
    }

    public CharacterTheme Theme { get; }

    public int AddBot(DisplayProvider provider, double tempo, int seed, string? look) =>
        Convert.ToInt32(_host.InvokeMethod("add", DrawerLooks.Kind(provider), seed, tempo, look ?? ""));

    public void SetLook(int bot, string? look) => _host.InvokeMethod("setLook", bot, look ?? "");

    public void SetState(int bot, string state, double phase) => _host.InvokeMethod("setState", bot, state, phase);

    /// The bots look along the pointer's offset from their centre.
    public void SetGaze(int bot, double left, double top, double width, double height, double x, double y) =>
        _host.InvokeMethod("setGaze", bot, x - (left + width / 2), y - (top + height / 2));

    public void ClearGaze(int bot) => _host.InvokeMethod("clearGaze", bot);

    public void Poke(int bot) => _host.InvokeMethod("poke", bot);

    /// A one-shot cue (done, note, ask, alert, error) over the bot's current
    /// pose, where the set has its own (Classic; the engine ignores a kind it
    /// lacks). Pixel's answer to every event is its poke hop; the host
    /// decides when.
    public void Cue(int bot, string kind)
    {
        if (_ownCues) _host.InvokeMethod("cue", bot, kind);
        else Poke(bot);
    }

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
