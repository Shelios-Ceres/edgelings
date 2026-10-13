using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Pins Grok's live phases from its ACP update frames: pending tool calls by
/// their x.ai tool name, parallel calls, MCP computer use through use_tool,
/// thought and message chunks, retries and the turn's close. Synthetic
/// frames only, mirroring the shapes observed in real logs on 2026-10-11.
public static class GrokPhaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a pending terminal command is running", () => Phase(AgentPhase.Running,
                Update("agent_thought_chunk", 8), Call("a", "run_terminal_command", 6))),
            ("a completed call reads as thinking", () => Phase(AgentPhase.Thinking,
                Call("a", "read_file", 6), Result("a", 5, "completed"))),
            ("a parallel slow edit is still writing", () => Phase(AgentPhase.Writing,
                Call("e", "search_replace", 6), Call("r", "read_file", 6), Result("r", 4, "completed"))),
            ("an in-progress update keeps the call open", () => Phase(AgentPhase.Searching,
                Call("g", "grep", 6), Result("g", 5, "in_progress"))),
            ("computer use through use_tool operates apps", () => Phase(AgentPhase.Controlling,
                Call("c", "use_tool", 4, new JsonObject { ["tool_name"] = "kimi-cu__click" }))),
            ("thought chunks are thinking", () => Phase(AgentPhase.Thinking,
                Update("user_message_chunk", 9), Update("agent_thought_chunk", 3))),
            ("message chunks are replying", () => Phase(AgentPhase.Streaming,
                Update("agent_thought_chunk", 5), Update("agent_message_chunk", 3))),
            ("turn_completed is finished", () => Phase(AgentPhase.Finished,
                Update("agent_message_chunk", 4), Update("turn_completed", 3))),
            ("retrying the API is network trouble", () => Phase(AgentPhase.NetworkTrouble,
                Update("agent_thought_chunk", 9), Update("retry_state", 2))),
            ("bookkeeping after the close keeps it finished", () => Phase(AgentPhase.Finished,
                Update("turn_completed", 5), Update("background_tasks", 4), Update("session_recap", 3))),
            ("the tool name is the detail", () => Detail("read_file",
                Call("a", "read_file", 2))),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS grok phase: {name}");
        }
        Console.WriteLine("GrokPhaseTests GREEN");
    }

    private static void Phase(AgentPhase expected, params string[] lines)
    {
        var actual = AgentPhaseClassifier.Grok(lines, Now).Phase;
        if (actual != expected) throw new Exception($"expected {expected}, got {actual}");
    }

    private static void Detail(string expected, params string[] lines)
    {
        var actual = AgentPhaseClassifier.Grok(lines, Now).Detail;
        if (actual != expected) throw new Exception($"expected {expected}, got {actual}");
    }

    private static string Update(string kind, int ago, JsonObject? fields = null)
    {
        var update = fields ?? new JsonObject();
        update["sessionUpdate"] = kind;
        return new JsonObject
        {
            ["timestamp"] = Now.AddSeconds(-ago).ToUnixTimeMilliseconds() / 1000.0,
            ["method"] = "session/update",
            ["params"] = new JsonObject { ["sessionId"] = "s", ["update"] = update },
        }.ToJsonString();
    }

    private static string Call(string id, string tool, int ago, JsonObject? rawInput = null) => Update("tool_call", ago, new JsonObject
    {
        ["toolCallId"] = id,
        ["_meta"] = new JsonObject { ["x.ai/tool"] = new JsonObject { ["name"] = tool } },
        ["rawInput"] = rawInput ?? new JsonObject(),
    });

    private static string Result(string id, int ago, string status) => Update("tool_call_update", ago, new JsonObject
    {
        ["toolCallId"] = id,
        ["status"] = status,
    });
}
