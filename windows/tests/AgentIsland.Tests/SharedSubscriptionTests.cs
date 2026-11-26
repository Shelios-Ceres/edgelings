using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Cursor works under Grok's character: it plays the busier app's phase and
/// adds up both apps' tokens. Neither has a quota any more.
public static class SharedSubscriptionTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("the busier app's phase wins, unnamed", TestPickBusier),
            ("a tie goes to Grok", TestPickTie),
            ("quota plays no part: the phases alone decide, billed apart or not", TestPickIgnoresQuota),
            ("no text on Grok's card names Cursor", TestNoCursorWord),
            ("the tray figure is the first line's percent, else null", TestTrayFigure),
            ("Grok Bot alone makes Grok detected, and so do sessions or auth.json", TestGrokDetection),
            ("both apps' tokens add up, dated by the older count", TestTokenSum),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SharedSubscriptionTests GREEN");
    }

    private static void TestPickBusier()
    {
        var cursorWrites = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.Writing, "Edit"));
        Expect(cursorWrites == (AgentPhase.Writing, "Edit"), $"Cursor at work shows, without naming the app: {cursorWrites}");
        var grokRuns = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.Idle, null));
        Expect(grokRuns == (AgentPhase.Running, "Bash"), $"Grok at work shows as itself: {grokRuns}");
        var cursorDone = SharedSubscription.Pick((AgentPhase.Working, null), (AgentPhase.Finished, null));
        Expect(cursorDone == (AgentPhase.Finished, null), $"a finished turn waiting on you outranks work: {cursorDone}");
        var grokAsks = SharedSubscription.Pick((AgentPhase.Asking, null), (AgentPhase.Failed, null));
        Expect(grokAsks.Phase == AgentPhase.Asking, $"a question outranks an error: {grokAsks}");
    }

    private static void TestPickTie()
    {
        var tie = SharedSubscription.Pick((AgentPhase.Thinking, null), (AgentPhase.Writing, null));
        Expect(tie == (AgentPhase.Thinking, null), $"two apps at work show Grok's: {tie}");
        var idle = SharedSubscription.Pick((AgentPhase.Idle, null), (AgentPhase.Idle, null));
        Expect(idle == (AgentPhase.Idle, null), $"both idle is plain idle: {idle}");
    }

    private static void TestPickIgnoresQuota()
    {
        var grokRuns = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.Idle, null));
        Expect(grokRuns == (AgentPhase.Running, "Bash"), $"Grok works on whatever Cursor's account says: {grokRuns}");
        var cursorFails = SharedSubscription.Pick((AgentPhase.Running, "Bash"), (AgentPhase.Failed, "API"));
        Expect(cursorFails.Phase == AgentPhase.Failed, $"an error outranks work, from either app: {cursorFails}");
        var cursorDone = SharedSubscription.Pick((AgentPhase.AuthRequired, null), (AgentPhase.Finished, null));
        Expect(cursorDone.Phase == AgentPhase.AuthRequired, $"a lapsed sign-in written in a transcript outranks a finished turn: {cursorDone}");
    }

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
                texts.Add(L10n.Tr("Grok and Grok Build desktop closed"));
                texts.Add(L10n.Tr("Grok and Grok Build desktop"));
                var named = texts.Where(t => t.Contains("Cursor", StringComparison.OrdinalIgnoreCase)).ToList();
                Expect(named.Count == 0, $"{language}: " + string.Join(" | ", named));
            }
            Expect(L10n.ChineseEntries.Keys.All(key => !key.Contains("billed by Cursor", StringComparison.Ordinal)), "the unreachable Cursor plan caption is gone");
        }
        finally { L10n.Current = previous; }
    }


    private static void TestTrayFigure()
    {
        Expect(StatusCards.TrayFigure(new[] { new StatusMeter("Week", "25%", 0.25, 0.25) }) == "25%", "a reading gives NN%");
        Expect(StatusCards.TrayFigure(new[] { new StatusMeter("5h", "—", null, null) }) is null, "a line without a reading gives null");
        Expect(StatusCards.TrayFigure(Array.Empty<StatusMeter>()) is null, "no line gives null");
    }

    private static void TestGrokDetection()
    {
        Expect(!ProviderVisibilityStore.DetectGrok(_ => false, _ => false, false), "nothing there");
        Expect(ProviderVisibilityStore.DetectGrok(_ => false, _ => false, true), "Grok Bot alone");
        Expect(ProviderVisibilityStore.DetectGrok(_ => true, _ => false, false), "the sessions folder");
        Expect(ProviderVisibilityStore.DetectGrok(_ => false, _ => true, false), "auth.json");
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
}
