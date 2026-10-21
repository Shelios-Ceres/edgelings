using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The drawer's V8 host (V8CharacterEngine) over the two engines the app
/// ships: each set builds, draws a frame and takes every cue without
/// throwing. Classic plays its cues through the host's `cue`, so a host
/// built without it threw on the first one, inside the game loop.
public static class CharacterHostTests
{
    public static void RunAll()
    {
        foreach (var theme in new[] { CharacterTheme.Pixel, CharacterTheme.Classic })
        {
            using var engine = V8CharacterEngine.Create(theme, EngineDirectory());
            var bot = engine.AddBot(DisplayProvider.Claude, 1, 7, null);
            engine.SetState(bot, "idle", 0);
            Expect(engine.Tick(new[] { 0.016 }, 44)[bot] is { Svg.Length: > 0 }, $"{theme}: the host draws a frame");
            foreach (var cue in new[] { "done", "note", "ask", "alert", "error" }) engine.Cue(bot, cue);
            Expect(engine.Tick(new[] { 0.016 }, 44)[bot] is { Svg.Length: > 0 }, $"{theme}: the host draws after its cues");
        }
        Console.WriteLine("PASS both character sets build, draw and take every cue through the V8 host");
        Console.WriteLine("CharacterHostTests GREEN");
    }

    /// The engines as shipped, found from the test binaries up to the repo.
    private static string EngineDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "src", "AgentIsland.WinUI", "Drawer", "Characters");
            if (File.Exists(Path.Combine(folder, "classic.mjs"))) return folder;
        }
        throw new FileNotFoundException("the shipped engines (Drawer/Characters)");
    }
}
