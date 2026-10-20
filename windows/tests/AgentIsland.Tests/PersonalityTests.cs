using AgentIsland.Model;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Each character keeps its own pace, so the drawer never moves in
/// lockstep. Its idle habits come from its engine (PixelThemeTests,
/// ClassicThemeTests).
public static class PersonalityTests
{
    public static void RunAll()
    {
        var tempos = DisplayProviders.All.Select(DrawerLooks.TempoOf).ToList();
        Expect(tempos.Distinct().Count() == tempos.Count, "no two characters share a tempo");
        Expect(tempos.All(t => t is >= 0.85 and <= 1.15), "tempos stay close enough to normal to look natural");
        Console.WriteLine("PASS every character has its own tempo");
        Console.WriteLine("PersonalityTests GREEN");
    }
}
