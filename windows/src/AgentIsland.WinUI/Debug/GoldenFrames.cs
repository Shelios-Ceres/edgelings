using AgentIsland.Model;
using Microsoft.Graphics.Canvas;
using AgentIsland.WinUI.Shell;

namespace AgentIsland.WinUI.Drawer;

/// AGENTISLAND_DEBUG_GOLDEN=<dir>: the reference frames for the Mac port's
/// drawing check. Every look of both styles in a few states, each from a
/// fresh bot with a fixed seed and three 0.4 s steps, as the SVG the engine
/// hands back and as the PNG the Win2D painter makes of it (44 DIP at 192 dpi,
/// Pixel aliased like the drawer). frames.tsv lists them in the order the bots
/// were added, which the Mac side replays so the engine's ids match.
internal static class GoldenFrames
{
    private const int Seed = 7;
    private const double Size = 44;
    private const float Dpi = 192;
    private static readonly string[] States = { "idle", "thinking", "running", "finished", "failed" };

    public static async Task WriteAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var device = CanvasDevice.GetSharedDevice();
            var canvas = new SvgCanvas();
            var rows = new List<string> { "name\ttheme\tkind\tlook\tstate\tbot\ttempo\tseed" };
            foreach (var theme in new[] { CharacterTheme.Pixel, CharacterTheme.Classic })
            {
                using var engine = V8CharacterEngine.Create(theme, V8CharacterEngine.Directory);
                var seen = new HashSet<string>();
                foreach (var provider in Enum.GetValues<DisplayProvider>())
                {
                    var kind = DrawerLooks.Kind(provider);
                    foreach (var look in LookCatalog.For(theme, provider))
                    {
                        if (!seen.Add(kind + "/" + look.Id)) continue;
                        foreach (var state in States)
                        {
                            if (!engine.Draws(state)) continue;
                            var tempo = DrawerLooks.TempoOf(provider);
                            var bot = engine.AddBot(provider, tempo, Seed, look.Id);
                            engine.SetState(bot, state, 0);
                            string? svg = null;
                            for (var step = 0; step < 3; step++) svg = engine.Tick(Advance(bot, 0.4), Size)[bot]?.Svg;
                            if (svg is null) continue;
                            var name = $"{theme.ToString().ToLowerInvariant()}-{kind}-{look.Id}-{state}";
                            await File.WriteAllTextAsync(Path.Combine(directory, name + ".svg"), svg);
                            using (var target = new CanvasRenderTarget(device, (float)Size, (float)Size, Dpi))
                            {
                                using (var ds = target.CreateDrawingSession())
                                {
                                    ds.Clear(Microsoft.UI.Colors.Transparent);
                                    if (theme == CharacterTheme.Pixel) ds.Antialiasing = CanvasAntialiasing.Aliased;
                                    canvas.Render(svg, ds);
                                }
                                await target.SaveAsync(Path.Combine(directory, name + ".png"));
                            }
                            rows.Add(string.Join('\t', name, theme.ToString().ToLowerInvariant(), kind, look.Id, state, bot,
                                tempo.ToString("R", System.Globalization.CultureInfo.InvariantCulture), Seed));
                        }
                    }
                }
            }
            await File.WriteAllLinesAsync(Path.Combine(directory, "frames.tsv"), rows);
            DebugTrace.Write($"golden frames: {rows.Count - 1} written to {directory}");
        }
        catch (Exception error)
        {
            DebugTrace.Write("golden frames failed: " + error);
        }
    }

    private static double[] Advance(int index, double dt)
    {
        var dts = new double[index + 1];
        Array.Fill(dts, double.NaN);
        dts[index] = dt;
        return dts;
    }
}
