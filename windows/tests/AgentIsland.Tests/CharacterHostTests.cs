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
        TestAThrowingBotKeepsTheLoopGoing();
        Console.WriteLine("CharacterHostTests GREEN");
    }

    /// An engine whose codex bot throws for two frames: the host keeps the other bot's frame and reports the throw,
    /// the throwing bot has no frame for those ticks (the drawer keeps its last), and it draws again after.
    private static void TestAThrowingBotKeepsTheLoopGoing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "edgelings-stub-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "characters.mjs"), """
                export const PHASES = ['idle', 'thinking'];
                export const fidgetsOf = () => [];
                export const cycleOf = () => 3;
                export function createBot(kind) {
                    let n = 0, state = 'idle';
                    return {
                        get state() { return state; },
                        setState(next) { state = next; },
                        advance() { n++; },
                        svg() { if (kind === 'codex' && n >= 2 && n <= 3) throw new Error('boom'); return '<svg>' + kind + n + '</svg>'; },
                        setGaze() {}, clearGaze() {}, poke() {},
                    };
                }
                """);
            var reported = new List<string>();
            using var engine = V8CharacterEngine.Create(CharacterTheme.Pixel, folder);
            engine.Failed = error => reported.Add(error.Message);
            var claude = engine.AddBot(DisplayProvider.Claude, 1, 7, null);
            var codex = engine.AddBot(DisplayProvider.Codex, 1, 7, null);
            var steps = new[] { 0.016, 0.016 };
            var first = engine.Tick(steps, 44);
            Expect(first[claude] is { Svg: "<svg>claude1</svg>" } && first[codex] is { Svg: "<svg>codex1</svg>" }, "both draw before the throw");
            var second = engine.Tick(steps, 44);
            Expect(second[claude] is { Svg: "<svg>claude2</svg>" } && second[codex] is null, "the other bot still draws, the throwing one has no new frame");
            var third = engine.Tick(steps, 44);
            Expect(third[claude] is { Svg: "<svg>claude3</svg>" } && third[codex] is null, "and still while it throws");
            var fourth = engine.Tick(steps, 44);
            Expect(fourth[codex] is { Svg: "<svg>codex4</svg>" }, "it draws again once it stops throwing");
            Expect(reported.Count == 2 && reported.All(message => message.Contains("boom") && message.Contains("Pixel")), $"each throw is reported with its message, got {reported.Count}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        Console.WriteLine("PASS a bot whose engine throws leaves the other bots and the loop going");
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
