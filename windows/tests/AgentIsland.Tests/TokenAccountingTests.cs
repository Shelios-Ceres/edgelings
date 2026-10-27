using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

public static class TokenAccountingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Cutoff => DateTimeOffset.Now.AddDays(-1);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("new input written to cache counts in every rollup", IncludesCacheWrites),
            ("active Codex and Claude writers remain readable", ActiveWriters),
            ("Claude final usage replaces an early stream fragment", FinalUsage),
            ("replayed partial usage cannot replace a final response", ReplayedPartial),
            ("distinct responses keep their own counts", DistinctResponses),
            ("response identity and finality survive cache reload", WarmCacheIdentity),
            ("failed reads preserve last success and retry unchanged files", RetryFailedRead),
            ("first-read failure is never cached as an empty log", RetryFirstFailure),
            ("a valid empty log is cached normally", ValidEmptyLog),
            ("a file changing during parsing is retried", ChangingFile),
            ("compressed log open failures reach the cache", CompressedReadFailure),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS token accounting: {name}");
        }
        Console.WriteLine("TokenAccountingTests GREEN");
    }

    private static void IncludesCacheWrites()
    {
        var token = new TokenEvent(TriggerTool.Claude, At, "synthetic", 2, 100, 400, 10_000);
        Expect(token.WireTokens == 10_502 && token.BillableTokens == 502, "only the 10,000 cached reads should be excluded");
        var summary = CostSummarizer.Summarize(new[] { token }, At);
        Expect(summary.TodayBillableTokens == 502 && summary.MonthBillableTokens == 502
            && summary.LastSevenDays.ByModel.Single().BillableTokens == 502
            && summary.DailyHistory.Single().BillableTokens == 502,
            "today, month, model and daily history must share the same definition");
    }

    private static void ActiveWriters()
    {
        using var fixture = new Fixture();
        var codex = fixture.Write("codex.jsonl", Codex(100, 80, 20, 100, 20));
        var claude = fixture.Write("claude.jsonl", Claude("a", 5, final: true));
        using var codexWriter = new FileStream(codex, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var claudeWriter = new FileStream(claude, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Expect(CodexLogReader.ParseFile(codex).Single().BillableTokens == 40, "active Codex log must include uncached input and output once");
        Expect(ClaudeLogReader.ParseFile(claude).Single().OutputTokens == 5, "active Claude log must be readable");
    }

    private static void FinalUsage()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("claude.jsonl", Claude("a", 2), Claude("a", 86_978, final: true, seconds: 1));
        var parsed = ClaudeLogReader.ParseFile(path);
        Expect(parsed.Count == 1 && parsed[0].OutputTokens == 86_978 && parsed[0].IsFinalUsage,
            "a later final usage record must update the same response, not disappear or add another call");
    }

    private static void ReplayedPartial()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("claude.jsonl", Claude("a", 200, final: true), Claude("a", 2, seconds: 30));
        Expect(ClaudeLogReader.ParseFile(path).Single().OutputTokens == 200, "a newer copied partial record cannot regress finalized usage");
    }

    private static void DistinctResponses()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("claude.jsonl", Claude("a", 50, final: true), Claude("b", 50, final: true));
        var events = ClaudeLogReader.ParseFile(path);
        Expect(ClaudeLogReader.Deduplicate(events).Sum(e => e.OutputTokens) == 100,
            "two real responses with equal timestamps and counts are not a replay");
    }

    private static void WarmCacheIdentity()
    {
        using var fixture = new Fixture();
        var early = fixture.Write("early.jsonl", Claude("a", 2));
        var final = fixture.Write("final.jsonl", Claude("a", 300, final: true, seconds: 1));
        var files = new[] { early, final };
        var coldCache = fixture.Cache();
        var cold = coldCache.Walk(files, Cutoff, ClaudeLogReader.ParseFile);
        var warm = fixture.Cache().Walk(files, Cutoff, _ => throw new Exception("warm cache unexpectedly reparsed"));
        Expect(ClaudeLogReader.Deduplicate(cold).Single().OutputTokens == 300, "cold cross-file responses must merge");
        var merged = ClaudeLogReader.Deduplicate(warm).Single();
        Expect(merged.OutputTokens == 300 && merged.IsFinalUsage && merged.ResponseKey is not null,
            "warm cache must preserve identity and finality, not double count copied fragments");
    }

    private static void RetryFailedRead()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("claude.jsonl", Claude("a", 20, final: true));
        var cache = fixture.Cache();
        var first = cache.Walk(new[] { path }, Cutoff, ClaudeLogReader.ParseFile);
        File.AppendAllText(path, Claude("b", 30, final: true) + "\n");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = cache.Walk(new[] { path }, Cutoff, ClaudeLogReader.ParseFile);
            Expect(failed.Sum(e => e.OutputTokens) == first.Sum(e => e.OutputTokens), "failed read must keep the previous successful counts");
        }
        var recovered = fixture.Cache().Walk(new[] { path }, Cutoff, ClaudeLogReader.ParseFile);
        Expect(recovered.Sum(e => e.OutputTokens) == 50, "unlocking without another write must still retry, even after a cache reload");
    }

    private static void RetryFirstFailure()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("claude.jsonl", Claude("a", 20, final: true));
        var cache = fixture.Cache();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Expect(cache.Walk(new[] { path }, Cutoff, ClaudeLogReader.ParseFile).Count == 0, "no fabricated count before the first successful read");
        Expect(cache.Walk(new[] { path }, Cutoff, ClaudeLogReader.ParseFile).Single().OutputTokens == 20,
            "an unchanged file must recover after its first read failed");
    }

    private static void ValidEmptyLog()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("empty.jsonl");
        var cache = fixture.Cache();
        Expect(cache.Walk(new[] { path }, Cutoff, ClaudeLogReader.ParseFile).Count == 0, "empty log has zero usage");
        Expect(fixture.Cache().Walk(new[] { path }, Cutoff, _ => throw new Exception("valid empty log was not cached")).Count == 0,
            "a successful empty read must remain distinguishable from failure");
    }

    private static void ChangingFile()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("growing.jsonl", Claude("a", 20, final: true));
        var cache = fixture.Cache();
        cache.Walk(new[] { path }, Cutoff, file =>
        {
            var events = ClaudeLogReader.ParseFile(file);
            File.AppendAllText(file, Claude("b", 30, final: true) + "\n");
            return events;
        });
        Expect(cache.Walk(new[] { path }, Cutoff, ClaudeLogReader.ParseFile).Sum(e => e.OutputTokens) == 50,
            "a growing file cannot cache the incomplete result with its completed fingerprint");
    }

    private static void CompressedReadFailure()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("session.zstd");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { _ = DeepSeekLogReader.ParseFile(path); }
        catch (IOException) { return; }
        throw new Exception("compressed cost-reader failure was silently turned into an empty log");
    }

    private static string Claude(string id, long output, bool final = false, int seconds = 0) => JsonSerializer.Serialize(new
    {
        type = "assistant", timestamp = At.AddSeconds(seconds), requestId = "request-" + id,
        message = new { id = "message-" + id, model = "claude-test-model", stop_reason = final ? "tool_use" : null,
            usage = new { input_tokens = 2, output_tokens = output, cache_creation_input_tokens = 400, cache_read_input_tokens = 10_000 } },
    });

    private static string Codex(long input, long cached, long output, long totalIn, long totalOut) => JsonSerializer.Serialize(new
    {
        type = "event_msg", timestamp = At,
        payload = new { type = "token_count", info = new {
            total_token_usage = new { input_tokens = totalIn, output_tokens = totalOut },
            last_token_usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output },
        } },
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        private readonly string _root;

        public Fixture()
        {
            _root = Path.GetFullPath(Path.Combine(_tempRoot, "agentisland-token-tests-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
        }

        public string Write(string name, params string[] lines)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllLines(path, lines);
            return path;
        }

        public LogParseCache Cache() => new(TriggerTool.Claude, "cache.json", _root);

        public void Dispose()
        {
            if (!_root.StartsWith(_tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(_root).StartsWith("agentisland-token-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its temporary directory");
            Directory.Delete(_root, recursive: true);
        }
    }
}
