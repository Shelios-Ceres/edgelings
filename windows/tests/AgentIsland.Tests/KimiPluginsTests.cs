using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Kimi Code's plugins: the data folder, the memory worker's background
/// runs and the mailbox. Each test runs against a temp KIMI_CODE_HOME.
public static class KimiPluginsTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("kimi home: KIMI_CODE_HOME wins, ~/.kimi-code otherwise", KimiHomeVariable),
            ("memory runs: the scan skips a session whose cwd is the memories folder", MemoryRunSkipped),
            ("memory runs: a project named memories elsewhere is a real session", MemoriesNamedProject),
            ("memory runs: their tokens list as one row, priced by the real model, totals unchanged", MemoryCostSplit),
            ("memory runs: the row is named in the shown language", MemoryRowName),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS kimi plugins: " + name);
        }
        Console.WriteLine("KimiPluginsTests GREEN");
    }

    /// Runs `body` with KIMI_CODE_HOME pointing at a fresh temp folder.
    private static void WithHome(Action<string> body)
    {
        var home = Path.Combine(Path.GetTempPath(), "agentisland-kimi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var before = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
        try
        {
            body(home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", before);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    private static void KimiHomeVariable()
    {
        var before = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", null);
            Expect(IslandPaths.KimiHome, Path.Combine(IslandPaths.Home, ".kimi-code"));
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", "  ");
            Expect(IslandPaths.KimiHome, Path.Combine(IslandPaths.Home, ".kimi-code"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", before);
        }
        WithHome(home =>
        {
            Expect(IslandPaths.KimiHome, home);
            Expect(IslandPaths.KimiSessionsRoot, Path.Combine(home, "sessions"));
        });
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private const string ProviderConfig = """
        [models."deepseek/deepseek-flash"]
        provider = "deepseek"
        model = "deepseek-flash"
        """;

    /// A Kimi session folder under the temp home with a state.json and a
    /// wire.jsonl of the given lines; the log is stamped `modified`.
    private static string Session(string home, string workdir, string id, string cwd, DateTimeOffset modified, params string[] lines)
    {
        var dir = Path.Combine(home, "sessions", workdir, "session_" + id);
        var agent = Path.Combine(dir, "agents", "main");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(new { id = "session_" + id, cwd }));
        var wire = Path.Combine(agent, "wire.jsonl");
        File.WriteAllLines(wire, lines);
        File.SetLastWriteTimeUtc(wire, modified.UtcDateTime);
        return wire;
    }

    private static string Usage(string model, long input, long output, int secondsAgo = 60) =>
        JsonSerializer.Serialize(new
        {
            type = "usage.record",
            time = Now.AddSeconds(-secondsAgo).ToUnixTimeMilliseconds(),
            model,
            usage = new { inputOther = input, output, inputCacheRead = 0, inputCacheCreation = 0 },
        });

    private static string Turn(string type, DateTimeOffset at) =>
        JsonSerializer.Serialize(new { type, time = at.ToUnixTimeMilliseconds() });

    private static void MemoryRunSkipped() => WithHome(home =>
    {
        var now = DateTimeOffset.UtcNow;
        var memories = Path.Combine(home, "memories");
        var open = new[] { Turn("agent.turn.started", now.AddSeconds(-3)) };
        Session(home, "wd_memories_a", "run1", memories, now, open);
        Session(home, "wd_memories_a", "run2", memories + Path.DirectorySeparatorChar, now, open);
        Session(home, "wd_memories_a", "run3", memories.ToUpperInvariant().Replace('\\', '/'), now, open);
        Session(home, "wd_work_b", "real", @"D:\work\app", now, open);
        var scanned = SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>());
        Expect(string.Join(",", scanned.Select(s => s.SessionId)), "real");
    });

    private static void MemoriesNamedProject() => WithHome(home =>
    {
        var now = DateTimeOffset.UtcNow;
        var open = new[] { Turn("agent.turn.started", now.AddSeconds(-3)) };
        // The workdir folder carries the same name Kimi gives the plugin's runs;
        // the cwd is what tells them apart.
        Session(home, "wd_memories_66683c140241", "mine", @"D:\projects\memories", now, open);
        Session(home, "wd_memories_66683c140241", "inside", Path.Combine(home, "memories", "sub"), now, open);
        Session(home, "wd_memories_66683c140241", "run", Path.Combine(home, "memories"), now, open);
        var scanned = SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>());
        Expect(string.Join(",", scanned.Select(s => s.SessionId).Order()), "inside,mine");
    });

    private static void MemoryCostSplit() => WithHome(home =>
    {
        var memories = Path.Combine(home, "memories");
        var run = Session(home, "wd_memories_a", "run", memories, Now,
            Usage("deepseek/deepseek-flash", 1_000_000, 200_000), Usage("deepseek/deepseek-flash", 500_000, 100_000, 30));
        var work = Session(home, "wd_work_b", "real", @"D:\work\app", Now,
            Usage("deepseek/deepseek-flash", 2_000_000, 300_000), Usage("kimi-code/k3", 1_000, 500));
        var providers = KimiLogReader.ProviderModels(ProviderConfig);
        var events = KimiLogReader.ParseFile(run).Concat(KimiLogReader.ParseFile(work)).ToList();
        var split = KimiLogReader.PriceByProvider(events, providers);
        // The same tokens with no tagging, as before the change.
        var plain = KimiLogReader.PriceByProvider(
            events.Select(e => e.Model.Contains('|') ? e with { Model = e.Model[(e.Model.IndexOf('|') + 1)..] } : e).ToList(), providers);

        Expect(split.Count(e => e.Model == KimiLogReader.MemoryModel), 2);
        Expect(split.Where(e => e.Model == KimiLogReader.MemoryModel).All(e => e.PricedAs == "deepseek-flash"), "priced by the model that ran them");
        Expect(split.Where(e => e.Model == KimiLogReader.MemoryModel).Sum(e => e.Dollars) > 0, "and they cost something");
        var at = Now.AddMinutes(5);
        var splitSummary = CostSummarizer.Summarize(split, at);
        var plainSummary = CostSummarizer.Summarize(plain, at);
        Expect(splitSummary.RecentModels.Sum(m => m.Tokens), plainSummary.RecentModels.Sum(m => m.Tokens));
        Expect(Math.Abs(splitSummary.RecentModels.Sum(m => m.Dollars) - plainSummary.RecentModels.Sum(m => m.Dollars)) < 1e-9, "dollars unchanged");
        Expect(splitSummary.TodayModels.Sum(m => m.Tokens), plainSummary.TodayModels.Sum(m => m.Tokens));
        var row = splitSummary.RecentModels.Single(m => m.Model == KimiLogReader.MemoryModel);
        Expect(row.Tokens, 1_800_000L);
        var flash = splitSummary.RecentModels.Single(m => m.Model == "deepseek/deepseek-flash");
        Expect(flash.Tokens, 2_300_000L);
        Expect(splitSummary.UnknownModels.Count, 0);
        // The row is not K2: Kimi Code's list keeps it.
        Expect(KimiLogReader.IsK2(KimiLogReader.MemoryModel, null), false);
    });

    private static void MemoryRowName()
    {
        var language = L10n.Current;
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            var summary = ProviderCostSummary.Empty;
            Expect(summary.NameOf(KimiLogReader.MemoryModel), "记忆（后台）");
            using (L10n.English())
            {
                Expect(summary.NameOf(KimiLogReader.MemoryModel), "Memory (background)");
            }
        }
        finally
        {
            L10n.Current = language;
        }
    }
}
