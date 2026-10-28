using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Model;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Per-session events: what a bot says when one session of its agent turns a
/// corner, even while the agent's overall phase stays Working. Each event
/// carries a key, so a later scan never repeats it.
public static class SessionEventTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("the first scan learns and never speaks", TestFirstScanIsQuiet),
            ("a session that finishes speaks once while another works", TestDoneWhileSiblingWorks),
            ("a finished turn is not repeated by later scans", TestDoneNotRepeated),
            ("a new turn of the same session is a new event", TestNewTurnIsNewEvent),
            ("a turn that ended long ago is not news", TestOldDoneIsNotNews),
            ("a turn that ended on an error is an error", TestErrorKind),
            ("a turn that ended on a lost connection, a limit or a sign-in is an error", TestEndedOnTroubleIsError),
            ("a question stays waiting while the connection drops", TestQuestionSurvivesNetworkTrouble),
            ("a hook prompt is an approval, once", TestApprovalFromHook),
            ("a question in the transcript speaks while the session works", TestQuestionFromTranscript),
            ("an answered prompt leaves the waiting list", TestAnsweredPromptLeaves),
            ("two sessions finishing together give two events", TestTwoSessions),
            ("a skipped scheduled task speaks once", TestScheduledSkip),
            ("recorded skips are parsed from the store", TestSkipParse),
            ("the queue says one event at a time", TestQueueOneAtATime),
            ("the queue lets approvals and questions go first", TestQueueUrgentFirst),
            ("three waiting events are said as one summary", TestQueueMerges),
            ("the queue leaves half a second between bubbles", TestQueueGap),
            ("the queue drops what a session has moved on from", TestQueueReconcile),
            ("a bubble is taken back when its session moves on", TestQueueTakesBack),
            ("a phase line stays quiet while a session's error speaks", TestQueueErrorSilencesPhaseLine),
            ("a session's error after a phase line takes its bubble", TestQueueErrorAfterPhaseLine),
            ("the card's list is ordered, capped and counts the rest", TestWaitingList),
            ("overall-phase lines no longer cover per-session events", TestPhaseLinesStepAside),
            ("classic merges from two status events", TestClassicMergesFromTwo),
            ("classic keeps only a session's newest status event", TestClassicNewerReplacesOlder),
            ("classic replaces a bubble once it has shown long enough", TestClassicReplacesAfterMinimum),
            ("every agent's finished turn, error and question speak per session", TestOtherAgents),
            ("kimi code's desktop approval is an approval event", TestKimiApproval),
            ("cursor's events speak through grok's character", TestCursorRidesOnGrok),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SessionEventTests GREEN");
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-19T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Rig
    {
        public readonly Dictionary<string, PhaseReading?> Phases = new(StringComparer.Ordinal);
        public readonly List<SkippedTask> Skips = new();
        public readonly SessionEventDetector Detector;

        public Rig()
        {
            Detector = new SessionEventDetector(
                (_, path, _) => Phases.TryGetValue(path, out var reading) ? reading : null,
                () => Skips);
        }
    }

    private static ScannedSession Session(
        string id, ActivityState status, DateTimeOffset modified, string? turn = "t1",
        PhaseReading? prompt = null, string? title = null) =>
        new(TriggerTool.Claude, id, @"D:\x", title ?? "Session " + id, modified, status, "/t/" + id + ".jsonl", turn,
            SessionLaunchTarget.ClaudeDesktop)
        { PendingPrompt = prompt };

    private static void TestFirstScanIsQuiet()
    {
        var rig = new Rig();
        var seen = rig.Detector.Observe(new[] { Session("a", ActivityState.NeedsYou, T0) }, T0.AddSeconds(5));
        Expect(seen.Fresh.Count == 0, "whatever waits at launch must not speak");
        Expect(seen.Waiting.Count == 1 && seen.Waiting[0].Kind == SessionEventKind.Done, "it still goes on the waiting list");
    }

    private static void TestDoneWhileSiblingWorks()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0), Session("b", ActivityState.Working, T0) }, T0);
        var seen = rig.Detector.Observe(
            new[] { Session("a", ActivityState.NeedsYou, T0.AddSeconds(30)), Session("b", ActivityState.Working, T0.AddSeconds(30)) },
            T0.AddSeconds(32));
        Expect(seen.Fresh.Count == 1, $"the finished session speaks once, got {seen.Fresh.Count}");
        var item = seen.Fresh[0];
        Expect(item.Kind == SessionEventKind.Done && item.SessionId == "a" && item.Title == "Session a", "the event names its session");
        Expect(item.Key == "done:claude:a:t1", $"the key is session, turn and kind, got {item.Key}");
    }

    private static void TestDoneNotRepeated()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        var done = new[] { Session("a", ActivityState.NeedsYou, T0.AddSeconds(30)) };
        Expect(rig.Detector.Observe(done, T0.AddSeconds(32)).Fresh.Count == 1, "first sight speaks");
        Expect(rig.Detector.Observe(done, T0.AddSeconds(38)).Fresh.Count == 0, "the next scan stays quiet");
        // The user glances at it (desktop activity), it reads Working, then Done again with the same turn.
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0.AddSeconds(40)) }, T0.AddSeconds(40));
        Expect(rig.Detector.Observe(done, T0.AddSeconds(44)).Fresh.Count == 0, "the same turn never speaks twice");
    }

    private static void TestNewTurnIsNewEvent()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        rig.Detector.Observe(new[] { Session("a", ActivityState.NeedsYou, T0.AddSeconds(30), "t1") }, T0.AddSeconds(31));
        var next = rig.Detector.Observe(new[] { Session("a", ActivityState.NeedsYou, T0.AddMinutes(5), "t2") }, T0.AddMinutes(5).AddSeconds(2));
        Expect(next.Fresh.Count == 1 && next.Fresh[0].Key == "done:claude:a:t2", "a later turn is its own event");
    }

    private static void TestOldDoneIsNotNews()
    {
        var rig = new Rig();
        rig.Detector.Observe(Array.Empty<ScannedSession>(), T0);
        var seen = rig.Detector.Observe(new[] { Session("a", ActivityState.NeedsYou, T0.AddMinutes(-9)) }, T0.AddSeconds(6));
        Expect(seen.Fresh.Count == 0, "a turn that ended nine minutes ago is not news");
        Expect(seen.Waiting.Count == 1, "but it still waits");
    }

    private static void TestErrorKind()
    {
        var rig = new Rig();
        rig.Phases["/t/a.jsonl"] = new PhaseReading(AgentPhase.Failed, "API", T0);
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        var seen = rig.Detector.Observe(new[] { Session("a", ActivityState.NeedsYou, T0.AddSeconds(10)) }, T0.AddSeconds(12));
        Expect(seen.Fresh.Count == 1 && seen.Fresh[0].Kind == SessionEventKind.Error && seen.Fresh[0].Key.StartsWith("error:"), "an error is not 'done'");
    }

    private static void TestEndedOnTroubleIsError()
    {
        foreach (var trouble in new[] { ActivityState.NetworkTrouble, ActivityState.RateLimited, ActivityState.AuthRequired })
        {
            var rig = new Rig();
            rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
            var ended = new[] { Session("a", trouble, T0.AddSeconds(10)) with { TurnEnded = true } };
            var seen = rig.Detector.Observe(ended, T0.AddSeconds(12));
            Expect(seen.Fresh.Count == 1 && seen.Fresh[0].Kind == SessionEventKind.Error && seen.Fresh[0].Key == "error:claude:a:t1",
                $"{trouble}: the turn's end speaks once, as an error");
            Expect(seen.Waiting.Count == 1, $"{trouble}: and it waits on the user");
            Expect(rig.Detector.Observe(ended, T0.AddSeconds(18)).Fresh.Count == 0, $"{trouble}: once");
        }
        var retrying = new Rig();
        retrying.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        var still = retrying.Detector.Observe(new[] { Session("a", ActivityState.NetworkTrouble, T0.AddSeconds(10)) }, T0.AddSeconds(12));
        Expect(still.Fresh.Count == 0 && still.Waiting.Count == 0, "a turn still retrying waits on nobody");
    }

    private static void TestQuestionSurvivesNetworkTrouble()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        rig.Phases["/t/a.jsonl"] = new PhaseReading(AgentPhase.Asking, "AskUserQuestion", T0.AddSeconds(20));
        var asked = rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0.AddSeconds(20)) }, T0.AddSeconds(22));
        Expect(asked.Fresh.Count == 1 && asked.Fresh[0].Kind == SessionEventKind.Question, "the question speaks");
        var offline = rig.Detector.Observe(new[] { Session("a", ActivityState.NetworkTrouble, T0.AddSeconds(20)) }, T0.AddSeconds(28));
        Expect(offline.Waiting.Count == 1 && offline.Waiting[0].Key == asked.Fresh[0].Key, "it stays on the list while offline");
        Expect(offline.Fresh.Count == 0, "without speaking again");
    }

    private static void TestApprovalFromHook()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        var prompt = new PhaseReading(AgentPhase.Approving, null, T0.AddSeconds(20));
        var withPrompt = new[] { Session("a", ActivityState.Working, T0.AddSeconds(20), prompt: prompt) };
        var seen = rig.Detector.Observe(withPrompt, T0.AddSeconds(22));
        Expect(seen.Fresh.Count == 1 && seen.Fresh[0].Kind == SessionEventKind.Approval, "a permission prompt is an approval");
        Expect(rig.Detector.Observe(withPrompt, T0.AddSeconds(28)).Fresh.Count == 0, "and it speaks once");
    }

    private static void TestQuestionFromTranscript()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        rig.Phases["/t/a.jsonl"] = new PhaseReading(AgentPhase.Asking, "AskUserQuestion", T0.AddSeconds(20));
        var seen = rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0.AddSeconds(20)) }, T0.AddSeconds(22));
        Expect(seen.Fresh.Count == 1 && seen.Fresh[0].Kind == SessionEventKind.Question, "AskUserQuestion is a question");
    }

    private static void TestAnsweredPromptLeaves()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0) }, T0);
        var prompt = new PhaseReading(AgentPhase.Approving, null, T0.AddSeconds(20));
        var open = rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0.AddSeconds(20), prompt: prompt) }, T0.AddSeconds(22));
        Expect(open.Waiting.Count == 1, "the prompt is on the list");
        var answered = rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0.AddSeconds(30)) }, T0.AddSeconds(32));
        Expect(answered.Waiting.Count == 0, "answering it takes it off the list");
    }

    private static void TestTwoSessions()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Session("a", ActivityState.Working, T0), Session("b", ActivityState.Working, T0) }, T0);
        var seen = rig.Detector.Observe(
            new[] { Session("a", ActivityState.NeedsYou, T0.AddSeconds(10)), Session("b", ActivityState.NeedsYou, T0.AddSeconds(12)) },
            T0.AddSeconds(14));
        Expect(seen.Fresh.Count == 2 && seen.Fresh[0].SessionId == "a" && seen.Fresh[1].SessionId == "b", "both speak, oldest first");
    }

    private static SessionEvent Item(string id, SessionEventKind kind, int seconds = 0) =>
        new(TriggerTool.Claude, id, "Session " + id, kind, kind.ToString().ToLowerInvariant() + ":" + id, T0.AddSeconds(seconds));

    private static HashSet<string> Keys(params SessionEvent[] items) =>
        items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

    private static void TestQueueOneAtATime()
    {
        var queue = new SpeechQueue();
        queue.Add(Item("a", SessionEventKind.Done));
        queue.Add(Item("b", SessionEventKind.Done));
        var first = queue.Next(0);
        Expect(first is { IsSummary: false } && first.First.SessionId == "a", "first come, first said");
        Expect(queue.Next(1) is null, "nothing else while one shows");
        queue.Dismissed(5);
        Expect(queue.Next(5.6)?.First.SessionId == "b", "the next follows");
        Expect(!queue.Add(Item("b", SessionEventKind.Done)), "an event already said is not queued again");
    }

    private static void TestQueueUrgentFirst()
    {
        var queue = new SpeechQueue();
        queue.Add(Item("a", SessionEventKind.Done));
        queue.Add(Item("b", SessionEventKind.Approval));
        Expect(queue.Next(0)?.First.SessionId == "b", "an approval goes ahead of a done line");
    }

    private static void TestQueueMerges()
    {
        var queue = new SpeechQueue();
        queue.Add(Item("a", SessionEventKind.Done));
        queue.Add(Item("b", SessionEventKind.Question));
        Expect(queue.Next(0) is { IsSummary: false }, "two are said one by one");
        queue.Dismissed(1);
        queue.Add(Item("c", SessionEventKind.Done));
        queue.Add(Item("d", SessionEventKind.Done));
        queue.Add(Item("e", SessionEventKind.Done));
        var summary = queue.Next(2);
        Expect(summary is { IsSummary: true } && summary.Events.Count == 4, "three or more waiting are one summary");
        Expect(queue.Count == 0, "the summary took them all");
    }

    private static void TestQueueGap()
    {
        var queue = new SpeechQueue();
        queue.Add(Item("a", SessionEventKind.Done));
        queue.Next(0);
        queue.Dismissed(10);
        queue.Add(Item("b", SessionEventKind.Done));
        Expect(queue.Next(10.2) is null, "half a second with no bubble first");
        Expect(queue.Next(10.5) is not null, "then the next");
    }

    private static void TestQueueReconcile()
    {
        var queue = new SpeechQueue();
        var a = Item("a", SessionEventKind.Done);
        var b = Item("b", SessionEventKind.Done);
        queue.Add(a);
        queue.Add(b);
        queue.Next(0);
        queue.Dismissed(1);
        queue.Reconcile(Keys(b));
        Expect(queue.Count == 1, "b still waits, so it stays");
        queue.Reconcile(Keys());
        Expect(queue.Count == 0, "a session that moved on is dropped from the queue");
    }

    private static void TestQueueTakesBack()
    {
        var queue = new SpeechQueue();
        var approval = Item("a", SessionEventKind.Approval);
        queue.Add(approval);
        queue.Next(0);
        Expect(!queue.Reconcile(Keys(approval)), "still waiting: the bubble stays");
        Expect(queue.Reconcile(Keys()), "answered: the bubble is taken back");
        var skipped = Item("t", SessionEventKind.TaskSkipped);
        queue.Dismissed(3);
        queue.Add(skipped);
        queue.Next(4);
        Expect(!queue.Reconcile(Keys()), "a skipped task has no session to move on");
    }

    // The drawer's whole-agent line for network trouble, a limit or a sign-in
    // asks Holds(Error) first: the per-session line is the one that speaks.
    private static void TestQueueErrorSilencesPhaseLine()
    {
        var queue = new SpeechQueue();
        Expect(!queue.Holds(SessionEventKind.Error), "nothing to say: the phase line speaks");
        queue.Add(Item("a", SessionEventKind.Done));
        queue.Next(0);
        queue.Add(Item("b", SessionEventKind.Error));
        Expect(queue.Holds(SessionEventKind.Error), "an error waiting its turn keeps the phase line quiet");
        queue.Dismissed(1);
        queue.Next(2);
        Expect(queue.Holds(SessionEventKind.Error), "an error on screen keeps the phase line quiet");
        queue.Dismissed(7);
        Expect(!queue.Holds(SessionEventKind.Error), "once it is gone, a later phase line speaks again");
        queue.Add(Item("c", SessionEventKind.Done));
        queue.Next(8);
        Expect(!queue.Holds(SessionEventKind.Error), "a done line leaves the phase line alone");
    }

    private static void TestQueueErrorAfterPhaseLine()
    {
        var queue = new SpeechQueue();
        // A phase line is on screen; the queue does not know about it.
        queue.Add(Item("a", SessionEventKind.Error));
        Expect(queue.Next(0)?.First.Kind == SessionEventKind.Error, "the error is said at once, in the phase line's bubble");
    }

    private static void TestWaitingList()
    {
        var events = new[]
        {
            Item("done1", SessionEventKind.Done, 10),
            Item("done2", SessionEventKind.Done, 20),
            Item("\u6D4B\u8BD5", SessionEventKind.Done, 5),
            Item("ask", SessionEventKind.Question, 1),
            Item("ok", SessionEventKind.Approval, 2),
            Item("skip", SessionEventKind.TaskSkipped, 30),
        };
        var list = StatusWaitingList.Of(events);
        Expect(list.Rows.Select(r => r.Title).SequenceEqual(new[] { "Session ok", "Session ask", "Session done2" }), "approval, question, then the newest done");
        Expect(list.More == 2, $"two more, got {list.More}");
        var chinese = StatusWaitingList.Of(new[] { Item("测试", SessionEventKind.Done) });
        Expect(chinese.Rows[0].Title == "A session", "a title the pixel face cannot set gives way to a stand-in");
        var classic = StatusWaitingList.ForClassic(events, T0.AddMinutes(1));
        Expect(classic.Rows.Count == 4 && classic.More == 2, $"classic lists four rows, got {classic.Rows.Count} and {classic.More} more");
        Expect(classic.Rows[2].Word == BotLines.Word(SessionEventKind.Error, classic: true) && classic.Rows[3].Word == BotLines.Word(SessionEventKind.Done, classic: true)
            && classic.Rows[0].Word == BotLines.Word(SessionEventKind.Approval, classic: true), "its own words, a task that did not run as an error");
    }

    private static void TestPhaseLinesStepAside()
    {
        foreach (var phase in new[] { AgentPhase.Finished, AgentPhase.Asking, AgentPhase.Approving, AgentPhase.Failed })
            Expect(!BotLines.Speaks(phase), $"{phase} is said per session now");
        foreach (var phase in new[] { AgentPhase.RateLimited, AgentPhase.AuthRequired, AgentPhase.NetworkTrouble })
            Expect(BotLines.Speaks(phase), $"{phase} stays a whole-agent line");
    }

    private static SpeechQueue ClassicQueue() => new() { Rules = SpeechRules.Classic };

    private static void TestClassicMergesFromTwo()
    {
        var queue = ClassicQueue();
        queue.Add(Item("a", SessionEventKind.Done));
        queue.Add(Item("b", SessionEventKind.Approval));
        var speech = queue.Next(0);
        Expect(speech is { IsSummary: true } && speech.Events.Count == 2, "two waiting together are one bubble");
        Expect(speech!.First.SessionId == "b", "the approval leads");
    }

    private static void TestClassicNewerReplacesOlder()
    {
        var queue = ClassicQueue();
        queue.Add(Item("a", SessionEventKind.Question, 1));
        queue.Add(Item("a", SessionEventKind.Approval, 2));
        Expect(queue.Count == 1, $"one event per session is queued, got {queue.Count}");
        Expect(queue.Next(0)?.First.Kind == SessionEventKind.Approval, "the newer one");
    }

    private static void TestClassicReplacesAfterMinimum()
    {
        var queue = ClassicQueue();
        queue.Add(Item("a", SessionEventKind.Done));
        queue.Next(0);
        queue.Add(Item("b", SessionEventKind.Done));
        Expect(queue.Next(2) is null, "not before 2.5 s");
        Expect(queue.Next(2.6)?.First.SessionId == "b", "after it the next replaces what shows");
        var pixel = new SpeechQueue();
        pixel.Add(Item("a", SessionEventKind.Done));
        pixel.Next(0);
        pixel.Add(Item("b", SessionEventKind.Done));
        Expect(pixel.Next(30) is null, "Pixel waits for the bubble to go");
    }

    private static ScannedSession Other(
        TriggerTool tool, string id, ActivityState status, DateTimeOffset modified, string? turn = "t1", PhaseReading? prompt = null) =>
        new(tool, id, @"D:\x", "Session " + id, modified, status, "/t/" + id + ".log", turn, SessionLaunchTarget.Cli)
        { PendingPrompt = prompt };

    private static void TestOtherAgents()
    {
        foreach (var tool in new[] { TriggerTool.Codex, TriggerTool.Grok, TriggerTool.Antigravity, TriggerTool.Kimi, TriggerTool.DeepSeek, TriggerTool.Cursor })
        {
            var rig = new Rig();
            rig.Detector.Observe(new[] { Other(tool, "a", ActivityState.Working, T0) }, T0);
            var done = rig.Detector.Observe(new[] { Other(tool, "a", ActivityState.NeedsYou, T0.AddSeconds(30)) }, T0.AddSeconds(32));
            Expect(done.Fresh.Count == 1 && done.Fresh[0].Kind == SessionEventKind.Done && done.Fresh[0].Tool == tool, $"{tool}: a finished turn speaks");

            var failing = new Rig();
            failing.Phases["/t/a.log"] = new PhaseReading(AgentPhase.Failed, null, T0);
            failing.Detector.Observe(new[] { Other(tool, "a", ActivityState.Working, T0) }, T0);
            var error = failing.Detector.Observe(new[] { Other(tool, "a", ActivityState.NeedsYou, T0.AddSeconds(30)) }, T0.AddSeconds(32));
            Expect(error.Fresh.Count == 1 && error.Fresh[0].Kind == SessionEventKind.Error, $"{tool}: an error speaks");

            var asking = new Rig();
            asking.Detector.Observe(new[] { Other(tool, "a", ActivityState.Working, T0) }, T0);
            asking.Phases["/t/a.log"] = new PhaseReading(AgentPhase.Asking, "ask_user_question", T0.AddSeconds(20));
            var question = asking.Detector.Observe(new[] { Other(tool, "a", ActivityState.Working, T0.AddSeconds(20)) }, T0.AddSeconds(22));
            Expect(question.Fresh.Count == 1 && question.Fresh[0].Kind == SessionEventKind.Question, $"{tool}: a question speaks");
        }
    }

    private static void TestKimiApproval()
    {
        var rig = new Rig();
        rig.Detector.Observe(new[] { Other(TriggerTool.Kimi, "a", ActivityState.Working, T0) }, T0);
        var prompt = new PhaseReading(AgentPhase.Approving, null, T0.AddSeconds(20));
        var seen = rig.Detector.Observe(new[] { Other(TriggerTool.Kimi, "a", ActivityState.Working, T0.AddSeconds(20), prompt: prompt) }, T0.AddSeconds(22));
        Expect(seen.Fresh.Count == 1 && seen.Fresh[0].Kind == SessionEventKind.Approval, "a desktop approval speaks");
    }

    private static void TestCursorRidesOnGrok()
    {
        Expect(TriggerTool.Cursor.ToDisplayProvider().Character() == DisplayProvider.Grok, "Cursor's events go to Grok's character");
        Expect(TriggerTool.Grok.ToDisplayProvider().Character() == DisplayProvider.Grok, "and Grok's own");
    }

    private static void TestScheduledSkip()
    {
        var rig = new Rig();
        rig.Skips.Add(new SkippedTask("task1", "Nightly check", "100", null));
        rig.Detector.Observe(Array.Empty<ScannedSession>(), T0);
        Expect(rig.Detector.Observe(Array.Empty<ScannedSession>(), T0.AddSeconds(6)).Fresh.Count == 0, "a skip recorded before launch is quiet");
        rig.Skips[0] = new SkippedTask("task1", "Nightly check", "200", null);
        var seen = rig.Detector.Observe(Array.Empty<ScannedSession>(), T0.AddSeconds(12));
        Expect(seen.Fresh.Count == 1 && seen.Fresh[0].Kind == SessionEventKind.TaskSkipped && seen.Fresh[0].Title == "Nightly check", "a new skip speaks, with the task's name");
        Expect(rig.Detector.Observe(Array.Empty<ScannedSession>(), T0.AddSeconds(18)).Fresh.Count == 0, "once");
    }

    private static void TestSkipParse()
    {
        using var doc = JsonDocument.Parse("""{"scheduledTasks":[{"id":"t1","name":"Nightly"}],"recordedSkips":{"t1":1760000000000}}""");
        var skips = ScheduledTaskSkips.Parse(doc.RootElement).ToList();
        Expect(skips.Count == 1 && skips[0].Name == "Nightly" && skips[0].At is not null, "a skip carries its task's name and time");
        using var empty = JsonDocument.Parse("""{"scheduledTasks":[],"recordedSkips":{}}""");
        Expect(!ScheduledTaskSkips.Parse(empty.RootElement).Any(), "no skips, no events");
    }
}
