using System.Text;

namespace AgentIsland.Core;

/// Discovers the sessions of Claude Code / Claude Desktop, Codex, Grok, Kimi
/// Code, DeepSeek Harness, Antigravity and Cursor from the local artifacts
/// those tools already write, and classifies each one through a conservative
/// state machine. Direct port of the macOS SessionScanner; the thresholds are
/// the tuned values from the shipping app. This file holds the entry point,
/// the state machine, the caches and the helpers; each agent's scanner is in
/// its own SessionScanner.<Agent>.cs.
public static partial class SessionScanner
{
    private static readonly TimeSpan NeedsYouCap = TimeSpan.FromMinutes(20);
    /// How long a guest transcript must sit unchanged, after visible
    /// activity, before quiet counts as turn-done.
    private const double GuestQuietAfterSeconds = 25;
    /// An open turn silent this long is presumed abandoned. Silence alone is
    /// never trouble: thinking and long commands write nothing for minutes.
    private static readonly TimeSpan AttentionWindow = TimeSpan.FromMinutes(30);
    /// Claude Desktop writes lastActivityAt 2.3-4.2s after the final assistant
    /// event as turn-completion bookkeeping; external activity inside this
    /// window must not suppress needsYou.
    private static readonly TimeSpan DesktopBookkeepingGrace = TimeSpan.FromSeconds(25);

    // MARK: - Entry points

    /// Monitoring scan: every Claude transcript (desktop-labelled when known),
    /// every recent Codex rollout with no project dedupe, plus every session
    /// of the other agents. Subagent / child threads never participate — machine
    /// fan-out finishes dozens of threads per prompt and a human is never "up"
    /// in any of them, so they are skipped outright rather than gated behind a
    /// toggle (owner call, 2026-08-08).
    public static List<ScannedSession> MonitoringScan(DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = ScanClaudeTranscripts(now, lastWorking);
        output.AddRange(ScanCodex(now, lastWorking));
        output.AddRange(ScanGrok(now, lastWorking));
        output.AddRange(ScanAntigravity(now, lastWorking));
        output.AddRange(ScanCursor(now, lastWorking));
        output.AddRange(ScanKimi(now, lastWorking));
        output.AddRange(ScanDeepSeek(now, lastWorking));
        output.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return output;
    }

    /// A file's write time and length, or nothing for a missing file.
    private static string FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// Recursively lists matching files, surviving a subdirectory that is
    /// inaccessible OR deleted mid-walk (Claude Code rotates project folders).
    /// A plain EnumerateFiles(AllDirectories) throws DURING iteration — outside
    /// any try around the call — which used to fault the whole scan and, with
    /// it, every agent's state. Skips reparse points to avoid symlink loops.
    internal static List<string> SafeEnumerateFiles(string root, string pattern)
    {
        var result = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            using var walker = Directory.EnumerateFiles(root, pattern, options).GetEnumerator();
            while (true)
            {
                try
                {
                    if (!walker.MoveNext()) break;
                }
                catch
                {
                    // A directory vanished or turned unreadable mid-walk —
                    // stop cleanly and keep what we already gathered.
                    break;
                }
                result.Add(walker.Current);
            }
        }
        catch
        {
        }
        return result;
    }

    /// Immediate subdirectories only, tolerating a missing root and a
    /// directory that vanishes mid-walk (Grok and Kimi Code both rotate their
    /// per-project folders while the monitor is running).
    internal static List<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // MARK: - State machine

    public static (ActivityState Status, string? TurnKey, DateTimeOffset Modified, bool TurnEnded) SessionState(
        string? path,
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        DateTimeOffset? externalActivityDate,
        Func<IReadOnlyList<string>, SessionTurnStatus> turnState,
        Func<string, List<string>>? tail = null,
        bool requireTurn = false,
        TimeSpan? externalGrace = null)
    {
        if (path is null)
            return (ActivityState.Idle, null, externalActivityDate ?? DateTimeOffset.MinValue, false);

        // Claude's transcripts can hold lines far bigger than the usual tail
        // read, so the scan that requires a turn looks further back for one.
        var (turn, fileModified) = ReadTurn(path, turnState, tail ?? (p => TailLines(p)), widen: requireTurn);
        // A transcript that shows no turn at all is working only while the
        // desktop says a turn began inside the attention window: the file's
        // write time alone proves nothing.
        if (requireTurn && turn.Key is null && externalActivityDate is null)
            return (ActivityState.Idle, null, fileModified, false);
        var semanticModified = LatestDate(turn.ActivityDate, externalActivityDate);
        var effectiveModified = semanticModified ?? fileModified;
        // For a finished turn, external activity inside the bookkeeping grace
        // is Claude Desktop's own post-turn write — not the user returning —
        // so it must not suppress needsYou. Genuine "user came back" activity
        // lands minutes later, far past the grace.
        var externalReference = turn.IsDone
            ? turn.ActivityDate?.Add(externalGrace ?? DesktopBookkeepingGrace)
            : turn.ActivityDate;
        var externalIsNewer = IsLater(externalActivityDate, externalReference);
        var age = now - effectiveModified;

        if (age > AttentionWindow || turn.Idle) return (ActivityState.Idle, turn.Key, effectiveModified, false);
        if (externalIsNewer) return (ActivityState.Working, turn.Key, effectiveModified, false);
        if (turn.IsDone)
        {
            if (age >= NeedsYouCap) return (ActivityState.Idle, turn.Key, effectiveModified, true);
            var ended = turn.Trouble switch
            {
                TurnTrouble.Network => ActivityState.NetworkTrouble,
                TurnTrouble.RateLimited => ActivityState.RateLimited,
                TurnTrouble.AuthRequired => ActivityState.AuthRequired,
                // Any other error is still the user's move; the drawer reads
                // the transcript and shows it as an error.
                _ => ActivityState.NeedsYou,
            };
            return (ended, turn.Key, effectiveModified, true);
        }
        if (turn.RetryingUntil is { } until && now < until)
            return (ActivityState.NetworkTrouble, turn.Key, effectiveModified, false);
        return (ActivityState.Working, turn.Key, effectiveModified, false);
    }

    /// Whether Grok's latest turn is still open (its transcript has no
    /// turn_completed after the last conversation update).
    internal static bool HasOpenGrokTurn(string path)
    {
        var (turn, _) = ReadTurn(path, SessionTurnState.Grok, p => TailLines(p));
        return turn.Key is not null && !turn.IsDone && !turn.Idle;
    }

    internal static bool HasOpenClaudeTurn(string path)
    {
        // Widened like the scan's own read: both share the turn cache.
        var (turn, _) = ReadTurn(path, SessionTurnState.Claude, p => TailLines(p), widen: true);
        return turn.Key is not null && !turn.IsDone && !turn.Idle;
    }

    // MARK: - Turn parse cache

    private static readonly long[] WidenedTailBytes = { 1L << 20, 4L << 20 };

    private static readonly Dictionary<string, (long Ticks, long Size, SessionTurnStatus Turn, DateTimeOffset Modified)>
        TurnCache = new(StringComparer.Ordinal);

    /// Scans can overlap on more than one worker, and all of them land here.
    /// An unsynchronized Dictionary written from two threads corrupts its
    /// bucket chain, and a corrupted chain makes the NEXT lookup spin forever —
    /// a hung scan thread, not an exception. The parse itself stays outside the lock so one slow tail
    /// read never blocks the other scan.
    private static readonly object TurnCacheGate = new();

    /// The expensive half of SessionState — the tail read plus the JSON turn
    /// parse — depends only on file CONTENT, so it is cached on a (mtime, size)
    /// fingerprint. Transcripts are append-only: a byte written bumps Length,
    /// so an unchanged fingerprint provably means the parsed turn still holds.
    ///
    /// This is the fix for the monitor pegging a CPU core on a machine with
    /// many sessions: the file-event path kicks a FULL scan on every transcript
    /// write, and without this every kick re-read and re-parsed the tail of
    /// EVERY session. Now an idle session is a two-field stat, and only the one
    /// transcript actually being written gets re-parsed. The time-based state
    /// (working / needsYou / idle) is still computed fresh each scan from
    /// the cached turn, so nothing about detection changes — only redundant IO.
    private static (SessionTurnStatus Turn, DateTimeOffset Modified) ReadTurn(
        string path, Func<IReadOnlyList<string>, SessionTurnStatus> turnState, Func<string, List<string>> tail,
        bool widen = false)
    {
        try
        {
            var info = new FileInfo(path);
            var ticks = info.LastWriteTimeUtc.Ticks;
            var size = info.Length;
            lock (TurnCacheGate)
            {
                if (TurnCache.TryGetValue(path, out var cached)
                    && cached.Ticks == ticks && cached.Size == size)
                {
                    return (cached.Turn, cached.Modified);
                }
            }
            var modified = Mtime(path);
            var parsed = turnState(tail(path));
            // Huge lines (attachments) can push the last conversation line
            // out of the usual window; look further back before giving up.
            if (widen && parsed.Key is null)
            {
                foreach (var bytes in WidenedTailBytes)
                {
                    parsed = turnState(TailLines(path, bytes, keep: 4000));
                    if (parsed.Key is not null) break;
                }
            }
            lock (TurnCacheGate)
            {
                // Bound the cache against pathological growth (project-folder
                // rotation minting new transcript paths forever); the working set
                // is one entry per real session, rebuilt cheaply after a clear.
                if (TurnCache.Count > 5000) TurnCache.Clear();
                TurnCache[path] = (ticks, size, parsed, modified);
            }
            return (parsed, modified);
        }
        catch
        {
            // Stat/read raced a folder rotation — read directly, uncached.
            // A second failure means the file is unreadable right now, which
            // is "no turn yet", never a fault that takes the whole scan (and
            // with it every provider's state) down with it.
            try { return (turnState(tail(path)), Mtime(path)); }
            catch { return (default, Mtime(path)); }
        }
    }

    /// Test seam: drop the fingerprint caches so a suite that rewrites content
    /// behind a reused path never reads a stale parse.
    internal static void ClearTurnCache()
    {
        lock (TurnCacheGate) TurnCache.Clear();
        lock (ScanCacheGate)
        {
            CodexMetas.Clear();
            TranscriptCwds.Clear();
            DesktopSessions.Clear();
            AntigravityTitles.Clear();
            AntigravityHistories.Clear();
        }
        _codexTitles = new("", new());
    }

    // MARK: - Scan caches

    // Every scan used to re-read each Codex rollout's first line and parse
    // every Claude Desktop session file: most of a scan's cost, for data that
    // rarely changes.
    private static readonly object ScanCacheGate = new();

    // MARK: - Helpers

    public static string Fallback(string cwd, string sid)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        var basename = trimmed.Length == 0 ? "" : Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(basename))
            return sid.Length <= 8 ? sid : sid[..8];
        return basename;
    }

    public static List<string> TailLines(string path, long bytes = 131_072, int keep = 200)
    {
        byte[] data;
        try
        {
            using var stream = SharedFile.Open(path);
            var size = stream.Length;
            if (size > bytes) stream.Seek(size - bytes, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            data = memory.ToArray();
        }
        catch
        {
            return new List<string>();
        }
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var start = Math.Max(0, lines.Length - keep);
        var output = new List<string>(Math.Min(keep, lines.Length));
        for (var i = start; i < lines.Length; i++)
        {
            output.Add(lines[i].TrimEnd('\r'));
        }
        return output;
    }

    public static DateTimeOffset Mtime(string path)
    {
        try
        {
            var utc = File.GetLastWriteTimeUtc(path);
            return utc.Year < 1700 ? DateTimeOffset.MinValue : new DateTimeOffset(utc);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static DateTimeOffset? LatestDate(DateTimeOffset? lhs, DateTimeOffset? rhs)
    {
        if (lhs is { } l && rhs is { } r) return l > r ? l : r;
        return lhs ?? rhs;
    }

    private static bool IsLater(DateTimeOffset? lhs, DateTimeOffset? rhs)
    {
        if (lhs is not { } l) return false;
        if (rhs is not { } r) return true;
        return (l - r).TotalSeconds > 0.5;
    }
}
