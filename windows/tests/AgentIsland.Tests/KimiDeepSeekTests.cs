using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;

namespace AgentIsland.Tests;

/// Pins the Kimi Code and DeepSeek Harness readers: turn boundaries with the
/// bookkeeping written after them, live phases with parallel tool calls,
/// token records, and DeepSeek's zstd-framed log. Synthetic events only,
/// mirroring the shapes observed in real logs on 2026-10-11.
public static class KimiDeepSeekTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("kimi turn: the close survives bookkeeping", () => KimiDone(true,
                Loop("tool.result", 9, new { toolCallId = "a" }), Kimi("agent.turn.ended", 8, new { outcome = "done" }),
                Kimi("turn.ended", 8, new { reason = "completed" }), Kimi("token_counting.turn_recorded", 7),
                Kimi("prompt.completed", 7, new { reason = "completed" }), Kimi("config.update", 6),
                Message("assistant", 6))),
            ("kimi turn: a user message reopens it", () => KimiDone(false,
                Kimi("turn.ended", 8, new { reason = "completed" }), Kimi("config.update", 6), Message("user", 5))),
            ("kimi turn: a running loop is open", () => KimiDone(false,
                Kimi("agent.turn.started", 4), Loop("step.begin", 3))),
            ("deepseek turn: the close survives bookkeeping", () => DeepSeekDone(true,
                DeepSeek("assistant/message", 9), DeepSeek("turn/end", 8), DeepSeek("session/end-seed", 7))),
            ("deepseek turn: a spliced inbox message reopens it", () => DeepSeekDone(false,
                DeepSeek("turn/end", 8), DeepSeek("agent/inbox/spliced", 7))),
            ("kimi phase: a pending Bash call is running", () => KimiPhase(AgentPhase.Running,
                Loop("step.begin", 6), Loop("tool.call", 5, new { name = "Bash", toolCallId = "b" }))),
            ("kimi phase: a returned call reads as thinking", () => KimiPhase(AgentPhase.Thinking,
                Loop("tool.call", 5, new { name = "Bash", toolCallId = "b" }), Loop("tool.result", 3, new { toolCallId = "b" }))),
            ("kimi phase: a parallel slow call is still running", () => KimiPhase(AgentPhase.Writing,
                Loop("tool.call", 6, new { name = "Edit", toolCallId = "a" }), Loop("tool.call", 6, new { name = "Read", toolCallId = "r" }),
                Loop("tool.result", 4, new { toolCallId = "r" }))),
            ("kimi phase: think parts are thinking", () => KimiPhase(AgentPhase.Thinking,
                Loop("content.part", 2, new { part = new { type = "think" } }))),
            ("kimi phase: text parts are streaming", () => KimiPhase(AgentPhase.Streaming,
                Loop("content.part", 2, new { part = new { type = "text" } }))),
            ("kimi phase: computer use is controlling", () => KimiPhase(AgentPhase.Controlling,
                Loop("tool.call", 2, new { name = "click", toolCallId = "c" }))),
            ("kimi phase: a completed turn is finished", () => KimiPhase(AgentPhase.Finished,
                Kimi("turn.ended", 30, new { reason = "completed" }), Kimi("token_counting.turn_recorded", 29))),
            ("kimi phase: a cancelled turn is idle", () => KimiPhase(AgentPhase.Idle,
                Loop("tool.call", 9, new { name = "Bash", toolCallId = "b" }), Kimi("turn.ended", 8, new { reason = "cancelled" }))),
            ("deepseek phase: a pending pwsh call is running", () => DeepSeekPhase(AgentPhase.Running,
                DeepSeek("step/start", 6), DeepSeek("tool/call", 5, new { name = "pwsh", callId = "p" }))),
            ("deepseek phase: a returned call reads as thinking", () => DeepSeekPhase(AgentPhase.Thinking,
                DeepSeek("tool/call", 5, new { name = "pwsh", callId = "p" }),
                DeepSeek("tool/result", 3, new { message = new { toolCallId = "p" } }))),
            ("deepseek phase: edit is writing", () => DeepSeekPhase(AgentPhase.Writing,
                DeepSeek("tool/call", 2, new { name = "edit", callId = "e" }))),
            ("deepseek phase: ask_user_question is asking", () => DeepSeekPhase(AgentPhase.Asking,
                DeepSeek("tool/call", 40, new { name = "ask_user_question", callId = "q" }))),
            ("deepseek phase: turn end is finished", () => DeepSeekPhase(AgentPhase.Finished,
                DeepSeek("step/end", 31), DeepSeek("turn/end", 30, new { reason = new { kind = "completed" } }))),
            ("kimi tokens: a usage record splits input and cache", KimiTokens),
            ("deepseek tokens: usage follows the header's model", DeepSeekTokens),
            ("zstd: frames decode, a torn tail frame is dropped", ZstdFrames),
            ("zstd: a window cut mid-frame starts at the next frame", ZstdWindow),
            ("deepseek cwd: the workspace folder decodes", () =>
                Expect(SessionScanner.DeepSeekCwd("--C-Users-me-proj--"), @"C:\Users\me\proj")),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("KimiDeepSeekTests GREEN");
    }

    private static void KimiTokens()
    {
        var line = Kimi("usage.record", 5, new
        {
            model = "kimi-code/kimi-for-coding",
            usage = new { inputOther = 120, output = 40, inputCacheRead = 9000, inputCacheCreation = 7 },
        });
        var parsed = KimiLogReader.Parse(line) ?? throw new Exception("no event");
        Expect(parsed.Model, "kimi-code/kimi-for-coding");
        Expect(parsed.InputTokens, 120L);
        Expect(parsed.OutputTokens, 40L);
        Expect(parsed.CacheReadTokens, 9000L);
        Expect(parsed.CacheCreationTokens, 7L);
        Expect(parsed.BillableTokens, 167L);
        Expect(parsed.Timestamp, Now.AddSeconds(-5));
    }

    private static void DeepSeekTokens()
    {
        var events = DeepSeekLogReader.ParseLines(new[]
        {
            DeepSeek("request/header", 9, new { header = new { config = new { model = "deepseek-flash" } } }),
            DeepSeek("assistant/message", 8, new
            {
                usage = new { inputTokens = 1434, outputTokens = 248, cacheReadTokens = 11264, cacheWriteTokens = 0, totalTokens = 12946 },
            }),
            DeepSeek("tool/call", 7, new { name = "pwsh", callId = "p" }),
        });
        Expect(events.Count, 1);
        Expect(events[0].Model, "deepseek-flash");
        Expect(events[0].WireTokens, 12946L);
        Expect(events[0].BillableTokens, 1682L);
    }

    private static void ZstdFrames()
    {
        var torn = Frame("line3\n");
        var data = Frame("line1\nline2\n").Concat(Frame("line2b\n")).Concat(torn[..(torn.Length / 2)]).ToArray();
        Expect(ZstdLog.Decode(data), "line1\nline2\nline2b\n");
    }

    private static void ZstdWindow()
    {
        var data = Frame("first\n").Concat(Frame("second\n")).ToArray();
        Expect(ZstdLog.Decode(data[3..]), "second\n");
    }

    private static byte[] Frame(string text)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static void KimiDone(bool expected, params string[] lines) =>
        Expect(SessionTurnState.Kimi(lines).IsDone, expected);

    private static void DeepSeekDone(bool expected, params string[] lines) =>
        Expect(SessionTurnState.DeepSeek(lines).IsDone, expected);

    private static void KimiPhase(AgentPhase expected, params string[] lines) =>
        Expect(AgentPhaseClassifier.Kimi(lines, Now).Phase, expected);

    private static void DeepSeekPhase(AgentPhase expected, params string[] lines) =>
        Expect(AgentPhaseClassifier.DeepSeek(lines, Now).Phase, expected);

    private static void Expect<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}, got {actual}");
    }

    private static long Ms(int secondsAgo) => Now.AddSeconds(-secondsAgo).ToUnixTimeMilliseconds();

    /// A top-level Kimi wire event; extra fields merge into the line.
    private static string Kimi(string type, int ago, object? fields = null)
    {
        var json = fields is null ? new System.Text.Json.Nodes.JsonObject() : JsonSerializer.SerializeToNode(fields)!.AsObject();
        json["type"] = type;
        json["time"] = Ms(ago);
        return json.ToJsonString();
    }

    private static string Loop(string eventType, int ago, object? fields = null)
    {
        var loop = fields is null ? new System.Text.Json.Nodes.JsonObject() : JsonSerializer.SerializeToNode(fields)!.AsObject();
        loop["type"] = eventType;
        return new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "context.append_loop_event",
            ["time"] = Ms(ago),
            ["event"] = loop,
        }.ToJsonString();
    }

    private static string Message(string role, int ago) =>
        Kimi("agent.message.appended", ago, new { kind = "event", message = new { message = new { role } } });

    private static string DeepSeek(string type, int ago, object? data = null) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = type,
            ["time"] = Ms(ago),
            ["seq"] = 1,
            ["data"] = data is null ? new System.Text.Json.Nodes.JsonObject() : JsonSerializer.SerializeToNode(data),
        }.ToJsonString();
}
