using AgentIsland.Core;

namespace AgentIsland.Tests;

/// The hover card's task count: threads mid-turn, each counted once.
public static class RunningTasksTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("running counts working and stalled threads only", TestCountsWorkingAndStalled),
            ("a thread split over two rollouts counts once", TestSplitThreadCountsOnce),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("RunningTasksTests GREEN");
    }

    private static void TestCountsWorkingAndStalled()
    {
        var sessions = new[]
        {
            Session(TriggerTool.Codex, "a", ActivityState.Working),
            Session(TriggerTool.Codex, "b", ActivityState.Stalled),
            Session(TriggerTool.Codex, "c", ActivityState.NeedsYou),
            Session(TriggerTool.Codex, "d", ActivityState.Idle),
            Session(TriggerTool.Claude, "e", ActivityState.Working),
        };
        var codex = ActivityMonitor.RunningCount(sessions, TriggerTool.Codex);
        Expect(codex == 2, $"a working and a stalled thread are running, a finished and an idle one are not; got {codex}");
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

    private static ScannedSession Session(TriggerTool tool, string id, ActivityState status, string? path = null) =>
        new(tool, id, "", id, DateTimeOffset.UtcNow, status, path ?? id + ".jsonl", null, SessionLaunchTarget.Cli);

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
