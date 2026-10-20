using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

public static class DrawerPresentationTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("Grok's character carries Cursor's work, each provider keeps its own", TestIndependentActivity),
            ("Grok's character follows the latest frame of both apps", TestActivityChanges),
            ("Cursor never gets a character of its own", TestVisibilityChoices),
            ("empty drawer fallback does not overwrite visibility choices", TestVisibilityFallback),
            ("one switch per character, none for Cursor", TestSwitches),
            ("the Agents page lists the shown in order, the rest below", TestLineup),
            ("pixel card rebuilds across DPI changes", TestPixelScale),
            ("glass card keeps its DPI-independent content cache", TestGlassScale),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("DrawerPresentationTests GREEN");
    }

    private static DrawerActivityFrame Frame(AgentPhase cursor, string? detail, int running) => new(new[]
    {
        (DisplayProvider.Grok, AgentPhase.Idle, (string?)null, 0),
        (DisplayProvider.Cursor, cursor, detail, running),
    });

    private static void TestIndependentActivity()
    {
        var frame = Frame(AgentPhase.Writing, "Edit", 2);
        Expect(frame.ForCharacter(DisplayProvider.Grok) == new DrawerActivity(AgentPhase.Writing, "Cursor · Edit", 2),
            "Grok's character follows Cursor, naming it");
        Expect(frame.ForProvider(DisplayProvider.Grok) == DrawerActivity.Idle, "Grok's own reading stays idle");
        Expect(frame.ForProvider(DisplayProvider.Cursor) == new DrawerActivity(AgentPhase.Writing, "Edit", 2),
            "Cursor keeps its own phase, detail and running count");
    }

    private static void TestActivityChanges()
    {
        var finished = Frame(AgentPhase.Finished, null, 0);
        Expect(finished.ForCharacter(DisplayProvider.Grok) == new DrawerActivity(AgentPhase.Finished, "Cursor", 0),
            "next frame replaces activity without stale tasks");
        var grokWorks = new DrawerActivityFrame(new[]
        {
            (DisplayProvider.Grok, AgentPhase.Running, (string?)"Bash", 1),
            (DisplayProvider.Cursor, AgentPhase.Idle, (string?)null, 0),
        });
        Expect(grokWorks.ForCharacter(DisplayProvider.Grok) == new DrawerActivity(AgentPhase.Running, "Bash", 1),
            "Grok's own work needs no app name");
        Expect(finished.ForProvider(DisplayProvider.Claude) == DrawerActivity.Idle, "missing observation is safely idle");
    }

    private static void TestVisibilityChoices()
    {
        var requested = new Dictionary<DisplayProvider, bool>
        {
            [DisplayProvider.Claude] = true,
            [DisplayProvider.Grok] = true,
            [DisplayProvider.Cursor] = true,
        };
        var shown = DrawerVisibility.Characters(requested);
        Expect(shown[DisplayProvider.Grok] && !shown[DisplayProvider.Cursor], "Cursor shows as Grok's character");
        requested[DisplayProvider.Grok] = false;
        requested[DisplayProvider.Cursor] = false;
        var hidden = DrawerVisibility.Characters(requested);
        Expect(!hidden[DisplayProvider.Grok] && !hidden[DisplayProvider.Cursor] && hidden[DisplayProvider.Claude],
            "Grok's choice hides the character");
    }

    private static void TestVisibilityFallback()
    {
        var requested = DisplayProviders.All.ToDictionary(provider => provider, _ => false);
        var shown = DrawerVisibility.Characters(requested);
        Expect(shown.Count(pair => pair.Value) == 1 && shown[DisplayProvider.Claude], "fallback leaves one reachable character");
        Expect(requested.All(pair => !pair.Value), "fallback does not masquerade as a saved preference");
    }

    private static void TestLineup()
    {
        var order = new[] { DisplayProvider.Codex, DisplayProvider.Claude, DisplayProvider.Cursor, DisplayProvider.Kimi, DisplayProvider.DeepSeek, DisplayProvider.Grok };
        var lineup = DrawerVisibility.Lineup(order, provider => provider is not (DisplayProvider.Claude or DisplayProvider.DeepSeek));
        Expect(lineup.Select(e => e.Provider).SequenceEqual(new[]
            {
                DisplayProvider.Codex, DisplayProvider.Kimi, DisplayProvider.Grok, DisplayProvider.Claude, DisplayProvider.DeepSeek,
            }), "the shown keep the order, the rest follow in it");
        Expect(lineup.Select(e => e.Shown).SequenceEqual(new[] { true, true, true, false, false }), "each says whether it shows");
        Expect(lineup.All(e => e.Provider != DisplayProvider.Cursor), "Cursor is on Grok's card, not its own");
    }

    private static void TestSwitches()
    {
        var requested = new (DisplayProvider Provider, bool Requested)[]
        {
            (DisplayProvider.Claude, true), (DisplayProvider.Grok, false), (DisplayProvider.Cursor, false),
        };
        var switches = DrawerVisibility.Switches(requested);
        Expect(switches.Select(s => s.Provider).SequenceEqual(new[] { DisplayProvider.Claude, DisplayProvider.Grok }),
            "one switch per character, Grok's standing for Cursor");
        Expect(DisplayProvider.Cursor.Character() == DisplayProvider.Grok && !DisplayProvider.Cursor.HasCharacter()
            && DisplayProvider.Kimi.HasCharacter(), "Cursor works under Grok's character");
    }

    private static readonly (StatusCard Card, uint Swatch)[] Sections =
    {
        (new StatusCard(DisplayProvider.Cursor, "Cursor", "Idle", null, null,
            Array.Empty<StatusMeter>(), null, Array.Empty<StatusPhrase>()), 0x3388FF),
    };

    private static void TestPixelScale()
    {
        var first = HoverCardContentKey.Create(Sections, false, true, 1);
        var moved = HoverCardContentKey.Create(Sections, false, true, 1.5);
        Expect(first != moved, "identical data at a new DPI cannot reuse old pixel fonts and measurements");
        Expect(first == HoverCardContentKey.Create(Sections, false, true, 1), "returning to the original DPI is stable");
    }

    private static void TestGlassScale() =>
        Expect(HoverCardContentKey.Create(Sections, false, false, 1) == HoverCardContentKey.Create(Sections, false, false, 1.5),
            "system-font content does not rebuild solely for scale");
}
