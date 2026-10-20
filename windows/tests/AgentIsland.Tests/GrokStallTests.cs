using System.Globalization;
using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Grok reads as stalled only when silence means something. Its thoughts are
/// written once they finish, and after a turn it writes bookkeeping (memory
/// dreams, background-task notices) that must not reopen the turn.
public static class GrokStallTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("grok thinking for eight minutes is working", TestLongThinkingIsWorking),
            ("grok memory dream after a finished turn is your turn", TestMemoryDreamKeepsTurnDone),
            ("grok background notice long after a turn is idle", TestLateBackgroundNoticeIsIdle),
            ("grok tool silent for six minutes is still working", TestQuietToolIsWorking),
            ("grok model silent for sixteen minutes is still working", TestLongSilenceIsWorking),
            ("grok repeated retries are network trouble", TestRepeatedRetriesAreNetworkTrouble),
            ("grok a single retry is still working", TestSingleRetryIsWorking),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("GrokStallTests GREEN");
    }

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-12T16:56:00Z", CultureInfo.InvariantCulture);

    private static void TestLongThinkingIsWorking()
    {
        var returned = Start.AddSeconds(70);
        var status = Scan(new[]
        {
            Update(Start, "user_message_chunk"),
            Update(Start.AddSeconds(60), "tool_call", "c1", "in_progress"),
            Update(returned, "tool_call_update", "c1", "completed"),
        }, returned.AddMinutes(8), lastWorking: returned.AddMinutes(7));
        Expect(status == ActivityState.Working, $"eight minutes of thinking on a tool result must be working, got {status}");
    }

    private static void TestMemoryDreamKeepsTurnDone()
    {
        var done = Start.AddMinutes(1);
        var status = Scan(new[]
        {
            Update(Start, "user_message_chunk"),
            Update(done, "agent_message_chunk"),
            Update(done, "turn_completed"),
            Update(done, "memory_dream_queued"),
            Update(done, "memory_dream_started"),
        }, done.AddMinutes(2), lastWorking: done);
        Expect(status == ActivityState.NeedsYou, $"a finished turn followed by a memory dream is your turn, got {status}");
    }

    private static void TestLateBackgroundNoticeIsIdle()
    {
        var done = Start.AddMinutes(1);
        var notice = done.AddMinutes(70);
        var status = Scan(new[]
        {
            Update(Start, "user_message_chunk"),
            Update(done, "turn_completed"),
            Update(notice, "background_tasks"),
        }, notice.AddMinutes(1), lastWorking: done);
        Expect(status == ActivityState.Idle, $"a background notice an hour after the turn does not reopen it, got {status}");
    }

    private static void TestQuietToolIsWorking()
    {
        var called = Start.AddSeconds(10);
        var status = Scan(new[]
        {
            Update(Start, "user_message_chunk"),
            Update(called, "tool_call", "c1", "in_progress"),
        }, called.AddMinutes(6), lastWorking: called.AddMinutes(4));
        Expect(status == ActivityState.Working, $"a long command is not trouble, got {status}");
    }

    private static void TestLongSilenceIsWorking()
    {
        var status = Scan(new[] { Update(Start, "user_message_chunk") },
            Start.AddMinutes(16), lastWorking: Start.AddMinutes(15).AddSeconds(-10));
        Expect(status == ActivityState.Working, $"silence alone is never trouble, got {status}");
    }

    private static void TestRepeatedRetriesAreNetworkTrouble()
    {
        var status = Scan(new[]
        {
            Update(Start, "user_message_chunk"),
            Update(Start.AddSeconds(10), "retry_state"),
            Update(Start.AddSeconds(20), "retry_state"),
            Update(Start.AddSeconds(40), "retry_state"),
        }, Start.AddSeconds(50), lastWorking: Start.AddSeconds(5));
        Expect(status == ActivityState.NetworkTrouble, $"three retries in a row are network trouble, got {status}");
    }

    private static void TestSingleRetryIsWorking()
    {
        var status = Scan(new[]
        {
            Update(Start, "user_message_chunk"),
            Update(Start.AddSeconds(10), "retry_state"),
        }, Start.AddSeconds(20), lastWorking: Start.AddSeconds(5));
        Expect(status == ActivityState.Working, $"one retry recovers on its own, got {status}");
    }

    private static string Update(DateTimeOffset at, string kind, string? call = null, string? status = null)
    {
        var fields = "\"sessionUpdate\":\"" + kind + "\"";
        if (call is not null) fields += ",\"toolCallId\":\"" + call + "\"";
        if (status is not null) fields += ",\"status\":\"" + status + "\"";
        var seconds = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return "{\"timestamp\":" + seconds + ",\"params\":{\"update\":{" + fields + "}}}";
    }

    private static ActivityState Scan(string[] lines, DateTimeOffset now, DateTimeOffset lastWorking)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-grok-stall-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddSeconds(-1));
        try
        {
            var seen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { [path] = lastWorking };
            return SessionScanner.SessionState(path, now, seen, null, SessionTurnState.Grok).Status;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
