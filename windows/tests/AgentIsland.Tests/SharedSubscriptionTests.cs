using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Model;

namespace AgentIsland.Tests;

/// Grok and Cursor on one X Subscription share a character: detected from the
/// one pool both report, playing the busier app's phase, with both apps'
/// tokens added up.
public static class SharedSubscriptionTests
{
    private static readonly DateTimeOffset Reset = new(2026, 10, 20, 2, 36, 35, 781, TimeSpan.Zero);
    private static readonly string Grok = ProviderIdentity.DisplayName(DisplayProvider.Grok);
    private static readonly string Cursor = ProviderIdentity.DisplayName(DisplayProvider.Cursor);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("one pool reset is one subscription", TestSharedPool),
            ("separate plans keep two characters", TestSeparatePlans),
            ("the busier app's phase wins, named", TestPickBusier),
            ("a tie goes to Grok, idle needs no name", TestPickTie),
            ("both apps' tokens add up, dated by the older count", TestTokenSum),
            ("the shared plan is the X Subscription's tier", TestPlan),
            ("the shared character is named both, either app, or the user's way", TestName),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SharedSubscriptionTests GREEN");
    }

    private static void TestName()
    {
        Expect(SharedSubscription.Name(SharedName.Both, null) == $"{Grok} + {Cursor}", "both apps by default");
        Expect(SharedSubscription.Name(SharedName.Grok, "ignored") == Grok, "Grok's name");
        Expect(SharedSubscription.Name(SharedName.Cursor, null) == Cursor, "Cursor's name");
        Expect(SharedSubscription.Name(SharedName.Custom, "  Pixel pal ") == "Pixel pal", "the user's own name, trimmed");
        Expect(SharedSubscription.Name(SharedName.Custom, "   ") == $"{Grok} + {Cursor}", "a blank name falls back to both");
        Expect(SharedSubscription.Name(SharedName.Custom, new string('x', 30)).Length == SharedSubscription.MaxNameLength,
            "a long name is cut to the limit");
        var robots = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F916), 30));
        var cut = SharedSubscription.Clean(robots);
        Expect(new System.Globalization.StringInfo(cut).LengthInTextElements == SharedSubscription.MaxNameLength
            && !char.IsHighSurrogate(cut[^1]), "the cut never splits an emoji");
    }

    private static void TestSharedPool()
    {
        Expect(SharedSubscription.GrokWithCursor(Reset, Reset), "the same reset instant is one pool");
        Expect(SharedSubscription.GrokWithCursor(Reset, Reset.AddSeconds(30)), "API rounding still counts as one pool");
    }

    private static void TestSeparatePlans()
    {
        Expect(!SharedSubscription.GrokWithCursor(Reset, Reset.AddMinutes(2)), "resets minutes apart are two plans");
        Expect(!SharedSubscription.GrokWithCursor(Reset, Reset.AddDays(-12)), "a monthly Cursor cycle is its own plan");
        Expect(!SharedSubscription.GrokWithCursor(null, Reset) && !SharedSubscription.GrokWithCursor(Reset, null),
            "without both pools there is nothing to merge");
    }

    private static void TestPickBusier()
    {
        var cursorWrites = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.Writing, "Edit"));
        Expect(cursorWrites == (AgentPhase.Writing, $"{Cursor} · Edit"), $"Cursor at work shows, named: {cursorWrites}");
        var grokRuns = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.Idle, null));
        Expect(grokRuns == (AgentPhase.Running, $"{Grok} · Bash"), $"Grok at work shows, named: {grokRuns}");
        var cursorDone = SharedSubscription.Pick((AgentPhase.Working, null), (AgentPhase.Finished, null));
        Expect(cursorDone == (AgentPhase.Finished, Cursor), $"a finished turn waiting on you outranks work: {cursorDone}");
        var grokAsks = SharedSubscription.Pick((AgentPhase.Asking, null), (AgentPhase.Failed, null));
        Expect(grokAsks.Phase == AgentPhase.Asking, $"a question outranks an error: {grokAsks}");
    }

    private static void TestPickTie()
    {
        var tie = SharedSubscription.Pick((AgentPhase.Thinking, null), (AgentPhase.Writing, null));
        Expect(tie == (AgentPhase.Thinking, Grok), $"two apps at work show Grok's: {tie}");
        var idle = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.Idle, null));
        Expect(idle == (AgentPhase.Idle, null), $"both idle is plain idle: {idle}");
    }

    private static void TestTokenSum()
    {
        var now = new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
        var grok = new TokenTotals(now, 64_400, 10_000, 64_400, 10_000);
        var cursor = new TokenTotals(now.AddMinutes(-3), 1_000, 500, 2_000, 900);
        var sum = TokenTotals.Sum(grok, cursor)!;
        Expect(sum.Today == 65_400 && sum.TodayBillable == 10_500 && sum.Month == 66_400 && sum.MonthBillable == 10_900,
            "every count adds up");
        Expect(sum.AsOf == cursor.AsOf, "dated by the older count");
        Expect(TokenTotals.Sum(grok, null) == grok && TokenTotals.Sum(null, cursor) == cursor && TokenTotals.Sum(null, null) is null,
            "a missing side leaves the other as it is");
    }

    private static void TestPlan()
    {
        Expect(SharedSubscription.Plan("SUPERGROK", "PREMIUM") == "PREMIUM", "Cursor reports the subscription's tier");
        Expect(SharedSubscription.Plan("SUPERGROK", null) == "SUPERGROK", "Grok's badge stands in without it");
        Expect(SharedSubscription.Plan(null, null) is null, "no plan, no chip");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
