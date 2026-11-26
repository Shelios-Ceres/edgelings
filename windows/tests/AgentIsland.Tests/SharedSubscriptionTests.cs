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

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("one pool reset is one subscription", TestSharedPool),
            ("separate plans keep two pools", TestSeparatePlans),
            ("the busier app's phase wins, unnamed", TestPickBusier),
            ("a tie goes to Grok", TestPickTie),
            ("billed apart, one app's spent quota never sleeps over the other's work", TestStandingStateBilledApart),
            ("kept numbers say how old they are, the desktop app's named beside Grok's", TestCursorQuotaAge),
            ("one pool is one line and one account", TestOnePoolOneLine),
            ("one pool keeps Grok's reading unless it is stale; a desktop app spent first is said", TestOnePoolFresher),
            ("Grok Build desktop alone keeps its line, account and warning, unnamed", TestDesktopAlone),
            ("installed but signed out, the desktop app adds no line and no warning", TestDesktopSignedOut),
            ("Grok not detected: none of its cached numbers, account or warning", TestGrokNotDetected),
            ("billed apart, two lines and both accounts; the desktop line is Desk on the card", TestBilledApart),
            ("the desktop app's spent cycle says when it refills", TestDesktopSpent),
            ("the tray reads the card's first line", TestTrayFigure),
            ("no line, warning or note on Grok's card names Cursor", TestNoCursorWord),
            ("both apps' tokens add up, dated by the older count", TestTokenSum),
            ("the character's plan is the X Subscription's tier when shared, never a stale Grok badge", TestPlan),
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
        Expect(cursorWrites == (AgentPhase.Writing, "Edit"), $"Cursor at work shows, without naming the app: {cursorWrites}");
        var grokRuns = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.Idle, null), NotAsked);
        Expect(grokRuns == (AgentPhase.Running, "Bash"), $"Grok at work shows as itself: {grokRuns}");
        var cursorDone = SharedSubscription.Pick((AgentPhase.Working, null), (AgentPhase.Finished, null), NotAsked);
        Expect(cursorDone == (AgentPhase.Finished, null), $"a finished turn waiting on you outranks work: {cursorDone}");
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
        Expect(cursorWrites == (AgentPhase.Writing, "Edit"), $"Cursor works while Grok's weekly pool is spent: {cursorWrites}");
        var cursorDone = SharedSubscription.Pick((AgentPhase.AuthRequired, null), (AgentPhase.Finished, null), Apart);
        Expect(cursorDone == (AgentPhase.Finished, null), $"a finished turn beats the other app's lapsed sign-in: {cursorDone}");
        var idle = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.RateLimited, null), NotAsked);
        Expect(idle == (AgentPhase.RateLimited, null), $"with nothing else going on, the spent quota still shows: {idle}");
        var shared = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.RateLimited, null), Shared);
        Expect(shared == (AgentPhase.RateLimited, null), $"one shared pool spent is the character's: {shared}");
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly WindowUsage GrokWeek = new(0.25, Now.AddDays(3), null, 7 * 86400) { Observed = true };
    private static readonly WindowUsage DesktopCycle = new(0.6, Now.AddDays(12).AddMinutes(5), null, CursorUsageStore.CycleSeconds) { Observed = true };

    private static GrokSide GrokAt(WindowUsage? window = null, DateTimeOffset? at = null, string? account = null, string? warning = null) =>
        new(window ?? GrokWeek, at ?? Now.AddMinutes(-1), account, warning);

    private static GrokSide DeskAt(WindowUsage? window = null, DateTimeOffset? at = null, string? account = null, string? warning = null) =>
        new(window ?? DesktopCycle, at ?? Now.AddMinutes(-1), account, warning);

    private static StatusMeter Meter(WindowUsage window, string label) => StatusCards.MeterFor(window, L10n.Tr(label), false, Now);

    private static IReadOnlyList<StatusMeter> Meters(GrokSides sides) => StatusCards.GrokMeters(sides, Now, Meter);

    private static IReadOnlyList<string> Notes(GrokSides sides) => StatusCards.GrokNotes(sides, Now, Interval).Select(n => n.Text).ToList();

    private static void InEnglish(Action test)
    {
        var previous = L10n.Current;
        L10n.Current = L10n.Language.English;
        try { test(); }
        finally { L10n.Current = previous; }
    }

    private static string Lines(IReadOnlyList<StatusMeter> meters) => string.Join(" | ", meters.Select(m => $"{m.Label}/{m.CardLabel} {m.Value} {m.Resets}"));

    /// Cursor's last numbers were kept through two days of failed refreshes.
    private static void TestCursorQuotaAge() => InEnglish(() =>
    {
        var kept = new WindowUsage(0.4, Now.AddDays(10), "network drop") { Kept = true, Observed = true };
        var alone = Notes(new GrokSides(null, DeskAt(kept, Now.AddDays(-2)), false));
        Expect(alone.SequenceEqual(new[] { "Quota updated 2d ago" }), "alone, unnamed: " + string.Join(" | ", alone));
        var beside = Notes(new GrokSides(GrokAt(), DeskAt(kept, Now.AddDays(-2)), false));
        Expect(beside.SequenceEqual(new[] { "Grok Build desktop: Quota updated 2d ago" }), "beside Grok's, named: " + string.Join(" | ", beside));
        var own = Notes(new GrokSides(GrokAt(kept, Now.AddDays(-2)), null, false));
        Expect(own.SequenceEqual(new[] { "Quota updated 2d ago" }), "Grok's own: " + string.Join(" | ", own));
        Expect(Notes(new GrokSides(GrokAt(), DeskAt(), false)).Count == 0, "live numbers need no note");
        Expect(Notes(new GrokSides(null, null, false)).Count == 0, "no numbers, no note");
        var kimi = StatusCards.QuotaAges((new AppUsage(kept, WindowUsage.Unknown), Now.AddDays(-2), false), Now, Interval);
        Expect(kimi.Count == 1 && kimi[0].Text == "Quota updated 2d ago", "other cards keep their own age");
    });

    private static void TestOnePoolOneLine() => InEnglish(() =>
    {
        var sides = new GrokSides(GrokAt(account: "me@x.example"), DeskAt(account: "me@x.example"), true);
        var one = Meters(sides);
        Expect(one.Count == 1 && one[0].Label == "Week" && one[0].Value == "25%", "one pool, Grok's reading: " + Lines(one));
        var noGrokNumbers = Meters(new GrokSides(GrokAt(WindowUsage.Unknown), DeskAt(), true));
        Expect(noGrokNumbers.Count == 1 && noGrokNumbers[0].Value == "60%", "without Grok's numbers the desktop app's stand in: " + Lines(noGrokNumbers));
        Expect(AgentReports.GrokAccount(sides) == "me@x.example", "one account");
        Expect(AgentReports.GrokAccount(null, "me@x.example", shared: true) == "me@x.example", "the desktop app's when Grok's is missing");
        Expect(AgentReports.GrokAccount("grok@x.example", "desktop@x.example", shared: true) == "grok@x.example", "Grok's first");
        Expect(!new GrokSides(null, DeskAt(), true).Shared, "the same reset with Grok absent is no shared line");
    });

    /// Each app refreshes on its own: the newer reading is the pool as it is now.
    private static void TestOnePoolFresher() => InEnglish(() =>
    {
        const string Spent = "Grok Build desktop's quota is used up, resets in 12d";
        var spent = DesktopCycle with { UsedPercent = 1 };
        foreach (var (grokAt, deskAt) in new[] { (Now.AddMinutes(-10), Now.AddMinutes(-1)), (Now.AddMinutes(-1), Now.AddMinutes(-10)) })
        {
            var steady = Meters(new GrokSides(GrokAt(GrokWeek, grokAt), DeskAt(DesktopCycle, deskAt), true));
            Expect(steady.Single().Value == "25%", "Grok's reading whichever refreshed last: " + Lines(steady));
        }
        var keptGrok = new GrokSides(GrokAt(GrokWeek with { Error = "http 500", Kept = true }), DeskAt(), true);
        Expect(Meters(keptGrok).Single().Value == "60%", "Grok's kept numbers give way to the desktop app's fresh ones: " + Lines(Meters(keptGrok)));
        var pastReset = new GrokSides(GrokAt(GrokWeek with { ResetAt = Now.AddMinutes(-1) }), DeskAt(), true);
        Expect(Meters(pastReset).Single().Value == "60%", "so do Grok's numbers past their reset: " + Lines(Meters(pastReset)));
        var bothKept = new GrokSides(GrokAt(GrokWeek with { Error = "http 500", Kept = true }), DeskAt(DesktopCycle with { Error = "http 500", Kept = true }), true);
        Expect(Meters(bothKept).Single().Value == "25%", "neither fresh: Grok's: " + Lines(Meters(bothKept)));
        var desktopFirst = new GrokSides(GrokAt(GrokWeek with { UsedPercent = 0.95 }), DeskAt(spent), true);
        Expect(Meters(desktopFirst).Single().Value == "95%" && Notes(desktopFirst).Contains(Spent),
            "the desktop app spent first: Grok's number, and the note: " + Lines(Meters(desktopFirst)) + " / " + string.Join(" | ", Notes(desktopFirst)));
        var bothFull = new GrokSides(GrokAt(GrokWeek with { UsedPercent = 1 }), DeskAt(spent), true);
        Expect(!Notes(bothFull).Contains(Spent), "Grok's line full already says it: " + string.Join(" | ", Notes(bothFull)));
        var keptGrokSpent = new GrokSides(GrokAt(GrokWeek with { Error = "http 500", Kept = true }), DeskAt(spent), true);
        Expect(Meters(keptGrokSpent).Single().Value == "100%" && Notes(keptGrokSpent).Contains(Spent), "the desktop app's fresh spent reading, said");
    });

    private static void TestDesktopAlone() => InEnglish(() =>
    {
        var sides = new GrokSides(null, DeskAt(account: "desk@x.example", warning: "http 500"), false);
        var alone = Meters(sides);
        Expect(alone.Count == 1 && alone[0].Label == "30d" && alone[0].CardLabel == "30d" && alone[0].Value == "60%" && alone[0].Resets == "Resets in 12d",
            "one line, labelled by its window: " + Lines(alone));
        var failed = Meters(new GrokSides(null, DeskAt(new WindowUsage(0, null, "Grok Build desktop data unavailable")), false));
        Expect(failed.Count == 1 && failed[0].Value == "—", "a signed-in app whose read failed keeps its line: " + Lines(failed));
        Expect(AgentReports.GrokAccount(sides) == "desk@x.example", "the desktop app's account");
        Expect(AgentReports.GrokWarnings(sides).SequenceEqual(new[] { "http 500" }), "alone, its warning needs no name");
        Expect(Meters(new GrokSides(null, null, false)).Count == 0, "neither app, no line");
    });

    /// Cursor installed and never signed in: the store has no session and no snapshot.
    private static void TestDesktopSignedOut()
    {
        Expect(!GrokSides.DesktopPresent(detected: true, hasSnapshot: false, signedIn: false), "installed, signed out, nothing kept: absent");
        Expect(GrokSides.DesktopPresent(detected: true, hasSnapshot: false, signedIn: true), "signed in, before its first numbers: present");
        Expect(GrokSides.DesktopPresent(detected: true, hasSnapshot: true, signedIn: false), "kept numbers: present");
        Expect(!GrokSides.DesktopPresent(detected: false, hasSnapshot: true, signedIn: true), "not installed: absent");
        var store = new CursorUsageStore(null, _ => { });
        store.Apply(new CursorUsageFetcher.Outcome.ReauthRequired(), new CursorCredentials.Snapshot(null, "free", null));
        Expect(!store.SignedIn && store.Snapshot is null && store.ErrorCaption is not null, "the store reads as signed out, with a sign-in caption");
        var sides = new GrokSides(GrokAt(warning: null),
            GrokSides.DesktopPresent(true, store.Snapshot is not null, store.SignedIn) ? DeskAt(warning: store.ErrorCaption) : null, false);
        Expect(Meters(sides).Count == 1 && AgentReports.GrokWarnings(sides).Count == 0, "Grok's card: Grok's line only, no desktop warning: " + Lines(Meters(sides)));
        store.Apply(new CursorUsageFetcher.Outcome.Failed("timeout"), new CursorCredentials.Snapshot(new CursorCredentials.Session("dummy-token", "account-a"), "pro", "a@x.example"));
        Expect(store.SignedIn, "a session reads as signed in");
    }

    /// grok CLI not detected: what Grok's store kept from an earlier login stays off the card.
    private static void TestGrokNotDetected() => InEnglish(() =>
    {
        var sides = new GrokSides(null, DeskAt(account: "desk@x.example"), SameReset: true);
        Expect(Meters(sides).Single().Label == "30d", "the desktop app's own line, not a shared one: " + Lines(Meters(sides)));
        Expect(AgentReports.GrokAccount(sides) == "desk@x.example", "only the desktop app's account");
        Expect(AgentReports.GrokWarnings(sides).Count == 0, "no Grok warning");
        Expect(Notes(sides).Count == 0, "no Grok quota age");
    });

    private static void TestBilledApart() => InEnglish(() =>
    {
        var sides = new GrokSides(GrokAt(account: "grok@x.example", warning: "sign in again — run grok login"),
            DeskAt(account: "desk@x.example", warning: "http 500"), false);
        var two = Meters(sides);
        Expect(two.Count == 2 && two[0].Label == "Week" && two[0].Value == "25%" && two[1].Label == "desktop" && two[1].CardLabel == "Desk" && two[1].Value == "60%",
            "Grok's week, then the desktop cycle, short on the card: " + Lines(two));
        Expect(AgentReports.GrokAccount(sides) == "grok@x.example · desk@x.example", "both accounts");
        Expect(AgentReports.GrokAccount("me@x.example", "ME@x.example", shared: false) == "me@x.example", "the same account once");
        Expect(AgentReports.GrokWarnings(sides).SequenceEqual(new[] { "sign in again — run grok login", "Grok Build desktop: http 500" }),
            "the desktop app's warning is named: " + string.Join(" | ", AgentReports.GrokWarnings(sides)));
        var self = AgentReports.GrokWarnings(null, "sign in again — open Grok Build desktop (Cursor)", grokDetected: true);
        Expect(self.SequenceEqual(new[] { "sign in again — open Grok Build desktop (Cursor)" }), "a warning naming the app is not named twice");
        Expect(AgentReports.GrokWarnings("same", "same", grokDetected: true).Count == 1, "one warning once");
        L10n.Current = L10n.Language.SimplifiedChinese;
        var zh = Meters(sides);
        Expect(zh[1].Label == "桌面版" && zh[1].CardLabel == "桌面版", "in Chinese: " + Lines(zh));
        Expect(AgentReports.GrokWarnings(null, "parse error", grokDetected: true).Single() == "Grok Build 桌面版：parse error", "named in Chinese");
    });

    private static void TestDesktopSpent() => InEnglish(() =>
    {
        var spent = DesktopCycle with { UsedPercent = 1 };
        Expect(StatusCards.DesktopSpent(spent, Now)?.Text == "Grok Build desktop's quota is used up, resets in 12d", "English: " + StatusCards.DesktopSpent(spent, Now)?.Text);
        Expect(StatusCards.DesktopSpent(spent with { ResetAt = null }, Now)?.Text == "Grok Build desktop's quota is used up", "without a reset");
        Expect(StatusCards.DesktopSpent(DesktopCycle, Now) is null, "a cycle with room says nothing");
        Expect(StatusCards.DesktopSpent(spent with { Error = "http 500", Kept = true }, Now) is null, "kept numbers are not a fresh reading");
        Expect(StatusCards.DesktopSpent(spent with { ResetAt = Now.AddMinutes(-1) }, Now) is null, "a cycle past its reset awaits a new reading");
        Expect(Notes(new GrokSides(GrokAt(), DeskAt(spent), false)).Contains("Grok Build desktop's quota is used up, resets in 12d"), "on the card billed apart");
        L10n.Current = L10n.Language.SimplifiedChinese;
        var zh = StatusCards.DesktopSpent(spent, Now)?.Text;
        Expect(zh == "Grok Build 桌面版的额度已用完，12 天后重置", "Chinese: " + zh);
    });

    private static void TestTrayFigure() => InEnglish(() =>
    {
        Expect(StatusCards.TrayFigure(Meters(new GrokSides(null, DeskAt(), false))) == "60%", "the desktop app's line when it is the only one");
        Expect(StatusCards.TrayFigure(Meters(new GrokSides(GrokAt(), DeskAt(), false))) == "25%", "the first line");
        Expect(StatusCards.TrayFigure(Meters(new GrokSides(null, DeskAt(new WindowUsage(0, null, "http 500")), false))) is null, "no figure without a reading");
        Expect(StatusCards.TrayFigure(Array.Empty<StatusMeter>()) is null, "no line, no figure");
    });

    /// 用户 2026-11-26 定：Cursor 按 Grok Build 桌面版处理，Grok 卡上两种语言都不出现 Cursor。
    private static void TestNoCursorWord()
    {
        var previous = L10n.Current;
        try
        {
            foreach (var language in new[] { L10n.Language.English, L10n.Language.SimplifiedChinese })
            {
                L10n.Current = language;
                var texts = new List<string>();
                texts.AddRange(Meters(new GrokSides(GrokAt(), DeskAt(), false)).SelectMany(m => new[] { m.Label, m.CardLabel }));
                texts.AddRange(Meters(new GrokSides(null, DeskAt(), false)).Select(m => m.Label));
                texts.AddRange(AgentReports.GrokWarnings(null, "http 500", grokDetected: true));
                texts.Add(StatusCards.DesktopSpent(DesktopCycle with { UsedPercent = 1 }, Now)?.Text ?? "");
                texts.Add(L10n.Tr("Grok Build desktop data unavailable"));
                texts.Add(L10n.Tr("Grok Build desktop billing cycle"));
                texts.Add(L10n.Tr("Shares Grok's quota pool"));
                texts.Add(L10n.Tr("Grok and Grok Build desktop closed"));
                texts.Add(L10n.Tr("Grok and Grok Build desktop"));
                var named = texts.Where(t => t.Contains("Cursor", StringComparison.OrdinalIgnoreCase)).ToList();
                Expect(named.Count == 0, $"{language}: " + string.Join(" | ", named));
            }
            Expect(L10n.ChineseEntries.Keys.All(key => !key.Contains("billed by Cursor", StringComparison.Ordinal)), "the unreachable Cursor plan caption is gone");
        }
        finally { L10n.Current = previous; }
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
        Expect(AgentReports.GrokPlan(false, "SUPERGROK", "PRO", sameReset: true) == "PRO", "Grok not detected: its cached badge is not used");
        Expect(AgentReports.GrokPlan(false, "SUPERGROK", null, sameReset: true) is null, "Grok not detected and no desktop plan: no chip");
        Expect(AgentReports.GrokPlan(true, "SUPERGROK", "PRO", sameReset: false) == "SUPERGROK", "Grok detected keeps its badge");
    }
}
