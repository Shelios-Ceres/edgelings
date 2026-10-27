using System.Text;
using AgentIsland.Cost;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// `codex archive` compresses finished rollouts to `.jsonl.zst`. They carry
/// most of the spend, so they must parse like their plain twins, and a
/// rollout caught mid-compression (both forms present) counts once.
public static class CodexArchiveTests
{
    public static void RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentisland-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CompressedMatchesPlain(directory);
            TornArchiveKeepsWhatDecoded(directory);
            PairCountsOnce();
            Console.WriteLine("CodexArchiveTests GREEN");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static string Event(long lastIn, long lastOut, long totalIn, long totalOut) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "event_msg",
            timestamp = "2026-07-16T10:00:00Z",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new { input_tokens = lastIn, cached_input_tokens = 0, output_tokens = lastOut },
                    total_token_usage = new { input_tokens = totalIn, output_tokens = totalOut },
                },
            },
        });

    private static readonly string[] Rollout =
    {
        """{"type":"turn_context","payload":{"model":"gpt-5.5"}}""",
        Event(100, 10, 100, 10),
        Event(100, 10, 100, 10),
        Event(50, 5, 150, 15),
    };

    private static byte[] Zstd(string text)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static void CompressedMatchesPlain(string directory)
    {
        var plain = Path.Combine(directory, "rollout-a.jsonl");
        var packed = Path.Combine(directory, "rollout-b.jsonl.zst");
        File.WriteAllLines(plain, Rollout);
        File.WriteAllBytes(packed, Zstd(string.Join("\n", Rollout) + "\n"));

        var fromPlain = CodexLogReader.ParseFile(plain);
        var fromPacked = CodexLogReader.ParseFile(packed);
        Expect(fromPlain.Count, 2);
        Expect(fromPacked.Count, fromPlain.Count);
        for (var i = 0; i < fromPlain.Count; i++)
        {
            Expect(fromPacked[i] == fromPlain[i], $"event {i} differs between plain and compressed");
        }
        Expect(fromPacked[0].Model, "gpt-5.5");
        Console.WriteLine("PASS a .jsonl.zst rollout parses to the events of its plain twin");
    }

    private static void TornArchiveKeepsWhatDecoded(string directory)
    {
        var torn = Path.Combine(directory, "rollout-torn.jsonl.zst");
        var whole = Zstd(string.Join("\n", Rollout) + "\n");
        File.WriteAllBytes(torn, whole[..(whole.Length - 6)]);
        var events = CodexLogReader.ParseFile(torn);
        Expect(events.Count <= 2, "a torn archive must not invent events");

        var garbage = Path.Combine(directory, "rollout-garbage.jsonl.zst");
        File.WriteAllBytes(garbage, new byte[] { 1, 2, 3, 4, 5 });
        Expect(CodexLogReader.ParseFile(garbage).Count, 0);
        Console.WriteLine("PASS a torn or corrupt archive ends the read without throwing");
    }

    private static void PairCountsOnce()
    {
        var files = new List<string>
        {
            @"C:\codex\sessions\2026\10\rollout-x.jsonl",
            @"C:\codex\archived_sessions\rollout-x.jsonl.zst",
            @"C:\codex\archived_sessions\rollout-y.jsonl.zst",
            @"C:\codex\archived_sessions\rollout-z.jsonl",
        };
        var kept = CodexLogReader.DropCompressedTwins(files);
        Expect(kept.Count, 3);
        Expect(kept.Contains(files[0]), "the plain file stays");
        Expect(!kept.Contains(files[1]), "its compressed twin goes");
        Expect(kept.Contains(files[2]), "a compressed-only rollout stays");
        Expect(kept.Contains(files[3]), "a plain-only rollout stays");
        Console.WriteLine("PASS a plain + compressed pair counts once, from the plain file");
    }
}
