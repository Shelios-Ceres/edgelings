using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using Microsoft.Data.Sqlite;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

public static class OtherTokenAccountingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("Kimi copied usage counts once across files and cache reload", KimiCopies),
            ("Kimi distinct agents and calls retain their counts", KimiDistinctCalls),
            ("Kimi records without identity are not guessed to be duplicates", KimiLegacy),
            ("Grok input includes cached reads and writes only once", GrokCategories),
            ("Grok stays readable during active writes", GrokActiveWriter),
            ("Grok reports read failure instead of false zero", GrokReadFailure),
            ("Grok sums per-model usage without adding the top-level total", GrokModels),
            ("Cursor reads committed usage still held in the WAL", CursorWal),
            ("Cursor usage without a date of its own is not counted as today's", CursorUndated),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS other token accounting: {name}");
        }
        Console.WriteLine("OtherTokenAccountingTests GREEN");
    }

    private static void KimiCopies()
    {
        using var fixture = new Fixture();
        var a = fixture.Write("original.jsonl", Kimi("main", 0));
        var b = fixture.Write("copied.jsonl", Kimi("main", 0), Kimi("main", 1));
        var files = new[] { a, b };
        var cold = fixture.Cache().Walk(files, DateTimeOffset.Now.AddDays(-1), KimiLogReader.ParseFile);
        var warm = fixture.Cache().Walk(files, DateTimeOffset.Now.AddDays(-1), _ => throw new Exception("unexpected cache miss"));
        Expect(KimiLogReader.Deduplicate(cold).Count == 2, "copy must not add another model call");
        var dedup = KimiLogReader.Deduplicate(warm);
        Expect(dedup.Count == 2 && dedup.Sum(e => e.BillableTokens) == 334, "cached identities must preserve the same distinct-call total");
    }

    private static void KimiDistinctCalls()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("agents.jsonl", Kimi("main", 0), Kimi("child", 0), Kimi("main", 1));
        Expect(KimiLogReader.ParseFile(path).Count == 3, "equal usage in another agent or at another time is a separate call");
    }

    private static void KimiLegacy()
    {
        using var fixture = new Fixture();
        var line = Kimi(null, 0);
        var path = fixture.Write("legacy.jsonl", line, line);
        Expect(KimiLogReader.ParseFile(path).Count == 2, "missing agent identity must not merge unrelated records");
    }

    private static void GrokCategories()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("grok.jsonl", Grok(1000, 900, 40, 50));
        var token = GrokLogReader.ParseFile(path).Single().Event;
        Expect(token.InputTokens == 60 && token.CacheCreationTokens == 40 && token.CacheReadTokens == 900,
            "Grok's 1,000 input tokens already include 900 reads and 40 writes");
        Expect(token.WireTokens == 1050 && token.BillableTokens == 150, "all and cache-excluded totals must each count input once");
        Expect(Math.Abs(token.Dollars - 0.158652) < 1e-10, "provider-reported dollar cost must be preserved");
    }

    private static void GrokActiveWriter()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("grok.jsonl", Grok(1000, 900, 0, 50));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Expect(GrokLogReader.ParseFile(path).Single().Event.BillableTokens == 150, "an active writer must not hide Grok usage");
    }

    private static void GrokReadFailure()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("grok.jsonl", Grok(1000, 900, 0, 50));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { _ = GrokLogReader.ParseFile(path); }
        catch (IOException) { return; }
        throw new Exception("failed read must reach CostStore, which retains its previous summary");
    }

    private static void GrokModels()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("models.jsonl", JsonSerializer.Serialize(new
        {
            timestamp = At.ToUnixTimeSeconds(),
            @params = new { update = new { sessionUpdate = "turn_completed", prompt_id = "p", usage = new {
                inputTokens = 300, outputTokens = 30, cachedReadTokens = 240,
                modelUsage = new Dictionary<string, object> {
                    ["model-a"] = new { inputTokens = 100, outputTokens = 10, cachedReadTokens = 80 },
                    ["model-b"] = new { inputTokens = 200, outputTokens = 20, cachedReadTokens = 160 },
                },
            } } },
        }));
        var events = GrokLogReader.ParseFile(path);
        Expect(events.Count == 2 && events.Sum(e => e.Event.WireTokens) == 330
            && events.Sum(e => e.Event.BillableTokens) == 90 && events.Select(e => e.DedupKey).Distinct().Count() == 2,
            "per-model counts share a prompt but must not drop a model or add the aggregate again");
    }

    private static void CursorWal()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("cursor.db");
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE cursorDiskKV(key TEXT PRIMARY KEY, value TEXT);";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO cursorDiskKV(key,value) VALUES ('bubbleId:test',@value)";
        command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(new { createdAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(), tokenCount = new { inputTokens = 100, outputTokens = 20 } }));
        command.ExecuteNonQuery();
        Expect(new FileInfo(path + "-wal").Length > 0, "fixture must retain uncheckpointed WAL data");
        Expect(CursorLogReader.ReadDatabase(path, 1).Single().WireTokens == 120,
            "read-only access must see committed usage before the writer checkpoints its WAL");
    }

    private static void CursorUndated()
    {
        static string Bubble(object? createdAt) => JsonSerializer.Serialize(createdAt is null
            ? new { tokenCount = new { inputTokens = 100, outputTokens = 20 } }
            : (object)new { createdAt, tokenCount = new { inputTokens = 100, outputTokens = 20 } });
        Expect(CursorLogReader.ParseBubble(Bubble(null)) is null, "a bubble without createdAt has no day to count on");
        Expect(CursorLogReader.ParseBubble(Bubble("not a date")) is null, "an unreadable createdAt has no day to count on");
        var at = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
        Expect(CursorLogReader.ParseBubble(Bubble(at.ToUnixTimeMilliseconds()))?.Timestamp == at, "a dated bubble counts on its own day");
    }

    private static string Kimi(string? agent, int seconds) => JsonSerializer.Serialize(new
    {
        type = "usage.record", time = At.AddSeconds(seconds).ToUnixTimeMilliseconds(), agentId = agent,
        usageScope = "turn", model = "kimi-test-model",
        usage = new { inputOther = 120, output = 40, inputCacheCreation = 7, inputCacheRead = 9000 },
    });

    private static string Grok(long input, long read, long write, long output) => JsonSerializer.Serialize(new
    {
        timestamp = At.ToUnixTimeSeconds(),
        @params = new { update = new { sessionUpdate = "turn_completed", prompt_id = "p", usage = new {
            inputTokens = input, outputTokens = output, cachedReadTokens = read, cacheCreationTokens = write,
            totalTokens = input + output, costUsdTicks = 158652000,
        } } },
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        private readonly string _root;
        public Fixture() { _root = Path.GetFullPath(Path.Combine(_temp, "agentisland-other-tokens-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(_root); }
        public string Write(string name, params string[] lines) { var path = Path.Combine(_root, name); File.WriteAllLines(path, lines); return path; }
        public LogParseCache Cache() => new(TriggerTool.Kimi, "cache.json", _root);
        public void Dispose()
        {
            if (!_root.StartsWith(_temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(_root).StartsWith("agentisland-other-tokens-", StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary directory");
            Directory.Delete(_root, recursive: true);
        }
    }
}
