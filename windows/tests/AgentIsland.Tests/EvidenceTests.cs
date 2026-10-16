using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// Trouble shown only on evidence: an agent with no process left is idle,
/// Windows reporting no internet holds up working agents, and a quota check
/// that fails on the network no longer reads as "out of quota".
public static class EvidenceTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a mid-task session whose agent is gone is idle", TestGoneAgentIsIdle),
            ("offline turns working agents into network trouble", TestOfflineIsNetworkTrouble),
            ("a closed app wins over being offline", TestGoneBeatsOffline),
            ("process check knows each agent by name", TestProcessNames),
            ("the Kimi desktop app does not count as Kimi Code", TestKimiDesktopIsNotKimiCode),
            ("an unreadable process path counts as alive", TestUnreadablePathIsAlive),
            ("a quota check failing on the network is not out of quota", TestQuotaNetworkErrorIsNotRateLimited),
            ("a quota check that is rate limited still is", TestQuotaRateLimitStillCounts),
            ("error texts sort into network, quota, sign-in and other", TestTroubleFromText),
            ("a finished turn with an error reading shows the error", TestCombineFailedTurn),
            ("a working agent's retry reading stays working", TestCombineTransientRetry),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("EvidenceTests GREEN");
    }

    private static void TestGoneAgentIsIdle()
    {
        var settled = ActivityMonitor.Settle(new List<ScannedSession>
        {
            Session(TriggerTool.Codex, "a", ActivityState.Working),
            Session(TriggerTool.Codex, "b", ActivityState.NeedsYou),
            Session(TriggerTool.Claude, "c", ActivityState.Working),
        }, new HashSet<TriggerTool> { TriggerTool.Codex }, offline: false);
        Expect(settled[0].Status == ActivityState.Idle, "a working session of a closed agent is idle");
        Expect(settled[1].Status == ActivityState.NeedsYou, "a finished turn keeps waiting on the user");
        Expect(settled[2].Status == ActivityState.Working, "other agents are untouched");
    }

    private static void TestOfflineIsNetworkTrouble()
    {
        var settled = ActivityMonitor.Settle(new List<ScannedSession>
        {
            Session(TriggerTool.Claude, "a", ActivityState.Working),
            Session(TriggerTool.Claude, "b", ActivityState.NeedsYou),
            Session(TriggerTool.Grok, "c", ActivityState.Idle),
        }, new HashSet<TriggerTool>(), offline: true);
        Expect(settled[0].Status == ActivityState.NetworkTrouble, "a working agent cannot get anywhere offline");
        Expect(settled[1].Status == ActivityState.NeedsYou, "a finished turn is not held up by the network");
        Expect(settled[2].Status == ActivityState.Idle, "an idle agent stays idle");
    }

    private static void TestGoneBeatsOffline()
    {
        var settled = ActivityMonitor.Settle(new List<ScannedSession> { Session(TriggerTool.Grok, "a", ActivityState.Working) },
            new HashSet<TriggerTool> { TriggerTool.Grok }, offline: true);
        Expect(settled[0].Status == ActivityState.Idle, "an agent that is not running is idle, network or not");
    }

    private static void TestProcessNames()
    {
        var gone = AgentProcesses.Gone(new[] { ("Claude", (string?)null), ("codex", null) });
        Expect(!gone.Contains(TriggerTool.Claude) && !gone.Contains(TriggerTool.Codex), "running agents are not gone");
        Expect(gone.Contains(TriggerTool.Grok) && gone.Contains(TriggerTool.Cursor)
            && gone.Contains(TriggerTool.DeepSeek) && gone.Contains(TriggerTool.Antigravity), "agents with no process are gone");
        var desktops = AgentProcesses.Gone(new[]
        {
            ("Kimi Code", (string?)null), ("Antigravity", null), ("DeepSeek Harness", null),
        });
        Expect(!desktops.Contains(TriggerTool.Kimi) && !desktops.Contains(TriggerTool.Antigravity)
            && !desktops.Contains(TriggerTool.DeepSeek), "the desktop apps run their agents' sessions");
        Expect(!AgentProcesses.Gone(new[] { ("agy", (string?)null) }).Contains(TriggerTool.Antigravity),
            "Antigravity's CLI starts as agy");
    }

    private static void TestKimiDesktopIsNotKimiCode()
    {
        var desktop = AgentProcesses.Gone(new[] { ("Kimi", (string?)@"C:\Users\x\AppData\Local\Programs\kimi-desktop\Kimi.exe") });
        Expect(desktop.Contains(TriggerTool.Kimi), "the Kimi desktop app is not Kimi Code");
        var code = AgentProcesses.Gone(new[] { ("kimi", (string?)@"C:\Users\x\.kimi-code\bin\kimi.exe") });
        Expect(!code.Contains(TriggerTool.Kimi), "kimi.exe under .kimi-code is Kimi Code");
    }

    private static void TestUnreadablePathIsAlive()
    {
        var gone = AgentProcesses.Gone(new[] { ("kimi", (string?)null) });
        Expect(!gone.Contains(TriggerTool.Kimi), "a session is never idled on a guess");
    }

    private static void TestQuotaNetworkErrorIsNotRateLimited()
    {
        foreach (var error in new[] { "network timeout", "http 503", "connection reset", "bad response" })
        {
            var state = ActivityMonitor.UsageAttentionState(AppUsage.ErrorPair(error));
            Expect(state is null, $"'{error}' only makes the quota unknown, got {state}");
        }
    }

    private static void TestQuotaRateLimitStillCounts()
    {
        Expect(ActivityMonitor.UsageAttentionState(AppUsage.ErrorPair("rate limited")) == ActivityState.RateLimited,
            "a rate-limited quota check still reads as out of quota");
        var full = AppUsage.Empty with { Weekly = new WindowUsage(1, null, null) };
        Expect(ActivityMonitor.UsageAttentionState(full) == ActivityState.RateLimited, "a used-up window is out of quota");
    }

    private static void TestTroubleFromText()
    {
        Expect(SessionTurnState.TroubleFrom("You've hit your session limit · resets 2am") == TurnTrouble.RateLimited, "session limit");
        Expect(SessionTurnState.TroubleFrom("Failed to authenticate. API Error: 403 Request not allowed") == TurnTrouble.AuthRequired, "authenticate");
        Expect(SessionTurnState.TroubleFrom("API Error: 401 unauthorized") == TurnTrouble.AuthRequired, "401");
        Expect(SessionTurnState.TroubleFrom("stream disconnected before completion") == TurnTrouble.Network, "stream disconnected");
        Expect(SessionTurnState.TroubleFrom("""{"code":"ECONNRESET"}""") == TurnTrouble.Network, "ECONNRESET");
        Expect(SessionTurnState.TroubleFrom("invalid_request_error: unsupported parameter") == TurnTrouble.Failed, "other");
        Expect(SessionTurnState.TroubleFrom("listening on port 14290") == TurnTrouble.Failed, "a 429 inside a number is no status code");
    }

    private static void TestCombineFailedTurn()
    {
        var failed = new PhaseReading(AgentPhase.Failed, "API", DateTimeOffset.UtcNow);
        Expect(AgentPhases.Combine(ActivityState.NeedsYou, failed, quotaExhausted: false) == AgentPhase.Failed, "an error, not your turn");
        var finished = new PhaseReading(AgentPhase.Finished, null, DateTimeOffset.UtcNow);
        Expect(AgentPhases.Combine(ActivityState.NeedsYou, finished, quotaExhausted: false) == AgentPhase.Finished, "a clean finish is your turn");
    }

    private static void TestCombineTransientRetry()
    {
        var retry = new PhaseReading(AgentPhase.NetworkTrouble, "attempt 1 of 10", DateTimeOffset.UtcNow);
        Expect(AgentPhases.Combine(ActivityState.Working, retry, quotaExhausted: false) == AgentPhase.Working,
            "a lone retry the scan does not count stays plain working");
    }

    private static ScannedSession Session(TriggerTool tool, string id, ActivityState status) =>
        new(tool, id, "", id, DateTimeOffset.UtcNow, status, id + ".jsonl", null, SessionLaunchTarget.Cli);

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
