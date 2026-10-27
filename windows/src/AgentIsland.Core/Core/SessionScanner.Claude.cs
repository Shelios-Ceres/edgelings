using System.Text;

namespace AgentIsland.Core;

// Claude: the transcripts under ~/.claude/projects, the sessions Claude Desktop labels them with, and the working directory a transcript records.
public static partial class SessionScanner
{
    private static List<ScannedSession> ScanClaudeTranscripts(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var (desktopSessions, replaced) = ClaudeDesktopIndex();
        var output = new List<ScannedSession>();
        foreach (var (sid, path) in ClaudeTranscriptIndex())
        {
            desktopSessions.TryGetValue(sid, out var desktop);
            // A transcript its desktop session has left behind is history:
            // the session goes on in the newer one, whose turns are its own.
            if (desktop is null && replaced.Contains(sid)) continue;
            // A headless SDK run (Claude Desktop's security reviews, say)
            // that no desktop session points at is machine work, not a
            // conversation: nobody is ever "up" in it.
            if (desktop is null && IsSdkTranscript(path)) continue;
            var cwd = desktop?.Cwd is { Length: > 0 } dc ? dc : TranscriptCwd(path);
            var title = desktop?.Title ?? "";
            var state = ClaudeSessionState(path, now, lastWorking, desktop);
            // An archived session is one the user put away: it may still run,
            // but a finished or failed turn in it never asks for them.
            if (desktop is { IsArchived: true } && state.Status != ActivityState.Working)
                state = (ActivityState.Idle, state.TurnKey, state.Modified, state.TurnEnded);
            output.Add(new ScannedSession(
                TriggerTool.Claude,
                sid,
                cwd,
                string.IsNullOrEmpty(title) ? Fallback(cwd, sid) : title,
                state.Modified,
                state.Status,
                path,
                state.TurnKey,
                desktop is null ? SessionLaunchTarget.Cli : SessionLaunchTarget.ClaudeDesktop) { TurnEnded = state.TurnEnded });
        }
        return output;
    }

    /// True when a transcript belongs to an orchestrated subagent rather
    /// than a user conversation: nested under a subagents/ directory (the
    /// current layout) or named agent-*.jsonl (flat layouts). Main session
    /// files are always UUID-named.
    internal static bool IsClaudeSubagentTranscript(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Contains("subagents", StringComparer.OrdinalIgnoreCase)) return true;
        return segments[^1].StartsWith("agent-", StringComparison.OrdinalIgnoreCase);
    }

    /// Claude Desktop's date for a session's latest turn start. Newer desktops
    /// write latestUserFrameAt: when the latest user or peer message reached
    /// the session, which matches every turn start. lastActivityAt is also
    /// written after a turn has ended, so it only stands in for a desktop that
    /// has no frame date, with the bookkeeping grace.
    private static (DateTimeOffset? Date, TimeSpan Grace) DesktopTurnStart(DesktopSession? desktop) =>
        desktop is null ? (null, DesktopBookkeepingGrace)
        : desktop.UserFrameAt is { } frame ? (frame, TimeSpan.Zero)
        : (desktop.LastActivityAt, DesktopBookkeepingGrace);

    internal static (ActivityState Status, string? TurnKey, DateTimeOffset Modified, bool TurnEnded) ClaudeSessionState(
        string? path,
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        DesktopSession? desktop)
    {
        var (date, grace) = DesktopTurnStart(desktop);
        return SessionState(path, now, lastWorking, date, SessionTurnState.Claude,
            requireTurn: true, externalGrace: grace);
    }

    private static readonly Dictionary<string, bool> SdkTranscripts = new(StringComparer.Ordinal);

    /// True when a transcript was written by a headless SDK run, which names
    /// its entrypoint "sdk-py", "sdk-ts" and the like in its first records.
    /// An answer is remembered once it can no longer change: an entrypoint
    /// was read, or the lines that could carry one were all read. A file seen
    /// before any record names its entrypoint is looked at again next scan.
    internal static bool IsSdkTranscript(string path)
    {
        lock (SdkTranscripts)
        {
            if (SdkTranscripts.TryGetValue(path, out var known)) return known;
        }
        var sdk = false;
        var settled = false;
        try
        {
            using var stream = SharedFile.Open(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var read = 0;
            while (read < SdkLinesRead && reader.ReadLine() is { } line)
            {
                read++;
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null || doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (Jsonl.GetString(doc.RootElement, "entrypoint") is not { } entrypoint) continue;
                sdk = entrypoint.StartsWith("sdk-", StringComparison.Ordinal);
                settled = true;
                break;
            }
            settled |= read >= SdkLinesRead;
        }
        catch
        {
            return false;
        }
        if (!settled) return sdk;
        lock (SdkTranscripts)
        {
            if (SdkTranscripts.Count > 5000) SdkTranscripts.Clear();
            SdkTranscripts[path] = sdk;
        }
        return sdk;
    }

    private const int SdkLinesRead = 40;

    public static Dictionary<string, string> ClaudeTranscriptIndex()
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in IslandPaths.ClaudeProjectRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in SafeEnumerateFiles(root, "*.jsonl"))
            {
                // Subagent transcripts (subagents/ dirs, agent-*.jsonl) are
                // machine fan-out, not user conversations, and never surface.
                // Matched on the path RELATIVE to the root: an absolute path
                // can carry an "agent-…" segment from the user's own folder
                // names (this repo lives in one).
                if (IsClaudeSubagentTranscript(Path.GetRelativePath(root, path)))
                {
                    continue;
                }
                var sid = Path.GetFileNameWithoutExtension(path);
                output.TryAdd(sid, path);
            }
        }
        return output;
    }

    internal sealed record DesktopSession(
        string CliSessionId, string Title, string Cwd, bool IsArchived, DateTimeOffset? LastActivityAt,
        DateTimeOffset? UserFrameAt)
    {
        /// The transcripts the session has left behind: /clear and /compact
        /// carry it on in a new one.
        public IReadOnlyList<string> PriorCliSessionIds { get; init; } = Array.Empty<string>();
    }

    private static IEnumerable<string> EnumerateDesktopSessionFiles() =>
        IslandPaths.ClaudeDesktopSessionsRoots.SelectMany(root => SafeEnumerateFiles(root, "local_*.json"));

    internal static DesktopSession? ParseDesktopSessionFile(string path)
    {
        try
        {
            using var stream = SharedFile.Open(path);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var cliSessionId = Jsonl.GetString(root, "cliSessionId") ?? "";
            var ms = Jsonl.GetDouble(root, "lastActivityAt") ?? Jsonl.GetDouble(root, "createdAt");
            var frame = Jsonl.GetDouble(root, "latestUserFrameAt");
            var prior = new List<string>();
            if (root.TryGetProperty("priorCliSessionIds", out var priors) && priors.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var id in priors.EnumerateArray())
                {
                    if (id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() is { Length: > 0 } text) prior.Add(text);
                }
            }
            return new DesktopSession(
                cliSessionId,
                Jsonl.GetString(root, "title") ?? "",
                Jsonl.GetString(root, "cwd") ?? "",
                Jsonl.GetBool(root, "isArchived") ?? false,
                ms is { } m ? DateTimeOffset.FromUnixTimeMilliseconds((long)m) : null,
                frame is { } f and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)f) : null)
            {
                PriorCliSessionIds = prior,
            };
        }
        catch
        {
            return null;
        }
    }

    /// Claude Desktop's sessions by the transcript each writes now, and the
    /// transcripts they have left behind.
    private static (Dictionary<string, DesktopSession> Current, HashSet<string> Replaced) ClaudeDesktopIndex() =>
        ClaudeDesktopIndex(EnumerateDesktopSessionFiles().Select(DesktopSessionAt));

    internal static (Dictionary<string, DesktopSession> Current, HashSet<string> Replaced) ClaudeDesktopIndex(
        IEnumerable<DesktopSession?> sessions)
    {
        var current = new Dictionary<string, DesktopSession>(StringComparer.Ordinal);
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (session is null) continue;
            // A session just cleared has no transcript yet, but its old ones count.
            replaced.UnionWith(session.PriorCliSessionIds);
            if (string.IsNullOrEmpty(session.CliSessionId)) continue;
            current[session.CliSessionId] = session;
        }
        return (current, replaced);
    }

    private static readonly Dictionary<string, string> TranscriptCwds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (long Ticks, long Size, DesktopSession Session)> DesktopSessions = new(StringComparer.Ordinal);

    /// Transcripts are append-only, so the cwd their first lines name holds.
    /// One that names none yet is read again next time.
    private static string TranscriptCwd(string path)
    {
        lock (ScanCacheGate)
        {
            if (TranscriptCwds.TryGetValue(path, out var known)) return known;
        }
        if (CwdInClaudeTranscript(path) is not { } cwd) return ProjectFromClaudeTranscript(path);
        lock (ScanCacheGate)
        {
            if (TranscriptCwds.Count > 5000) TranscriptCwds.Clear();
            TranscriptCwds[path] = cwd;
        }
        return cwd;
    }

    /// Claude Desktop rewrites a session's file whenever the session changes,
    /// so a parse holds while the file's write time and length do.
    private static DesktopSession? DesktopSessionAt(string path)
    {
        long ticks, size;
        try
        {
            var info = new FileInfo(path);
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        lock (ScanCacheGate)
        {
            if (DesktopSessions.TryGetValue(path, out var cached) && cached.Ticks == ticks && cached.Size == size)
                return cached.Session;
        }
        if (ParseDesktopSessionFile(path) is not { } session) return null;
        lock (ScanCacheGate)
        {
            if (DesktopSessions.Count > 5000) DesktopSessions.Clear();
            DesktopSessions[path] = (ticks, size, session);
        }
        return session;
    }

    /// The transcript itself records the true working directory on nearly
    /// every entry ("cwd") — authoritative, unlike the lossy encoded folder
    /// name, whose dashes un-munge real hyphenated paths into the wrong
    /// directory and then break `claude --resume` launched from it.
    internal static string CwdFromClaudeTranscript(string path) =>
        CwdInClaudeTranscript(path) ?? ProjectFromClaudeTranscript(path);

    private static string? CwdInClaudeTranscript(string path)
    {
        try
        {
            using var stream = SharedFile.Open(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            for (var i = 0; i < 30 && reader.ReadLine() is { } line; i++)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("cwd", out var cwd)
                        && cwd.ValueKind == System.Text.Json.JsonValueKind.String
                        && cwd.GetString() is { Length: > 0 } value)
                    {
                        return value;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// Display-only fallback when the transcript carries no cwd: reverse
    /// the encoded project directory name. The encoding is lossy (path
    /// separators and ':' both became '-'), so this is a best-effort label,
    /// same as on macOS.
    private static string ProjectFromClaudeTranscript(string path)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        if (parent.Length == 0) return "";
        // Windows transcripts encode e.g. C:\Users\me\proj as C--Users-me-proj.
        if (parent.Length > 3 && char.IsAsciiLetter(parent[0]) && parent[1] == '-' && parent[2] == '-')
        {
            return parent[0] + ":\\" + parent[3..].Replace('-', '\\');
        }
        // A UNC root (\\server\share\proj) loses both leading separators to
        // the same dash, so it arrives as --server-share-proj.
        if (parent.StartsWith("--", StringComparison.Ordinal))
        {
            return @"\\" + parent[2..].Replace('-', '\\');
        }
        // Anything else is a session recorded from a shell with its own path
        // shape (MSYS, WSL). Un-munge to backslashes anyway rather than mint a
        // POSIX-looking path no Windows surface can act on.
        return parent.Replace('-', '\\');
    }
}
