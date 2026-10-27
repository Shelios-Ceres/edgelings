using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// What the monitor does when a scan throws, and CoreLog's write-once rule.
public static class ActivityMonitorScanTests
{
    public static void RunAll()
    {
        var logged = new List<(string Context, string Message)>();
        var previous = CoreLog.Sink;
        try
        {
            CoreLog.Sink = (error, context) => logged.Add((context, error.Message));

            CoreLog.Reset();
            CoreLog.Error(new InvalidOperationException("one"), "scan");
            CoreLog.Error(new InvalidOperationException("one"), "scan");
            CoreLog.Error(new InvalidOperationException("two"), "scan");
            CoreLog.Error(new InvalidOperationException("one"), "other");
            CoreLog.Error(new ArgumentException("one"), "scan");
            Expect(logged.Count == 4, $"each (context, type, message) is written once, got {logged.Count}");
            Console.WriteLine("PASS core log: a failure that repeats is written once");

            logged.Clear();
            CoreLog.Reset();
            CoreLog.Error(new IOException("cannot write 'settings.json.tmp-0123456789abcdef0123456789abcdef'"), "save");
            CoreLog.Error(new IOException("cannot write 'settings.json.tmp-fedcba9876543210fedcba9876543210'"), "save");
            Expect(logged.Count == 1, $"messages that differ only by a temp file's id read as one failure, got {logged.Count}");
            Console.WriteLine("PASS core log: a per-write temp name does not make a failure new");

            CoreLog.Sink = (_, _) => throw new IOException("the log itself failed");
            CoreLog.Error(new InvalidOperationException("three"), "scan");
            CoreLog.Sink = null;
            CoreLog.Error(new InvalidOperationException("four"), "scan");
            Console.WriteLine("PASS core log: a failing or missing sink never throws");

            CoreLog.Sink = (error, context) => logged.Add((context, error.Message));
            CoreLog.Reset();
            logged.Clear();
            KeepsStatesWhenAScanFails(logged);
            Console.WriteLine("PASS activity monitor: a scan that throws keeps the states and is logged once");
            WaitingListChangesAreAnnounced();
            Console.WriteLine("PASS activity monitor: a change to the waiting list is announced even when the state stays");
            WatchesRootsThatAppearLater();
            Console.WriteLine("PASS event stream: a folder that appears after start is watched from the next poll");
            Console.WriteLine("ActivityMonitorScanTests GREEN");
        }
        finally
        {
            CoreLog.Sink = previous;
            CoreLog.Reset();
        }
    }

    private static void KeepsStatesWhenAScanFails(List<(string Context, string Message)> logged)
    {
        // Grok has no usage overlay, and a finished turn is not abandoned by
        // a missing process or a missing network, so its state is the scan's.
        var waiting = new ScannedSession(TriggerTool.Grok, "s1", "", "A session", DateTimeOffset.UtcNow,
            ActivityState.NeedsYou, null, "turn-1", SessionLaunchTarget.Cli);
        var sessions = new List<ScannedSession> { waiting };
        var failure = (Exception?)null;
        var monitor = new ActivityMonitor
        {
            Scanner = (_, _) => failure is null ? sessions : throw failure,
        };

        Scan(monitor);
        Expect(monitor.StateFor(TriggerTool.Grok) == ActivityState.NeedsYou, "the scan's state is published");
        Expect(logged.Count == 0, "a good scan logs nothing");

        failure = new InvalidOperationException("boom");
        Scan(monitor);
        Expect(monitor.StateFor(TriggerTool.Grok) == ActivityState.NeedsYou, "a failed scan leaves the state as it was, not idle");
        Expect(logged.Count == 1 && logged[0].Context == "Activity scan" && logged[0].Message == "boom",
            "the failure is logged with its context");

        Scan(monitor);
        Expect(logged.Count == 1, "the same failure on the next scan is not written again");

        failure = null;
        sessions.Clear();
        Scan(monitor);
        Expect(monitor.StateFor(TriggerTool.Grok) == ActivityState.Idle, "a scan that really finds nothing still clears the state");

        failure = new InvalidOperationException("another");
        Scan(monitor);
        Expect(logged.Count == 2, "a different failure is written");
    }

    /// A second session starts waiting, or one of two stops, while the agent's
    /// state stays NeedsYou: an open card must still hear of it.
    private static void WaitingListChangesAreAnnounced()
    {
        static ScannedSession Waiting(string id) => new(TriggerTool.Grok, id, "", "Session " + id, DateTimeOffset.UtcNow,
            ActivityState.NeedsYou, null, "turn-" + id, SessionLaunchTarget.Cli);
        var sessions = new List<ScannedSession> { Waiting("s1") };
        var monitor = new ActivityMonitor { Scanner = (_, _) => sessions };
        var raised = 0;
        monitor.WaitingChanged += () => raised++;

        Scan(monitor);
        Expect(raised, 1);

        sessions.Add(Waiting("s2"));
        Scan(monitor);
        Expect(monitor.Waiting.Count, 2);
        Expect(monitor.StateFor(TriggerTool.Grok) == ActivityState.NeedsYou, "the combined state did not move");
        Expect(raised, 2);

        Scan(monitor);
        Expect(raised, 2);

        sessions.RemoveAt(1);
        Scan(monitor);
        Expect(monitor.Waiting.Count, 1);
        Expect(raised, 3);

        var asked = new SessionEvent(TriggerTool.Grok, "s1", "Session s1", SessionEventKind.Question, "k", DateTimeOffset.UtcNow);
        var done = asked with { Kind = SessionEventKind.Done };
        Expect(!ActivityMonitor.SameWaiting(new[] { asked }, new[] { done }), "a session that changed kind is a change");
    }

    /// The Claude hook's signal folder is made by the hook on its first
    /// prompt, usually after the app started.
    private static void WatchesRootsThatAppearLater()
    {
        var home = Path.Combine(Path.GetTempPath(), $"agentisland-watch-{Guid.NewGuid():N}");
        var present = Path.Combine(home, "projects");
        var later = Path.Combine(home, "signals");
        Directory.CreateDirectory(present);
        // Not disposed: a watcher thread may still release it after the test.
        var changed = new SemaphoreSlim(0);
        var stream = new TranscriptEventStream(() => changed.Release(), () => new[] { present, later });
        try
        {
            stream.Start();
            File.WriteAllText(Path.Combine(present, "a.jsonl"), "{}\n");
            Expect(changed.Wait(TimeSpan.FromSeconds(5)), "a root there at start is watched");
            Directory.CreateDirectory(later);
            stream.WatchNewRoots();
            while (changed.Wait(TimeSpan.FromMilliseconds(200)))
            {
            }
            File.WriteAllText(Path.Combine(later, "s1.jsonl"), "{}\n");
            Expect(changed.Wait(TimeSpan.FromSeconds(5)), "a root made later is watched once the poll looks again");
        }
        finally
        {
            stream.Dispose();
            Directory.Delete(home, recursive: true);
        }
    }

    private static void Scan(ActivityMonitor monitor)
    {
        monitor.Tick();
        monitor.LastScan!.GetAwaiter().GetResult();
    }
}
