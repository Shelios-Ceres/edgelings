using AgentIsland.Core;
using AgentIsland.Cost;

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
            Console.WriteLine("LogParseCacheTests GREEN");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
