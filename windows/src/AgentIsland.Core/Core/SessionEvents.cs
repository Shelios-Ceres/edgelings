using System.IO;
using System.Text.Json;

namespace AgentIsland.Core;

/// What a session wants from the user. These are the things an agent's own
/// app would notify about, read from the transcripts instead of from the
/// notifications, so they keep working with those switched off.
public enum SessionEventKind
{
    /// A turn ended and waits on the user (a scheduled task's run included).
    Done,
    /// AskUserQuestion, ExitPlanMode, an MCP elicitation.
    Question,
    /// A permission prompt is open.
    Approval,
    /// A turn ended on an error.
    Error,
    /// A scheduled task could not run.
    TaskSkipped,
}

/// One thing a session wants, or wanted at a moment. Key identifies it for
/// good — session, turn and kind — so a later scan never repeats it.
public sealed record SessionEvent(
    TriggerTool Tool,
    string SessionId,
    string Title,
    SessionEventKind Kind,
    string Key,
    DateTimeOffset At,
    string? Text = null)
{
    public string SessionKey => Tool.RawValue() + ":" + SessionId;
}

/// What one scan found: Fresh is what turned up since the last scan, Waiting
/// everything that still waits on the user right now.
public sealed record SessionObservation(IReadOnlyList<SessionEvent> Fresh, IReadOnlyList<SessionEvent> Waiting)
{
    public static readonly SessionObservation Empty = new(Array.Empty<SessionEvent>(), Array.Empty<SessionEvent>());
}

/// Compares what each scan sees of every session with what it has already
/// told. The first scan only learns: whatever already waits at launch is on
/// the list but never speaks.
public sealed class SessionEventDetector
{
    /// A turn that ended longer ago than this is not news, however a scan
    /// came to see it first (a session file that appeared late, say).
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    private const int SeenLimit = 4000;

    private readonly Func<TriggerTool, string, DateTimeOffset, PhaseReading?> _classify;
    private readonly Func<IReadOnlyList<SkippedTask>> _skips;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _primed;

    public SessionEventDetector()
        : this(CachedPhase.Classify, ScheduledTaskSkips.Read)
    {
    }

    internal SessionEventDetector(
        Func<TriggerTool, string, DateTimeOffset, PhaseReading?> classify,
        Func<IReadOnlyList<SkippedTask>> skips)
    {
        _classify = classify;
        _skips = skips;
    }

    public SessionObservation Observe(IEnumerable<ScannedSession> sessions, DateTimeOffset now)
    {
        var fresh = new List<SessionEvent>();
        var waiting = new List<SessionEvent>();
        foreach (var session in ActivityMonitor.LatestSessions(sessions))
        {
            var own = new List<SessionEvent>();
            if (Wait(session, now, session.Label) is { } wait) own.Add(wait);

            foreach (var item in own)
            {
                waiting.Add(item);
                if (_seen.Add(item.Key) && _primed && IsNews(item, now)) fresh.Add(item);
            }
        }

        foreach (var skipped in _skips())
        {
            var key = "skip:" + skipped.Id + ":" + skipped.Stamp;
            var item = new SessionEvent(TriggerTool.Claude, skipped.Id, skipped.Name, SessionEventKind.TaskSkipped, key, skipped.At ?? now);
            if (_seen.Add(key) && _primed) fresh.Add(item);
        }

        if (_seen.Count > SeenLimit)
        {
            // Everything still waiting must stay known, or it would speak again.
            var keep = new HashSet<string>(waiting.Select(item => item.Key), StringComparer.Ordinal);
            _seen.RemoveWhere(key => !keep.Contains(key) && !key.StartsWith("skip:", StringComparison.Ordinal));
        }

        _primed = true;
        fresh.Sort((a, b) => a.At.CompareTo(b.At));
        return new SessionObservation(fresh, waiting);
    }

    /// A finished turn is news only while it is fresh; a prompt
    /// is news the moment it first shows, however it came to be seen.
    private static bool IsNews(SessionEvent item, DateTimeOffset now) =>
        item.Kind is SessionEventKind.Question or SessionEventKind.Approval || now - item.At < FreshFor;

    private SessionEvent? Wait(ScannedSession session, DateTimeOffset now, string title)
    {
        var sessionKey = session.Id;
        if (session.PendingPrompt is { } prompt && prompt.Phase is AgentPhase.Asking or AgentPhase.Approving)
            return Prompt(session, title, prompt, now);

        if (session.Status == ActivityState.Working && session.TranscriptPath is { } working
            && _classify(session.Tool, working, now) is { } reading
            && reading.Phase is AgentPhase.Asking or AgentPhase.Approving)
        {
            return Prompt(session, title, reading, now);
        }

        if (session.Status != ActivityState.NeedsYou) return null;
        var turn = session.TurnKey ?? session.Modified.UtcTicks.ToString();
        var failed = session.TranscriptPath is { } done && _classify(session.Tool, done, now) is { Phase: AgentPhase.Failed };
        return new SessionEvent(
            session.Tool, session.SessionId, title,
            failed ? SessionEventKind.Error : SessionEventKind.Done,
            (failed ? "error:" : "done:") + sessionKey + ":" + turn,
            session.Modified);
    }

    private static SessionEvent Prompt(ScannedSession session, string title, PhaseReading prompt, DateTimeOffset now)
    {
        var at = prompt.At ?? now;
        var approval = prompt.Phase == AgentPhase.Approving;
        return new SessionEvent(
            session.Tool, session.SessionId, title,
            approval ? SessionEventKind.Approval : SessionEventKind.Question,
            (approval ? "approval:" : "question:") + session.Id + ":" + at.UtcTicks,
            at);
    }
}

/// A transcript's phase, read again only when the file changed: a scan runs
/// every few seconds, and a working session's tail is 64 KB. An open question
/// stays open as the file ages, so the age the classifier folds in is not
/// needed here; a permission prompt reaches a scan as the session's own
/// pending prompt, not through the file.
internal static class CachedPhase
{
    private static readonly Dictionary<string, (long Ticks, long Size, PhaseReading? Reading)> Cache =
        new(StringComparer.Ordinal);

    public static PhaseReading? Classify(TriggerTool tool, string path, DateTimeOffset now)
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
        var key = tool.RawValue() + "|" + path;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached) && cached.Ticks == ticks && cached.Size == size) return cached.Reading;
        }
        var reading = AgentPhaseClassifier.Classify(tool, path, now);
        lock (Cache)
        {
            if (Cache.Count > 2000) Cache.Clear();
            Cache[key] = (ticks, size, reading);
        }
        return reading;
    }
}

/// A scheduled task Claude Desktop recorded as skipped.
internal readonly record struct SkippedTask(string Id, string Name, string Stamp, DateTimeOffset? At);

/// Claude Desktop's scheduled-tasks store (`scheduled-tasks.json`, one per
/// account and organisation under claude-code-sessions) records a run it
/// could not make under `recordedSkips`, keyed by task. The entry's shape
/// has not been seen on this machine, so a skip is told by its key and its
/// value changing, nothing more.
internal static class ScheduledTaskSkips
{
    public static IReadOnlyList<SkippedTask> Read()
    {
        var output = new List<SkippedTask>();
        try
        {
            var root = IslandPaths.ClaudeDesktopSessionsRoot;
            if (!Directory.Exists(root)) return output;
            foreach (var file in SessionScanner.SafeEnumerateFiles(root, "scheduled-tasks.json"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(stream);
                    output.AddRange(Parse(doc.RootElement));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return output;
    }

    internal static IEnumerable<SkippedTask> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("recordedSkips", out var skips)
            || skips.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("scheduledTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in tasks.EnumerateArray())
            {
                var id = Jsonl.GetString(task, "id") ?? Jsonl.GetString(task, "taskId");
                var name = Jsonl.GetString(task, "name") ?? Jsonl.GetString(task, "title");
                if (id is not null && name is not null) names[id] = name;
            }
        }
        foreach (var skip in skips.EnumerateObject())
        {
            var stamp = skip.Value.GetRawText();
            var at = skip.Value.ValueKind == JsonValueKind.Number && skip.Value.TryGetInt64(out var ms) && ms > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : (DateTimeOffset?)null;
            yield return new SkippedTask(skip.Name, names.TryGetValue(skip.Name, out var title) ? title : "", stamp, at);
        }
    }
}
