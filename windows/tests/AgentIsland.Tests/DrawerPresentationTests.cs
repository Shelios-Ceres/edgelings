using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;

namespace AgentIsland.Tests;

public static class DrawerPresentationTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("merged character preserves each provider's activity", TestIndependentActivity),
            ("split and remerged characters use the latest provider frame", TestActivityChanges),
            ("both shared provider visibility choices can be disabled", TestVisibilityChoices),
            ("empty drawer fallback does not overwrite visibility choices", TestVisibilityFallback),
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
        var merged = frame.ForCharacter(DisplayProvider.Grok, merged: true);
        Expect(merged == new DrawerActivity(AgentPhase.Writing, "Cursor · Edit", 2), "merged card follows Cursor");
        Expect(frame.ForProvider(DisplayProvider.Grok) == DrawerActivity.Idle, "Grok settings remain idle");
        Expect(frame.ForProvider(DisplayProvider.Cursor) == new DrawerActivity(AgentPhase.Writing, "Edit", 2),
            "Cursor settings retain their own phase, detail and running count");
    }

    private static void TestActivityChanges()
    {
        var working = Frame(AgentPhase.Writing, "Edit", 2);
        _ = working.ForCharacter(DisplayProvider.Grok, merged: true);
        Expect(working.ForCharacter(DisplayProvider.Grok, merged: false) == DrawerActivity.Idle, "unmerge restores Grok");
        Expect(working.ForCharacter(DisplayProvider.Cursor, merged: false).Phase == AgentPhase.Writing, "unmerge retains Cursor");
        var finished = Frame(AgentPhase.Finished, null, 0);
        Expect(finished.ForCharacter(DisplayProvider.Grok, merged: true) == new DrawerActivity(AgentPhase.Finished, "Cursor", 0),
            "next frame replaces activity without stale tasks");
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
        var merged = DrawerVisibility.Characters(requested, merged: true);
        Expect(merged[DisplayProvider.Grok] && !merged[DisplayProvider.Cursor], "two choices share one rendered character");
        Expect(requested[DisplayProvider.Grok] && requested[DisplayProvider.Cursor], "menu still has both checked choices");
        requested[DisplayProvider.Grok] = false;
        var oneHidden = DrawerVisibility.Characters(requested, merged: true);
        Expect(oneHidden[DisplayProvider.Grok] && !requested[DisplayProvider.Grok], "Cursor keeps character, Grok choice stays unchecked");
        requested[DisplayProvider.Cursor] = false;
        var bothHidden = DrawerVisibility.Characters(requested, merged: true);
        Expect(!bothHidden[DisplayProvider.Grok] && !bothHidden[DisplayProvider.Cursor] && bothHidden[DisplayProvider.Claude],
            "unchecking the remaining checked choice hides the shared character");
        requested[DisplayProvider.Cursor] = true;
        var split = DrawerVisibility.Characters(requested, merged: false);
        Expect(!split[DisplayProvider.Grok] && split[DisplayProvider.Cursor], "unmerge preserves both choices");
    }

    private static void TestVisibilityFallback()
    {
        var requested = DisplayProviders.All.ToDictionary(provider => provider, _ => false);
        var shown = DrawerVisibility.Characters(requested, merged: true);
        Expect(shown.Count(pair => pair.Value) == 1 && shown[DisplayProvider.Claude], "fallback leaves one reachable character");
        Expect(requested.All(pair => !pair.Value), "fallback does not masquerade as a saved preference");
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

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
