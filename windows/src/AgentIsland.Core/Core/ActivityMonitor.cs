using System.ComponentModel;
using AgentIsland.Usage;

namespace AgentIsland.Core;

/// Aggregates scanned sessions into one visible state per provider, mixing in
/// usage-fetch attention states. Poll every 6 seconds plus file-event kicks
/// throttled to ~2/s with a guaranteed trailing scan: a streaming transcript
/// writes many times per second, but the LAST write of a turn (end_turn /
/// task_complete) must never wait for the fallback poll.
public sealed class ActivityMonitor : INotifyPropertyChanged
{
    public static ActivityMonitor Shared { get; } = new();
    private ActivityMonitor() { }

    public sealed record ActiveThread(
        string SessionId,
        string Label,
        string Cwd,
        DateTimeOffset Modified,
        string? TranscriptPath,
        string? TurnKey,
        SessionLaunchTarget LaunchTarget);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Providers whose session status is actually scanned. Cursor is absent
    /// All five ride the scan. Cursor graduated once the real signal was
    /// found: each workspace's state.vscdb (and its -wal journal) is written
    /// continuously while a Cursor window is open — unlike the batch-written
    /// conversation-search.db that disqualified it earlier. Its turn
    /// detector is MtimeOnly, so it shows working/idle but can never raise a
    /// false "your turn".
    private static readonly TriggerTool[] MonitoredProviders =
    {
        TriggerTool.Claude,
        TriggerTool.Codex,
        TriggerTool.Grok,
        TriggerTool.Antigravity,
        TriggerTool.Cursor,
        TriggerTool.Kimi,
        TriggerTool.DeepSeek,
    };

    // Per-provider maps rather than per-provider fields: with fields, every
    // provider past Codex silently read CODEX's state (the macOS 2.1.1 bug
    // this port fixes). Replaced wholesale on each scan, never mutated in
    // place, so a binding reading `States` can never see a half-built map.
    private Dictionary<TriggerTool, ActivityState> _states = new();
    private Dictionary<TriggerTool, ActivityState> _rawStates = new();
    private Dictionary<TriggerTool, ActiveThread> _threads = new();
    private Dictionary<TriggerTool, int> _running = new();
    private Dictionary<TriggerTool, ActivityState> _demoStates = new();
    private Dictionary<string, DateTimeOffset> _lastWorking = new();

    /// Visible state for every monitored provider. Raised as one change so
    /// five-wide surfaces refresh together.
    public IReadOnlyDictionary<TriggerTool, ActivityState> States => _states;

    public IReadOnlyDictionary<TriggerTool, ActiveThread> Threads => _threads;

    public ActivityState StateFor(TriggerTool provider)
    {
        if (_demoStates.TryGetValue(provider, out var demo)) return demo;
        return _states.TryGetValue(provider, out var state) ? state : ActivityState.Idle;
    }

    /// Pre-overlay scan state. The turn-alarm confirm gate must read this:
    /// the usage overlay (rateLimited/authRequired outrank needsYou) would
    /// otherwise swallow alarms exactly when the quota is exhausted or the
    /// network is down — the moments a finished turn most needs surfacing.
    public ActivityState RawStateFor(TriggerTool provider) =>
        _rawStates.TryGetValue(provider, out var state) ? state : ActivityState.Idle;

    public ActiveThread? ThreadFor(TriggerTool provider) =>
        _threads.TryGetValue(provider, out var thread) ? thread : null;

    /// Threads mid-turn, including ones held up by the network.
    public int RunningFor(TriggerTool provider) =>
        _running.TryGetValue(provider, out var count) ? count : 0;

    /// Windows has reported no internet for a while.
    public bool IsOffline { get; private set; }

    // The island and tray bind to these two by name; they stay as thin views
    // over the maps so those bindings keep working unchanged.
    public ActivityState Claude => StateFor(TriggerTool.Claude);
    public ActivityState Codex => StateFor(TriggerTool.Codex);
    public ActiveThread? ClaudeThread => ThreadFor(TriggerTool.Claude);
    public ActiveThread? CodexThread => ThreadFor(TriggerTool.Codex);

    /// Recording rig / settings preview: pins every provider's published
    /// state, or clears the pin when passed null.
    public void Demo(ActivityState? state)
    {
        var next = new Dictionary<TriggerTool, ActivityState>();
        if (state is { } forced)
        {
            foreach (var tool in TriggerToolExtensions.All) next[tool] = forced;
        }
        _demoStates = next;
        RaiseAll();
    }

    private UiTimer? _timer;
    private TranscriptEventStream? _eventStream;
    private DateTimeOffset _lastEventKick = DateTimeOffset.MinValue;
    private bool _kickPending;
    private bool _scanInFlight;
    private bool _rescanQueued;

    public void Start()
    {
        Tick();
        _timer = new UiTimer
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        var stream = new TranscriptEventStream(() =>
        {
            // File events arrive on watcher threads; hop to the UI thread.
            UiThread.Post(EventKick);
        });
        stream.Start();
        _eventStream = stream;
    }

    /// Minimum spacing between event-driven full scans. A marathon session
    /// (this repo's own development sessions hit 70MB+) writes its
    /// transcript several times a second, and at the old 0.5s spacing the
    /// monitor burned ~45% of a core purely on directory enumeration + a
    /// thousand stats per sweep. 2s keeps the your-turn alarm inside the
    /// "it just finished" moment (macOS lands at ~1.2s) at a quarter of
    /// the scan bill; the 6s timer still backstops missed events.
    private static readonly TimeSpan EventKickSpacing = TimeSpan.FromSeconds(2);

    private void EventKick()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastEventKick;
        if (elapsed >= EventKickSpacing)
        {
            _lastEventKick = now;
            Tick();
            return;
        }
        if (_kickPending) return;
        _kickPending = true;
        var delay = TimeSpan.FromSeconds(
            Math.Max(EventKickSpacing.TotalSeconds - elapsed.TotalSeconds, 0.05));
        var trailing = new UiTimer
        {
            Interval = delay,
        };
        trailing.Tick += (_, _) =>
        {
            trailing.Dispose();
            _kickPending = false;
            _lastEventKick = DateTimeOffset.UtcNow;
            Tick();
        };
        trailing.Start();
    }

    private void Tick()
    {
        // One scan at a time; a kick that lands mid-scan queues exactly one
        // follow-up so the trailing write of a turn is never dropped.
        if (_scanInFlight)
        {
            _rescanQueued = true;
            return;
        }
        _scanInFlight = true;
        var now = DateTimeOffset.UtcNow;
        // The comparer has to be restated: the copy constructor takes the
        // entries but NOT the source's comparer, and the scanner looks these
        // paths up again — Windows hands back whatever casing the directory
        // entry carries, so an ordinal snapshot would lose a session's
        // last-working stamp and keep a finished guest turn reading as work.
        var lastWorkingSnapshot = new Dictionary<string, DateTimeOffset>(
            _lastWorking, StringComparer.OrdinalIgnoreCase);
        Task.Run(() => new Scan(
                SessionScanner.MonitoringScan(now, lastWorkingSnapshot),
                AgentProcesses.Gone(),
                InternetStatus.IsOffline(now)))
            .ContinueWith(task =>
            {
                var scan = task.IsCompletedSuccessfully ? task.Result : Scan.Empty;
                // Apply reaches the alarm center and every bound surface. A
                // throw there is swallowed by the continuation's own task, so
                // without the finally the in-flight flag would latch and the
                // monitor would silently never scan again.
                try
                {
                    IsOffline = scan.Offline;
                    Apply(Settle(scan.Sessions, scan.Gone, scan.Offline), now);
                }
                finally
                {
                    _scanInFlight = false;
                }
                if (_rescanQueued)
                {
                    _rescanQueued = false;
                    Tick();
                }
            }, UiThread.Scheduler);
    }

    private sealed record Scan(List<ScannedSession> Sessions, IReadOnlySet<TriggerTool> Gone, bool Offline)
    {
        public static readonly Scan Empty = new(new List<ScannedSession>(), new HashSet<TriggerTool>(), false);
    }

    /// Evidence beyond the transcripts: a mid-task session whose agent has no
    /// process left was abandoned, and while Windows reports no internet a
    /// working agent cannot be getting anywhere.
    internal static List<ScannedSession> Settle(List<ScannedSession> sessions, IReadOnlySet<TriggerTool> gone, bool offline) =>
        sessions.ConvertAll(session => session.Status switch
        {
            ActivityState.Working or ActivityState.NetworkTrouble when gone.Contains(session.Tool) =>
                session with { Status = ActivityState.Idle },
            ActivityState.Working when offline => session with { Status = ActivityState.NetworkTrouble },
            _ => session,
        });

    private void Apply(List<ScannedSession> sessions, DateTimeOffset now)
    {
        // Usage-level attention (rate-limited / auth-required red) only
        // applies to providers switched ON in Settings. Someone who only
        // runs Claude keeps Codex hidden - its missing login must not
        // pulse the island red forever.
        var visibility = Model.ProviderVisibilityStore.Shared;
        UpdateLastWorking(sessions, now);
        var nextStates = new Dictionary<TriggerTool, ActivityState>();
        var nextRaw = new Dictionary<TriggerTool, ActivityState>();
        var nextThreads = new Dictionary<TriggerTool, ActiveThread>();
        var nextRunning = new Dictionary<TriggerTool, int>();
        foreach (var tool in MonitoredProviders)
        {
            nextRunning[tool] = RunningCount(sessions, tool);
            var result = BestSession(sessions, tool);
            nextRaw[tool] = result.State;
            if (result.Thread is { } thread) nextThreads[tool] = thread;
            nextStates[tool] = visibility.IsVisible(tool)
                ? OverlayUsageAttention(result.State, UsageFor(tool))
                : result.State;
        }
        _rawStates = nextRaw;
        _threads = nextThreads;
        _running = nextRunning;
        _states = nextStates;
        RaiseAll();
    }

    /// Only Claude and Codex have a usage endpoint; the guests carry no quota
    /// of their own, so nothing can overlay onto their scan state.
    private static AppUsage UsageFor(TriggerTool tool) => tool switch
    {
        TriggerTool.Claude => UsageStore.Shared.Claude,
        TriggerTool.Codex => UsageStore.Shared.Codex,
        _ => AppUsage.Empty,
    };

    private void UpdateLastWorking(List<ScannedSession> sessions, DateTimeOffset now)
    {
        foreach (var session in sessions)
        {
            if (session.Status == ActivityState.Working && session.TranscriptPath is { } path)
                _lastWorking[path] = now;
        }
        var livePaths = sessions
            .Where(s => s.TranscriptPath is not null)
            .Select(s => s.TranscriptPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lastWorking = _lastWorking
            .Where(kv => livePaths.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static ActivityState OverlayUsageAttention(ActivityState state, AppUsage usage)
    {
        if (UsageAttentionState(usage) is not { } attention) return state;
        return (int)attention > (int)state ? attention : state;
    }

    internal static ActivityState? UsageAttentionState(AppUsage usage)
    {
        if (usage.FiveHour.UsedPercent >= 1 || usage.Weekly.UsedPercent >= 1)
            return ActivityState.RateLimited;
        var messages = new[] { usage.FiveHour.Error, usage.Weekly.Error }
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!.ToLowerInvariant())
            .ToList();
        if (messages.Any(m => m.Contains("rate limited") || m.Contains("rate_limit")))
            return ActivityState.RateLimited;
        if (messages.Any(m =>
                ClaudeCredentials.IsAuthRecoverableError(m)
                || m.Contains("auth")
                || m.Contains("login")
                || m.Contains("no codex")))
        {
            return ActivityState.AuthRequired;
        }
        // Any other failure (the network, a bad response) only means the quota
        // is unknown. It used to read as "out of quota"; the app's own request
        // failing says nothing about the agents, which may reach their
        // service through a proxy this app does not use.
        return null;
    }

    /// A running session outranks a finished one: with the turn alarms gone
    /// nothing acknowledges a finished turn, so a needsYou ranked first would
    /// mask a genuinely running sibling until it ages out. Trouble a session
    /// wrote down stays above working so real anomalies surface.
    private static int SelectionPriority(ScannedSession session) =>
        session.Status switch
        {
            ActivityState.NetworkTrouble or ActivityState.RateLimited or ActivityState.AuthRequired => 3,
            ActivityState.Working => 2,
            ActivityState.NeedsYou => 1,
            _ => 0,
        };

    /// Codex can spread one thread over several rollout files, so threads
    /// are counted by id, not by file.
    internal static int RunningCount(IEnumerable<ScannedSession> sessions, TriggerTool tool) =>
        sessions
            .Where(s => s.Tool == tool && s.Status is ActivityState.Working or ActivityState.NetworkTrouble)
            .Select(s => s.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static (ActivityState State, ActiveThread? Thread) BestSession(
        List<ScannedSession> sessions,
        TriggerTool tool)
    {
        var ranked = sessions
            .Where(s => s.Tool == tool)
            .Select(s => (Session: s, Priority: SelectionPriority(s)))
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.Session.Modified)
            .ToList();
        if (ranked.Count == 0) return (ActivityState.Idle, null);
        var top = ranked[0].Session;
        var thread = top.Status == ActivityState.Idle ? null : MakeThread(top);
        return (top.Status, thread);
    }

    private static ActiveThread MakeThread(ScannedSession session) => new(
        session.SessionId,
        session.Label,
        session.Cwd,
        session.Modified,
        session.TranscriptPath,
        session.TurnKey,
        session.LaunchTarget);

    /// Both the maps and the two named views have to be announced: the island
    /// and tray listen for "Claude"/"Codex", the five-wide panel reads the
    /// maps, and a provider whose notification is missing simply stops
    /// updating on screen.
    private void RaiseAll()
    {
        Raise(nameof(States));
        Raise(nameof(Threads));
        Raise(nameof(Claude));
        Raise(nameof(Codex));
        Raise(nameof(ClaudeThread));
        Raise(nameof(CodexThread));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
