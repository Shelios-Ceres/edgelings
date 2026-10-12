using System.IO;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Token counts from Kimi Code's session logs. Every model call writes a
/// `usage.record` line to its agent's wire.jsonl:
///
///   %USERPROFILE%\.kimi-code\sessions\&lt;workdir&gt;\session_&lt;uuid&gt;\agents\&lt;agent&gt;\wire.jsonl
///
/// with usage {inputOther, output, inputCacheRead, inputCacheCreation} and the
/// model id. Subagents count too — they spend the same plan. Kimi Code is a
/// subscription with no per-token price, so these are tokens only, never
/// dollars.
public static class KimiLogReader
{
    private static readonly LogParseCache Cache = new(TriggerTool.Kimi, "kimi-parse-cache.v1.json");

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var root = IslandPaths.KimiSessionsRoot;
        if (!Directory.Exists(root)) return new List<TokenEvent>();
        var files = SessionScanner.SafeEnumerateFiles(root, "wire.jsonl");
        return Cache.Walk(files, DateTimeOffset.Now.AddDays(-lookbackDays), ParseFile);
    }

    internal static List<TokenEvent> ParseFile(string path)
    {
        var output = new List<TokenEvent>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                // Tool results and appended messages dominate the log; only
                // usage records matter, and the substring guard skips the rest
                // before any JSON parse.
                if (line.Length > 100_000 || !line.Contains("\"usage.record\"", StringComparison.Ordinal)) continue;
                if (Parse(line) is { } tokenEvent) output.Add(tokenEvent);
            }
        }
        catch
        {
            // A log being written can race the reader; the next scan re-reads.
        }
        return output;
    }

    internal static TokenEvent? Parse(string line)
    {
        using var doc = Jsonl.TryParseLine(line);
        if (doc is null) return null;
        var root = doc.RootElement;
        if (Jsonl.GetString(root, "type") != "usage.record") return null;
        if (Jsonl.GetObject(root, "usage") is not { } usage) return null;
        if (Jsonl.GetDouble(root, "time") is not { } ms || ms <= 0 || ms > 253_402_300_799_999) return null;
        var input = Jsonl.GetLong(usage, "inputOther") ?? 0;
        var output = Jsonl.GetLong(usage, "output") ?? 0;
        var cacheRead = Jsonl.GetLong(usage, "inputCacheRead") ?? 0;
        var cacheCreation = Jsonl.GetLong(usage, "inputCacheCreation") ?? 0;
        if (input == 0 && output == 0 && cacheRead == 0 && cacheCreation == 0) return null;
        return new TokenEvent(
            TriggerTool.Kimi,
            DateTimeOffset.FromUnixTimeMilliseconds((long)ms),
            Jsonl.GetString(root, "model") ?? "kimi",
            input, output, cacheCreation, cacheRead);
    }
}
