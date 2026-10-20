using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Reconstructs Claude Code spend from the assistant events in the local
/// project transcripts. Dedup key is messageId:requestId (one API response
/// can be re-written across lines); synthetic placeholder models and
/// zero-token bookkeeping lines are skipped.
public static class ClaudeLogReader
{
    // v2 re-reads first-fragment counts and keeps response identity in cache.
    private static readonly LogParseCache Cache = new(TriggerTool.Claude, "claude-parse-cache.v2.json");

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-lookbackDays);
        var files = new List<string>();
        foreach (var root in IslandPaths.ClaudeProjectRoots)
        {
            if (!Directory.Exists(root)) continue;
            // Subagent transcripts stay in: their tokens are real spend.
            files.AddRange(SessionScanner.SafeEnumerateFiles(root, "*.jsonl"));
        }
        var events = Cache.Walk(files, cutoff, ParseFile);
        return Deduplicate(events);
    }

    internal static List<TokenEvent> ParseFile(string path)
    {
        var output = new List<TokenEvent>();
        using var stream = SharedFile.Open(path);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            // A pathological multi-MB line (an embedded payload) can't be
            // a usage event; skip before parsing so it never balloons
            // peak memory (the macOS 64 MiB backstop's job, done harder).
            if (line.Length > 1_000_000) continue;
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (Jsonl.GetString(root, "type") != "assistant") continue;
            if (Jsonl.GetObject(root, "message") is not { } message) continue;
            if (Jsonl.GetObject(message, "usage") is not { } usage) continue;

            var model = Jsonl.GetString(message, "model") ?? "";
            if (model.Length == 0
                || model == "<synthetic>"
                || model.StartsWith("synthetic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var input = Jsonl.GetLong(usage, "input_tokens") ?? 0;
            var outputTokens = Jsonl.GetLong(usage, "output_tokens") ?? 0;
            var cacheCreation = Jsonl.GetLong(usage, "cache_creation_input_tokens") ?? 0;
            var cacheRead = Jsonl.GetLong(usage, "cache_read_input_tokens") ?? 0;
            if (input == 0 && outputTokens == 0 && cacheCreation == 0 && cacheRead == 0) continue;

            if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } timestamp) continue;

            var messageId = Jsonl.GetString(message, "id");
            var requestId = Jsonl.GetString(root, "requestId");
            var dedupKey = messageId is { Length: > 0 } && requestId is { Length: > 0 }
                ? $"{messageId}:{requestId}"
                : null;

            output.Add(new TokenEvent(
                TriggerTool.Claude, timestamp, model,
                input, outputTokens, cacheCreation, cacheRead)
            {
                ResponseKey = dedupKey,
                IsFinalUsage = Jsonl.GetString(message, "stop_reason") is { Length: > 0 },
            });
        }
        // Within-file dedup happens here so the cache stores clean events;
        // cross-file dedup happens in Scan.
        return Deduplicate(output);
    }

    internal static List<TokenEvent> Deduplicate(List<TokenEvent> events)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<TokenEvent>(events.Count);
        foreach (var tokenEvent in events)
        {
            var key = tokenEvent.ResponseKey is { } identity ? "id:" + identity
                : $"shape:{tokenEvent.Timestamp.ToUnixTimeMilliseconds()}|{tokenEvent.Model}|" +
                      $"{tokenEvent.InputTokens}|{tokenEvent.OutputTokens}|" +
                      $"{tokenEvent.CacheCreationTokens}|{tokenEvent.CacheReadTokens}";
            if (positions.TryGetValue(key, out var index))
            {
                var previous = output[index];
                var replace = tokenEvent.IsFinalUsage != previous.IsFinalUsage
                    ? tokenEvent.IsFinalUsage
                    : tokenEvent.Timestamp > previous.Timestamp
                        || tokenEvent.Timestamp == previous.Timestamp && tokenEvent.OutputTokens >= previous.OutputTokens;
                if (replace) output[index] = tokenEvent;
                continue;
            }
            positions[key] = output.Count;
            output.Add(tokenEvent);
        }
        return output;
    }
}
