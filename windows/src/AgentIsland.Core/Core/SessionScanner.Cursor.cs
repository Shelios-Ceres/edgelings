using System.Text;

namespace AgentIsland.Core;

// Cursor: the conversations in its globalStorage state.vscdb, read again when the db or its -wal journal changed.
public static partial class SessionScanner
{
    /// Cursor conversations, read from the same globalStorage db the cost
    /// reader opens through winsqlite3. Rows keyed `composerData:&lt;id&gt;` are
    /// the conversations; `bubbleId:&lt;composerId&gt;:&lt;bubbleId&gt;` rows are the
    /// messages, and a bubble's `type` is a REAL turn boundary — 1 user,
    /// 2 assistant. Same rule Claude gets: assistant spoke last and has gone
    /// quiet means it is your turn.
    ///
    /// Read again when the db or its -wal journal changed, or every few
    /// seconds anyway: a running Cursor writes the journal through a handle
    /// it keeps open, and Windows can leave an open file's write time behind.
    public static List<ScannedSession> ScanCursor(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var path = CursorGlobalDbPath;
        if (!File.Exists(path)) return new List<ScannedSession>();
        var fingerprint = FileStamp(path) + "|" + FileStamp(path + "-wal");

        List<CursorConversation>? cached = null;
        lock (CursorGate)
        {
            if (fingerprint == _cursorStamp && now - _cursorReadAt < CursorRereadAfter) cached = _cursorCache;
        }
        if (cached is not null)
        {
            // Status is NEVER cached — only the parsed conversation is. A
            // finished turn becomes "your turn" purely by the clock moving
            // past the quiet gap, and the db stops changing the moment the
            // assistant stops writing, so a cached status would freeze at
            // "working" and the alarm would never fire.
            return cached.ConvertAll(c => CursorSession(c, now));
        }

        var parsed = new List<CursorConversation>();
        foreach (var (composerId, json) in CursorConversations.NewestBubblePerConversation(path))
        {
            var turn = SessionTurnState.Cursor(new[] { json });
            // No usable bubble timestamp means we cannot say WHEN this
            // conversation last moved. Falling back to the db mtime was the
            // bug behind a permanently spinning logo: Cursor rewrites that
            // file continuously while it is merely open.
            if (turn.ActivityDate is not { } stamp) continue;
            parsed.Add(new CursorConversation(
                composerId,
                CursorConversations.Title(json) ?? composerId[..Math.Min(8, composerId.Length)],
                turn.IsDone,
                turn.Key,
                stamp));
        }
        parsed.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));

        lock (CursorGate)
        {
            _cursorStamp = fingerprint;
            _cursorReadAt = now;
            _cursorCache = parsed;
        }
        return parsed.ConvertAll(c => CursorSession(c, now));
    }

    private static readonly object CursorGate = new();

    private static string _cursorStamp = "";

    private static DateTimeOffset _cursorReadAt = DateTimeOffset.MinValue;

    private static List<CursorConversation> _cursorCache = new();

    private static TimeSpan CursorRereadAfter => TimeSpan.FromSeconds(10);

    /// One parsed conversation. Deliberately holds no status: status is a
    /// function of the CURRENT clock and is recomputed on every scan.
    private readonly record struct CursorConversation(
        string Id, string Label, bool IsDone, string? TurnKey, DateTimeOffset Stamp);

    private static ScannedSession CursorSession(CursorConversation conversation, DateTimeOffset now)
    {
        var key = "cursor:" + conversation.Id;
        var turn = new SessionTurnStatus(conversation.IsDone, conversation.TurnKey, conversation.Stamp);
        return new ScannedSession(
            TriggerTool.Cursor,
            conversation.Id,
            string.Empty,
            conversation.Label,
            conversation.Stamp,
            CursorStatus(turn, conversation.Stamp, now),
            key,
            conversation.TurnKey,
            SessionLaunchTarget.Cli);
    }

    private static string CursorGlobalDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    /// Assistant-last plus a quiet gap means the turn finished; assistant-last
    /// while still streaming reads as working. A user bubble last means the
    /// agent is thinking.
    private static ActivityState CursorStatus(SessionTurnStatus turn, DateTimeOffset stamp, DateTimeOffset now)
    {
        var age = now - stamp;
        if (turn.IsDone)
        {
            if (age.TotalSeconds <= GuestQuietAfterSeconds) return ActivityState.Working;
            return age < NeedsYouCap ? ActivityState.NeedsYou : ActivityState.Idle;
        }
        return age < AttentionWindow ? ActivityState.Working : ActivityState.Idle;
    }
}
