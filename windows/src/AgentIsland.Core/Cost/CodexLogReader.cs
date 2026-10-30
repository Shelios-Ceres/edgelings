using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Reconstructs Codex spend from rollout files. `turn_context` events set
/// the active model for subsequent `token_count` events (mid-session model
/// changes are respected); cached input bills at the cache-read discount.
public static class CodexLogReader
{
    // v4 drops the parent history a fork or subagent rollout copies and
    // settles every event through a high-water mark (macOS af01f42).
    private static readonly LogParseCache Cache = new(TriggerTool.Codex, "codex-parse-cache.v4.json");

    /// Early CLI versions emitted token_count before any turn_context.
    private const string FallbackModel = "gpt-5.4";

    /// Every line ParseFile reads carries one of these; the rest (tool
    /// output, reasoning, messages) are never JSON-parsed.
    private static readonly string[] Markers = { "token_count", "turn_context", "session_meta" };

    /// The writer re-stamps a fork's copied history to the fork instant.
    private static readonly TimeSpan BurstGap = TimeSpan.FromSeconds(2);

    private sealed record Usage(
        DateTimeOffset? Timestamp, string Model, long Input, long Cached, long Output, (long Input, long Output)? Totals);

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-lookbackDays);
        // `codex archive` moves finished rollouts to a sibling root; the spend
        // is just as real there, and it compresses what it archives to
        // `.jsonl.zst`. Subagent rollouts stay in: same reason.
        var files = new List<string>();
        foreach (var root in new[] { IslandPaths.CodexSessionsRoot, IslandPaths.CodexArchivedSessionsRoot })
        {
            if (!Directory.Exists(root)) continue;
            files.AddRange(SessionScanner.SafeEnumerateFiles(root, "*.jsonl"));
            files.AddRange(SessionScanner.SafeEnumerateFiles(root, "*.jsonl.zst"));
        }
        files = DropCompressedTwins(files);
        if (files.Count == 0) return new List<TokenEvent>();
        return Cache.Walk(files, cutoff, ParseFile);
    }

    /// A rollout caught mid-compression exists both ways. Count it once, from
    /// the plain file.
    internal static List<string> DropCompressedTwins(List<string> files)
    {
        var plain = new HashSet<string>(
            files.Where(path => !IsCompressed(path)).Select(FileNameOf),
            StringComparer.OrdinalIgnoreCase);
        return files
            .Where(path => !IsCompressed(path) || !plain.Contains(FileNameOf(path)[..^".zst".Length]))
            .ToList();
    }

    /// The last segment, split on either separator.
    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];

    private static bool IsCompressed(string path) => path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);

    internal static List<TokenEvent> ParseFile(string path)
    {
        var usages = new List<Usage>();
        var currentModel = FallbackModel;
        var forked = false;
        // Multi-MB embedded payload lines can't be token_count events; they
        // are skipped before parsing to keep peak memory flat.
        foreach (var line in LogLines.Read(path, maxLength: 1_000_000, Markers))
        {
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = Jsonl.GetString(root, "type");
            if (type == "session_meta")
            {
                if (Jsonl.GetObject(root, "payload") is { } meta && IsForkOrSubagent(meta)) forked = true;
                continue;
            }
            if (type == "turn_context")
            {
                if (Jsonl.GetObject(root, "payload") is { } context
                    && Jsonl.GetString(context, "model") is { Length: > 0 } model)
                {
                    currentModel = model;
                }
                continue;
            }
            if (type != "event_msg") continue;
            if (Jsonl.GetObject(root, "payload") is not { } payload) continue;
            if (Jsonl.GetString(payload, "type") != "token_count") continue;
            if (Jsonl.GetObject(payload, "info") is not { } info) continue;
            if (Jsonl.GetObject(info, "last_token_usage") is not { } last) continue;

            (long, long)? totals = Jsonl.GetObject(info, "total_token_usage") is { } total
                ? (Jsonl.GetLong(total, "input_tokens") ?? 0, Jsonl.GetLong(total, "output_tokens") ?? 0)
                : null;
            usages.Add(new Usage(
                Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")),
                currentModel,
                Jsonl.GetLong(last, "input_tokens") ?? 0,
                Jsonl.GetLong(last, "cached_input_tokens") ?? 0,
                Jsonl.GetLong(last, "output_tokens") ?? 0,
                totals));
        }
        return Settle(usages, forked);
    }

    /// A forked thread names its parent; a spawned one says so in its source.
    private static bool IsForkOrSubagent(JsonElement meta)
    {
        static bool Named(JsonElement meta, string property) =>
            meta.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null;
        if (Named(meta, "forked_from_id") || Named(meta, "parent_thread_id")) return true;
        if (!meta.TryGetProperty("source", out var source)) return false;
        var raw = source.GetRawText();
        return raw.Contains("subagent", StringComparison.Ordinal) || raw.Contains("thread_spawn", StringComparison.Ordinal);
    }

    /// The writer forces two rules (openai/codex, studied for macOS af01f42).
    /// A fork or subagent rollout begins with its parent's whole token_count
    /// history, copied with the timestamps rewritten to the fork instant: a
    /// burst of three or more events at most 2 s apart. The burst is dropped
    /// and its last total seeds the file's high-water mark. And the runtime
    /// re-emits stale events (429, cancel, compaction) and interleaves other
    /// threads' counters, so each event counts min(its own delta, growth of
    /// the total above the mark), never below zero: it can only undercount.
    private static List<TokenEvent> Settle(List<Usage> usages, bool forked)
    {
        var cut = 0;
        if (forked && usages.Count >= 3)
        {
            var run = 1;
            while (run < usages.Count
                && usages[run].Timestamp is { } at && usages[run - 1].Timestamp is { } before
                && at - before <= BurstGap)
            {
                run++;
            }
            if (run >= 3) cut = run;
        }

        var mark = cut > 0 && usages[cut - 1].Totals is { } seed ? seed : (Input: 0L, Output: 0L);
        var output = new List<TokenEvent>(usages.Count - cut);
        foreach (var usage in usages.Skip(cut))
        {
            long input, outputTokens;
            if (usage.Totals is { } totals)
            {
                input = Math.Max(0, Math.Min(usage.Input, totals.Input - mark.Input));
                outputTokens = Math.Max(0, Math.Min(usage.Output, totals.Output - mark.Output));
                mark = (Math.Max(mark.Input, totals.Input), Math.Max(mark.Output, totals.Output));
            }
            else
            {
                // Old CLI lines carry no running total.
                input = usage.Input;
                outputTokens = usage.Output;
            }
            if (usage.Timestamp is not { } timestamp) continue;

            // Codex's input includes the cached part. When the mark clips the
            // input, the counted part is taken as cached first, so the
            // estimate never rises.
            var cached = Math.Min(usage.Cached, input);
            if (input == 0 && outputTokens == 0) continue;
            output.Add(new TokenEvent(
                TriggerTool.Codex, timestamp, usage.Model,
                input - cached, outputTokens, 0, cached));
        }
        return output;
    }
}
