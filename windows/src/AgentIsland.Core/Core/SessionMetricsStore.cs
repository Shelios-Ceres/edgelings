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
        public DateTimeOffset ReadAt { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SessionMetricsStore(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// 读新增的日志，重算过期的读数。返回读数有没有变（卡开着就该刷新一次）。
    public bool Refresh(IEnumerable<ScannedSession> sessions)
    {
        var wanted = sessions
            .Where(session => session.Status == ActivityState.Working && ParserFor(session) is not null)
            .OrderByDescending(session => session.Modified)
            .Take(MaxSessions)
            .ToList();
        var now = _clock();
        var changed = false;
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
                if (now - entry.ReadAt < Interval) continue;
                entry.ReadAt = now;
                var metrics = entry.File.Update();
                if (!Equals(metrics, entry.Metrics))
                {
                    entry.Metrics = metrics;
                    changed = true;
                }
            }
        }
        return changed;
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
