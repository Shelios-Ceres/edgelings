using AgentIsland.Model;

namespace AgentIsland.Tests;

/// Where a locked or unlocked drawer stands, and the order its characters
/// stand in. Numbers are the user's screen: 1600 px tall at 150 %, with the
/// taskbar's 48 px across the top.
public static class DrawerPlacementTests
{
    private const int WorkTop = 48;
    private const int WorkBottom = 1600;
    private const double Scale = 1.5;

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("unlocked, the offset places the drawer", TestOffset),
            ("locked, the top stays put when characters come and go", TestPinnedTopHolds),
            ("locked, it moves only as far as it must to stay on screen", TestPinnedClampsOnScreen),
            ("locking and unlocking never move the drawer", TestRoundTrip),
            ("the saved order comes first, then the rest by default", TestOrder),
            ("a character left out of the list keeps its slot", TestReorderedKeepsSlot),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("DrawerPlacementTests GREEN");
    }

    private static void TestOffset()
    {
        Expect(DrawerPlacement.Top(WorkTop, WorkBottom, 507, Scale, 0, null) == WorkTop, "offset 0 is the top");
        Expect(DrawerPlacement.Top(WorkTop, WorkBottom, 507, Scale, 1, null) == WorkBottom - 507, "offset 1 is the bottom");
        var shrunk = DrawerPlacement.Top(WorkTop, WorkBottom, 426, Scale, 0.917, null);
        var grown = DrawerPlacement.Top(WorkTop, WorkBottom, 588, Scale, 0.917, null);
        Expect(shrunk != grown, "unlocked, a column that grows or shrinks moves");
    }

    private static void TestPinnedTopHolds()
    {
        var six = DrawerPlacement.Top(WorkTop, WorkBottom, 507, Scale, 0.917, 640);
        var seven = DrawerPlacement.Top(WorkTop, WorkBottom, 588, Scale, 0.917, 640);
        var five = DrawerPlacement.Top(WorkTop, WorkBottom, 426, Scale, 0.917, 640);
        Expect(six == WorkTop + 960 && seven == six && five == six, $"the pinned top holds: {five} / {six} / {seven}");
    }

    private static void TestPinnedClampsOnScreen()
    {
        var tall = DrawerPlacement.Top(WorkTop, WorkBottom, 700, Scale, 0.917, 640);
        Expect(tall == WorkBottom - 700, $"a column that would run off the bottom rises just enough: {tall}");
        var above = DrawerPlacement.Top(WorkTop, WorkBottom, 507, Scale, 0.5, -20);
        Expect(above == WorkTop, "a pin above the work area comes back onto it");
    }

    private static void TestRoundTrip()
    {
        const int top = 1002;
        var pinned = DrawerPlacement.PinFor(top, WorkTop, Scale);
        Expect(DrawerPlacement.Top(WorkTop, WorkBottom, 507, Scale, 0, pinned) == top, "locking keeps the drawer where it is");
        var offset = DrawerPlacement.OffsetFor(top, WorkTop, WorkBottom, 507);
        Expect(DrawerPlacement.Top(WorkTop, WorkBottom, 507, Scale, offset, null) == top, "and so does unlocking");
        Expect(DrawerPlacement.OffsetFor(48, WorkTop, WorkBottom, 2000) == 0.5, "a column taller than the screen centres");
    }

    private static void TestOrder()
    {
        Expect(DrawerPlacement.Order(null).SequenceEqual(DisplayProviders.All), "no saved order is the default");
        var order = DrawerPlacement.Order(new[] { "kimi", "claude", "nonsense", "kimi" });
        Expect(order[0] == DisplayProvider.Kimi && order[1] == DisplayProvider.Claude, "the saved order comes first");
        Expect(order.Count == DisplayProviders.All.Length && order.Distinct().Count() == order.Count,
            "every provider appears once, unknown and repeated names dropped");
        var rest = order.Skip(2).ToList();
        Expect(rest.SequenceEqual(DisplayProviders.All.Where(p => p is not (DisplayProvider.Kimi or DisplayProvider.Claude))),
            "the rest follow in the default order");
    }

    private static void TestReorderedKeepsSlot()
    {
        var saved = DrawerPlacement.Order(null);
        var shown = saved.Where(provider => provider != DisplayProvider.Cursor).ToList();
        shown.Remove(DisplayProvider.Grok);
        shown.Insert(0, DisplayProvider.Grok);
        var order = DrawerPlacement.Reordered(saved, shown);
        Expect(order.SequenceEqual(new[]
            {
                DisplayProvider.Grok, DisplayProvider.Claude, DisplayProvider.Codex, DisplayProvider.Antigravity,
                DisplayProvider.Cursor, DisplayProvider.Kimi, DisplayProvider.DeepSeek,
            }), "Grok moves to the top; Cursor, left out, stays in its slot: " + string.Join(", ", order));
        Expect(DrawerPlacement.Reordered(saved, saved.Reverse()).SequenceEqual(saved.Reverse()), "a full list is taken as it is");
        Expect(DrawerPlacement.Reordered(order, Array.Empty<DisplayProvider>()).SequenceEqual(order), "an empty list changes nothing");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
