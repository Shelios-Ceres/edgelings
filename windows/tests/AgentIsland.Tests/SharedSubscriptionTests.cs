using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Cursor works under Grok's character: it plays the busier app's phase,
/// adds up both apps' tokens, and shows one quota pool when both are on one
/// X Subscription (detected from the reset both report).
public static class SharedSubscriptionTests
{
    private static readonly DateTimeOffset Reset = new(2026, 10, 20, 2, 36, 35, 781, TimeSpan.Zero);
    private static readonly string Cursor = ProviderIdentity.DisplayName(DisplayProvider.Cursor);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("one pool reset is one subscription", TestSharedPool),
            ("separate plans keep two pools", TestSeparatePlans),
            ("the busier app's phase wins, named", TestPickBusier),
            ("a tie goes to Grok, only Cursor's work is named", TestPickTie),
            ("billed apart, one app's spent quota never sleeps over the other's work", TestStandingStateBilledApart),
            ("Cursor's kept numbers on Grok's card say how old they are", TestCursorQuotaAge),
            ("both apps' tokens add up, dated by the older count", TestTokenSum),
            ("the character's plan is the X Subscription's tier when shared", TestPlan),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SharedSubscriptionTests GREEN");
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
        var cursorWrites = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.Writing, "Edit"), NotAsked);
        Expect(cursorWrites == (AgentPhase.Writing, $"{Cursor} · Edit"), $"Cursor at work shows, named: {cursorWrites}");
        var grokRuns = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.Idle, null), NotAsked);
        Expect(grokRuns == (AgentPhase.Running, "Bash"), $"Grok at work shows as itself: {grokRuns}");
        var cursorDone = SharedSubscription.Pick((AgentPhase.Working, null), (AgentPhase.Finished, null), NotAsked);
        Expect(cursorDone == (AgentPhase.Finished, Cursor), $"a finished turn waiting on you outranks work: {cursorDone}");
        var grokAsks = SharedSubscription.Pick((AgentPhase.Asking, null), (AgentPhase.Failed, null), NotAsked);
        Expect(grokAsks.Phase == AgentPhase.Asking, $"a question outranks an error: {grokAsks}");
    }

    private static void TestPickTie()
    {
        var tie = SharedSubscription.Pick((AgentPhase.Thinking, null), (AgentPhase.Writing, null), NotAsked);
        Expect(tie == (AgentPhase.Thinking, null), $"two apps at work show Grok's: {tie}");
        var idle = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.Idle, null), NotAsked);
        Expect(idle == (AgentPhase.Idle, null), $"both idle is plain idle: {idle}");
    }

    private static void TestStandingStateBilledApart()
    {
        var grokRuns = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.RateLimited, null), Apart);
        Expect(grokRuns == (AgentPhase.Running, "Bash"), $"Grok works while Cursor's own quota is spent: {grokRuns}");
        var cursorWrites = SharedSubscription.Pick((AgentPhase.RateLimited, null), (AgentPhase.Writing, "Edit"), Apart);
        Expect(cursorWrites == (AgentPhase.Writing, $"{Cursor} · Edit"), $"Cursor works while Grok's weekly pool is spent: {cursorWrites}");
        var cursorDone = SharedSubscription.Pick((AgentPhase.AuthRequired, null), (AgentPhase.Finished, null), Apart);
        Expect(cursorDone == (AgentPhase.Finished, Cursor), $"a finished turn beats the other app's lapsed sign-in: {cursorDone}");
        var idle = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.RateLimited, null), NotAsked);
        Expect(idle == (AgentPhase.RateLimited, Cursor), $"with nothing else going on, the spent quota still shows: {idle}");
        var shared = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.RateLimited, null), Shared);
        Expect(shared == (AgentPhase.RateLimited, Cursor), $"one shared pool spent is the character's: {shared}");
    }

    /// No grok CLI login, so Grok's own numbers are missing; Cursor's last
    /// numbers were kept through two days of failed refreshes.
    private static void TestCursorQuotaAge()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var interval = TimeSpan.FromMinutes(5);
        var noGrok = (AppUsage.Empty, (DateTimeOffset?)null, false);
        var kept = new AppUsage(new WindowUsage(0.4, now.AddDays(10), "network drop") { Kept = true, Observed = true }, WindowUsage.Unknown);
        var previous = L10n.Current;
        L10n.Current = L10n.Language.English;
        try
        {
            var ages = StatusCards.QuotaAges(noGrok, (kept, now.AddDays(-2)), now, interval);
            Expect(ages.Count == 1 && ages[0].Text == $"{Cursor}: Quota updated 2d ago", "named: " + string.Join(" | ", ages.Select(a => a.Text)));
            var live = new AppUsage(new WindowUsage(0.4, now.AddDays(10), null) { Observed = true }, WindowUsage.Unknown);
            Expect(StatusCards.QuotaAges(noGrok, (live, now.AddMinutes(-1)), now, interval).Count == 0, "live numbers need no note");
            Expect(StatusCards.QuotaAges(noGrok, null, now, interval).Count == 0, "no Cursor line, no Cursor note");
        }
        finally
        {
            L10n.Current = previous;
        }
    }

    private static readonly Func<bool> Apart = () => false;
    private static readonly Func<bool> Shared = () => true;
    private static readonly Func<bool> NotAsked = () => throw new Exception("asked whether the pool is shared for no reason");

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
        Expect(SharedSubscription.Plan("SUPERGROK", "PREMIUM", shared: true) == "PREMIUM", "Cursor reports the subscription's tier");
        Expect(SharedSubscription.Plan("SUPERGROK", null, shared: true) == "SUPERGROK", "Grok's badge stands in without it");
        Expect(SharedSubscription.Plan("SUPERGROK", "PRO", shared: false) == "SUPERGROK", "billed apart, the badge is Grok's");
        Expect(SharedSubscription.Plan(null, "PRO", shared: false) == "PRO", "with only Cursor, its plan");
        Expect(SharedSubscription.Plan(null, null, shared: true) is null, "no plan, no chip");
    }
}
