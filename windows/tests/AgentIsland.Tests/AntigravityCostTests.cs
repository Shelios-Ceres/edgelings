using System.Text;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Antigravity's cost from its conversation transcripts: the step parser, the
/// model each step is matched to by its three counts, the price a model id
/// trims to, and Gemini's prices that change on a date.
public static class AntigravityCostTests
{
    private const string Conversation = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a model step carries its counts and time", StepFields),
            ("a zero count is left out of a step and still reads as zero", StepZeroFields),
            ("steps with no model call or no tokens are skipped", StepsSkipped),
            ("a file counts each step once, under its conversation", FileOnce),
            ("a step takes the model whose generation has its three counts", MatchOnCounts),
            ("a step nobody names is unpriced under a placeholder", PlaceholderUnpriced),
            ("two models with the same counts leave the step unnamed", AmbiguousCounts),
            ("a conversation is asked about once per length", AskedOncePerLength),
            ("no server running leaves the question for next time", NoServerNotRemembered),
            ("the models survive a restart", ModelsSurviveRestart),
            ("a model id trims to its longest price key", FamilyTrim),
            ("Gemini 3.8 Flash changes price at midnight Pacific on 2027-01-01", FlashDated),
            ("Gemini 4 Argon changes price at midnight Pacific on 2026-12-01", ArgonDated),
            ("Gemini 4 Carbon, Neon, Hydrogen and Flash-Lite price at their published rates", NewGeminiModels),
            ("an unnamed step reads in the language of the page that shows it", UnnamedInShownLanguage),
            ("a step carrying an inline model resolves without asking a server", InlineModelNamed),
            ("SYSTEM CHECKPOINT compaction steps with tokens are counted", CheckpointCounted),
            ("distinct model calls reusing a step_index are not dropped", ReusedStepIndexPreserved),
            ("Orchestrator Neon, Argon and Hydrogen worker aliases price and name cleanly", OrchestratorAliases),
        };
        try
        {
            foreach (var (name, test) in tests)
            {
                test();
                Console.WriteLine($"PASS antigravity cost: {name}");
            }
        }
        finally
        {
            foreach (var map in TempMaps) File.Delete(map);
        }
        Console.WriteLine("AntigravityCostTests GREEN");
    }

    private static string Step(int index, string created, long? input, long? cacheRead, long? output, string source = "MODEL", string type = "PLANNER_RESPONSE", string? model = null)
    {
        var fields = new List<string>
        {
            $"\"step_index\":{index}", $"\"source\":\"{source}\"", $"\"type\":\"{type}\"", "\"status\":\"DONE\"", $"\"created_at\":\"{created}\"",
        };
        if (input is { } i) fields.Add($"\"input_tokens\":{i}");
        if (cacheRead is { } c) fields.Add($"\"cache_read_tokens\":{c}");
        if (output is { } o) fields.Add($"\"output_tokens\":{o}");
        if (model is { Length: > 0 } m) fields.Add($"\"model\":\"{m}\"");
        fields.Add("\"thinking\":\"hmm\"");
        return "{" + string.Join(",", fields) + "}";
    }

    private static TokenEvent Planned(int index, long input, long cacheRead, long output) =>
        AntigravityLogReader.Parse(Step(index, "2026-10-26T12:00:00Z", input, cacheRead, output), Conversation)!;

    /// A server's GetCascadeTrajectoryGeneratorMetadata reply: counts as strings, zero counts left out.
    private static byte[] Reply(params (string Model, long Input, long CacheRead, long Output)[] generations)
    {
        string Counts(long input, long cacheRead, long output)
        {
            var fields = new List<string>();
            if (input != 0) fields.Add($"\"inputTokens\":\"{input}\"");
            if (cacheRead != 0) fields.Add($"\"cacheReadTokens\":\"{cacheRead}\"");
            if (output != 0) fields.Add($"\"outputTokens\":\"{output}\"");
            return string.Join(",", fields);
        }
        var entries = generations.Select(g =>
            $"{{\"chatModel\":{{\"responseModel\":\"{g.Model}\",\"usage\":{{{Counts(g.Input, g.CacheRead, g.Output)}}}}}}}");
        return Encoding.UTF8.GetBytes($"{{\"generatorMetadata\":[{string.Join(",", entries)}]}}");
    }

    private static readonly List<string> TempMaps = new();

    private static string TempMap()
    {
        var path = Path.Combine(Path.GetTempPath(), "island-ag-" + Guid.NewGuid().ToString("N") + ".json");
        TempMaps.Add(path);
        return path;
    }

    private static void StepFields()
    {
        var step = AntigravityLogReader.Parse(Step(7, "2026-10-19T02:55:24Z", 9557, 34979, 1003), Conversation)!;
        Expect(step.InputTokens, 9557L);
        Expect(step.CacheReadTokens, 34979L);
        Expect(step.OutputTokens, 1003L);
        Expect(step.CacheCreationTokens, 0L);
        Expect(step.Timestamp, new DateTimeOffset(2026, 10, 19, 2, 55, 24, TimeSpan.Zero));
        Expect(step.ResponseKey, $"{Conversation}|7");
        Expect(step.Provider, TriggerTool.Antigravity);
    }

    private static void StepZeroFields()
    {
        var step = AntigravityLogReader.Parse(Step(2, "2026-10-19T02:54:55Z", 27596, null, 495), Conversation)!;
        Expect(step.CacheReadTokens, 0L);
        Expect(step.InputTokens, 27596L);
        var cached = AntigravityLogReader.Parse(Step(3, "2026-10-19T02:54:56Z", null, 5000, 12), Conversation)!;
        Expect(cached.InputTokens, 0L);
        Expect(cached.CacheReadTokens, 5000L);
    }

    private static void StepsSkipped()
    {
        Expect(AntigravityLogReader.Parse(Step(1, "2026-10-19T02:54:55Z", 10, 0, 10, source: "USER_EXPLICIT", type: "USER_INPUT"), Conversation) is null, "a user step");
        Expect(AntigravityLogReader.Parse(Step(2, "2026-10-19T02:54:55Z", 10, 0, 10, type: "RUN_COMMAND"), Conversation) is null, "a tool step");
        Expect(AntigravityLogReader.Parse(Step(3, "2026-10-19T02:54:55Z", null, null, null), Conversation) is null, "a step with no tokens");
        Expect(AntigravityLogReader.Parse("{\"PLANNER_RESPONSE\": broken", Conversation) is null, "a broken line");
        Expect(AntigravityLogReader.Parse(Step(4, "not a time", 10, 0, 10), Conversation) is null, "a step with no time");
    }

    private static void FileOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "island-ag-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "brain", Conversation, ".system_generated", "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var path = Path.Combine(logs, "transcript_full.jsonl");
            File.WriteAllLines(path, new[]
            {
                Step(1, "2026-10-26T12:00:00Z", 10, 0, 5, source: "USER_EXPLICIT", type: "USER_INPUT"),
                Step(2, "2026-10-26T12:00:01Z", 100, 0, 7),
                Step(2, "2026-10-26T12:00:01Z", 100, 0, 7),
                Step(5, "2026-10-26T12:00:09Z", 40, 300, 9),
            });
            var steps = AntigravityLogReader.ParseFile(path);
            Expect(steps.Count, 2);
            Expect(steps.All(step => step.ResponseKey!.StartsWith(Conversation + "|")), "the conversation is its folder's name");
            Expect(steps.Sum(step => step.InputTokens), 140L);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void MatchOnCounts()
    {
        var map = new AntigravityModelMap(TempMap());
        var steps = new List<TokenEvent> { Planned(2, 27596, 0, 495), Planned(5, 9557, 34979, 1003) };
        var named = map.Resolve(steps, _ => new AntigravityModelMap.Generations(true, Reply(
            ("gemini-3.8-flash-n", 27596, 0, 495), ("gemini-4-argon-trial", 9557, 34979, 1003), ("gemini-4-argon-trial", 1, 2, 3))));
        Expect(named[0].Model, "gemini-3.8-flash");
        Expect(named[1].Model, "gemini-4-argon");
        Expect(named[0].Dollars > 0 && named[1].Dollars > 0, "both are priced");
    }

    private static void PlaceholderUnpriced()
    {
        var map = new AntigravityModelMap(TempMap());
        var steps = new List<TokenEvent> { Planned(2, 27596, 0, 495), Planned(5, 9557, 34979, 1003) };
        var named = map.Resolve(steps, _ => new AntigravityModelMap.Generations(true, Reply(("gemini-4-argon-trial", 9557, 34979, 1003))));
        Expect(named[0].Model, "antigravity-unknown");
        Expect(named[0].Dollars, 0.0);
        Expect(!Pricing.IsKnown(named[0].Model), "the placeholder has no price");
        Expect(named[0].InputTokens, 27596L);
        Expect(named[1].Model, "gemini-4-argon");
        var refused = new AntigravityModelMap(TempMap()).Resolve(steps, _ => new AntigravityModelMap.Generations(true, null));
        Expect(refused.All(step => step.Model == "antigravity-unknown"), "a server that refuses names nothing");
    }

    private static void AmbiguousCounts()
    {
        var map = new AntigravityModelMap(TempMap());
        var named = map.Resolve(new List<TokenEvent> { Planned(2, 100, 0, 5) },
            _ => new AntigravityModelMap.Generations(true, Reply(("gemini-4-argon-trial", 100, 0, 5), ("gemini-3.8-flash-n", 100, 0, 5))));
        Expect(named[0].Model, "antigravity-unknown");
    }

    private static void AskedOncePerLength()
    {
        var map = new AntigravityModelMap(TempMap());
        var asked = 0;
        AntigravityModelMap.Generations Ask(string conversation)
        {
            asked++;
            Expect(conversation, Conversation);
            return new AntigravityModelMap.Generations(true, null);
        }
        var steps = new List<TokenEvent> { Planned(2, 100, 0, 5) };
        map.Resolve(steps, Ask);
        map.Resolve(steps, Ask);
        Expect(asked, 1);
        steps.Add(Planned(5, 200, 0, 6));
        map.Resolve(steps, Ask);
        Expect(asked, 2);
        map.Resolve(steps, Ask);
        Expect(asked, 2);
    }

    private static void NoServerNotRemembered()
    {
        var map = new AntigravityModelMap(TempMap());
        var asked = 0;
        var steps = new List<TokenEvent> { Planned(2, 100, 0, 5) };
        map.Resolve(steps, _ => { asked++; return new AntigravityModelMap.Generations(false, null); });
        var named = map.Resolve(steps, _ => { asked++; return new AntigravityModelMap.Generations(true, Reply(("gemini-4-argon-trial", 100, 0, 5))); });
        Expect(asked, 2);
        Expect(named[0].Model, "gemini-4-argon");
    }

    private static void ModelsSurviveRestart()
    {
        var path = TempMap();
        try
        {
            var steps = new List<TokenEvent> { Planned(2, 100, 0, 5) };
            new AntigravityModelMap(path).Resolve(steps, _ => new AntigravityModelMap.Generations(true, Reply(("gemini-3.8-flash-n", 100, 0, 5))));
            var asked = 0;
            var named = new AntigravityModelMap(path).Resolve(steps, _ => { asked++; return new AntigravityModelMap.Generations(true, null); });
            Expect(asked, 0);
            Expect(named[0].Model, "gemini-3.8-flash");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void FamilyTrim()
    {
        Expect(Pricing.Family("gemini-4-argon-trial"), "gemini-4-argon");
        Expect(Pricing.Family("gemini-4-carbon-high"), "gemini-4-carbon");
        Expect(Pricing.Family("gemini-4-neon-preview"), "gemini-4-neon");
        Expect(Pricing.Family("gemini-4-hydrogen-fast"), "gemini-4-hydrogen");
        Expect(Pricing.Family("gemini-3.8-flash-n"), "gemini-3.8-flash");
        Expect(Pricing.Family("gemini-3.8-flash-tiered"), "gemini-3.8-flash");
        Expect(Pricing.Family("gemini-4-argon"), "gemini-4-argon");
        Expect(Pricing.Family("claude-opus-5-5-thinking"), "claude-opus-5-5");
        Expect(Pricing.Family("claude-opus-5-thinking"), "claude-opus-5");
        Expect(Pricing.Family("gemini-4-argonaut"), "gemini-4-argonaut");
        Expect(Pricing.Family("gpt-oss-120b-medium"), "gpt-oss-120b-medium");
    }

    private static double Million(string model, DateTimeOffset at) =>
        Pricing.Cost(new TokenEvent(TriggerTool.Antigravity, at, model, 1_000_000, 1_000_000, 0, 1_000_000));

    private static void FlashDated()
    {
        // 2027-01-01 00:00 Pacific is 08:00 UTC.
        var before = new DateTimeOffset(2027, 1, 1, 7, 59, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2027, 1, 1, 8, 0, 0, TimeSpan.Zero);
        Expect(Math.Abs(Million("gemini-3.8-flash", before) - (0.75 + 3.75 + 0.075)) < 1e-9, "through 2026-12-31: 0.75, 3.75, 0.075");
        Expect(Math.Abs(Million("gemini-3.8-flash", after) - (1.5 + 7.5 + 0.15)) < 1e-9, "from 2027-01-01: 1.50, 7.50, 0.15");
        Expect(Math.Abs(Million("gemini-3.8-flash", At) - (0.75 + 3.75 + 0.075)) < 1e-9, "October's rates");
        Expect(Pricing.IsKnown("gemini-3.8-flash"), "the dated model is known");
    }

    private static void ArgonDated()
    {
        // 2026-12-01 00:00 Pacific is 08:00 UTC.
        var before = new DateTimeOffset(2026, 12, 1, 7, 59, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);
        Expect(Math.Abs(Million("gemini-4-argon", before) - (2 + 10 + 0.1)) < 1e-9, "through 2026-11-30: 2, 10, 0.10");
        Expect(Math.Abs(Million("gemini-4-argon", after) - (4 + 20 + 0.2)) < 1e-9, "from 2026-12-01: 4, 20, 0.20");
        Expect(Math.Abs(Million("gemini-4-argon", At) - (2 + 10 + 0.1)) < 1e-9, "October's rates");
    }

    private static void NewGeminiModels()
    {
        var before = new DateTimeOffset(2026, 12, 1, 7, 59, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);
        Expect(Math.Abs(Million("gemini-4-carbon", before) - (2 + 10 + 0.1)) < 1e-9, "Carbon through 2026-11-30: 2, 10, 0.10");
        Expect(Math.Abs(Million("gemini-4-carbon", after) - (4 + 20 + 0.2)) < 1e-9, "Carbon from 2026-12-01: 4, 20, 0.20");
        Expect(Math.Abs(Million("gemini-4-neon", At) - (3 + 15 + 0.15)) < 1e-9, "Neon: 3, 15, 0.15");
        Expect(Math.Abs(Million("gemini-4-hydrogen", At) - (1.5 + 7.5 + 0.075)) < 1e-9, "Hydrogen: 1.5, 7.5, 0.075");
        Expect(Math.Abs(Million("gemini-3.5-flash-lite", At) - (0.3 + 2.5 + 0.03)) < 1e-9, "3.5 Flash-Lite: 0.3, 2.5, 0.03");
        Expect(Math.Abs(Million("gemini-3.1-flash-lite", At) - (0.25 + 1.5 + 0.025)) < 1e-9, "3.1 Flash-Lite: 0.25, 1.5, 0.025");

        var names = AntigravityLogReader.ModelNames();
        Expect(names["gemini-4-carbon"], "Gemini 4 Carbon");
        Expect(names["gemini-4-neon"], "Gemini 4 Neon");
        Expect(names["gemini-4-hydrogen"], "Gemini 4 Hydrogen");
        Expect(names["gemini-3.5-flash-lite"], "Gemini 3.5 Flash-Lite");
        Expect(names["gemini-3.1-flash-lite"], "Gemini 3.1 Flash-Lite");
    }

    private static void UnnamedInShownLanguage()
    {
        var language = L10n.Current;
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            // Built on a scan thread, as CostStore builds it.
            var summary = Task.Run(() => ProviderCostSummary.Empty with { ModelNames = AntigravityLogReader.ModelNames() }).Result;
            Expect(summary.NameOf(AntigravityLogReader.UnknownModel) == "未命名的模型", "a Chinese page reads it in Chinese");
            using (L10n.English())
            {
                Expect(summary.NameOf(AntigravityLogReader.UnknownModel) == "Model not named", "a page set in English (Pixel) reads it in English");
            }
            Expect(summary.NameOf("gemini-4-argon") == "Gemini 4 Argon", "a named model keeps its name");
        }
        finally
        {
            L10n.Current = language;
        }
    }

    private static void InlineModelNamed()
    {
        var step = AntigravityLogReader.Parse(
            Step(10, "2026-11-20T12:00:00Z", 5000, 20000, 400, model: "gemini-4-neon-xhigh"), Conversation)!;
        Expect(step.Model, "gemini-4-neon");
        Expect(step.Dollars > 0, "inline model is priced immediately");
        var asked = 0;
        var resolved = new AntigravityModelMap(TempMap()).Resolve(new List<TokenEvent> { step }, _ =>
        {
            asked++;
            return new AntigravityModelMap.Generations(true, null);
        });
        Expect(asked, 0);
        Expect(resolved[0].Model, "gemini-4-neon");
    }

    private static void CheckpointCounted()
    {
        var checkpoint = AntigravityLogReader.Parse(
            Step(156, "2026-11-16T18:21:28Z", 16606, 501769, 14376, source: "SYSTEM", type: "CHECKPOINT"), Conversation);
        Expect(checkpoint is not null, "SYSTEM CHECKPOINT with tokens must be counted");
        Expect(checkpoint!.InputTokens, 16606L);
        Expect(checkpoint.CacheReadTokens, 501769L);
        Expect(checkpoint.OutputTokens, 14376L);
        Expect(AntigravityLogReader.Parse(
            Step(157, "2026-11-16T18:21:29Z", null, null, null, source: "SYSTEM", type: "CHECKPOINT"), Conversation) is null,
            "CHECKPOINT with no tokens is skipped");
    }

    private static void ReusedStepIndexPreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "island-ag-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "brain", Conversation, ".system_generated", "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var path = Path.Combine(logs, "transcript_full.jsonl");
            File.WriteAllLines(path, new[]
            {
                Step(79, "2026-11-13T11:30:50Z", 243574, 0, 149),
                Step(79, "2026-11-13T11:30:50Z", 243574, 0, 149), // exact duplicate -> dropped
                Step(79, "2026-11-13T11:31:06Z", 2486, 243829, 1194), // second real call reusing step_index -> kept
                Step(156, "2026-11-16T18:21:28Z", 16606, 501769, 14376, source: "SYSTEM", type: "CHECKPOINT"),
            });
            var steps = AntigravityLogReader.ParseFile(path);
            Expect(steps.Count, 3);
            Expect(steps.Sum(s => s.InputTokens), 243574L + 2486L + 16606L);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void OrchestratorAliases()
    {
        var before = new DateTimeOffset(2026, 12, 1, 7, 59, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);
        Expect(Pricing.Family("gemini-pro-orchestrator-neon"), "gemini-pro-orchestrator-neon");
        Expect(Pricing.Family("gemini-flash-worker-agy"), "gemini-flash-worker-agy");
        Expect(Pricing.Family("gemini-pro-orchestrator-g1-pro"), "gemini-pro-orchestrator-g1-pro");

        Expect(Math.Abs(Million("gemini-pro-orchestrator-neon", At) - (3 + 15 + 0.15)) < 1e-9, "Orchestrator Neon prices at Neon rates");
        Expect(Math.Abs(Million("gemini-flash-worker-agy", At) - (1.5 + 7.5 + 0.075)) < 1e-9, "Orchestrator Worker prices at Hydrogen rates");
        Expect(Math.Abs(Million("gemini-pro-orchestrator-g1-pro", before) - (2 + 10 + 0.1)) < 1e-9, "Orchestrator Argon before 2026-12-01");
        Expect(Math.Abs(Million("gemini-pro-orchestrator-g1-pro", after) - (4 + 20 + 0.2)) < 1e-9, "Orchestrator Argon from 2026-12-01");

        var names = AntigravityLogReader.ModelNames();
        Expect(names["gemini-pro-orchestrator-neon"], "Orchestrator (Neon)");
        Expect(names["gemini-pro-orchestrator-g1-pro"], "Orchestrator (Argon)");
        Expect(names["gemini-flash-worker-agy"], "Orchestrator Worker (Hydrogen)");
    }
}
