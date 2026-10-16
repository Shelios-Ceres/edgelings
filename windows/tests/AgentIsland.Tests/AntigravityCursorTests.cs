using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Pins how Antigravity and Cursor runs read: Antigravity's step records
/// (written once each step is done) and what its language server says about
/// a run, and a Cursor tool still running. Synthetic records mirroring the
/// shapes observed on 2026-10-16: transcript_full.jsonl steps with
/// source/type/status/tool_calls, GetAllCascadeTrajectories' summaries,
/// and Cursor bubbles' toolFormerData.
public static class AntigravityCursorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("antigravity turn: a call with nothing after it is open", () => AntigravityDone(false,
                Step("USER_EXPLICIT", "USER_INPUT", 60), Step("MODEL", "PLANNER_RESPONSE", 50, calls: new[] { "run_command" }))),
            ("antigravity turn: an answer without calls closes it", () => AntigravityDone(true,
                Step("MODEL", "GENERIC", 20), Step("MODEL", "PLANNER_RESPONSE", 10, content: "Done."))),
            ("antigravity phase: a pending run_command is running", () => AntigravityPhase(AgentPhase.Running,
                Step("MODEL", "PLANNER_RESPONSE", 50, calls: new[] { "view_file", "run_command" }))),
            ("antigravity phase: view_file is reading", () => AntigravityPhase(AgentPhase.Reading,
                Step("MODEL", "PLANNER_RESPONSE", 5, calls: new[] { "view_file" }))),
            ("antigravity phase: a tool's result is the model at work", () => AntigravityPhase(AgentPhase.Thinking,
                Step("MODEL", "PLANNER_RESPONSE", 9, calls: new[] { "write_to_file" }), Step("MODEL", "GENERIC", 4))),
            ("antigravity phase: notify_user asks", () => AntigravityPhase(AgentPhase.Asking,
                Step("MODEL", "PLANNER_RESPONSE", 5, calls: new[] { "notify_user" }))),
            ("antigravity phase: an answer is finished, system notes skipped", () => AntigravityPhase(AgentPhase.Finished,
                Step("MODEL", "PLANNER_RESPONSE", 9, content: "All set."), Step("SYSTEM_SDK", "EPHEMERAL_MESSAGE", 8))),
            ("antigravity phase: a prompt is thinking", () => AntigravityPhase(AgentPhase.Thinking,
                Step("USER_EXPLICIT", "USER_INPUT", 3))),
            ("antigravity server: run statuses parse by cascade", ServerParse),
            ("antigravity server: a running cascade works through silence", () =>
                Expect(AntigravityLiveCascades.Apply(ActivityState.Idle, Run("CASCADE_RUN_STATUS_RUNNING", null)), ActivityState.Working)),
            ("antigravity server: an idle cascade mid-turn waits on the user", () =>
                Expect(AntigravityLiveCascades.Apply(ActivityState.Working, Run("CASCADE_RUN_STATUS_IDLE", "EXECUTOR_TERMINATION_REASON_HALTED_STEP")),
                    ActivityState.NeedsYou)),
            ("antigravity server: a cancelled run owes nothing", () =>
                Expect(AntigravityLiveCascades.Apply(ActivityState.Working, Run("CASCADE_RUN_STATUS_IDLE", "EXECUTOR_TERMINATION_REASON_USER_CANCELED")),
                    ActivityState.Idle)),
            ("antigravity server: an answered run leaves the transcript's reading", () =>
                Expect(AntigravityLiveCascades.Apply(ActivityState.NeedsYou, Run("CASCADE_RUN_STATUS_IDLE", "EXECUTOR_TERMINATION_REASON_NO_TOOL_CALL")),
                    ActivityState.NeedsYou)),
            ("cursor turn: a tool still running is not an answer", () => CursorDone(false, Bubble(2, "loading"))),
            ("cursor turn: a finished tool is", () => CursorDone(true, Bubble(2, "completed"))),
            ("cursor turn: plain assistant text is an answer, a user bubble is not", () =>
            {
                CursorDone(true, Bubble(2, null));
                CursorDone(false, Bubble(1, null));
            }),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("AntigravityCursorTests GREEN");
    }

    /// The shape Antigravity's desktop server returned on 2026-10-16.
    private static void ServerParse()
    {
        using var doc = JsonDocument.Parse("""
            {"trajectorySummaries":{
              "ead2f8c6-4e9e-44c0-a83c-e5a0fc60bcdb":{"summary":"x","stepCount":71,"status":"CASCADE_RUN_STATUS_RUNNING",
                "lastModifiedTime":"2026-10-16T07:40:43.803919800Z","trajectoryType":"CORTEX_TRAJECTORY_TYPE_CASCADE"},
              "771b6d91-eb51-47e7-a04b-54799e957d56":{"summary":"y","stepCount":64,"status":"CASCADE_RUN_STATUS_IDLE",
                "latestExecutorTerminationReason":"EXECUTOR_TERMINATION_REASON_NO_TOOL_CALL"},
              "broken":{"stepCount":3}}}
            """);
        var runs = AntigravityLiveCascades.Parse(doc.RootElement).ToDictionary(r => r.Id, r => r.Run);
        Expect(runs.Count, 2);
        Expect(runs["ead2f8c6-4e9e-44c0-a83c-e5a0fc60bcdb"], Run("CASCADE_RUN_STATUS_RUNNING", null));
        Expect(runs["771b6d91-eb51-47e7-a04b-54799e957d56"], Run("CASCADE_RUN_STATUS_IDLE", "EXECUTOR_TERMINATION_REASON_NO_TOOL_CALL"));
        using var empty = JsonDocument.Parse("{}");
        Expect(AntigravityLiveCascades.Parse(empty.RootElement).Count(), 0);
    }

    private static AntigravityLiveCascades.Run Run(string status, string? stopped) => new(status, stopped);

    private static void AntigravityDone(bool expected, params string[] lines) =>
        Expect(SessionTurnState.Antigravity(lines).IsDone, expected);

    private static void AntigravityPhase(AgentPhase expected, params string[] lines) =>
        Expect(AgentPhaseClassifier.Antigravity(lines, Now).Phase, expected);

    private static void CursorDone(bool expected, string bubble) =>
        Expect(SessionTurnState.Cursor(new[] { bubble }).IsDone, expected);

    /// One transcript_full.jsonl step, done `ago` seconds before Now.
    private static string Step(string source, string type, int ago, string[]? calls = null, string? content = null)
    {
        var step = new JsonObject
        {
            ["step_index"] = 1,
            ["source"] = source,
            ["type"] = type,
            ["status"] = "DONE",
            ["created_at"] = Now.AddSeconds(-ago).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
        if (content is not null) step["content"] = content;
        if (calls is not null)
        {
            step["tool_calls"] = new JsonArray(calls.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["args"] = new JsonObject() }).ToArray());
        }
        return step.ToJsonString();
    }

    /// A Cursor bubble: type 1 user, 2 assistant; a tool's status if any.
    private static string Bubble(int type, string? toolStatus)
    {
        var bubble = new JsonObject
        {
            ["type"] = type,
            ["bubbleId"] = "b1",
            ["createdAt"] = Now.AddSeconds(-40).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
        if (toolStatus is not null) bubble["toolFormerData"] = new JsonObject { ["name"] = "run_terminal_command_v2", ["status"] = toolStatus };
        return bubble.ToJsonString();
    }

    private static void Expect<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}, got {actual}");
    }
}
