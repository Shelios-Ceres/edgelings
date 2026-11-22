using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Reconstructs Codex spend from rollout files. `turn_context` events set
/// the active model for subsequent usage events (mid-session model changes
/// are respected); cached input bills at the cache-read discount.
///
/// Since Sep 2026 Codex writes an authoritative `token_usage_record` per API
/// call carrying a unique `response_id`, which is never copied into forked
/// rollouts or affected by `total_token_usage` counter resets. Older rollouts
/// (and pre-upgrade turns of resumed rollouts) fall back to `token_count`
/// settled through a reset-aware high-water mark.
public static class CodexLogReader
{
    // v5 prefers authoritative token_usage_record (deduped by response_id),
    // drops 1-2 event copied fork snapshots, and recognizes confirmed
    // total_token_usage counter resets on resumed legacy sessions.
    private static readonly LogParseCache Cache = new(TriggerTool.Codex, "codex-parse-cache.v5.json");

    /// Early CLI versions emitted token_count before any turn_context.
    private const string FallbackModel = "gpt-5.4";

    /// Every line ParseFile reads carries one of these; the rest (tool
    /// output, reasoning, messages) are never JSON-parsed.
    private static readonly string[] Markers = { "token_count", "token_usage_record", "turn_context", "session_meta" };

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
        return Deduplicate(Cache.Walk(files, cutoff, ParseFile));
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
        var legacyUsages = new List<Usage>();
        var recorded = new List<TokenEvent>();
        var currentModel = FallbackModel;
        var forked = false;
        DateTimeOffset? sessionMetaAt = null;
        var sawUsageRecord = false;
        // Multi-MB embedded payload lines can't be usage events; they are
        // skipped before parsing to keep peak memory flat.
        foreach (var line in LogLines.Read(path, maxLength: 1_000_000, Markers))
        {
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = Jsonl.GetString(root, "type");
            if (type == "session_meta")
            {
                sessionMetaAt ??= Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp"));
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
            if (type == "token_usage_record")
            {
                if (Jsonl.GetObject(root, "payload") is not { } recordPayload) continue;
                if (Jsonl.GetObject(recordPayload, "usage") is not { } usageObj) continue;
                sawUsageRecord = true;
                if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } at) continue;
                var input = Math.Max(0, Jsonl.GetLong(usageObj, "input_tokens") ?? 0);
                var cached = Math.Clamp(Jsonl.GetLong(usageObj, "cached_input_tokens") ?? 0, 0, input);
                var outputTokens = Math.Max(0, Jsonl.GetLong(usageObj, "output_tokens") ?? 0);
                if (input == 0 && outputTokens == 0) continue;
                var responseId = Jsonl.GetString(recordPayload, "response_id");
                recorded.Add(new TokenEvent(
                    TriggerTool.Codex, at, currentModel,
                    input - cached, outputTokens, 0, cached)
                {
                    ResponseKey = responseId is { Length: > 0 } ? "resp:" + responseId : null,
                });
                continue;
            }
            if (sawUsageRecord) continue;
            if (type != "event_msg") continue;
            if (Jsonl.GetObject(root, "payload") is not { } payload) continue;
            if (Jsonl.GetString(payload, "type") != "token_count") continue;
            if (Jsonl.GetObject(payload, "info") is not { } info) continue;
            if (Jsonl.GetObject(info, "last_token_usage") is not { } last) continue;

            (long, long)? totals = Jsonl.GetObject(info, "total_token_usage") is { } total
                ? (Jsonl.GetLong(total, "input_tokens") ?? 0, Jsonl.GetLong(total, "output_tokens") ?? 0)
                : null;
            legacyUsages.Add(new Usage(
                Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")),
                currentModel,
                Jsonl.GetLong(last, "input_tokens") ?? 0,
                Jsonl.GetLong(last, "cached_input_tokens") ?? 0,
                Jsonl.GetLong(last, "output_tokens") ?? 0,
                totals));
        }
        var output = Settle(legacyUsages, forked, sessionMetaAt, sawUsageRecord);
        if (recorded.Count > 0) output.AddRange(Deduplicate(recorded));
        return output;
    }

    internal static List<TokenEvent> Deduplicate(List<TokenEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return events.Where(e => e.ResponseKey is null || seen.Add(e.ResponseKey)).ToList();
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
    /// A fork or subagent rollout begins with its parent's copied token_count
    /// history, re-stamped to the fork instant: either a burst of three or
    /// more events at most 2 s apart, or a compacted 1-2 event snapshot right
    /// at session_meta whose running total already exceeds the event's own
    /// delta (or precedes modern token_usage_record entries). The burst is
    /// dropped and its last total seeds the file's high-water mark.
    ///
    /// Stale re-emits (429, cancel, compaction) add nothing because each event
    /// counts min(its own delta, growth of the total above the mark). When a
    /// resumed legacy session genuinely resets total_token_usage back to 0 and
    /// the next advancing event confirms the new counter epoch, the mark
    /// resets so resumed turns are not wiped out.
    private static List<TokenEvent> Settle(
        List<Usage> usages, bool forked, DateTimeOffset? sessionMetaAt, bool hasLaterUsageRecords)
    {
        if (usages.Count == 0) return new List<TokenEvent>();
        var cut = 0;
        if (forked)
        {
            var run = 1;
            while (run < usages.Count
                && usages[run].Timestamp is { } at && usages[run - 1].Timestamp is { } before
                && at - before <= BurstGap)
            {
                run++;
            }
            var copiedAtFork = sessionMetaAt is { } metaAt
                && usages[0].Timestamp is { } firstAt
                && firstAt - metaAt <= BurstGap
                && (hasLaterUsageRecords
                    || (usages[0].Totals is { } firstTotals
                        && (firstTotals.Input > usages[0].Input || firstTotals.Output > usages[0].Output)));
            if (run >= 3 || copiedAtFork) cut = run;
        }

        var mark = cut > 0 && usages[cut - 1].Totals is { } seed ? seed : (Input: 0L, Output: 0L);
        var output = new List<TokenEvent>(usages.Count - cut);
        for (var i = cut; i < usages.Count; i++)
        {
            var usage = usages[i];
            long input, outputTokens;
            if (usage.Totals is { } totals)
            {
                if ((totals.Input < mark.Input || totals.Output < mark.Output)
                    && IsConfirmedReset(usages, i, totals, usage))
                {
                    mark = (0L, 0L);
                }
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

    /// True when a drop in total_token_usage starts a fresh counter from 0
    /// (totals == last) AND the next advancing event confirms it by adding its
    /// own delta cleanly onto those totals. Isolated stale/interleaved events
    /// fail confirmation and remain ignored.
    private static bool IsConfirmedReset(
        List<Usage> usages, int index, (long Input, long Output) totals, Usage current)
    {
        if ((current.Input <= 0 && current.Output <= 0)
            || totals.Input != current.Input
            || totals.Output != current.Output)
        {
            return false;
        }
        for (var j = index + 1; j < usages.Count; j++)
        {
            var next = usages[j];
            if (next.Totals is not { } nextTotals) return false;
            if (nextTotals == totals) continue;
            if (next.Input == 0 && next.Output == 0) continue;
            return nextTotals.Input == totals.Input + next.Input
                && nextTotals.Output == totals.Output + next.Output;
        }
        return false;
    }
}
