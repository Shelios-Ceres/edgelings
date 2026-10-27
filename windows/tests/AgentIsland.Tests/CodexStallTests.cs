using System.Globalization;
using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Codex reads as stalled only after five quiet minutes, never because a
/// turn is long. Codex Desktop keeps the rollout open, and Windows leaves the
/// file's write time at creation until the handle closes, so these fixtures
/// pin the mtime to the turn start the way a live rollout looks.
public static class CodexStallTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("codex long turn that keeps writing is working", TestLongTurnStillWritingIsWorking),
            ("codex turn start past the tail is still working", TestTurnStartOutsideTailIsWorking),
            ("codex turn quiet for six minutes is still working", TestQuietTurnIsWorking),
            ("codex turn silent for half an hour is idle", TestAbandonedTurnIsIdle),
            ("codex failed turn is an error, not your turn", TestFailedTurnIsError),
            ("codex usage limit is out of quota", TestUsageLimitIsRateLimited),
            ("codex interrupted turn is idle, not stalled", TestInterruptedTurnIsIdle),
            ("codex new turn after an interrupt is working", TestTurnAfterInterruptIsWorking),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("CodexStallTests GREEN");
    }

    private static readonly DateTimeOffset Start = Date("2026-10-12T14:00:00.000Z");

    private static void TestLongTurnStillWritingIsWorking()
    {
        var now = Start.AddMinutes(9).AddSeconds(10);
        var state = Scan(new[]
        {
            Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            Line(Start, "response_item", """{"type":"message","role":"user","content":[{"type":"input_text","text":"build it"}]}"""),
            Line(Start.AddMinutes(4), "response_item", """{"type":"reasoning","summary":[]}"""),
            Line(Start.AddMinutes(9), "response_item", """{"type":"custom_tool_call","name":"exec","call_id":"c1","input":"cargo build"}"""),
        }, now, lastWorking: now.AddSeconds(-30));
        Expect(state.Status == ActivityState.Working, $"a nine-minute turn that wrote 10s ago must be working, got {state.Status}");
        Expect(state.Modified == Start.AddMinutes(9), "an open turn is dated by its newest line");
    }

    private static void TestTurnStartOutsideTailIsWorking()
    {
        // Real reasoning lines carry kilobytes of encrypted content, so a long
        // turn's start soon falls out of the tail the scanner reads.
        var now = Start.AddMinutes(40);
        var lines = Enumerable.Range(0, 30)
            .Select(i => Line(now.AddSeconds(-300 + i * 10), "response_item", """{"type":"reasoning","summary":[]}"""))
            .Prepend(Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""))
            .ToArray();
        var state = Scan(lines, now, lastWorking: now.AddSeconds(-20), tailBytes: 2_048);
        Expect(state.Status == ActivityState.Working, $"a turn writing every 10s must be working however old the file is, got {state.Status}");
    }

    private static void TestQuietTurnIsWorking()
    {
        var now = Start.AddMinutes(7);
        var state = Scan(new[]
        {
            Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            Line(Start.AddMinutes(1), "response_item", """{"type":"function_call","name":"shell","call_id":"c1","arguments":"{}"}"""),
        }, now, lastWorking: Start.AddMinutes(1));
        Expect(state.Status == ActivityState.Working, $"six quiet minutes inside a turn are not trouble, got {state.Status}");
    }

    private static void TestAbandonedTurnIsIdle()
    {
        var now = Start.AddMinutes(32);
        var state = Scan(new[]
        {
            Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            Line(Start.AddMinutes(1), "response_item", """{"type":"function_call","name":"shell","call_id":"c1","arguments":"{}"}"""),
        }, now, lastWorking: Start.AddMinutes(1));
        Expect(state.Status == ActivityState.Idle, $"a turn silent for half an hour was abandoned, got {state.Status}");
    }

    private static void TestFailedTurnIsError()
    {
        var failed = Start.AddMinutes(1);
        var lines = new[]
        {
            Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            Line(failed, "event_msg", """{"type":"task_complete","turn_id":"t1","error":{"message":"{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\"}}"}}"""),
        };
        var now = failed.AddMinutes(1);
        var state = Scan(lines, now, lastWorking: failed);
        Expect(state.Status == ActivityState.NeedsYou, $"a failed turn is over and back to the user, got {state.Status}");
        var phase = AgentPhases.Combine(state.Status, AgentPhaseClassifier.Codex(lines, now), quotaExhausted: false);
        Expect(phase == AgentPhase.Failed, $"a failed turn shows as an error, not your turn, got {phase}");
    }

    private static void TestUsageLimitIsRateLimited()
    {
        var failed = Start.AddMinutes(1);
        var state = Scan(new[]
        {
            Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            Line(failed, "event_msg", """{"type":"task_complete","turn_id":"t1","error":"usage_limit_reached: try again later"}"""),
        }, failed.AddMinutes(1), lastWorking: failed);
        Expect(state.Status == ActivityState.RateLimited, $"a usage-limit failure is out of quota, got {state.Status}");
    }

    private static void TestInterruptedTurnIsIdle()
    {
        var aborted = Start.AddMinutes(2);
        var now = aborted.AddMinutes(6);
        var state = Scan(new[]
        {
            Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            Line(Start.AddMinutes(1), "response_item", """{"type":"function_call","name":"shell","call_id":"c1","arguments":"{}"}"""),
            Line(aborted, "response_item", """{"type":"message","role":"developer","content":[{"type":"input_text","text":"<turn_aborted>\nThe previous turn was interrupted on purpose."}]}"""),
            Line(aborted, "event_msg", """{"type":"turn_aborted","turn_id":"t1","reason":"interrupted"}"""),
            Line(aborted.AddSeconds(1), "event_msg", """{"type":"item_completed"}"""),
        }, now, lastWorking: aborted);
        Expect(state.Status == ActivityState.Idle, $"a turn the user stopped is over, not stalled, got {state.Status}");
    }

    private static void TestTurnAfterInterruptIsWorking()
    {
        var aborted = Start.AddMinutes(2);
        var now = aborted.AddSeconds(30);
        var state = Scan(new[]
        {
            Line(Start, "event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            Line(aborted, "event_msg", """{"type":"turn_aborted","turn_id":"t1","reason":"interrupted"}"""),
            Line(aborted.AddSeconds(15), "event_msg", """{"type":"task_started","turn_id":"t2"}"""),
            Line(aborted.AddSeconds(20), "response_item", """{"type":"message","role":"user","content":[{"type":"input_text","text":"try again"}]}"""),
        }, now, lastWorking: aborted);
        Expect(state.Status == ActivityState.Working, $"a turn started after an interrupt must be working, got {state.Status}");
    }

    private static (ActivityState Status, DateTimeOffset Modified) Scan(
        string[] lines, DateTimeOffset now, DateTimeOffset lastWorking, long tailBytes = 131_072)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-codex-stall-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        // A live rollout's write time stays at creation on Windows.
        File.SetLastWriteTimeUtc(path, Start.UtcDateTime);
        try
        {
            var seen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { [path] = lastWorking };
            var (status, _, modified, _) = SessionScanner.SessionState(path, now, seen, null, SessionTurnState.Codex,
                tail: p => SessionScanner.TailLines(p, bytes: tailBytes));
            return (status, modified);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Line(DateTimeOffset at, string type, string payload) =>
        $$"""{"timestamp":"{{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}","type":"{{type}}","payload":{{payload}}}""";

    private static DateTimeOffset Date(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
