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

    internal static List<TokenEvent> ReadDatabase(string path, int lookbackDays)
    {
        // Missing, locked or incomplete databases are failed scans. CostStore
        // commits only successful tasks, preserving the last good totals.
        if (!File.Exists(path)) throw new FileNotFoundException("Cursor ledger unavailable", path);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%'";
        using var reader = command.ExecuteReader();
        var output = new List<TokenEvent>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var json = reader.GetString(0);
            if (!json.Contains("tokenCount", StringComparison.Ordinal)) continue;
            var parsed = ParseBubble(json);
            if (parsed is not null && parsed.Timestamp >= cutoff) output.Add(parsed);
        }
        return output;
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
