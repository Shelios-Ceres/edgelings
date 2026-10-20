using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The hover card's task count: threads mid-turn, each counted once.
public static class RunningTasksTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("running counts working threads and ones held up by the network", TestCountsWorkingAndNetworkTrouble),
            ("a thread split over two rollouts counts once", TestSplitThreadCountsOnce),
            ("a continued rollout's completion supersedes old work", TestLatestCompletionWins),
            ("a continued rollout's interruption supersedes old work", TestLatestInterruptionWins),
            ("a new turn supersedes an older completion", TestLatestWorkWins),
            ("equal timestamps choose a close consistently", TestEqualTimestamps),
            ("missing session ids fall back to paths or separate entries", TestMissingIds),
            ("session ids are scoped to their provider", TestProviderScope),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("RunningTasksTests GREEN");
    }

    private static void TestCountsWorkingAndNetworkTrouble()
    {
        var sessions = new[]
        {
            Session(TriggerTool.Codex, "a", ActivityState.Working),
            Session(TriggerTool.Codex, "b", ActivityState.NetworkTrouble),
            Session(TriggerTool.Codex, "c", ActivityState.NeedsYou),
            Session(TriggerTool.Codex, "d", ActivityState.Idle),
            Session(TriggerTool.Claude, "e", ActivityState.Working),
        };
        var codex = ActivityMonitor.RunningCount(sessions, TriggerTool.Codex);
        Expect(codex == 2, $"a working thread and one held up by the network are running, a finished and an idle one are not; got {codex}");
        Expect(ActivityMonitor.RunningCount(sessions, TriggerTool.Grok) == 0, "a provider with no sessions runs nothing");
    }

    private static void TestSplitThreadCountsOnce()
    {
        var sessions = new[]
        {
            Session(TriggerTool.Codex, "01a064f3", ActivityState.Working, "rollout-a.jsonl"),
            Session(TriggerTool.Codex, "01a064f3", ActivityState.Working, "rollout-a_continued.jsonl"),
        };
        var count = ActivityMonitor.RunningCount(sessions, TriggerTool.Codex);
        Expect(count == 1, $"two rollout files of one thread are one task, got {count}");
    }

    private static void TestLatestCompletionWins()
    {
        var sessions = SplitThread(ActivityState.NeedsYou);
        Expect(ActivityMonitor.RunningCount(sessions, TriggerTool.Codex) == 0, "a completed thread is no longer running");
        var selected = ActivityMonitor.BestSession(sessions, TriggerTool.Codex);
        Expect(selected.State == ActivityState.NeedsYou && selected.Thread?.TranscriptPath == "continued.jsonl",
            "the continued rollout supplies the completed state and transcript");
    }

    private static void TestLatestInterruptionWins()
    {
        var sessions = SplitThread(ActivityState.Idle);
        Expect(ActivityMonitor.RunningCount(sessions, TriggerTool.Codex) == 0, "an interrupted thread is no longer running");
        var selected = ActivityMonitor.BestSession(sessions, TriggerTool.Codex);
        Expect(selected.State == ActivityState.Idle && selected.Thread is null, "old work cannot revive an interrupted thread");
    }

    private static void TestLatestWorkWins()
    {
        var sessions = SplitThread(ActivityState.Working);
        sessions[0] = sessions[0] with { Status = ActivityState.NeedsYou };
        Expect(ActivityMonitor.RunningCount(sessions, TriggerTool.Codex) == 1, "a new turn runs once");
        Expect(ActivityMonitor.BestSession(sessions, TriggerTool.Codex).Thread?.TranscriptPath == "continued.jsonl",
            "the new turn wins over an old completion");
    }

    private static void TestEqualTimestamps()
    {
        var sessions = SplitThread(ActivityState.NeedsYou);
        sessions[0] = sessions[0] with { Modified = sessions[1].Modified };
        foreach (var order in new[] { sessions, sessions.AsEnumerable().Reverse().ToList() })
        {
            Expect(ActivityMonitor.RunningCount(order, TriggerTool.Codex) == 0, "a timestamp tie prefers the close");
            Expect(ActivityMonitor.BestSession(order, TriggerTool.Codex).State == ActivityState.NeedsYou,
                "directory enumeration order cannot change the selected state");
        }
    }

    private static void TestMissingIds()
    {
        var sessions = new[]
        {
            Session(TriggerTool.Codex, "", ActivityState.Working, "a.jsonl"),
            Session(TriggerTool.Codex, "", ActivityState.Working, "A.jsonl"),
            Session(TriggerTool.Codex, "", ActivityState.Working, "b.jsonl"),
            Session(TriggerTool.Codex, "", ActivityState.Working) with { TranscriptPath = null },
            Session(TriggerTool.Codex, "", ActivityState.Working) with { TranscriptPath = null },
        };
        Expect(ActivityMonitor.RunningCount(sessions, TriggerTool.Codex) == 4,
            "unknown ids share a session only when their Windows transcript paths match");
    }

    private static void TestProviderScope()
    {
        var sessions = new[]
        {
            Session(TriggerTool.Codex, "shared-id", ActivityState.Working),
            Session(TriggerTool.Claude, "shared-id", ActivityState.NeedsYou),
        };
        Expect(ActivityMonitor.RunningCount(sessions, TriggerTool.Codex) == 1, "other providers cannot supersede a Codex thread");
    }

    private static List<ScannedSession> SplitThread(ActivityState latest)
    {
        var now = DateTimeOffset.UtcNow;
        return new()
        {
            Session(TriggerTool.Codex, "thread", ActivityState.Working, "old.jsonl") with { Modified = now.AddMinutes(-2) },
            Session(TriggerTool.Codex, "thread", latest, "continued.jsonl") with { Modified = now.AddSeconds(-10) },
        };
    }

    private static ScannedSession Session(TriggerTool tool, string id, ActivityState status, string? path = null) =>
        new(tool, id, "", id, DateTimeOffset.UtcNow, status, path ?? id + ".jsonl", null, SessionLaunchTarget.Cli);
}
