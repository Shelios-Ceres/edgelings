using System.Globalization;
using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Claude reads as stalled only when silence means something: long thinking
/// writes nothing until its block completes, and an interrupt or a local
/// command such as /compact leaves the session waiting on nobody.
public static class ClaudeStallTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("claude thinking for nine minutes is working", TestLongThinkingAfterPromptIsWorking),
            ("claude thinking on a tool result for twelve minutes is working", TestLongThinkingAfterToolResultIsWorking),
            ("claude model silent for sixteen minutes is still working", TestLongSilenceIsWorking),
            ("claude tool silent for six minutes is still working", TestQuietToolIsWorking),
            ("claude third failed connection attempt is network trouble", TestRepeatedRetriesAreNetworkTrouble),
            ("claude single retry is still working", TestSingleRetryIsWorking),
            ("claude retries long over are working again", TestOldRetriesAreWorking),
            ("claude session limit is out of quota", TestSessionLimitIsRateLimited),
            ("claude sign-in error needs sign-in", TestNotLoggedInNeedsSignIn),
            ("claude connection error that ended the turn is network trouble", TestFinalConnectionErrorIsNetworkTrouble),
            ("claude other API error is an error, not your turn", TestOtherApiErrorIsError),
            ("claude /compact leaves the session idle", TestLocalCommandIsIdle),
            ("claude interrupt is idle, not stalled", TestInterruptIsIdle),
            ("claude image caption does not hide the prompt", TestImageCaptionIsNotAPrompt),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("ClaudeStallTests GREEN");
    }

    private static readonly DateTimeOffset Start = Date("2026-10-12T15:00:00.000Z");

    private static void TestLongThinkingAfterPromptIsWorking()
    {
        var status = Scan(new[] { Prompt(Start) }, Start.AddMinutes(9), lastWorking: Start.AddMinutes(8));
        Expect(status == ActivityState.Working, $"nine minutes of thinking on a prompt must be working, got {status}");
    }

    private static void TestLongThinkingAfterToolResultIsWorking()
    {
        var status = Scan(new[]
        {
            Prompt(Start),
            ToolUse(Start.AddMinutes(1)),
            ToolResult(Start.AddMinutes(2)),
        }, Start.AddMinutes(14), lastWorking: Start.AddMinutes(13));
        Expect(status == ActivityState.Working, $"twelve minutes of thinking on a tool result must be working, got {status}");
    }

    private static void TestLongSilenceIsWorking()
    {
        var status = Scan(new[] { Prompt(Start) }, Start.AddMinutes(16), lastWorking: Start.AddMinutes(15).AddSeconds(-10));
        Expect(status == ActivityState.Working, $"silence alone is never trouble, got {status}");
    }

    private static void TestRepeatedRetriesAreNetworkTrouble()
    {
        var failing = Start.AddMinutes(2);
        var status = Scan(new[]
        {
            Prompt(Start),
            ToolUse(Start.AddMinutes(1)),
            ToolResult(Start.AddMinutes(1)),
            Retry(failing, 1),
            Retry(failing.AddSeconds(1), 2),
            Retry(failing.AddSeconds(3), 3),
        }, failing.AddSeconds(10), lastWorking: failing);
        Expect(status == ActivityState.NetworkTrouble, $"three failed connection attempts in a row are network trouble, got {status}");
    }

    private static void TestSingleRetryIsWorking()
    {
        var failing = Start.AddMinutes(2);
        var status = Scan(new[] { Prompt(Start), Retry(failing, 1) }, failing.AddSeconds(10), lastWorking: failing);
        Expect(status == ActivityState.Working, $"one retry recovers on its own, got {status}");
    }

    private static void TestOldRetriesAreWorking()
    {
        var failing = Start.AddMinutes(2);
        var status = Scan(new[] { Prompt(Start), Retry(failing, 3) }, failing.AddMinutes(4), lastWorking: failing);
        Expect(status == ActivityState.Working, $"retries that stopped minutes ago succeeded, got {status}");
    }

    private static void TestSessionLimitIsRateLimited()
    {
        var status = Scan(new[] { Prompt(Start), ApiError(Start.AddMinutes(1), "You've hit your session limit · resets 2am (Asia/Shanghai)") },
            Start.AddMinutes(3), lastWorking: Start.AddMinutes(1));
        Expect(status == ActivityState.RateLimited, $"a session limit is out of quota, got {status}");
    }

    private static void TestNotLoggedInNeedsSignIn()
    {
        var status = Scan(new[] { Prompt(Start), ApiError(Start.AddMinutes(1), "Not logged in · Please run /login") },
            Start.AddMinutes(3), lastWorking: Start.AddMinutes(1));
        Expect(status == ActivityState.AuthRequired, $"a lost login needs signing in, got {status}");
    }

    private static void TestFinalConnectionErrorIsNetworkTrouble()
    {
        var status = Scan(new[] { Prompt(Start), ApiError(Start.AddMinutes(1), "API Error: Connection error.") },
            Start.AddMinutes(3), lastWorking: Start.AddMinutes(1));
        Expect(status == ActivityState.NetworkTrouble, $"giving up on a lost connection is network trouble, got {status}");
    }

    private static void TestOtherApiErrorIsError()
    {
        var lines = new[] { Prompt(Start), ApiError(Start.AddMinutes(1), "API Error: 500 Internal server error") };
        var now = Start.AddMinutes(3);
        var status = Scan(lines, now, lastWorking: Start.AddMinutes(1));
        Expect(status == ActivityState.NeedsYou, $"a turn that ended on an error is back to the user, got {status}");
        var phase = AgentPhases.Combine(status, AgentPhaseClassifier.Claude(lines, now), quotaExhausted: false);
        Expect(phase == AgentPhase.Failed, $"it shows as an error, not your turn, got {phase}");
    }

    private static void TestQuietToolIsWorking()
    {
        var status = Scan(new[]
        {
            Prompt(Start),
            ToolUse(Start.AddMinutes(1)),
        }, Start.AddMinutes(7), lastWorking: Start.AddMinutes(5));
        Expect(status == ActivityState.Working, $"a long command is not trouble, got {status}");
    }

    private static void TestLocalCommandIsIdle()
    {
        var compacted = Start.AddMinutes(3);
        var status = Scan(new[]
        {
            Prompt(Start),
            Line(Start.AddMinutes(1), """{"type":"assistant","uuid":"a9","message":{"stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}}"""),
            Line(Start.AddMinutes(2), """{"type":"user","isMeta":true,"uuid":"m1","message":{"role":"user","content":"<local-command-caveat>Caveat: generated by local commands.</local-command-caveat>"}}"""),
            Line(Start.AddMinutes(2), """{"type":"user","uuid":"c1","message":{"role":"user","content":"<command-name>/compact</command-name>\n<command-message>compact</command-message>\n<command-args></command-args>"}}"""),
            Line(compacted, """{"type":"system","subtype":"compact_boundary","uuid":"b1"}"""),
            Line(compacted, """{"type":"user","isCompactSummary":true,"isVisibleInTranscriptOnly":true,"uuid":"s1","message":{"role":"user","content":"This session is being continued from a previous conversation."}}"""),
            Line(compacted, """{"type":"user","uuid":"o1","message":{"role":"user","content":"<local-command-stdout>Compacted</local-command-stdout>"}}"""),
        }, compacted.AddMinutes(6), lastWorking: compacted);
        Expect(status == ActivityState.Idle, $"after /compact nobody is working and nothing waits on the user, got {status}");
    }

    private static void TestInterruptIsIdle()
    {
        var stopped = Start.AddMinutes(2);
        var status = Scan(new[]
        {
            Prompt(Start),
            ToolUse(Start.AddMinutes(1)),
            Line(stopped, """{"type":"user","uuid":"i1","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}"""),
        }, stopped.AddMinutes(6), lastWorking: stopped);
        Expect(status == ActivityState.Idle, $"a turn the user stopped is over, not stalled, got {status}");
    }

    private static void TestImageCaptionIsNotAPrompt()
    {
        var status = Scan(new[]
        {
            Prompt(Start),
            Line(Start, """{"type":"user","isMeta":true,"uuid":"im","message":{"role":"user","content":[{"type":"text","text":"[Image: source: C:\\shot.png]"}]}}"""),
        }, Start.AddMinutes(7), lastWorking: Start.AddMinutes(6));
        Expect(status == ActivityState.Working, $"a prompt with an image is still being thought about, got {status}");
    }

    private static string Prompt(DateTimeOffset at) =>
        Line(at, """{"type":"user","uuid":"u1","message":{"role":"user","content":"build the widget"}}""");

    private static string ToolUse(DateTimeOffset at) =>
        Line(at, """{"type":"assistant","uuid":"a1","message":{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"PowerShell","input":{}}]}}""");

    /// A Claude Code retry record after the connection dropped.
    private static string Retry(DateTimeOffset at, int attempt) =>
        Line(at, """{"type":"system","subtype":"api_error","level":"error","uuid":"rN","error":{"message":"Connection error.","formatted":"Connection dropped (ECONNRESET)","connection":{"code":"ECONNRESET"}},"retryInMs":DELAY,"retryAttempt":N,"maxRetries":10}"""
            .Replace("DELAY", (500 << attempt).ToString(CultureInfo.InvariantCulture))
            .Replace("\"rN\"", "\"r" + attempt + "\"")
            .Replace("\"retryAttempt\":N", "\"retryAttempt\":" + attempt));

    private static string ApiError(DateTimeOffset at, string text) =>
        Line(at, """{"type":"assistant","isApiErrorMessage":true,"uuid":"e1","message":{"stop_reason":"stop_sequence","content":[{"type":"text","text":"TEXT"}]}}""".Replace("TEXT", text));

    private static string ToolResult(DateTimeOffset at) =>
        Line(at, """{"type":"user","uuid":"u2","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""");

    /// Stamps a record with its time, the way Claude Code writes them.
    private static string Line(DateTimeOffset at, string record) =>
        $$"""{"timestamp":"{{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}",{{record[1..]}}""";

    private static ActivityState Scan(string[] lines, DateTimeOffset now, DateTimeOffset lastWorking)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-claude-stall-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddSeconds(-1));
        try
        {
            var seen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { [path] = lastWorking };
            return SessionScanner.SessionState(path, now, seen, null, SessionTurnState.Claude).Status;
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

    private static DateTimeOffset Date(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
