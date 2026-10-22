
namespace AgentIsland.Core;

// Grok, Kimi Code and DeepSeek Harness: each keeps one folder per working directory and one per session.
public static partial class SessionScanner
{
    /// Grok mirrors Claude's layout almost exactly — one directory per
    /// percent-encoded cwd, one directory per session inside it.
    /// `summary.json` carries identity + title; `updates.jsonl` is the live
    /// event stream the turn detector reads, with `chat_history.jsonl` as the
    /// fallback for sessions that predate the updates stream.
    public static List<ScannedSession> ScanGrok(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = new List<ScannedSession>();
        foreach (var projectDir in SafeEnumerateDirectories(IslandPaths.GrokSessionsRoot))
        {
            var cwd = DecodePathSegment(Path.GetFileName(projectDir));
            foreach (var sessionDir in SafeEnumerateDirectories(projectDir))
            {
                var summary = Path.Combine(sessionDir, "summary.json");
                if (!File.Exists(summary)) continue;
                var sid = Path.GetFileName(sessionDir);
                var title = GrokTitle(summary);
                var updates = Path.Combine(sessionDir, "updates.jsonl");
                var transcript = File.Exists(updates)
                    ? updates
                    : Path.Combine(sessionDir, "chat_history.jsonl");
                var state = SessionState(transcript, now, lastWorking, null, SessionTurnState.Grok);
                output.Add(new ScannedSession(
                    TriggerTool.Grok,
                    sid,
                    cwd,
                    string.IsNullOrEmpty(title) ? Fallback(cwd, sid) : title,
                    state.Modified,
                    state.Status,
                    transcript,
                    state.TurnKey,
                    SessionLaunchTarget.Cli));
            }
        }
        return output;
    }

    private static string GrokTitle(string summaryPath)
    {
        try
        {
            using var stream = SharedFile.Open(summaryPath);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return (Jsonl.GetString(doc.RootElement, "session_summary") ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    /// One folder per working directory, one per session; the main agent's
    /// live event log is agents\main\wire.jsonl and state.json carries the
    /// cwd. Subagents log under agents\<name>\ and never surface. Sessions
    /// untouched for a day are skipped before any read: the tree holds every
    /// session ever run. The CLI and the desktop app share this tree; while
    /// the desktop app is open its server also says which sessions wait on
    /// the user (KimiLiveSessions).
    public static List<ScannedSession> ScanKimi(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = new List<ScannedSession>();
        IReadOnlyDictionary<string, KimiLiveSessions.Report>? live = null;
        foreach (var workdir in SafeEnumerateDirectories(IslandPaths.KimiSessionsRoot))
        {
            foreach (var sessionDir in SafeEnumerateDirectories(workdir))
            {
                var wire = Path.Combine(sessionDir, "agents", "main", "wire.jsonl");
                if (!IsRecent(wire, now)) continue;
                var name = Path.GetFileName(sessionDir);
                var sid = name.StartsWith("session_", StringComparison.Ordinal) ? name["session_".Length..] : name;
                var cwd = KimiCwd(Path.Combine(sessionDir, "state.json"));
                var state = SessionState(wire, now, lastWorking, null, SessionTurnState.Kimi);
                var session = new ScannedSession(
                    TriggerTool.Kimi, sid, cwd, Fallback(cwd, sid),
                    state.Modified, state.Status, wire, state.TurnKey, SessionLaunchTarget.Cli);
                live ??= KimiLiveSessions.Read(now);
                output.Add(live.TryGetValue(name, out var report) ? KimiLiveSessions.Apply(session, report) : session);
            }
        }
        return output;
    }

    private static string KimiCwd(string statePath)
    {
        try
        {
            using var stream = SharedFile.Open(statePath);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return Jsonl.GetString(doc.RootElement, "cwd") ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// session.v4.jsonl.zstd is the session's event log as appended zstd
    /// frames; the turn detector reads a decoded tail. The workspace folder
    /// name is the cwd with separators turned into dashes.
    public static List<ScannedSession> ScanDeepSeek(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = new List<ScannedSession>();
        foreach (var workspace in SafeEnumerateDirectories(IslandPaths.DeepSeekSessionsRoot))
        {
            var cwd = DeepSeekCwd(Path.GetFileName(workspace));
            foreach (var sessionDir in SafeEnumerateDirectories(workspace))
            {
                var log = Path.Combine(sessionDir, "session.v4.jsonl.zstd");
                if (!IsRecent(log, now)) continue;
                var name = Path.GetFileName(sessionDir);
                var sid = name.StartsWith("session-", StringComparison.Ordinal) ? name["session-".Length..] : name;
                var state = SessionState(log, now, lastWorking, null, SessionTurnState.DeepSeek,
                    tail: path => ZstdLog.TailLines(path));
                output.Add(new ScannedSession(
                    TriggerTool.DeepSeek, sid, cwd, Fallback(cwd, sid),
                    state.Modified, state.Status, log, state.TurnKey, SessionLaunchTarget.Cli));
            }
        }
        return output;
    }

    /// "--C-Users-me-proj--" → C:\Users\me\proj. Lossy like Claude's folder
    /// names (a real dash reads as a separator); used for the label only.
    internal static string DeepSeekCwd(string folder)
    {
        var name = folder.Trim('-');
        if (name.Length > 2 && char.IsAsciiLetter(name[0]) && name[1] == '-')
        {
            return name[0] + ":\\" + name[2..].Replace('-', '\\');
        }
        return name;
    }

    /// Guest trees keep every session ever run; anything this old cannot be
    /// working or waiting on the user, so it is not even opened.
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(1);

    /// One stat answers both whether the log exists and how old it is.
    private static bool IsRecent(string path, DateTimeOffset now)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && now - new DateTimeOffset(info.LastWriteTimeUtc) <= RecentWindow;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// Grok's per-cwd folder name is percent-encoded, the same trick Claude's
    /// projects folder uses. Decoding is best-effort: a name that is not
    /// valid percent-encoding — or that decodes to nothing — keeps its raw
    /// form rather than leaving the session with an empty cwd.
    private static string DecodePathSegment(string name)
    {
        if (name.Length == 0) return name;
        try
        {
            var decoded = Uri.UnescapeDataString(name);
            return decoded.Length == 0 ? name : decoded;
        }
        catch
        {
            return name;
        }
    }
}
