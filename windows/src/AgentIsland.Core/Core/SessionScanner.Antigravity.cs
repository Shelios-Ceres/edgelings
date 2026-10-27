using System.Text;

namespace AgentIsland.Core;

// Antigravity: the conversations under ~/.gemini (its three known data roots) and the titles in conversation_summaries.db.
public static partial class SessionScanner
{
    /// Google has renamed the data directory twice already (1.x
    /// `antigravity`, 2.x `antigravity-ide`, plus the separate
    /// `antigravity-cli` root), so every known variant is probed.
    private static readonly string[] AntigravityRootNames = { "antigravity", "antigravity-ide", "antigravity-cli" };

    /// Antigravity keeps a readable transcript per conversation at
    /// `<root>/brain/<conversation-id>/.system_generated/logs/transcript_full.jsonl`
    /// — the desktop IDE and the `agy` CLI write the same shape into their
    /// own roots. Always `transcript_full`, never `transcript` (truncated).
    /// Steps are written once done, so a long command or model call is
    /// silence: an open turn stays work, and Antigravity's own server says
    /// when a run is going or has stopped for the user (AntigravityLiveCascades).
    public static List<ScannedSession> ScanAntigravity(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = new List<ScannedSession>();
        (DateTimeOffset At, IReadOnlyDictionary<string, AntigravityLiveCascades.Run> Runs)? live = null;
        var home = Path.Combine(IslandPaths.Home, ".gemini");
        foreach (var rootName in AntigravityRootNames)
        {
            var root = Path.Combine(home, rootName);
            var brain = Path.Combine(root, "brain");
            if (!Directory.Exists(brain)) continue;
            var summaries = AntigravitySummaries(root);
            foreach (var conversationDir in SafeEnumerateDirectories(brain))
            {
                var conversation = Path.GetFileName(conversationDir);
                var path = Path.Combine(
                    conversationDir, ".system_generated", "logs", "transcript_full.jsonl");
                if (!File.Exists(path)) continue;
                var state = SessionState(path, now, lastWorking, null, SessionTurnState.Antigravity);
                live ??= AntigravityLiveCascades.Read(now);
                var (readAt, runs) = live.Value;
                var status = runs.TryGetValue(conversation, out var run)
                    ? AntigravityLiveCascades.Apply(state.Status, run, readSinceTranscript: readAt > state.Modified)
                    : state.Status;
                var summary = summaries.TryGetValue(conversation, out var s) ? s : default;
                var cwd = summary.Workspace
                    ?? AntigravityWorkspace(root, conversation) ?? "";
                var label = summary.Title
                    ?? AntigravityTitle(path)
                    ?? (conversation.Length > 8 ? conversation[..8] : conversation);
                output.Add(new ScannedSession(
                    TriggerTool.Antigravity,
                    conversation,
                    cwd,
                    label,
                    state.Modified,
                    status,
                    path,
                    state.TurnKey,
                    SessionLaunchTarget.Cli) { TurnEnded = state.TurnEnded });
            }
        }
        return output;
    }

    /// One row per conversation in `conversation_summaries.db`, the plain
    /// SQLite index the CLI keeps beside `brain/`. The db is WAL-mode: a
    /// read-only open needs the -shm file, which only exists while agy
    /// holds the db open — with agy closed the plain open fails, so the
    /// immutable URI (which skips the WAL entirely) is the fallback. Safe:
    /// a cleanly closed db is fully checkpointed, and a stale miss only
    /// costs a nicer label.
    private static Dictionary<string, (string? Title, string? Workspace)> AntigravitySummaries(string root)
    {
        var dbPath = Path.Combine(root, "conversation_summaries.db");
        if (!File.Exists(dbPath)) return new();
        return AntigravitySummaryRows($"Data Source={dbPath};Mode=ReadOnly")
            ?? AntigravitySummaryRows(
                $"Data Source=file:{Uri.EscapeDataString(dbPath).Replace("%5C", "/").Replace("%3A", ":")}?immutable=1;Mode=ReadOnly")
            ?? new();
    }

    /// null means this open failed and the caller should try the other
    /// mode; an empty dictionary means the table really had nothing.
    private static Dictionary<string, (string? Title, string? Workspace)>? AntigravitySummaryRows(
        string connectionString)
    {
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT conversation_id, title, preview, workspace_uris FROM conversation_summaries";
            using var reader = command.ExecuteReader();
            var output = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (string.IsNullOrEmpty(id)) continue;
                // title is usually blank; preview carries the generated name.
                var title = NonEmpty(reader.IsDBNull(1) ? null : reader.GetString(1))
                    ?? NonEmpty(reader.IsDBNull(2) ? null : reader.GetString(2));
                if (title is { Length: > 48 }) title = title[..48];
                var workspace = NonEmpty(reader.IsDBNull(3) ? null : reader.GetString(3)) is { } uris
                    ? AntigravityWorkspaceUri(uris)
                    : null;
                output[id] = (title, workspace);
            }
            return output;
        }
        catch
        {
            return null;
        }

        static string? NonEmpty(string? raw)
        {
            var trimmed = raw?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
    }

    /// `workspace_uris` is a JSON array of file:// URIs; the first one is
    /// the session's directory. Empty for sessions started outside a
    /// project.
    private static string? AntigravityWorkspaceUri(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var text = entry.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile)
                {
                    return uri.LocalPath;
                }
                return text;
            }
        }
        catch
        {
            // Malformed JSON only costs the cwd nicety.
        }
        return null;
    }

    /// The first user message, unwrapped from the `<USER_REQUEST>` envelope
    /// the CLI writes — the fallback when the summaries table has no row
    /// yet (it is written asynchronously).
    private static string? AntigravityTitle(string path)
    {
        try
        {
            using var stream = SharedFile.Open(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            for (var i = 0; i < 40 && reader.ReadLine() is { } line; i++)
            {
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                if (Jsonl.GetString(doc.RootElement, "type") != "USER_INPUT") continue;
                if (Jsonl.GetString(doc.RootElement, "content") is not { Length: > 0 } content) continue;
                return AntigravityRequestText(content);
            }
        }
        catch
        {
            // Unreadable transcript — the id-prefix fallback covers it.
        }
        return null;
    }

    internal static string? AntigravityRequestText(string content)
    {
        var body = content;
        var start = content.IndexOf("<USER_REQUEST>", StringComparison.Ordinal);
        var end = content.IndexOf("</USER_REQUEST>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            body = content[(start + "<USER_REQUEST>".Length)..end];
        }
        var first = body.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (first.Length == 0) return null;
        return first.Length > 48 ? first[..48] : first;
    }

    /// `history.jsonl` maps conversationId to its workspace.
    private static string? AntigravityWorkspace(string root, string conversation)
    {
        try
        {
            var historyPath = Path.Combine(root, "history.jsonl");
            if (!File.Exists(historyPath)) return null;
            foreach (var line in File.ReadLines(historyPath))
            {
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                if (Jsonl.GetString(doc.RootElement, "conversationId") != conversation) continue;
                if (Jsonl.GetString(doc.RootElement, "workspace") is { Length: > 0 } workspace)
                {
                    return workspace;
                }
            }
        }
        catch
        {
            // A torn history file only costs the cwd nicety.
        }
        return null;
    }
}
