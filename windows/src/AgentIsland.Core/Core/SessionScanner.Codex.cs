using System.Text;

namespace AgentIsland.Core;

// Codex: the rollouts under ~/.codex/sessions and its session index.
public static partial class SessionScanner
{
    private const int MonitoringCodexLimit = 120;

    /// Every recent rollout, newest first, up to the limit; a project's
    /// several threads each count.
    public static List<ScannedSession> ScanCodex(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var root = IslandPaths.CodexSessionsRoot;
        if (!Directory.Exists(root)) return new List<ScannedSession>();

        var files = SafeEnumerateFiles(root, "*.jsonl");
        // Precompute mtimes once — calling Mtime() inside the comparator issues
        // O(n log n) GetLastWriteTime syscalls over a full recursive tree.
        var mtimes = new Dictionary<string, DateTimeOffset>(files.Count, StringComparer.Ordinal);
        foreach (var f in files) mtimes[f] = Mtime(f);
        files.Sort((a, b) => mtimes[b].CompareTo(mtimes[a]));

        var titles = CodexTitleIndex();
        var output = new List<ScannedSession>();
        foreach (var path in files)
        {
            if (CachedCodexMeta(path) is not { } meta || string.IsNullOrEmpty(meta.Sid)) continue;
            var (sid, cwd, kind) = meta;
            // Neither machine tier ever surfaces: automation runs have no
            // human in them at all, and subagent fan-out finishes constantly
            // without anyone's turn coming up.
            if (kind is CodexRolloutKind.Automation or CodexRolloutKind.Subagent) continue;
            var state = SessionState(path, now, lastWorking, null, SessionTurnState.Codex);
            output.Add(new ScannedSession(
                TriggerTool.Codex,
                sid,
                cwd,
                titles.TryGetValue(sid, out var title) ? title : Fallback(cwd, sid),
                state.Modified,
                state.Status,
                path,
                state.TurnKey,
                SessionLaunchTarget.Cli));
            if (output.Count >= MonitoringCodexLimit) break;
        }
        return output;
    }

    internal enum CodexRolloutKind
    {
        Interactive,
        Subagent,
        Automation,
    }

    /// Reads the first JSONL line in full. Codex's `session_meta` is line 1
    /// but can be tens of KB (it embeds the full base instructions), so keep
    /// pulling chunks until the first newline.
    private static (string Sid, string Cwd, CodexRolloutKind Kind)? CodexMeta(string path)
    {
        byte[] firstLine;
        try
        {
            using var stream = SharedFile.Open(path);
            var buffer = new List<byte>(65_536);
            var chunk = new byte[65_536];
            var newline = -1;
            while (newline < 0)
            {
                var read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                for (var i = 0; i < read && newline < 0; i++)
                {
                    if (chunk[i] == (byte)'\n') newline = buffer.Count + i;
                }
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                if (buffer.Count > 2_000_000) break;
            }
            firstLine = newline >= 0 ? buffer.Take(newline).ToArray() : buffer.ToArray();
        }
        catch
        {
            return null;
        }
        return ParseCodexMeta(Encoding.UTF8.GetString(firstLine));
    }

    /// Classifies a rollout from its session_meta line. `payload.source` is
    /// a plain string for direct sessions ("cli", "vscode", "exec", "mcp")
    /// and an object for machine-driven ones: {"subagent": …} for spawned /
    /// review / compact threads, {"internal": …} for probes. Subagent
    /// threads share the interactive codex originator, so the source field
    /// is the only thing separating a fan-out worker from the user's own
    /// thread — without it every finished subagent raises a turn alarm.
    internal static (string Sid, string Cwd, CodexRolloutKind Kind)? ParseCodexMeta(string firstLine)
    {
        using var doc = Jsonl.TryParseLine(firstLine);
        if (doc is null) return null;
        var root = doc.RootElement;
        if (Jsonl.GetString(root, "type") != "session_meta") return null;
        if (Jsonl.GetObject(root, "payload") is not { } payload) return null;

        var kind = CodexRolloutKind.Interactive;
        if (payload.TryGetProperty("source", out var source))
        {
            if (source.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (source.GetString() is "exec" or "mcp") kind = CodexRolloutKind.Automation;
            }
            else if (source.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (source.TryGetProperty("subagent", out _)) kind = CodexRolloutKind.Subagent;
                else if (source.TryGetProperty("internal", out _)) kind = CodexRolloutKind.Automation;
            }
        }

        // Automation rollouts (orchestrator-driven runs, probes, and
        // `codex exec`) finish constantly; a human is never "up" in them,
        // so they must not raise turn alarms or drive the logo. Interactive
        // sessions carry a codex-family originator; missing originator =
        // old CLI, treat as interactive. The prefix check is
        // case-insensitive: the Windows desktop app stamps "Codex Desktop".
        var originator = Jsonl.GetString(payload, "originator") ?? "";
        if (originator.Length > 0
            && (!originator.StartsWith("codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(originator, "codex_exec", StringComparison.OrdinalIgnoreCase)))
        {
            kind = CodexRolloutKind.Automation;
        }

        return (Jsonl.GetString(payload, "id") ?? "", Jsonl.GetString(payload, "cwd") ?? "", kind);
    }

    private sealed record TitleIndex(string Stamp, Dictionary<string, string> Titles);

    private static TitleIndex _codexTitles = new("", new());

    /// Codex rewrites its title index only when a thread is named.
    private static Dictionary<string, string> CodexTitleIndex()
    {
        var stamp = FileStamp(IslandPaths.CodexSessionIndexFile);
        var cached = _codexTitles;
        if (stamp.Length > 0 && stamp == cached.Stamp) return cached.Titles;
        var titles = ReadCodexTitleIndex();
        _codexTitles = new TitleIndex(stamp, titles);
        return titles;
    }

    private static Dictionary<string, string> ReadCodexTitleIndex()
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = IslandPaths.CodexSessionIndexFile;
        if (!File.Exists(path)) return output;
        string[] lines;
        try
        {
            using var stream = SharedFile.Open(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch
        {
            return output;
        }
        foreach (var line in lines)
        {
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            var id = Jsonl.GetString(doc.RootElement, "id");
            var title = Jsonl.GetString(doc.RootElement, "thread_name")?.Trim();
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title)) output[id!] = title!;
        }
        return output;
    }

    private static readonly Dictionary<string, (string Sid, string Cwd, CodexRolloutKind Kind)> CodexMetas = new(StringComparer.Ordinal);

    /// A rollout's session_meta is its first line, written once, so each
    /// file's is read once. A line still being written parses as nothing and
    /// is read again next time.
    private static (string Sid, string Cwd, CodexRolloutKind Kind)? CachedCodexMeta(string path)
    {
        lock (ScanCacheGate)
        {
            if (CodexMetas.TryGetValue(path, out var known)) return known;
        }
        if (CodexMeta(path) is not { } meta) return null;
        lock (ScanCacheGate)
        {
            if (CodexMetas.Count > 5000) CodexMetas.Clear();
            CodexMetas[path] = meta;
        }
        return meta;
    }
}
