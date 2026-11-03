using System.Globalization;
using System.Text;
using AgentIsland.Core;
using AgentIsland.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace AgentIsland.WinUI.Drawer;

// The work groups' film for design QA: each look plays its set's sample turn
// twice through the real engine, once as the drawer played it before (every
// phase its own state) and once through the groups, and a frame is kept every
// two seconds. A text trace beside the strips lists every change of state.
internal sealed partial class DrawerWindow
{
    private const double GroupsFrameEvery = 2;

    // The sample turns of the design chats' pages (Task 4), phase and seconds.
    private static readonly IReadOnlyDictionary<CharacterTheme, IReadOnlyDictionary<string, (AgentPhase Phase, double Seconds)[]>> GroupTurns =
        new Dictionary<CharacterTheme, IReadOnlyDictionary<string, (AgentPhase, double)[]>>
        {
            [CharacterTheme.Pixel] = new Dictionary<string, (AgentPhase, double)[]>
            {
                ["claude"] = Turn("thinking 14, running 6, streaming 4, thinking 18, reading 3, writing 5, running 16, streaming 6, finished 4"),
                ["codex"] = Turn("thinking 26, streaming 10, running 8, thinking 30, streaming 12, finished 4"),
                ["antigravity"] = Turn("thinking 8, running 10, writing 12, reading 6, thinking 10, web 8, finished 4"),
                ["grok"] = Turn("thinking 16, searching 6, running 12, streaming 14, finished 4"),
                ["kimi"] = Turn("thinking 43, running 2, thinking 30, spawning 12, thinking 20, finished 4"),
                ["deepseek"] = Turn("thinking 22, writing 8, thinking 34, running 6, finished 4"),
            },
            [CharacterTheme.Classic] = new Dictionary<string, (AgentPhase, double)[]>
            {
                ["claude"] = Turn("thinking 18, streaming 4, running 9, thinking 12, reading 2, writing 3, streaming 5, running 14, thinking 22, streaming 6, finished 7"),
                ["codex"] = Turn("thinking 34, streaming 8, running 6, thinking 26, writing 2, streaming 10, running 5, thinking 8, finished 7"),
                ["kimi"] = Turn("thinking 43, running 2, thinking 18, spawning 2, orbit 14, thinking 12, writing 2, streaming 4, finished 7"),
                ["antigravity"] = Turn("running 8, reading 4, thinking 6, writing 10, searching 4, running 12, thinking 8, controlling 6, web 4, reading 3, writing 6, thinking 4, running 10, finished 7"),
                ["grok"] = Turn("thinking 25, web 6, streaming 8, thinking 15, searching 4, streaming 6, thinking 14, streaming 5, finished 7"),
                ["deepseek"] = Turn("thinking 20, streaming 6, running 12, writing 6, thinking 18, streaming 8, running 6, thinking 10, finished 7"),
            },
        };

    private static (AgentPhase, double)[] Turn(string text) => text.Split(',', StringSplitOptions.TrimEntries).Select(part =>
    {
        var pair = part.Split(' ');
        var phase = pair[0] switch
        {
            "web" => AgentPhase.WebSearching,
            "orbit" => AgentPhase.WaitingOnAgents,
            _ => Enum.Parse<AgentPhase>(pair[0], ignoreCase: true),
        };
        return (phase, double.Parse(pair[1], CultureInfo.InvariantCulture));
    }).ToArray();

    /// On the game loop's thread: every look of every agent (Cursor wears Grok's), one strip each
    /// and groups-trace-<style>.txt, in light ground.
    private void GroupsFilm(CanvasDevice device, ICharacterEngine engine, string directory, bool pixel)
    {
        var theme = _loopTheme;
        var style = pixel ? "pixel" : "classic";
        var trace = new StringBuilder();
        var text = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 9 };
        var ground = Color.FromArgb(255, 0xF2, 0xF2, 0xF0);
        var ink = Color.FromArgb(255, 0x17, 0x19, 0x1F);
        foreach (var bot in _bots.Where(b => b.Provider != DisplayProvider.Cursor))
        {
            var kind = DrawerLooks.Kind(bot.Provider);
            if (!GroupTurns[theme].TryGetValue(kind, out var turn)) continue;
            var total = turn.Sum(part => part.Seconds);
            foreach (var look in LookCatalog.For(theme, bot.Provider))
            {
                if (!AskedFor($"groups-{kind}-{look.Id}") && !AskedFor("groups")) continue;
                engine.SetLook(bot.Index, look.Id);
                var rows = new List<(string Label, List<(double At, AgentPhase Phase, string State, string Svg)> Frames)>();
                foreach (var grouped in new[] { false, true })
                {
                    engine.SetState(bot.Index, "idle", 0);
                    var rotation = new GroupRotation(theme, kind, look.Id, state => engine.CycleSeconds(bot.Index, state));
                    var frames = new List<(double, AgentPhase, string, string)>();
                    string? playing = null;
                    var dts = Enumerable.Repeat(double.NaN, _slots.Length).ToArray();
                    dts[bot.Index] = 1 / 30.0;
                    var nextFrame = GroupsFrameEvery / 2;
                    trace.AppendLine($"== {style} {kind}/{look.Id} {(grouped ? "grouped" : "today")}");
                    for (var t = 0.0; t < total; t += 1 / 30.0)
                    {
                        var phase = PhaseAt(turn, t);
                        string state;
                        if (grouped && StateGroups.GroupOf(theme, kind, look.Id, phase) is not null)
                        {
                            state = rotation.State(t, phase);
                        }
                        else
                        {
                            if (grouped) rotation.Leave();
                            state = DrawerLooks.Entry(phase, theme);
                        }
                        if (state != playing)
                        {
                            trace.AppendLine($"{t,6:0.0} s  {phase,-16} -> {state}");
                            engine.SetState(bot.Index, state, 0);
                            playing = state;
                        }
                        var step = engine.Tick(dts, BotSize);
                        if (t >= nextFrame && step[bot.Index] is var (_, svg))
                        {
                            frames.Add((t, phase, playing ?? "", svg));
                            nextFrame += GroupsFrameEvery;
                        }
                    }
                    rows.Add((grouped ? "grouped" : "today", frames));
                }
                const int perRow = 28;
                const float cell = 56, gap = 4, label = 24, header = 22;
                var count = rows[0].Frames.Count;
                var lines = (count + perRow - 1) / perRow;
                var width = gap + Math.Min(count, perRow) * (cell + gap);
                var height = header + lines * 2 * (cell + label + gap) + gap;
                using var target = new CanvasRenderTarget(device, width, height, FilmDpi);
                var rasters = new List<CanvasRenderTarget>();
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Clear(ground);
                    ds.DrawText($"{style} · {kind}/{look.Id} · a frame every {GroupsFrameEvery:0} s · top row today, bottom row grouped · 150 %", gap, 4, ink, text);
                    for (var line = 0; line < lines; line++)
                    {
                        for (var mode = 0; mode < 2; mode++)
                        {
                            var y = header + (line * 2 + mode) * (cell + label + gap);
                            for (var k = 0; k < perRow && line * perRow + k < count; k++)
                            {
                                var (at, phase, state, svg) = rows[mode].Frames[line * perRow + k];
                                var x = gap + k * (cell + gap);
                                var raster = new CanvasRenderTarget(device, (float)BotSize, (float)BotSize, FilmDpi);
                                using (var session = raster.CreateDrawingSession())
                                {
                                    session.Clear(Microsoft.UI.Colors.Transparent);
                                    if (pixel) session.Antialiasing = CanvasAntialiasing.Aliased;
                                    _svg.Render(svg, session);
                                }
                                rasters.Add(raster);
                                ds.DrawImage(raster, x + (cell - (float)BotSize) / 2, y);
                                ds.DrawText($"{at:0}s {phase.ToString()[..Math.Min(5, phase.ToString().Length)]}", x, y + cell - 6, ink, text);
                                ds.DrawText(state, x, y + cell + 4, ink, text);
                            }
                        }
                    }
                }
                target.SaveAsync(Path.Combine(directory, $"groups-{style}-{kind}-{look.Id}.png")).AsTask().GetAwaiter().GetResult();
                foreach (var raster in rasters) raster.Dispose();
            }
        }
        File.WriteAllText(Path.Combine(directory, $"groups-trace-{style}.txt"), trace.ToString());
        // Back to what each agent is doing.
        foreach (var bot in _bots)
        {
            var look = LookCatalog.Of(theme, bot.Provider).Id;
            engine.SetLook(bot.Index, look);
            engine.SetState(bot.Index, _slots[bot.Index].Hold, -1);
            _slots[bot.Index].NextStep = 0;
        }
    }

    /// Only on request: AGENTISLAND_DEBUG_FILM_ONLY must name it (groups, or groups-<kind>-<look>).
    private static bool AskedFor(string name) =>
        Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FILM_ONLY")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(name) == true;

    private static AgentPhase PhaseAt((AgentPhase Phase, double Seconds)[] turn, double t)
    {
        foreach (var (phase, seconds) in turn)
        {
            if (t < seconds) return phase;
            t -= seconds;
        }
        return turn[^1].Phase;
    }
}
