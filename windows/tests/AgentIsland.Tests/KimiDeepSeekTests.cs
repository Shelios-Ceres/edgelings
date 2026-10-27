using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using static AgentIsland.Tests.Check;

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
            ("kimi turn: reopening an old session leaves its close", KimiReopened),
            ("kimi turn: injected context, undo, fork and mode switches leave it closed", () => KimiDone(true,
                Kimi("prompt.aborted", 20), Kimi("context.undo", 19), Kimi("token_counting.truncated", 19),
                Kimi("forked", 18), Kimi("plan_mode.enter", 17), Kimi("plan_mode.cancel", 16),
                Kimi("changeNotifier.snapshot", 5), Kimi("context.append_message", 5, new
                {
                    message = new { role = "user", origin = new { kind = "injection", variant = "skills_change" } },
                }))),
            ("kimi turn: a mode switch inside a turn keeps it open", () => KimiDone(false,
                Kimi("turn.prompt", 9), Kimi("llm.request", 8), Kimi("plan_mode.enter", 7))),
            ("kimi turn: a compaction finished between turns leaves it closed", () => KimiDone(true,
                Kimi("prompt.completed", 300, new { reason = "completed" }), Kimi("full_compaction.begin", 200),
                Kimi("llm.request", 200), Kimi("usage.record", 110), Kimi("context.apply_compaction", 110),
                Kimi("token_counting.rebased", 110), Kimi("full_compaction.complete", 110))),
            ("kimi turn: a compaction still running, inside a turn or under a queued prompt is open", KimiCompactingOpen),
            ("kimi turn: a third lost connection is network trouble", KimiRetryTrouble),
            ("kimi turn: early retries, rate limits and recovered retries are not", KimiRetryQuiet),
            ("kimi phase: a retry reads as reconnecting", () => KimiPhase(AgentPhase.NetworkTrouble,
                Loop("step.begin", 6), Kimi("llm.request", 5),
                Kimi("turn.step.retrying", 4, Retry(1, "APIConnectionError", "Connection error.", 1000)))),
            ("deepseek turn: the close survives bookkeeping", () => DeepSeekDone(true,
                DeepSeek("assistant/message", 9), DeepSeek("turn/end", 8), DeepSeek("session/end-seed", 7))),
            ("deepseek turn: a spliced inbox message reopens it", () => DeepSeekDone(false,
                DeepSeek("turn/end", 8), DeepSeek("agent/inbox/spliced", 7))),
            ("deepseek turn: a record it does not know leaves the close", () => DeepSeekDone(true,
                DeepSeek("assistant/message", 9), DeepSeek("turn/end", 8), DeepSeek("session/resumed", 7), DeepSeek("session/rebind", 6))),
            ("deepseek turn: an open turn is dated by its newest line", DeepSeekOpenTurn),
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
            ("kimi names: its settings name the plan's models", KimiNames),
            ("kimi list: K2 is told by name, by id without a name, and a K3 name on a K2 id is not K2", KimiK2Rule),
            ("model rows: a cache-heavy model ranks by billable in one mode and by all tokens in the other", ModelRowsFollowMode),
            ("kimi prices: a connected provider's model prices by the provider's own id", KimiProviderPrices),
            ("deepseek tokens: usage follows the header's model", DeepSeekTokens),
            ("deepseek tokens: a framed log streams frame after frame, a torn tail frame is dropped", DeepSeekFramedLog),
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

    private static void KimiNames()
    {
        var names = KimiLogReader.ModelNames("""
            default_model = "kimi-code/kimi-for-coding"
            [secondary_model.models]
            "kimi-code/k3" = "a note, not a name"
            [models."kimi-code/kimi-for-coding"]
            model = "kimi-for-coding"
            display_name = "K2.8 Preview"
            [models."kimi-code/k3"] # the large context
            display_name = "K3"
            [models."kimi-code/blank"]
            display_name = ""
            [providers."managed:kimi-code"]
            display_name = "not a model"
            """);
        Expect(names.Count, 2);
        Expect(names["kimi-code/kimi-for-coding"], "K2.8 Preview");
        Expect(names["KIMI-CODE/K3"], "K3");
    }

    private const string ProviderConfig = """
        default_model = "deepseek/deepseek-flash"
        [providers.deepseek]
        type = "openai"
        base_url = "https://api.deepseek.com"
        api_key = "sk-fixture-not-a-real-key"
        [providers."managed:kimi-code"]
        type = "kimi"
        [models."deepseek/deepseek-flash"]
        provider = "deepseek"
        model = "deepseek-flash"
        display_name = "DeepSeek V4.1 Flash 1017"
        [models."deepseek/deepseek-v4-pro"]
        provider = "deepseek"
        model = "deepseek-v4-pro"
        display_name = "DeepSeek V4 Pro"
        [models."kimi-code/unpriced-sample"]
        provider = "managed:kimi-code"
        model = "unpriced-sample"
        display_name = "Sample"
        [models."kimi-code/k3"]
        provider = "elsewhere"
        model = "not-a-priced-id"
        """;

    private static void KimiProviderPrices()
    {
        var providers = KimiLogReader.ProviderModels(ProviderConfig);
        Expect(providers.Count, 3);
        Expect(providers["deepseek/deepseek-flash"], "deepseek-flash");
        Expect(providers["deepseek/deepseek-v4-pro"], "deepseek-v4-pro");
        Expect(providers.ContainsKey("kimi-code/unpriced-sample"), false);
        Expect(providers.Values.Any(v => v.Contains("sk-") || v.Contains("api.deepseek")), false);

        // Tuesday 07:00 UTC is DeepSeek's peak: flash 0.3 + 1.2 + 0.3 + 0.006, pro 1.32 + 3.96 + 1.32 + 0.044.
        var peak = new DateTimeOffset(2026, 10, 27, 7, 0, 0, TimeSpan.Zero);
        TokenEvent Event(string model) => new(TriggerTool.Kimi, peak, model, 1_000_000, 1_000_000, 1_000_000, 1_000_000);
        var events = KimiLogReader.PriceByProvider(
            new List<TokenEvent> { Event("deepseek/deepseek-flash"), Event("deepseek/deepseek-v4-pro"), Event("kimi-code/unpriced-sample"), Event("kimi-code/k3") },
            providers);
        Expect(string.Join(",", events.Select(e => e.Model)),
            "deepseek/deepseek-flash,deepseek/deepseek-v4-pro,kimi-code/unpriced-sample,kimi-code/k3");
        Expect(Math.Abs(events[0].Dollars - 1.806) < 1e-9, $"flash priced, got {events[0].Dollars}");
        Expect(Math.Abs(events[1].Dollars - 6.644) < 1e-9, $"pro priced, got {events[1].Dollars}");
        Expect(events[2].Dollars, 0.0);
        Expect(events[2].PricedAs, null);
        Expect(events[3].PricedAs, null);
        Expect(Math.Abs(events[3].Dollars - 21.3) < 1e-9, "a model that already has a price keeps it");
        Expect(string.Join(",", CostSummarizer.Summarize(events, peak.AddHours(1)).UnknownModels), "kimi-code/unpriced-sample");
        Expect(KimiLogReader.ModelNames(ProviderConfig)["deepseek/deepseek-flash"], "DeepSeek V4.1 Flash 1017");
    }

    private static void KimiK2Rule()
    {
        Expect(KimiLogReader.IsK2("kimi-code/kimi-for-coding", "K2.8 Preview"), true);
        Expect(KimiLogReader.IsK2("kimi-code/kimi-for-coding-highspeed", "K2.7 Code Highspeed"), true);
        Expect(KimiLogReader.IsK2("kimi-code/anything", "k2.9"), true);
        // Named K3 or later: listed, even on an id that used to be K2.
        Expect(KimiLogReader.IsK2("kimi-code/kimi-for-coding", "K3.1 Pro"), false);
        Expect(KimiLogReader.IsK2("kimi-code/k3", "K3"), false);
        Expect(KimiLogReader.IsK2("kimi-code/k3-256k", "K3 256k"), false);
        Expect(KimiLogReader.IsK2("deepseek/deepseek-flash", "DeepSeek V4.1 Flash 1017"), false);
        // No name: by id.
        Expect(KimiLogReader.IsK2("kimi-code/kimi-for-coding", null), true);
        Expect(KimiLogReader.IsK2("kimi-code/kimi-for-coding-highspeed", null), true);
        Expect(KimiLogReader.IsK2("kimi-k2.7-code", null), true);
        Expect(KimiLogReader.IsK2("kimi-code/kimi-k2.6", null), true);
        Expect(KimiLogReader.IsK2("kimi-code/k3", null), false);
        Expect(KimiLogReader.IsK2("kimi-k3.1-pro", null), false);
        Expect(KimiLogReader.IsK2("deepseek/deepseek-flash", null), false);
        Expect(KimiLogReader.IsK2("deepseek/kimi-k2-thinking", null), false);
    }

    private static void ModelRowsFollowMode()
    {
        // "cached" has the most tokens on the wire but the fewest billable ones.
        var models = new[]
        {
            new ModelSpend("cached", 900_000, 20_000, 1),
            new ModelSpend("fresh", 300_000, 250_000, 1),
            new ModelSpend("middle", 400_000, 100_000, 1),
        };
        Expect(string.Join(",", ModelSpend.Ranked(models, billable: true).Select(m => m.Model)), "fresh,middle,cached");
        Expect(string.Join(",", ModelSpend.Ranked(models, billable: false).Select(m => m.Model)), "cached,middle,fresh");
        // The top three picked from four differ by mode too.
        var four = models.Append(new ModelSpend("small", 50_000, 40_000, 1)).ToArray();
        Expect(string.Join(",", ModelSpend.Ranked(four, true).Take(3).Select(m => m.Model)), "fresh,middle,small");
        Expect(string.Join(",", ModelSpend.Ranked(four, false).Take(3).Select(m => m.Model)), "cached,middle,fresh");
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

    /// The log is a run of frames, one or two events each, and the writer may
    /// be partway through the last one.
    private static void DeepSeekFramedLog()
    {
        static string Step(int ago, long input) => DeepSeek("assistant/message", ago, new
        {
            usage = new { inputTokens = input, outputTokens = 10, cacheReadTokens = 0, cacheWriteTokens = 0 },
        });
        var torn = Frame(Step(5, 400) + "\n");
        var data = Frame(DeepSeek("request/header", 9, new { header = new { config = new { model = "deepseek-flash" } } }) + "\n" + Step(8, 100) + "\n")
            .Concat(Frame(Step(7, 200) + "\n"))
            .Concat(Frame(DeepSeek("tool/call", 7, new { name = "pwsh", callId = "p" }) + "\n" + Step(6, 300) + "\n"))
            .Concat(torn[..(torn.Length / 2)])
            .ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-deepseek-{Guid.NewGuid():N}.jsonl.zstd");
        try
        {
            File.WriteAllBytes(path, data);
            var events = DeepSeekLogReader.ParseFile(path);
            Expect(events.Count, 3);
            Expect(events.Sum(e => e.InputTokens), 600L);
            Expect(events.All(e => e.Model == "deepseek-flash"), "every step follows the first frame's header");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
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

    /// Kimi Code retries a failed step up to ten times, logging each one
    /// (failedAttempt, delayMs, the SDK's error name and message) before the
    /// request that retries it.
    private static void KimiRetryTrouble()
    {
        var status = SessionTurnState.Kimi(new[]
        {
            Kimi("agent.turn.started", 30),
            Kimi("turn.step.retrying", 20, Retry(3, "APIConnectionError", "terminated", 4000)),
            Kimi("llm.request", 16),
        });
        Expect(status.IsDone, false);
        Expect(status.RetryingUntil, Now.AddSeconds(-20) + TimeSpan.FromMilliseconds(4000) + TimeSpan.FromSeconds(60));
        var timeout = SessionTurnState.Kimi(new[] { Kimi("turn.step.retrying", 5, Retry(4, "APITimeoutError", "Request timed out.", 0)) });
        Expect(timeout.RetryingUntil, Now.AddSeconds(-5) + TimeSpan.FromSeconds(60));
    }

    /// Opening a session in Kimi Code's desktop app logs its binding and MCP
    /// tools again, a day after its last turn: the turn stays closed, dated
    /// by its close, so the attention window has long passed.
    private static void KimiReopened()
    {
        var status = SessionTurnState.Kimi(new[]
        {
            Kimi("turn.ended", 86_400, new { reason = "completed" }), Kimi("token_counting.turn_recorded", 86_400),
            Kimi("prompt.completed", 86_400, new { reason = "completed" }),
            Kimi("metadata", 5), Kimi("runtime.set_binding", 5), Kimi("mcp.tools_discovered", 5),
            Kimi("permission.set_mode", 5), Kimi("profile.bind", 5), Kimi("mcp.tools_discovered", 4),
        });
        Expect(status.IsDone, true);
        Expect(status.ActivityDate, Now.AddSeconds(-86_400));
    }

    private static void KimiCompactingOpen()
    {
        KimiDone(false, Kimi("prompt.completed", 300), Kimi("full_compaction.begin", 20), Kimi("llm.request", 20));
        KimiDone(false, Kimi("turn.prompt", 300), Loop("step.end", 200), Kimi("full_compaction.begin", 200),
            Kimi("llm.request", 200), Kimi("context.apply_compaction", 110), Kimi("full_compaction.complete", 110));
        KimiDone(false, Kimi("prompt.completed", 300), Kimi("full_compaction.begin", 200), Kimi("llm.request", 200),
            Kimi("config.update", 150), Kimi("prompt.accepted", 150), Kimi("context.apply_compaction", 110),
            Kimi("full_compaction.complete", 110));
    }

    private static void KimiRetryQuiet()
    {
        Expect(SessionTurnState.Kimi(new[] { Kimi("turn.step.retrying", 5, Retry(2, "APIConnectionError", "Connection error.", 1000)) })
            .RetryingUntil, null);
        Expect(SessionTurnState.Kimi(new[]
            {
                Kimi("turn.step.retrying", 5, Retry(4, "APIProviderRateLimitError", "429 The engine is currently overloaded", 1000)),
            }).RetryingUntil, null);
        Expect(SessionTurnState.Kimi(new[]
            {
                Kimi("turn.step.retrying", 9, Retry(5, "APIConnectionError", "Connection error.", 1000)),
                Loop("content.part", 3, new { part = new { type = "text" } }),
            }).RetryingUntil, null);
    }

    private static object Retry(int failed, string name, string message, int delayMs) => new
    {
        failedAttempt = failed,
        nextAttempt = failed + 1,
        maxAttempts = 10,
        delayMs,
        errorName = name,
        errorMessage = message,
    };

    private static void KimiDone(bool expected, params string[] lines) =>
        Expect(SessionTurnState.Kimi(lines).IsDone, expected);

    private static void DeepSeekOpenTurn()
    {
        var turn = SessionTurnState.DeepSeek(new[]
        {
            DeepSeek("turn/end", 30), DeepSeek("turn/start", 20), DeepSeek("step/start", 19), DeepSeek("step/progress", 4),
        });
        Expect(!turn.IsDone, "a step after the close opens the next turn");
        Expect(turn.ActivityDate == Now.AddSeconds(-4) && turn.Key?.EndsWith(":step/progress", StringComparison.Ordinal) == true,
            $"dated by its newest line, got {turn.Key}");
    }

    private static void DeepSeekDone(bool expected, params string[] lines) =>
        Expect(SessionTurnState.DeepSeek(lines).IsDone, expected);

    private static void KimiPhase(AgentPhase expected, params string[] lines) =>
        Expect(AgentPhaseClassifier.Kimi(lines, Now).Phase, expected);

    private static void DeepSeekPhase(AgentPhase expected, params string[] lines) =>
        Expect(AgentPhaseClassifier.DeepSeek(lines, Now).Phase, expected);

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
