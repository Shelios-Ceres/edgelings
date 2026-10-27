using System.Globalization;
using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 1:1 port of the macOS SessionTurnStateTests — the behavioral contract for
/// turn detection, delivery keys, and the desktop bookkeeping grace.
public static class SessionTurnStateTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("claude user after assistant suppresses stale alarm", TestClaudeUserAfterAssistantIsNotNeedsYou),
            ("claude metadata does not change turn key", TestClaudeMetadataAfterAssistantKeepsAssistantTurnKey),
            ("codex user after complete suppresses stale alarm", TestCodexUserAfterTaskCompleteIsNotNeedsYou),
            ("codex start after complete suppresses stale alarm", TestCodexTaskStartedAfterTaskCompleteIsNotNeedsYou),
            ("codex complete still triggers needs-you", TestCodexTaskCompleteIsNeedsYou),
            ("metadata-touched old claude transcript is idle", TestMetadataTouchedOldClaudeTranscriptIsIdle),
            ("fresh claude end_turn is immediately needs-you", TestFreshClaudeEndTurnIsImmediatelyNeedsYou),
            ("newer claude desktop activity suppresses old end_turn", TestClaudeDesktopNewerActivitySuppressesOldEndTurn),
            ("desktop bookkeeping write does not suppress fresh end_turn", TestDesktopBookkeepingWriteDoesNotSuppressFreshEndTurn),
            ("desktop activity well after end_turn still suppresses", TestDesktopActivityWellAfterEndTurnStillSuppresses),
            ("claude streaming assistant is working", TestClaudeStreamingAssistantIsWorking),
            ("sdk run ending on a structured output is idle", TestSdkStructuredOutputIsIdle),
            ("sdk transcript is recognised by its entrypoint", TestSdkTranscriptDetected),
            ("a transcript is not remembered as ordinary before it names its entrypoint", TestSdkTranscriptNamedLater),
            ("huge attachment line after the last assistant line keeps the turn known", TestHugeLineKeepsTurnKnown),
            ("unknown turn works only while the desktop's turn start is recent", TestUnknownTurnWithoutDesktopDateIsIdle),
            ("desktop lastActivityAt after the turn does not make it work", TestLateLastActivityDoesNotWork),
            ("peer message after end_turn starts a working turn", TestPeerMessageStartsTurn),
            ("session file without latestUserFrameAt behaves as before", TestNoFrameDateFallsBack),
            ("desktop archived flag is read", TestArchivedFlagIsRead),
            ("a desktop session file caught mid-rewrite keeps its last reading", TestDesktopFileMidRewriteKeepsReading),
            ("desktop prior transcript ids are read", TestPriorCliSessionIdsAreRead),
            ("a transcript its desktop session left behind is replaced", TestLeftBehindTranscriptsAreReplaced),
            ("claude desktop's moved data folder is found", TestMovedDesktopDataFolderIsFound),
        };

        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SessionTurnStateTests GREEN");
    }

    private static void TestClaudeUserAfterAssistantIsNotNeedsYou()
    {
        var expected = Date("2026-07-02T01:15:42.000Z");
        var lines = new[]
        {
            """{"type":"assistant","uuid":"a1","timestamp":"2026-06-25T16:27:50.585Z","message":{"stop_reason":"end_turn"}}""",
            """{"type":"custom-title","sessionId":"s1"}""",
            """{"type":"user","uuid":"u2","timestamp":"2026-07-02T01:15:42.000Z","message":{"role":"user","content":"next turn"}}""",
        };
        var state = SessionTurnState.Claude(lines);
        Expect(!state.IsDone, "new user turn after assistant must suppress stale needs-you");
        Expect(state.Key == "u2", "latest user event should become the turn key");
        Expect(state.ActivityDate == expected, "latest user event should carry its timestamp");
    }

    private static void TestClaudeMetadataAfterAssistantKeepsAssistantTurnKey()
    {
        var expected = Date("2026-06-25T16:27:50.585Z");
        var lines = new[]
        {
            """{"type":"assistant","uuid":"a1","timestamp":"2026-06-25T16:27:50.585Z","message":{"stop_reason":"end_turn"}}""",
            """{"type":"mode","sessionId":"s1"}""",
            """{"type":"custom-title","sessionId":"s1"}""",
        };
        var state = SessionTurnState.Claude(lines);
        Expect(state.IsDone, "metadata after assistant should not hide a completed turn");
        Expect(state.Key == "a1", "metadata writes should not create a new delivery key");
        Expect(state.ActivityDate == expected, "metadata writes should not change semantic activity time");
    }

    private static void TestCodexUserAfterTaskCompleteIsNotNeedsYou()
    {
        var expected = Date("2026-07-02T01:15:42.000Z");
        var lines = new[]
        {
            """{"timestamp":"2026-07-02T01:14:00.000Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1"}}""",
            """{"timestamp":"2026-07-02T01:15:42.000Z","type":"event_msg","payload":{"type":"user_message","turn_id":"t2","message":"next"}}""",
        };
        var state = SessionTurnState.Codex(lines);
        Expect(!state.IsDone, "new codex user message after task complete must suppress stale needs-you");
        Expect(state.Key == "t2", "latest codex user event should become the turn key");
        Expect(state.ActivityDate == expected, "latest codex user event should carry its timestamp");
    }

    private static void TestCodexTaskStartedAfterTaskCompleteIsNotNeedsYou()
    {
        var lines = new[]
        {
            """{"type":"event_msg","payload":{"type":"task_complete","turn_id":"t1"}}""",
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"t2"}}""",
        };
        var state = SessionTurnState.Codex(lines);
        Expect(!state.IsDone, "new codex task start after completion must suppress stale needs-you");
        Expect(state.Key == "t2", "latest codex task start should become the turn key");
    }

    private static void TestCodexTaskCompleteIsNeedsYou()
    {
        var lines = new[]
        {
            """{"timestamp":"2026-07-02T01:14:00.000Z","type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}""",
            """{"timestamp":"2026-07-02T01:15:41.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}}""",
            """{"timestamp":"2026-07-02T01:15:42.000Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1"}}""",
        };
        var state = SessionTurnState.Codex(lines);
        Expect(state.IsDone, "codex task complete should be needs-you");
        Expect(state.Key == "t1", "completed codex task should keep its turn id");
    }

    private static void TestMetadataTouchedOldClaudeTranscriptIsIdle()
    {
        var old = Date("2026-06-25T16:27:50.585Z");
        var now = Date("2026-07-02T01:15:42.000Z");
        var tmp = WriteTranscript(new[]
        {
            """{"type":"assistant","uuid":"a1","timestamp":"2026-06-25T16:27:50.585Z","message":{"stop_reason":"end_turn"}}""",
            """{"type":"custom-title","sessionId":"s1"}""",
            """{"type":"mode","sessionId":"s1"}""",
        }, modified: now);
        try
        {
            var state = SessionScanner.SessionState(
                tmp, now, EmptyLastWorking, null, SessionTurnState.Claude);
            Expect(state.Status == ActivityState.Idle,
                "metadata-only file writes must not resurrect old completed Claude turns");
            Expect(state.Modified == old, "semantic modified time should come from the last real turn");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static void TestFreshClaudeEndTurnIsImmediatelyNeedsYou()
    {
        var now = Date("2026-07-02T01:15:42.000Z");
        var tmp = WriteTranscript(new[]
        {
            """{"type":"user","uuid":"u1","timestamp":"2026-07-02T01:15:00.000Z","message":{"role":"user","content":"run"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-07-02T01:15:41.000Z","message":{"stop_reason":"end_turn"}}""",
        }, modified: now);
        try
        {
            var state = SessionScanner.SessionState(
                tmp, now, EmptyLastWorking, null, SessionTurnState.Claude);
            Expect(state.Status == ActivityState.NeedsYou,
                "fresh Claude end_turn should immediately become needs-you");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static void TestClaudeDesktopNewerActivitySuppressesOldEndTurn()
    {
        var old = Date("2026-06-25T16:27:50.585Z");
        var desktopActivity = Date("2026-07-02T01:15:42.000Z");
        var now = Date("2026-07-02T01:15:44.000Z");
        var tmp = WriteTranscript(new[]
        {
            """{"type":"assistant","uuid":"a1","timestamp":"2026-06-25T16:27:50.585Z","message":{"stop_reason":"end_turn"}}""",
            """{"type":"mode","sessionId":"s1"}""",
        }, modified: now);
        try
        {
            var state = SessionScanner.SessionState(
                tmp, now, EmptyLastWorking, desktopActivity, SessionTurnState.Claude);
            Expect(state.Status == ActivityState.Working,
                "newer Claude Desktop activity should suppress stale end_turn and show running");
            Expect(state.Modified == desktopActivity,
                "newer desktop activity should drive the effective state time");
            Expect(old < state.Modified, "test fixture must keep transcript older than desktop activity");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static void TestDesktopBookkeepingWriteDoesNotSuppressFreshEndTurn()
    {
        // Desktop stamps lastActivityAt ~2-4s after the final assistant event
        // as turn-completion bookkeeping. That write must not eat the alarm.
        var turnDone = Date("2026-07-02T01:15:41.000Z");
        var bookkeeping = turnDone.AddSeconds(3);
        var now = Date("2026-07-02T01:15:48.000Z");
        var tmp = WriteTranscript(new[]
        {
            """{"type":"user","uuid":"u1","timestamp":"2026-07-02T01:15:00.000Z","message":{"role":"user","content":"run"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-07-02T01:15:41.000Z","message":{"stop_reason":"end_turn"}}""",
        }, modified: now);
        try
        {
            var state = SessionScanner.SessionState(
                tmp, now, EmptyLastWorking, bookkeeping, SessionTurnState.Claude);
            Expect(state.Status == ActivityState.NeedsYou,
                "desktop bookkeeping write seconds after end_turn must not suppress needs-you");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static void TestDesktopActivityWellAfterEndTurnStillSuppresses()
    {
        // Past the bookkeeping grace the user genuinely came back — suppress.
        var turnDone = Date("2026-07-02T01:15:41.000Z");
        var userReturned = turnDone.AddSeconds(120);
        var now = Date("2026-07-02T01:17:45.000Z");
        var tmp = WriteTranscript(new[]
        {
            """{"type":"user","uuid":"u1","timestamp":"2026-07-02T01:15:00.000Z","message":{"role":"user","content":"run"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-07-02T01:15:41.000Z","message":{"stop_reason":"end_turn"}}""",
        }, modified: now);
        try
        {
            var state = SessionScanner.SessionState(
                tmp, now, EmptyLastWorking, userReturned, SessionTurnState.Claude);
            Expect(state.Status == ActivityState.Working,
                "desktop activity well after end_turn means the user is back — suppress the alarm");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static void TestClaudeStreamingAssistantIsWorking()
    {
        var now = Date("2026-07-02T01:15:44.000Z");
        var tmp = WriteTranscript(new[]
        {
            """{"type":"user","uuid":"u1","timestamp":"2026-07-02T01:15:00.000Z","message":{"role":"user","content":"run"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-07-02T01:15:42.000Z","message":{"stop_reason":null}}""",
        }, modified: now);
        try
        {
            var state = SessionScanner.SessionState(
                tmp, now, EmptyLastWorking, null, SessionTurnState.Claude);
            Expect(state.Status == ActivityState.Working,
                "Claude assistant output without end_turn should stay running");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // MARK: - Phase 3.1: honest Claude state

    private static readonly string[] EndedTurn =
    {
        """{"type":"user","uuid":"u1","timestamp":"2026-10-19T13:00:00.000Z","message":{"role":"user","content":"hi"}}""",
        """{"type":"assistant","uuid":"a1","timestamp":"2026-10-19T13:00:10.000Z","message":{"stop_reason":"end_turn"}}""",
    };

    private sealed class DesktopFile : IDisposable
    {
        public string Path { get; }

        public DesktopFile(long? activityMs, long? frameMs, bool archived)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agent-island-desktop-{Guid.NewGuid()}.json");
            var fields = new List<string> { "\"cliSessionId\":\"s1\"", "\"cwd\":\"D:\\\\x\"" };
            if (activityMs is { } a) fields.Add($"\"lastActivityAt\":{a}");
            if (frameMs is { } f) fields.Add($"\"latestUserFrameAt\":{f}");
            if (archived) fields.Add("\"isArchived\":true");
            File.WriteAllText(Path, "{" + string.Join(",", fields) + "}");
        }

        public void Dispose() => File.Delete(Path);
    }

    private static long Ms(string raw) => Date(raw).ToUnixTimeMilliseconds();

    private static (ActivityState Status, string? TurnKey, DateTimeOffset Modified, bool TurnEnded) ClaudeScan(
        string[] lines, DateTimeOffset now, DesktopFile? desktop)
    {
        SessionScanner.ClearTurnCache();
        var tmp = WriteTranscript(lines, now);
        try
        {
            var session = desktop is null ? null : SessionScanner.ParseDesktopSessionFile(desktop.Path);
            return SessionScanner.ClaudeSessionState(tmp, now, EmptyLastWorking, session);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static void TestSdkStructuredOutputIsIdle()
    {
        var now = Date("2026-10-19T13:05:00.000Z");
        var state = ClaudeScan(new[]
        {
            """{"type":"user","uuid":"u1","entrypoint":"sdk-py","timestamp":"2026-10-19T13:00:00.000Z","message":{"role":"user","content":"Review this change"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-10-19T13:04:00.000Z","message":{"stop_reason":"tool_use"}}""",
            """{"type":"user","uuid":"u2","toolEndsTurn":true,"timestamp":"2026-10-19T13:04:30.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"Structured output provided successfully"}]}}""",
        }, now, desktop: null);
        Expect(state.Status == ActivityState.Idle, $"a finished sdk run needs no one, got {state.Status}");
    }

    private static void TestSdkTranscriptDetected()
    {
        var at = Date("2026-10-19T13:00:00.000Z");
        var sdk = WriteTranscript(new[]
        {
            """{"type":"queue-operation","sessionId":"s"}""",
            """{"type":"user","entrypoint":"sdk-py","uuid":"u1","message":{"role":"user","content":"x"}}""",
        }, at);
        var desktopRun = WriteTranscript(new[]
        {
            """{"type":"user","entrypoint":"claude-desktop","uuid":"u1","message":{"role":"user","content":"x"}}""",
        }, at);
        var noEntrypoint = WriteTranscript(new[] { """{"type":"user","uuid":"u1"}""" }, at);
        try
        {
            Expect(SessionScanner.IsSdkTranscript(sdk), "sdk-py is a machine run");
            Expect(!SessionScanner.IsSdkTranscript(desktopRun), "claude-desktop is a conversation");
            Expect(!SessionScanner.IsSdkTranscript(noEntrypoint), "no entrypoint is a conversation");
        }
        finally
        {
            File.Delete(sdk);
            File.Delete(desktopRun);
            File.Delete(noEntrypoint);
        }
    }

    private static void TestSdkTranscriptNamedLater()
    {
        var at = Date("2026-10-19T13:00:00.000Z");
        // Seen while only a bookkeeping record had been written.
        var path = WriteTranscript(new[] { """{"type":"queue-operation","sessionId":"s"}""" }, at);
        // A long ordinary transcript whose first lines never name an entrypoint.
        var legacy = WriteTranscript(
            Enumerable.Range(0, 45).Select(i => $$"""{"type":"user","uuid":"u{{i}}"}""").ToArray(), at);
        try
        {
            Expect(!SessionScanner.IsSdkTranscript(path), "no record names an entrypoint yet");
            File.AppendAllText(path, "\n" + """{"type":"user","entrypoint":"sdk-py","uuid":"u1","message":{"role":"user","content":"x"}}""");
            Expect(SessionScanner.IsSdkTranscript(path), "once its entrypoint is written it is a machine run");

            Expect(!SessionScanner.IsSdkTranscript(legacy), "no entrypoint in the first lines is a conversation");
            File.AppendAllText(legacy, "\n" + """{"type":"user","entrypoint":"sdk-py","uuid":"late"}""");
            Expect(!SessionScanner.IsSdkTranscript(legacy), "records past the lines that decide it do not change the answer");
        }
        finally
        {
            File.Delete(path);
            File.Delete(legacy);
        }
    }

    private static void TestHugeLineKeepsTurnKnown()
    {
        // The attachment line after the last assistant line is far bigger
        // than the usual 128 KB tail, so only a wider read finds the turn.
        var now = Date("2026-10-19T13:05:00.000Z");
        var big = "{\"type\":\"attachment\",\"uuid\":\"x\",\"data\":\"" + new string('x', 300_000) + "\"}";
        var lines = new List<string>(EndedTurn) { big };
        using var desktop = new DesktopFile(null, Ms("2026-10-19T13:00:00.000Z"), archived: false);
        var state = ClaudeScan(lines.ToArray(), now, desktop);
        Expect(state.TurnKey == "a1", $"the turn must be found past the huge line, got {state.TurnKey}");
        Expect(state.Status == ActivityState.NeedsYou, $"a finished turn waits on the user, got {state.Status}");
    }

    private static void TestUnknownTurnWithoutDesktopDateIsIdle()
    {
        var now = Date("2026-10-19T13:05:00.000Z");
        var noTurn = new[] { """{"type":"mode","sessionId":"s1"}""" };
        // Nothing in the file reads as a turn, and no desktop session says one began.
        var bare = ClaudeScan(noTurn, now, desktop: null);
        Expect(bare.Status == ActivityState.Idle, $"a file's write time alone is not work, got {bare.Status}");
        using var started = new DesktopFile(null, Ms("2026-10-19T13:04:00.000Z"), archived: false);
        var working = ClaudeScan(noTurn, now, started);
        Expect(working.Status == ActivityState.Working, $"a turn the desktop started a minute ago works, got {working.Status}");
        using var old = new DesktopFile(null, Ms("2026-10-19T12:00:00.000Z"), archived: false);
        var idle = ClaudeScan(noTurn, now, old);
        Expect(idle.Status == ActivityState.Idle, $"outside the attention window it is idle, got {idle.Status}");
    }

    private static void TestLateLastActivityDoesNotWork()
    {
        // The desktop wrote lastActivityAt 13 minutes after the turn began;
        // the turn start (latestUserFrameAt) is before the turn's end.
        var now = Date("2026-10-19T13:20:00.000Z");
        using var desktop = new DesktopFile(Ms("2026-10-19T13:13:00.000Z"), Ms("2026-10-19T13:00:00.000Z"), archived: false);
        var state = ClaudeScan(EndedTurn, now, desktop);
        Expect(state.Status == ActivityState.NeedsYou, $"a turn that ended ten minutes ago still waits, got {state.Status}");
    }

    private static void TestPeerMessageStartsTurn()
    {
        // A peer message is an isMeta line the transcript scan skips; the
        // desktop's frame date after the last end_turn is the only trace.
        var now = Date("2026-10-19T13:05:00.000Z");
        var frame = Ms("2026-10-19T13:04:00.000Z");
        using var desktop = new DesktopFile(frame, frame, archived: false);
        var lines = new List<string>(EndedTurn)
        {
            """{"type":"user","uuid":"m1","isMeta":true,"timestamp":"2026-10-19T13:04:00.100Z","message":{"role":"user","content":"peer says go"}}""",
        };
        var state = ClaudeScan(lines.ToArray(), now, desktop);
        Expect(state.Status == ActivityState.Working, $"a peer message starts a turn, got {state.Status}");
    }

    private static void TestNoFrameDateFallsBack()
    {
        // lastActivityAt 3 s after the end: bookkeeping, inside the grace.
        using (var desktop = new DesktopFile(Ms("2026-10-19T13:00:13.000Z"), null, archived: false))
        {
            var state = ClaudeScan(EndedTurn, Date("2026-10-19T13:00:48.000Z"), desktop);
            Expect(state.Status == ActivityState.NeedsYou, $"bookkeeping inside the grace must not suppress, got {state.Status}");
        }
        // lastActivityAt two minutes after the end: the user is back.
        using (var desktop = new DesktopFile(Ms("2026-10-19T13:02:10.000Z"), null, archived: false))
        {
            var state = ClaudeScan(EndedTurn, Date("2026-10-19T13:02:30.000Z"), desktop);
            Expect(state.Status == ActivityState.Working, $"later activity without a frame date still means work, got {state.Status}");
        }
    }

    private static void TestArchivedFlagIsRead()
    {
        using var desktop = new DesktopFile(null, Ms("2026-10-19T13:00:00.000Z"), archived: true);
        var session = SessionScanner.ParseDesktopSessionFile(desktop.Path);
        Expect(session is { IsArchived: true }, "the archived flag must be read from the session file");
    }

    private static void TestDesktopFileMidRewriteKeepsReading()
    {
        using var desktop = new DesktopFile(null, Ms("2026-10-19T13:00:00.000Z"), archived: true);
        Expect(SessionScanner.DesktopSessionAt(desktop.Path) is { IsArchived: true }, "the session is read");
        File.WriteAllText(desktop.Path, """{"cliSessionId":"s1","isArch""");
        Expect(SessionScanner.DesktopSessionAt(desktop.Path) is { IsArchived: true },
            "a torn rewrite keeps the archived session archived");
        File.WriteAllText(desktop.Path, """{"cliSessionId":"s1","isArchived":false}""");
        Expect(SessionScanner.DesktopSessionAt(desktop.Path) is { IsArchived: false }, "the finished rewrite is read");
        using var fresh = new DesktopFile(null, null, archived: false);
        File.WriteAllText(fresh.Path, """{"cliSessionId":""");
        Expect(SessionScanner.DesktopSessionAt(fresh.Path) is null, "a file never read whole has no reading");
    }

    // A session's /clear and /compact carry it on in a new transcript and
    // list the old ones under priorCliSessionIds.
    private static void TestPriorCliSessionIdsAreRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-island-desktop-{Guid.NewGuid()}.json");
        File.WriteAllText(path, """{"cliSessionId":"s3","title":"Main","priorCliSessionIds":["s1",7,"","s2"]}""");
        try
        {
            var session = SessionScanner.ParseDesktopSessionFile(path);
            Expect(session is not null && session.PriorCliSessionIds.SequenceEqual(new[] { "s1", "s2" }),
                $"prior ids are the non-empty strings in order, got {string.Join(",", session?.PriorCliSessionIds ?? Array.Empty<string>())}");
        }
        finally
        {
            File.Delete(path);
        }
        var bare = Path.Combine(Path.GetTempPath(), $"agent-island-desktop-{Guid.NewGuid()}.json");
        File.WriteAllText(bare, """{"cliSessionId":"s1"}""");
        try
        {
            Expect(SessionScanner.ParseDesktopSessionFile(bare) is { PriorCliSessionIds.Count: 0 }, "a session never cleared has no prior ids");
        }
        finally
        {
            File.Delete(bare);
        }
    }

    private static void TestLeftBehindTranscriptsAreReplaced()
    {
        var main = new SessionScanner.DesktopSession("s2", "Main", "D:\\x", false, null, null) { PriorCliSessionIds = new[] { "s1" } };
        // Just cleared: no transcript of its own yet.
        var cleared = new SessionScanner.DesktopSession("", "Builder", "D:\\x", false, null, null) { PriorCliSessionIds = new[] { "s0" } };
        var (current, replaced) = SessionScanner.ClaudeDesktopIndex(new[] { main, cleared, null });
        Expect(current.Count == 1 && current["s2"].Title == "Main", "only a session's own transcript maps to it");
        Expect(replaced.SetEquals(new[] { "s0", "s1" }), $"every left-behind transcript is replaced, got {string.Join(",", replaced)}");
    }

    private static void TestMovedDesktopDataFolderIsFound()
    {
        var home = Path.Combine(Path.GetTempPath(), $"agent-island-appdata-{Guid.NewGuid()}");
        var roaming = Path.Combine(home, "Roaming");
        var local = Path.Combine(home, "Local");
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(local);
        try
        {
            var before = IslandPaths.ClaudeDesktopDataRootsUnder(roaming, local);
            Expect(before.SequenceEqual(new[] { Path.Combine(roaming, "Claude") }), "before the move only the roaming folder is looked at");
            var moved = Path.Combine(local, "Claude-Data", "claude_pzs8sxrjxfjjc");
            Directory.CreateDirectory(moved);
            var after = IslandPaths.ClaudeDesktopDataRootsUnder(roaming, local);
            Expect(after.SequenceEqual(new[] { Path.Combine(roaming, "Claude"), moved }), $"the moved folder comes after the old one, got {string.Join(" | ", after)}");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> EmptyLastWorking =
        new Dictionary<string, DateTimeOffset>();

    private static string WriteTranscript(string[] lines, DateTimeOffset modified)
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            $"agent-island-session-state-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(tmp, string.Join("\n", lines));
        File.SetLastWriteTimeUtc(tmp, modified.UtcDateTime);
        return tmp;
    }

    private static DateTimeOffset Date(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
