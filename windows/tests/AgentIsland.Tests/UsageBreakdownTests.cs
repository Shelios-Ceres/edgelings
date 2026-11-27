using AgentIsland.Core;
using AgentIsland.Cost;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 第 9 批 Usage 页：按模型的四类 token 拆分、“未定价”不进合计金额，以及外接 API
/// 的用量留在宿主名下、照常显示模型名（QUEUE Q3，quota-by-entry-plan-v2 第 6 步）。
/// 时区固定用 UTC，路径用 Path.Combine，Mac CI 上也一样。
public static class UsageBreakdownTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("each model splits its tokens into input, output, cache write and cache read, today and this month", Split),
            ("the 7-day slice splits its models the same way", WeekSplit),
            ("a model with no price is marked unpriced and adds nothing to the dollars", Unpriced),
            ("the total leaves unpriced models out, and is null when nothing has a price", PricedTotal),
            ("a free model, a self-reported cost and a provider-priced model all count as priced", PricedKinds),
            ("a model with priced and unpriced calls is partly unpriced", PartlyUnpriced),
            ("an external model in a Claude log stays under Claude with its own name", ClaudeExternal),
            ("Kimi Code's connected DeepSeek stays under Kimi, named as Kimi names it, priced as DeepSeek", KimiExternal),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS usage breakdown: {name}");
        }
        Console.WriteLine("UsageBreakdownTests GREEN");
    }

    private static TokenEvent At(int day, int hour, string model, long input, long output, long write, long read, double? dollars = null) =>
        new(TriggerTool.Claude, new DateTimeOffset(2026, 11, day, hour, 0, 0, TimeSpan.Zero), model, input, output, write, read, dollars);

    private static void Split()
    {
        var summary = CostSummarizer.Summarize(new[]
        {
            At(20, 9, "claude-opus-5-5", 100, 20, 300, 4000),
            At(20, 11, "claude-opus-5-5-20261101", 10, 2, 30, 400),
            At(3, 10, "claude-opus-5-5", 1000, 200, 0, 9000),
            At(20, 10, "claude-sonnet-5-5", 7, 3, 0, 0),
            // 上个月的不进本月。
            new TokenEvent(TriggerTool.Claude, new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero), "claude-opus-5-5", 5, 5, 5, 5),
        }, Now, Zone);
        var today = summary.TodayModels.Single(model => model.Model == "claude-opus-5-5");
        Expect(today is { InputTokens: 110, OutputTokens: 22, CacheCreationTokens: 330, CacheReadTokens: 4400, Tokens: 4862, BillableTokens: 462 },
            $"today's opus row: the date-suffixed id groups under its family and the four kinds add up ({today})");
        var month = summary.MonthModels.Single(model => model.Model == "claude-opus-5-5");
        Expect(month is { InputTokens: 1110, OutputTokens: 222, CacheCreationTokens: 330, CacheReadTokens: 13400 },
            $"this month's opus row covers the 3rd and today, not October ({month})");
        var total = ModelSpend.Total(summary.MonthModels);
        Expect(total.Tokens == summary.MonthTokens && total.BillableTokens == summary.MonthBillableTokens,
            "the rows add up to the month's totals");
        Expect(total.InputTokens + total.OutputTokens + total.CacheCreationTokens + total.CacheReadTokens == total.Tokens,
            "the four kinds add up to every token");
        Expect(Math.Abs(total.Dollars - summary.MonthDollars) < 1e-12, "the rows' dollars add up to the month's");
    }

    private static void WeekSplit()
    {
        var slice = CostSummarizer.Slice(new[]
        {
            At(18, 9, "gpt-6.2-sol", 40, 10, 0, 500),
            At(19, 9, "gpt-6.2-sol", 60, 30, 0, 700),
            At(2, 9, "gpt-6.2-sol", 1, 1, 0, 1),
        }, CostSummarizer.LastSevenDaysStart(Now, Zone), Now.AddTicks(1), Zone);
        Expect(slice.ByModel.Single() is { InputTokens: 100, OutputTokens: 40, CacheCreationTokens: 0, CacheReadTokens: 1200, UnpricedTokens: 0 },
            "the week's row holds the 18th and 19th only");
    }

    private static void Unpriced()
    {
        var summary = CostSummarizer.Summarize(new[]
        {
            At(20, 9, "claude-opus-5-5", 1_000_000, 1_000_000, 1_000_000, 1_000_000),
            At(20, 10, "glm-5.1", 500, 100, 0, 4000),
        }, Now, Zone);
        var glm = summary.TodayModels.Single(model => model.Model == "glm-5.1");
        Expect(glm.Unpriced && !glm.PartlyUnpriced && glm.Dollars == 0 && glm.UnpricedTokens == glm.Tokens, "glm has no price");
        var opus = summary.TodayModels.Single(model => model.Model == "claude-opus-5-5");
        Expect(!opus.Unpriced && opus.UnpricedTokens == 0, "opus has a price");
        // Opus 5.5：输入 4、输出 20、5 分钟缓存写入 4 × 1.25、缓存读取 0.2，每百万。
        Expect(Math.Abs(opus.Dollars - 29.2) < 1e-9, $"opus prices at its list rates ({opus.Dollars})");
        Expect(Math.Abs(summary.TodayDollars - 29.2) < 1e-9, "the day's dollars are opus's alone");
        Expect(summary.UnknownModels.Contains("glm-5.1"), "the summary still names the unpriced model");
    }

    private static void PricedTotal()
    {
        var priced = new ModelSpend("a", 100, 50, 1.5);
        var unpriced = new ModelSpend("b", 200, 80, 0) { UnpricedTokens = 200 };
        Expect(ModelSpend.PricedDollars(new[] { priced, unpriced }) is { } sum && Math.Abs(sum - 1.5) < 1e-12, "only the priced row counts");
        Expect(ModelSpend.PricedDollars(new[] { unpriced }) is null, "nothing priced: no amount, not $0");
        Expect(ModelSpend.PricedDollars(Array.Empty<ModelSpend>()) is null, "no rows: no amount");
        var total = ModelSpend.Total(new[] { priced, unpriced });
        Expect(total is { Tokens: 300, BillableTokens: 130, UnpricedTokens: 200, PartlyUnpriced: true }, "the total keeps the unpriced part");
    }

    private static void PricedKinds()
    {
        var summary = CostSummarizer.Summarize(new[]
        {
            new TokenEvent(TriggerTool.Codex, Now.AddHours(-1), "codex-auto-review", 100, 10, 0, 500),
            new TokenEvent(TriggerTool.Grok, Now.AddHours(-1), "grok-4.7-build", 100, 10, 0, 500, 0.25),
            new TokenEvent(TriggerTool.Kimi, Now.AddHours(-1), "deepseek/deepseek-flash", 100, 10, 0, 500) { PricedAs = "deepseek-flash" },
        }, Now, Zone);
        Expect(summary.TodayModels.All(model => model.UnpricedTokens == 0), "none of the three is unpriced");
        Expect(summary.TodayModels.Single(model => model.Model == "codex-auto-review").Dollars == 0, "auto-review is free, not unpriced");
        Expect(Math.Abs(summary.TodayModels.Single(model => model.Model == "grok-4.7-build").Dollars - 0.25) < 1e-12, "Grok's own figure wins");
        Expect(summary.TodayModels.Single(model => model.Model == "deepseek/deepseek-flash").Dollars > 0, "the connected model prices as its provider's id");
    }

    private static void PartlyUnpriced()
    {
        var summary = CostSummarizer.Summarize(new[]
        {
            new TokenEvent(TriggerTool.Grok, Now.AddHours(-2), "grok-4.7-build", 100, 10, 0, 0, 0.5),
            new TokenEvent(TriggerTool.Grok, Now.AddHours(-1), "grok-4.7-build", 300, 30, 0, 0),
        }, Now, Zone);
        var grok = summary.TodayModels.Single();
        Expect(grok is { PartlyUnpriced: true, Unpriced: false, UnpricedTokens: 330, Tokens: 440 } && Math.Abs(grok.Dollars - 0.5) < 1e-12,
            $"the call without a figure is unpriced, the other keeps its dollars ({grok})");
    }

    private static void ClaudeExternal()
    {
        var folder = Path.Combine(Path.GetTempPath(), "limen-usage-breakdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "session.jsonl");
            File.WriteAllLines(file, new[]
            {
                Line("m1", "r1", "claude-opus-5-5", "2026-11-20T09:00:00Z"),
                Line("m2", "r2", "glm-5.1", "2026-11-20T10:00:00Z"),
            });
            var events = ClaudeLogReader.ParseFile(file);
            Expect(events.Count == 2 && events.All(e => e.Provider == TriggerTool.Claude), "both calls are Claude's");
            var summary = CostSummarizer.Summarize(events, Now, Zone);
            Expect(summary.TodayModels.Any(model => model.Model == "glm-5.1" && model.Unpriced && model.InputTokens == 120),
                "the external model lists under its own name, unpriced");
            Expect(summary.TodayTokens == events.Sum(e => e.WireTokens), "its tokens count in Claude's totals");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static string Line(string id, string request, string model, string at) =>
            $"{{\"type\":\"assistant\",\"timestamp\":\"{at}\",\"requestId\":\"{request}\",\"message\":{{\"id\":\"{id}\",\"model\":\"{model}\",\"stop_reason\":\"end_turn\",\"usage\":{{\"input_tokens\":120,\"output_tokens\":30,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":900}}}}}}";
    }

    private static void KimiExternal()
    {
        var events = new List<TokenEvent>
        {
            new(TriggerTool.Kimi, Now.AddHours(-1), "deepseek/deepseek-flash", 1000, 200, 0, 5000),
            new(TriggerTool.Kimi, Now.AddHours(-1), "kimi-code/k3.1-pro", 1000, 200, 0, 5000),
        };
        var priced = KimiLogReader.PriceByProvider(events, new Dictionary<string, string> { ["deepseek/deepseek-flash"] = "deepseek-flash" });
        var summary = CostSummarizer.Summarize(priced, Now, Zone);
        var deepseek = summary.TodayModels.Single(model => model.Model == "deepseek/deepseek-flash");
        Expect(!deepseek.Unpriced && deepseek.Dollars > 0, "the connected model has DeepSeek's price");
        Expect(summary.TodayTokens == events.Sum(e => e.WireTokens), "both models count in Kimi's totals");
        Expect(Math.Abs(summary.TodayDollars - summary.TodayModels.Sum(model => model.Dollars)) < 1e-12, "and in its dollars");
    }
}
