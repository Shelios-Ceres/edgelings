namespace AgentIsland.Core;

/// 各会话此刻的缓存率和速度，从本机日志的尾部算出来（只读本机文件，不发任何请求）。
/// Refresh 在后台线程调（读文件不能占 UI 线程），每个会话最多每 Interval 重算一次；
/// 悬浮卡在 UI 线程上只读 Snapshot，是一次字典拷贝。
/// 只算在跑的 Claude、Codex、Kimi 会话：别的家日志里没有可用的缓存字段或耗时（Grok 按回合、秒级时间；
/// agy 的字段没核实；Cursor 没有），等你、出错、空闲的会话卡上不显示，也就不读。
public sealed class SessionMetricsStore
{
    public static readonly SessionMetricsStore Shared = new();

    /// 数字最多这么久更新一次（设计稿 §4.2）。
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// 同时读的会话数上限：卡上本来也最多露出几个主对话。
    private const int MaxSessions = 12;

    private sealed class Entry(string path, SessionMetricsFile file)
    {
        public string Path { get; } = path;
        public SessionMetricsFile File { get; } = file;
        public SessionMetrics? Metrics { get; set; }
        public TimeSpan? ReadAt { get; set; }
    }

    /// 单调时钟（不随系统时间回拨）：只用来量两次读之间隔了多久。测试里换成假的。
    private readonly Func<TimeSpan> _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    /// 保护 _entries 和各 Entry 的读数；只做内存里的事，不在里面读文件，UI 线程的 Snapshot 不会被读文件卡住。
    private readonly object _gate = new();
    /// 让 Refresh 一次只跑一个：读文件时不握 _gate，同一个文件不能被两个线程同时 Update。
    private readonly object _refreshGate = new();

    public SessionMetricsStore(Func<TimeSpan>? clock = null) =>
        _clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));

    /// 读新增的日志，重算过期的读数。返回读数有没有变（卡开着就该刷新一次）。
    public bool Refresh(IEnumerable<ScannedSession> sessions)
    {
        var wanted = sessions
            .Where(session => session.Status == ActivityState.Working && ParserFor(session) is not null)
            .OrderByDescending(session => session.Modified)
            .Take(MaxSessions)
            .ToList();
        lock (_refreshGate)
        {
            var now = _clock();
            var changed = false;
            var due = new List<(string Id, Entry Entry)>();
            lock (_gate)
            {
                var ids = wanted.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var gone in _entries.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    _entries.Remove(gone);
                    changed = true;
                }
                foreach (var session in wanted)
                {
                    var path = session.TranscriptPath!;
                    if (!_entries.TryGetValue(session.Id, out var entry) || entry.Path != path)
                    {
                        entry = new Entry(path, new SessionMetricsFile(path, ParserFor(session)!));
                        _entries[session.Id] = entry;
                    }
                    // 时钟读数比上次小（不该发生）也当作到期，不会卡住。
                    if (entry.ReadAt is { } last && now >= last && now - last < Interval) continue;
                    entry.ReadAt = now;
                    due.Add((session.Id, entry));
                }
            }
            // 读文件在 _gate 外：慢的磁盘不会让 Snapshot 等。
            foreach (var (id, entry) in due)
            {
                SessionMetrics? metrics;
                try
                {
                    metrics = entry.File.Update();
                }
                catch
                {
                    continue;
                }
                lock (_gate)
                {
                    if (!_entries.TryGetValue(id, out var current) || !ReferenceEquals(current, entry)) continue;
                    if (Equals(metrics, entry.Metrics)) continue;
                    entry.Metrics = metrics;
                    changed = true;
                }
            }
            return changed;
        }
    }

    /// 会话 id → 最近一次算出的读数（还没按“现在”判断显不显示，那是 SessionMetrics.Shown 的事）。
    public IReadOnlyDictionary<string, SessionMetrics> Snapshot()
    {
        lock (_gate)
        {
            return _entries
                .Where(pair => pair.Value.Metrics is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Metrics!, StringComparer.Ordinal);
        }
    }

    private static Func<MetricsParser>? ParserFor(ScannedSession session)
    {
        if (session.TranscriptPath is not { Length: > 0 } path || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        return session.Tool switch
        {
            TriggerTool.Claude => () => new ClaudeMetricsParser(),
            TriggerTool.Codex => () => new CodexMetricsParser(),
            TriggerTool.Kimi => () => new KimiMetricsParser(),
            _ => null,
        };
    }
}
