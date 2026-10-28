using System.Text.Json;
using AgentIsland.Core;
using Microsoft.Data.Sqlite;

namespace AgentIsland.Cost;

public static class CursorLogReader
{
    private static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public static List<TokenEvent> Scan(int lookbackDays) => ReadDatabase(DatabasePath, lookbackDays);

    private sealed record LastRead(string Path, string Stamp, DateTimeOffset Cutoff, List<TokenEvent> Events);

    /// The ledger can be GBs, and while Cursor is closed nothing in it moves:
    /// the last read stands until the database or its WAL changes.
    private static volatile LastRead? _last;

    internal static List<TokenEvent> ReadDatabase(string path, int lookbackDays)
    {
        // Missing, locked or incomplete databases are failed scans. CostStore
        // commits only successful tasks, preserving the last good totals.
        if (!File.Exists(path)) throw new FileNotFoundException("Cursor ledger unavailable", path);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        // Taken before the read, so a commit landing during it reads anew.
        var stamp = Stamp(path);
        if (stamp is not null && _last is { } last && string.Equals(last.Path, path, StringComparison.OrdinalIgnoreCase)
            && last.Stamp == stamp && cutoff >= last.Cutoff)
        {
            return last.Events.Where(tokenEvent => tokenEvent.Timestamp >= cutoff).ToList();
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // Bubbles without a count never leave SQLite as strings.
        command.CommandText = "SELECT value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND instr(value, 'tokenCount') > 0";
        using var reader = command.ExecuteReader();
        var output = new List<TokenEvent>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var parsed = ParseBubble(reader.GetString(0));
            if (parsed is not null && parsed.Timestamp >= cutoff) output.Add(parsed);
        }
        _last = stamp is null ? null : new LastRead(path, stamp, cutoff, output);
        return output.ToList();
    }

    /// Write time and length alone can miss a commit: NTFS stamps a file that a
    /// writer holds open lazily, a commit into free pages keeps the length, and a
    /// reset WAL is rewritten from its start at the same length. So the stamp
    /// adds SQLite's own change counters: the database header's change counter
    /// and page count (rollback mode), the WAL header's checkpoint sequence and
    /// salts (they change on each reset), and the wal-index header's iChange and
    /// mxFrame (they move on every WAL write). Null when any of it cannot be
    /// read, so the caller reads the database instead of guessing.
    internal static string? Stamp(string path)
    {
        static string Of(string file, int offset, int count)
        {
            var info = new FileInfo(file);
            if (!info.Exists) return "none";
            var buffer = new byte[count];
            var read = 0;
            // Shared with the writer, and no SQLite lock is taken: SQLite's
            // byte-range locks sit far past these header bytes.
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
            {
                stream.Position = offset;
                int step;
                while (read < count && (step = stream.Read(buffer, read, count - read)) > 0) read += step;
            }
            return $"{info.LastWriteTimeUtc.Ticks}/{info.Length}/{Convert.ToHexString(buffer, 0, read)}";
        }
        try
        {
            return Of(path, 24, 8) + "|" + Of(path + "-wal", 0, 32) + "|" + Of(path + "-shm", 0, 48);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Returns null for bubbles without a usable token count. Absent or {0,0}
    /// counts are skipped — the survey machine is all historical zero sessions,
    /// so the reader simply yields nothing there rather than a wall of zeros.
    /// A bubble with no date of its own is skipped too: dated by the database,
    /// whose mtime is today while Cursor runs, old usage would read as today's
    /// on every scan.
    internal static TokenEvent? ParseBubble(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tokenCount", out var counts)
                || counts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var input = ReadInt(counts, "inputTokens");
            var output = ReadInt(counts, "outputTokens");
            if (input == 0 && output == 0) return null;
            if (BubbleDate(root) is not { } createdAt) return null;

            return new TokenEvent(
                TriggerTool.Cursor,
                createdAt,
                // A Cursor bubble carries no model field; a constant keeps
                // downstream grouping stable. Nothing prices it, so cost is null.
                "cursor",
                input,
                output,
                0,
                0,
                null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    /// A bubble's `createdAt` when present (epoch ms as Cursor writes it, or an
    /// ISO string defensively).
    private static DateTimeOffset? BubbleDate(JsonElement root)
    {
        if (!root.TryGetProperty("createdAt", out var raw)) return null;
        if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var epoch))
        {
            // Values this large are milliseconds; anything smaller is seconds.
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        if (raw.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(raw.GetString(), out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
