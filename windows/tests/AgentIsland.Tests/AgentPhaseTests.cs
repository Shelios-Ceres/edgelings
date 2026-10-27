using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Pins the side drawer's live phases: which tool-level transcript events
/// read as which phase, the parallel-call rule (a fast call's result must not
/// hide a slow sibling still running), bookkeeping skips, ageing, and how the
/// scanner's ActivityState combines with the transcript. Synthetic events
/// only, mirroring the shapes observed in real transcripts on 2026-10-11.
public static class AgentPhaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("claude: a prompt reads as thinking", () => Claude(AgentPhase.Thinking, UserText("prompt", 2))),
            ("claude: a pending Bash call is running", () => Claude(AgentPhase.Running, ToolUse("Bash", 5, "b"))),
            ("claude: a returned call reads as thinking", () => Claude(AgentPhase.Thinking, ToolUse("Read", 6, "r"), ToolResult(4, "r"))),
            ("claude: a finished thinking block reads as writing what follows", () => Claude(AgentPhase.Streaming,
                ToolUse("Read", 60, "r"), ToolResult(58, "r"), Thinking(20))),
            ("claude: nine minutes of thinking is still thinking", () => Claude(AgentPhase.Thinking, ToolUse("Read", 9 * 60 + 2, "r"), ToolResult(9 * 60, "r"))),
            ("claude: a thought past the attention window is idle", () => Claude(AgentPhase.Idle, UserText("prompt", 31 * 60))),
            ("claude: bookkeeping after end_turn stays finished", () => Claude(AgentPhase.Finished, Text("end_turn", 30),
                Line(new { type = "system", subtype = "stop_hook_summary" }, 29), Line(new { type = "custom-title" }, 28))),
            ("claude: Edit is writing", () => Claude(AgentPhase.Writing, ToolUse("Edit", 1, "e"))),
            ("claude: Grep is searching", () => Claude(AgentPhase.Searching, ToolUse("Grep", 1, "g"))),
            ("claude: WebFetch is web searching", () => Claude(AgentPhase.WebSearching, ToolUse("WebFetch", 1, "w"))),
            ("claude: a fresh Task is spawning", () => Claude(AgentPhase.Spawning, ToolUse("Task", 1, "t"))),
            ("claude: a long Task is waiting on agents", () => Claude(AgentPhase.WaitingOnAgents, ToolUse("Task", 20, "t"))),
            ("claude: AskUserQuestion is asking", () => Claude(AgentPhase.Asking, ToolUse("AskUserQuestion", 40, "q"))),
            ("claude: a browser MCP tool is controlling", () => Claude(AgentPhase.Controlling, ToolUse("mcp__Claude_Browser__navigate", 2, "n"))),
            ("claude: an API error line has failed", () => Claude(AgentPhase.Failed, Line(new { type = "assistant", isApiErrorMessage = true,
                message = new { stop_reason = "stop_sequence", content = new[] { new { type = "text" } } } }, 3))),
            ("claude: compaction", () => Claude(AgentPhase.Compacting, Line(new { type = "system", subtype = "compact_boundary" }, 3))),
            ("claude: sidechain lines are ignored", () => Claude(AgentPhase.Running, ToolUse("Bash", 4, "b"),
                Line(new { type = "assistant", isSidechain = true,
                    message = new { stop_reason = "tool_use", content = new[] { new { type = "tool_use", name = "Read", id = "s" } } } }, 1))),
            ("claude: an old end_turn is idle", () => Claude(AgentPhase.Idle, Text("end_turn", 20 * 60))),
            ("claude: a stale tool is idle", () => Claude(AgentPhase.Idle, ToolUse("Bash", 45 * 60, "b"))),
            ("claude: an interrupt is idle", () => Claude(AgentPhase.Idle, ToolUse("Bash", 9, "b"), UserText("[Request interrupted by user]", 8))),
            ("claude: a parallel slow call is still running", () => Claude(AgentPhase.Running,
                ToolUse("Edit", 6, "a"), ToolUse("Bash", 6, "b"), ToolResult(5, "a"))),
            ("claude: parallel calls all returned", () => Claude(AgentPhase.Thinking,
                ToolUse("Edit", 6, "a"), ToolUse("Bash", 6, "b"), ToolResult(5, "a"), ToolResult(1, "b"))),
            ("claude: an earlier step's call is not pending", () => Claude(AgentPhase.Thinking,
                ToolUse("Read", 20, "x"), ToolResult(19, "x"), Thinking(10), ToolUse("Grep", 9, "y"), ToolResult(8, "y"))),
            ("codex: task started", () => Codex(AgentPhase.Thinking, Event("task_started", 2))),
            ("codex: reasoning", () => Codex(AgentPhase.Thinking, Item("reasoning", 2))),
            ("codex: a pending exec is running", () => Codex(AgentPhase.Running, Item("reasoning", 5), Call("custom_tool_call", "exec", 3, "c"))),
            ("codex: a returned exec reads as thinking", () => Codex(AgentPhase.Thinking, Call("custom_tool_call", "exec", 5, "c"), Output(3, "c"))),
            ("codex: apply_patch is writing", () => Codex(AgentPhase.Writing, Call("custom_tool_call", "apply_patch", 2, "p"))),
            ("codex: spawn_agent is spawning", () => Codex(AgentPhase.Spawning, Call("function_call", "spawn_agent", 1, "s"))),
            ("codex: wait_agent is waiting on agents", () => Codex(AgentPhase.WaitingOnAgents, Call("function_call", "wait_agent", 30, "w"))),
            ("codex: request_user_input is asking", () => Codex(AgentPhase.Asking, Call("function_call", "request_user_input_async", 50, "q"))),
            ("codex: developer messages are skipped", () => Codex(AgentPhase.Reading, Call("function_call", "read_file", 4, "r"),
                Line(new { type = "response_item", payload = new { type = "message", role = "developer" } }, 1))),
            ("codex: completion survives bookkeeping", () => Codex(AgentPhase.Finished, Event("task_complete", 9),
                Event("token_count", 8), Line(new { type = "world_state" }, 7))),
            ("codex: an aborted turn is idle", () => Codex(AgentPhase.Idle, Call("custom_tool_call", "exec", 9, "c"), Event("turn_aborted", 8))),
            ("codex: a parallel slow call is still running", () => Codex(AgentPhase.Running,
                Call("function_call", "exec", 6, "c1"), Call("function_call", "read_file", 6, "c2"), Output(5, "c2"))),
            ("combine: the scan decides working at all", () => Combined(AgentPhase.Idle, ActivityState.Idle, AgentPhase.Running)),
            ("combine: a working scan takes the transcript phase", () => Combined(AgentPhase.Running, ActivityState.Working, AgentPhase.Running)),
            ("combine: a lagging transcript falls back to working", () => Combined(AgentPhase.Working, ActivityState.Working, AgentPhase.Finished)),
            ("combine: needsYou is finished", () => Combined(AgentPhase.Finished, ActivityState.NeedsYou, null)),
            ("combine: exhausted quota sleeps", () => Combined(AgentPhase.RateLimited, ActivityState.Working, AgentPhase.Running, quotaExhausted: true)),
            ("combine: auth outranks everything", () => Combined(AgentPhase.AuthRequired, ActivityState.AuthRequired, AgentPhase.Running, quotaExhausted: true)),
            ("classify: a transcript that has not changed is not read again, and still ages", KeptReadingAges),
            ("classify: a kept reading's detail follows the language", KeptReadingFollowsLanguage),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("AgentPhaseTests GREEN");
    }

    /// The drawer asks twice a second; the file is read once per change.
    private static void KeptReadingAges()
    {
        var path = Transcript(Call("function_call", "spawn_agent", 1, "s"));
        try
        {
            Expect(Classified(path, Now), AgentPhase.Spawning);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Expect(Classified(path, Now), AgentPhase.Spawning);
                Expect(Classified(path, Now.AddSeconds(10)), AgentPhase.WaitingOnAgents);
            }
            File.AppendAllText(path, "\n" + Event("task_complete", 0));
            Expect(Classified(path, Now), AgentPhase.Finished);
            Expect(Classified(path, Now.AddMinutes(11)), AgentPhase.Idle);
        }
        finally
        {
            File.Delete(path);
        }

        static AgentPhase? Classified(string path, DateTimeOffset now) =>
            AgentPhaseClassifier.Classify(TriggerTool.Codex, path, now) is { } reading ? reading.Phase : null;
    }

    private static void KeptReadingFollowsLanguage()
    {
        var retry = new { type = "turn.step.retrying", time = Now.AddSeconds(-2).ToUnixTimeMilliseconds(), nextAttempt = 3, maxAttempts = 10 };
        var path = Transcript(JsonSerializer.Serialize(retry));
        var language = Localization.L10n.Current;
        try
        {
            Localization.L10n.Current = Localization.L10n.Language.SimplifiedChinese;
            var chinese = AgentPhaseClassifier.Classify(TriggerTool.Kimi, path, Now)?.Detail;
            string? english;
            using (Localization.L10n.English()) english = AgentPhaseClassifier.Classify(TriggerTool.Kimi, path, Now)?.Detail;
            if (chinese != "正在重连 · 第 3/10 次" || english != "Reconnecting · attempt 3 of 10")
                throw new Exception($"each language reads its own detail, got '{chinese}' and '{english}'");
        }
        finally
        {
            Localization.L10n.Current = language;
            File.Delete(path);
        }
    }

    private static string Transcript(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-phase-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines));
        return path;
    }

    private static void Expect(AgentPhase? actual, AgentPhase expected)
    {
        if (actual != expected) throw new Exception($"expected {expected}, got {actual?.ToString() ?? "nothing"}");
    }

    private static void Claude(AgentPhase expected, params string[] lines) =>
        Expect(AgentPhaseClassifier.Claude(lines, Now).Phase, expected);

    private static void Codex(AgentPhase expected, params string[] lines) =>
        Expect(AgentPhaseClassifier.Codex(lines, Now).Phase, expected);

    private static void Combined(AgentPhase expected, ActivityState state, AgentPhase? transcript, bool quotaExhausted = false)
    {
        PhaseReading? reading = transcript is { } phase ? new PhaseReading(phase, null, Now) : null;
        Expect(AgentPhases.Combine(state, reading, quotaExhausted), expected);
    }

    private static void Expect(AgentPhase actual, AgentPhase expected)
    {
        if (actual != expected) throw new Exception($"expected {expected}, got {actual}");
    }

    private static string Line(object value, int secondsAgo)
    {
        var json = JsonSerializer.SerializeToNode(value)!.AsObject();
        json["timestamp"] = Now.AddSeconds(-secondsAgo).ToString("o");
        return json.ToJsonString();
    }

    private static string UserText(string text, int ago) =>
        Line(new { type = "user", message = new { role = "user", content = text } }, ago);

    private static string ToolResult(int ago, string id) =>
        Line(new { type = "user", message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = id } } } }, ago);

    private static string ToolUse(string name, int ago, string id) =>
        Line(new { type = "assistant", message = new { stop_reason = "tool_use", content = new[] { new { type = "tool_use", name, id } } } }, ago);

    private static string Thinking(int ago) =>
        Line(new { type = "assistant", message = new { stop_reason = "tool_use", content = new[] { new { type = "thinking" } } } }, ago);

    private static string Text(string stop, int ago) =>
        Line(new { type = "assistant", message = new { stop_reason = stop, content = new[] { new { type = "text" } } } }, ago);

    private static string Event(string kind, int ago) => Line(new { type = "event_msg", payload = new { type = kind } }, ago);

    private static string Item(string kind, int ago) => Line(new { type = "response_item", payload = new { type = kind } }, ago);

    private static string Call(string kind, string name, int ago, string callId) =>
        Line(new { type = "response_item", payload = new { type = kind, name, call_id = callId } }, ago);

    private static string Output(int ago, string callId) =>
        Line(new { type = "response_item", payload = new { type = "function_call_output", call_id = callId } }, ago);
}
