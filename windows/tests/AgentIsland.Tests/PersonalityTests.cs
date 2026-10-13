using AgentIsland.Model;

namespace AgentIsland.Tests;

/// Each character keeps its own pace and habits, so the drawer never moves
/// in lockstep.
public static class PersonalityTests
{
    public static void RunAll()
    {
        var personalities = DisplayProviders.All.Select(DrawerLooks.PersonalityOf).ToList();
        Expect(personalities.Select(p => p.Tempo).Distinct().Count() == personalities.Count,
            "no two characters share a tempo");
        Expect(personalities.All(p => p.Tempo is >= 0.85 and <= 1.15),
            "tempos stay close enough to normal to look natural");
        Expect(personalities.All(p => p.Fidgets.Length > 0), "every character has something to do when idle");
        Expect(personalities.SelectMany(p => p.Fidgets).All(f => !DrawerLooks.OneShotStates.Contains(f)),
            "fidgets loop, so the drawer decides when they end");
        Console.WriteLine("PASS every character has its own tempo and idle habits");
        Console.WriteLine("PersonalityTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
