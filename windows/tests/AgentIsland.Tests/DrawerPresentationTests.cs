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
            ("one switch stands for the shared character and can always hide it", TestSharedSwitch),
            ("the Agents page groups follow the order, hidden agents apart", TestGroups),
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

    private static void TestGroups()
    {
        var order = new[] { DisplayProvider.Codex, DisplayProvider.Claude, DisplayProvider.Kimi, DisplayProvider.DeepSeek, DisplayProvider.Grok };
        var (detected, missing, hidden) = DrawerVisibility.Groups(order,
            provider => provider is DisplayProvider.Claude or DisplayProvider.Grok,
            provider => provider is not (DisplayProvider.DeepSeek or DisplayProvider.Grok));
        Expect(detected.SequenceEqual(new[] { DisplayProvider.Codex, DisplayProvider.Kimi }), "detected agents keep the order");
        Expect(missing.SequenceEqual(new[] { DisplayProvider.DeepSeek }), "agents not found here form their own group");
        Expect(hidden.SequenceEqual(new[] { DisplayProvider.Claude, DisplayProvider.Grok }), "hidden agents wait apart, found or not");
    }

    private static void TestSharedSwitch()
    {
        var requested = new Dictionary<DisplayProvider, bool>
        {
            [DisplayProvider.Claude] = true,
            [DisplayProvider.Grok] = false,
            [DisplayProvider.Cursor] = true,
        };
        IReadOnlyList<(DisplayProvider Provider, bool Requested)> Choices() =>
            requested.Select(pair => (pair.Key, pair.Value)).ToList();

        var merged = DrawerVisibility.Switches(Choices(), merged: true);
        Expect(merged.All(choice => choice.Provider != DisplayProvider.Cursor), "no Cursor switch while merged");
        Expect(merged.Single(choice => choice.Provider == DisplayProvider.Grok).On,
            "Grok's switch is on while Cursor's choice shows the shared character");
        foreach (var choice in DrawerVisibility.SetBy(DisplayProvider.Grok, merged: true)) requested[choice] = false;
        var hidden = DrawerVisibility.Characters(requested, merged: true);
        Expect(!hidden[DisplayProvider.Grok] && !hidden[DisplayProvider.Cursor] && hidden[DisplayProvider.Claude],
            "switching Grok off hides the shared character");

        var split = DrawerVisibility.Switches(Choices(), merged: false);
        Expect(split.Count == 3 && DrawerVisibility.SetBy(DisplayProvider.Grok, merged: false).SequenceEqual(new[] { DisplayProvider.Grok }),
            "split, every provider has its own switch");
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
