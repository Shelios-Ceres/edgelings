using AgentIsland.Cost;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Pins Codex's token accounting (the macOS v3 rules of af01f42): an event
/// counts min(its last_token_usage, growth of total_token_usage above the
/// file's high-water mark), so replayed and stale re-emitted events add
/// nothing; a fork or subagent rollout drops the parent history it copies.
public static class CodexReplayGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("replayed deltas skipped, shrunk totals add nothing, legacy lines kept", Replays),
            ("a fork's copied parent history is dropped, its own turns count", Fork),
            ("a subagent's copied parent history is dropped", Subagent),
            ("stale re-emits after the copy add nothing", StaleAfterFork),
            ("a session that is not a fork keeps a quick first burst", NotForked),
            ("a fork without a copied burst counts every turn", ForkWithoutBurst),
            ("a mark-clipped input is counted as cached first", ClippedInputIsCached),
            ("a confirmed counter reset on resume counts all resumed turns", ConfirmedCounterReset),
            ("authoritative token_usage_record wins and deduplicates by response_id", AuthoritativeUsageRecords),
            ("legacy turns before the first token_usage_record stay counted", MixedLegacyAndUsageRecords),
            ("a forked subagent drops a single copied parent snapshot", SingleCopiedParentSnapshot),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS Codex accounting: {name}");
        }
        Console.WriteLine("CodexReplayGuardTests GREEN");
    }

    private static void Replays()
    {
        var events = Parse(
            Event(0, 100, 10, 100, 10),   // turn 1 counts
            Event(1, 100, 10, 100, 10),   // its replay adds nothing
            Event(60, 50, 5, 150, 15),    // turn 2, totals advanced, counts
            Event(61, 50, 5, 150, 15),    // its replay adds nothing
            Event(120, 30, 3, 30, 3),     // totals below the mark add nothing
            BareEvent(180, 20, 2));       // an old CLI line without totals counts
        Expect(events.Count == 3, $"expected 3 billed events, got {events.Count}");
        Expect(events.Sum(e => e.InputTokens) == 170, $"expected 170 input tokens (100+50+20), got {events.Sum(e => e.InputTokens)}");
        Expect(events.Sum(e => e.OutputTokens) == 17, $"expected 17 output tokens (10+5+2), got {events.Sum(e => e.OutputTokens)}");
    }

    /// The parent used 600 in / 60 out before the fork; the copy carries its
    /// four events, all stamped at the fork instant.
    private static string[] CopiedParent() => new[]
    {
        Event(0, 100, 10, 100, 10),
        Event(0, 150, 15, 250, 25),
        Event(0, 150, 15, 400, 40),
        Event(0, 200, 20, 600, 60),
    };

    private static void Fork()
    {
        var events = Parse(new[] { Meta(new { id = "child", forked_from_id = "parent", source = "cli" }) }
            .Concat(CopiedParent())
            .Append(Event(30, 50, 5, 650, 65))
            .Append(Event(90, 70, 7, 720, 72))
            .ToArray());
        Expect(events.Count == 2, $"only the fork's own two turns count, got {events.Count}");
        Expect(events.Sum(e => e.InputTokens) == 120 && events.Sum(e => e.OutputTokens) == 12,
            $"the fork's own turns are 120 in / 12 out, got {events.Sum(e => e.InputTokens)} / {events.Sum(e => e.OutputTokens)}");
        Expect(events.All(e => e.Timestamp >= Start.AddSeconds(30)), "nothing is dated at the fork instant");
    }

    private static void Subagent()
    {
        var meta = Meta(new { id = "child", source = new { subagent = new { thread_spawn = new { parent_thread_id = "parent", depth = 1 } } } });
        var events = Parse(new[] { meta }.Concat(CopiedParent()).Append(Event(45, 40, 4, 640, 64)).ToArray());
        Expect(events.Count == 1 && events[0].InputTokens == 40 && events[0].OutputTokens == 4,
            $"only the subagent's own 40 / 4 count, got {events.Count} events, {events.Sum(e => e.InputTokens)} / {events.Sum(e => e.OutputTokens)}");
        var review = Meta(new { id = "child", source = new { subagent = "review" } });
        Expect(Parse(new[] { review }.Concat(CopiedParent()).ToArray()).Count == 0, "a review subagent's copied history counts nothing");
    }

    private static void StaleAfterFork()
    {
        var events = Parse(new[] { Meta(new { id = "child", forked_from_id = "parent" }) }
            .Concat(CopiedParent())
            .Append(Event(30, 50, 5, 650, 65))      // own turn: counts
            .Append(Event(40, 150, 15, 400, 40))    // stale re-emit of the parent's third event: nothing
            .Append(Event(50, 50, 5, 650, 65))      // re-emit of the own turn: nothing
            .Append(Event(60, 80, 8, 700, 70))      // last says 80, the total grew 50 above the mark: 50
            .ToArray());
        Expect(events.Sum(e => e.InputTokens) == 100 && events.Sum(e => e.OutputTokens) == 10,
            $"own turns count once and never more than the total grew, got {events.Sum(e => e.InputTokens)} / {events.Sum(e => e.OutputTokens)}");
    }

    private static void NotForked()
    {
        var events = Parse(new[] { Meta(new { id = "solo", source = "cli" }) }
            .Append(Event(0, 100, 10, 100, 10))
            .Append(Event(1, 100, 10, 200, 20))
            .Append(Event(2, 100, 10, 300, 30))
            .ToArray());
        Expect(events.Sum(e => e.InputTokens) == 300, $"a session of its own keeps every turn, got {events.Sum(e => e.InputTokens)}");
    }

    private static void ForkWithoutBurst()
    {
        var events = Parse(new[] { Meta(new { id = "child", forked_from_id = "parent" }) }
            .Append(Event(0, 100, 10, 100, 10))
            .Append(Event(1, 100, 10, 200, 20))
            .Append(Event(30, 100, 10, 300, 30))
            .ToArray());
        Expect(events.Sum(e => e.InputTokens) == 300, $"two quick events are no copied history, got {events.Sum(e => e.InputTokens)}");
    }

    private static void ClippedInputIsCached()
    {
        var events = Parse(
            Event(0, 100, 10, 100, 10),
            Event(60, 80, 8, 150, 15, cached: 60));
        var clipped = events[1];
        Expect(clipped.CacheReadTokens == 50 && clipped.InputTokens == 0 && clipped.OutputTokens == 5,
            $"50 counted input of which 60 were cached is all cache reads, got {clipped.InputTokens} / {clipped.CacheReadTokens} / {clipped.OutputTokens}");
    }

    private static void ConfirmedCounterReset()
    {
        var events = Parse(
            Event(0, 1000, 100, 1000, 100),
            Event(60, 2000, 200, 3000, 300),
            // Session resumed hours later: total_token_usage resets to 0 and climbs cleanly.
            Event(3600, 400, 40, 400, 40),
            Event(3601, 400, 40, 400, 40), // replay inside resumed epoch adds nothing
            Event(3660, 500, 50, 900, 90),
            Event(3720, 600, 60, 1500, 150));
        Expect(events.Count == 5, $"expected 5 billed events across reset, got {events.Count}");
        Expect(events.Sum(e => e.InputTokens) == 4500 && events.Sum(e => e.OutputTokens) == 450,
            $"expected 4500 in / 450 out, got {events.Sum(e => e.InputTokens)} / {events.Sum(e => e.OutputTokens)}");
    }

    private static void AuthoritativeUsageRecords()
    {
        var events = Parse(
            UsageRecord(0, "resp_1", 1000, 600, 100),
            Event(0, 1000, 100, 1000, 100, cached: 600), // paired token_count ignored once TUR starts
            UsageRecord(1, "resp_1", 1000, 600, 100),    // duplicate response_id ignored
            UsageRecord(60, "resp_2", 500, 400, 50),
            Event(60, 500, 50, 500, 50, cached: 400));   // even a counter reset doesn't drop TUR
        Expect(events.Count == 2, $"expected 2 authoritative records, got {events.Count}");
        Expect(events.Sum(e => e.WireTokens) == 1650 && events.Sum(e => e.BillableTokens) == 650,
            $"expected 1650 wire / 650 billable, got {events.Sum(e => e.WireTokens)} / {events.Sum(e => e.BillableTokens)}");
    }

    private static void MixedLegacyAndUsageRecords()
    {
        var events = Parse(
            Event(0, 300, 30, 300, 30),
            Event(60, 400, 40, 700, 70),
            // Upgraded CLI resumed this rollout and began emitting token_usage_record.
            UsageRecord(3600, "resp_a", 500, 300, 50),
            Event(3600, 500, 50, 1200, 120, cached: 300));
        Expect(events.Count == 3, $"expected 2 legacy + 1 recorded event, got {events.Count}");
        Expect(events.Sum(e => e.WireTokens) == 1320 && events.Sum(e => e.OutputTokens) == 120,
            $"expected 1320 wire / 120 out, got {events.Sum(e => e.WireTokens)} / {events.Sum(e => e.OutputTokens)}");
    }

    private static void SingleCopiedParentSnapshot()
    {
        var meta = Meta(new { id = "child", parent_thread_id = "parent", source = new { subagent = new { other = "guardian" } } });
        var events = Parse(
            meta,
            // Single compacted parent token_count copied at fork instant (total > last).
            Event(0, 82_000, 200, 750_000, 1_700, cached: 70_000),
            UsageRecord(5, "resp_child", 88_000, 80_000, 120),
            Event(5, 88_000, 120, 838_000, 1_820, cached: 80_000));
        Expect(events.Count == 1, $"only the subagent's own call counts, got {events.Count}");
        Expect(events[0].WireTokens == 88_120 && events[0].BillableTokens == 8_120,
            $"expected 88120 wire / 8120 billable, got {events[0].WireTokens} / {events[0].BillableTokens}");

        // Even with no subsequent turns (aborted subagent), a single copied snapshot with total > last counts nothing.
        var aborted = Parse(meta, Event(0, 82_000, 200, 750_000, 1_700, cached: 70_000));
        Expect(aborted.Count == 0, $"aborted subagent's copied parent snapshot must count nothing, got {aborted.Count}");
    }

    private static string UsageRecord(int seconds, string responseId, long input, long cached, long output) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "token_usage_record",
            timestamp = Start.AddSeconds(seconds),
            payload = new
            {
                response_id = responseId,
                usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output },
            },
        });

    private static List<TokenEvent> Parse(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, lines);
            return CodexLogReader.ParseFile(path);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string Meta(object payload) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "session_meta", timestamp = Start, payload });

    private static string Event(int seconds, long lastIn, long lastOut, long totalIn, long totalOut, long cached = 0) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "event_msg",
            timestamp = Start.AddSeconds(seconds),
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new { input_tokens = lastIn, cached_input_tokens = cached, output_tokens = lastOut },
                    total_token_usage = new { input_tokens = totalIn, output_tokens = totalOut },
                },
            },
        });

    private static string BareEvent(int seconds, long lastIn, long lastOut) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "event_msg",
            timestamp = Start.AddSeconds(seconds),
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new { input_tokens = lastIn, cached_input_tokens = 0, output_tokens = lastOut },
                },
            },
        });
}
