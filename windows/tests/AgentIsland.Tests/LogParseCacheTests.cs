using AgentIsland.Core;
using AgentIsland.Cost;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// LogParseCache when scans overlap or two copies of the app share a cache.
public static class LogParseCacheTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentisland-parse-cache-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        var cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(logs);
        try
        {
            OverlappingWalksShareOneCache(logs, cacheDirectory);
            Console.WriteLine("PASS parse cache: overlapping walks of one cache neither fail nor lose events");
            TwoCopiesSaveTheSameFile(logs, cacheDirectory);
            Console.WriteLine("PASS parse cache: two copies saving one file leave a valid cache and no temp files");
            ReloadSharesModelNames(logs, cacheDirectory);
            Console.WriteLine("PASS parse cache: a reloaded cache keeps its events and one string per model");
            OlderVersionsAreSwept(cacheDirectory);
            Console.WriteLine("PASS parse cache: loading a cache removes its older versions and nothing else");
            Console.WriteLine("LogParseCacheTests GREEN");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// Older versions of the same cache go when the current one loads; another
    /// cache's files, a temp file, a version that isn't a number and a file
    /// of another kind stay.
    private static void OlderVersionsAreSwept(string parent)
    {
        // Its own folder: the other tests' caches live beside it.
        var cacheDirectory = Directory.CreateDirectory(Path.Combine(parent, "sweep")).FullName;
        var names = new[]
        {
            "x-parse-cache.v1.json", "x-parse-cache.v3.json", "x-parse-cache.v4.json", "x-parse-cache.v4.json.tmp-abc",
            "x-parse-cache.vX.json", "y-parse-cache.v1.json", "antigravity-models.v1.json",
        };
        foreach (var name in names) File.WriteAllText(Path.Combine(cacheDirectory, name), "{}");
        var cache = new LogParseCache(TriggerTool.Kimi, "x-parse-cache.v4.json", cacheDirectory);
        cache.Walk(Array.Empty<string>(), DateTimeOffset.UtcNow.AddDays(-1), _ => throw new Exception("nothing to parse"));
        var left = Directory.EnumerateFiles(cacheDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Expect(string.Join(",", left),
            "antigravity-models.v1.json,x-parse-cache.v4.json,x-parse-cache.v4.json.tmp-abc,x-parse-cache.vX.json,y-parse-cache.v1.json");
        // A cache whose name carries no version sweeps nothing.
        new LogParseCache(TriggerTool.Kimi, "plain.json", cacheDirectory).Walk(Array.Empty<string>(), DateTimeOffset.UtcNow, _ => new List<TokenEvent>());
        Expect(Directory.EnumerateFiles(cacheDirectory).Count(), 5);
    }

    private static List<TokenEvent> Parse(string path) => new()
    {
        new TokenEvent(TriggerTool.Claude, new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), "synthetic", 10, 5, 0, 0),
    };

    private static void OverlappingWalksShareOneCache(string logs, string cacheDirectory)
    {
        var stable = Enumerable.Range(0, 40).Select(i => Path.Combine(logs, $"stable-{i}.jsonl")).ToList();
        foreach (var file in stable) File.WriteAllText(file, "x");
        var cache = new LogParseCache(TriggerTool.Claude, "overlap.json", cacheDirectory);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var failures = new List<Exception>();
        var wrong = 0;
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            try
            {
                for (var round = 0; round < 40; round++)
                {
                    // A file of this worker's own comes and goes, so entries are
                    // added and pruned while the other walks run.
                    var own = Path.Combine(logs, $"own-{worker}-{round}.jsonl");
                    File.WriteAllText(own, "x");
                    var found = cache.Walk(stable.Append(own), cutoff, Parse);
                    File.Delete(own);
                    if (found.Count != stable.Count + 1) Interlocked.Increment(ref wrong);
                }
            }
            catch (Exception error)
            {
                lock (failures) failures.Add(error);
            }
        })).ToArray();
        if (!Task.WaitAll(workers, TimeSpan.FromSeconds(60))) throw new Exception("overlapping walks did not finish (a hung thread)");
        Expect(failures.Count == 0, $"no walk failed: {failures.FirstOrDefault()?.Message}");
        Expect(wrong == 0, $"every walk returned every file's events, {wrong} did not");
    }

    private static void TwoCopiesSaveTheSameFile(string logs, string cacheDirectory)
    {
        var files = Enumerable.Range(0, 20).Select(i => Path.Combine(logs, $"shared-{i}.jsonl")).ToList();
        foreach (var file in files) File.WriteAllText(file, "x");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var first = new LogParseCache(TriggerTool.Claude, "shared.json", cacheDirectory);
        var second = new LogParseCache(TriggerTool.Claude, "shared.json", cacheDirectory);
        var failures = new List<Exception>();
        var copies = new[] { first, second }.Select((copy, index) => Task.Run(() =>
        {
            try
            {
                for (var round = 0; round < 60; round++)
                {
                    // Touching a file makes the next walk reparse it and save; each
                    // copy touches its own half, so only the cache is contended.
                    File.WriteAllText(files[(round * 2 + index) % files.Count], "x" + round);
                    copy.Walk(files, cutoff, Parse);
                }
            }
            catch (Exception error)
            {
                lock (failures) failures.Add(error);
            }
        })).ToArray();
        Task.WaitAll(copies);
        Expect(failures.Count == 0, $"no copy failed: {failures.FirstOrDefault()?.Message}");
        var left = Directory.GetFiles(cacheDirectory, "shared.json*").Select(Path.GetFileName).Order().ToList();
        Expect(left.Count == 1 && left[0] == "shared.json", $"only the cache is left behind, got {string.Join(", ", left)}");
        first.Walk(files, cutoff, Parse);
        var reread = new LogParseCache(TriggerTool.Claude, "shared.json", cacheDirectory);
        var warm = reread.Walk(files, cutoff, _ => throw new Exception("a saved entry was not read back"));
        Expect(warm.Count > 0, "the saved cache is valid JSON that a new copy reads back");
    }

    private static void ReloadSharesModelNames(string logs, string cacheDirectory)
    {
        var files = Enumerable.Range(0, 3).Select(i => Path.Combine(logs, $"models-{i}.jsonl")).ToList();
        foreach (var file in files) File.WriteAllText(file, "x");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var at = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        // Each parse makes its own copy of the name, as reading a log line does.
        List<TokenEvent> ParseMany(string path) => Enumerable.Range(0, 50).Select(i =>
            new TokenEvent(TriggerTool.Claude, at.AddMinutes(i), new string("model-x".AsSpan()), 10, 5, 0, 0) { ResponseKey = $"{path}|{i}" }).ToList();
        var parsed = new LogParseCache(TriggerTool.Claude, "models.json", cacheDirectory).Walk(files, cutoff, ParseMany);
        var reloaded = new LogParseCache(TriggerTool.Claude, "models.json", cacheDirectory)
            .Walk(files, cutoff, _ => throw new Exception("a saved entry was not read back"));
        Expect(reloaded.Count == parsed.Count && reloaded.Select(e => e.ResponseKey).Order().SequenceEqual(parsed.Select(e => e.ResponseKey).Order()),
            "the reloaded cache holds the same events");
        Expect(reloaded.Select(e => e.Model).Distinct(ReferenceEqualityComparer.Instance).Count() == 1,
            "every reloaded event shares one model string");
    }
}
