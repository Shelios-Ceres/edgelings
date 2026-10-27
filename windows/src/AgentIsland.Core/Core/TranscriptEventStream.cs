namespace AgentIsland.Core;

/// File-event watcher over the transcript roots — the Windows counterpart of
/// the macOS FSEvents stream. Polling alone means a state change waits up to
/// a full tick to surface; file events let the monitor react the moment a
/// transcript line lands, so the logo starts and stops with the run instead
/// of seconds behind it. The poll stays as a fallback sweep.
public sealed class TranscriptEventStream : IDisposable
{
    // A root that could not be watched is kept as null, so it is not tried
    // again on every poll.
    private readonly Dictionary<string, FileSystemWatcher?> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _onChange;
    private readonly Func<IEnumerable<string>> _roots;

    public TranscriptEventStream(Action onChange)
        : this(onChange, Roots)
    {
    }

    internal TranscriptEventStream(Action onChange, Func<IEnumerable<string>> roots)
    {
        _onChange = onChange;
        _roots = roots;
    }

    private static IEnumerable<string> Roots()
    {
        var roots = new List<string>();
        roots.AddRange(IslandPaths.ClaudeProjectRoots);
        roots.Add(IslandPaths.CodexSessionsRoot);
        roots.AddRange(IslandPaths.ClaudeDesktopSessionsRoots);
        roots.Add(IslandPaths.GrokSessionsRoot);
        roots.Add(IslandPaths.KimiSessionsRoot);
        roots.Add(IslandPaths.DeepSeekSessionsRoot);
        roots.Add(ClaudeHooks.SignalDirectory);
        roots.Add(GrokHooks.Signals);
        return roots;
    }

    public void Start() => WatchNewRoots();

    /// Watches every root that exists now and is not watched yet. Roots
    /// appear while the app runs: a hook's signal folder on its first prompt,
    /// an agent installed since, so the monitor's poll calls this again.
    public void WatchNewRoots()
    {
        foreach (var root in _roots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (_watchers.ContainsKey(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                // Buffer overflow drops individual events; a full rescan is the
                // correct recovery either way.
                watcher.Error += (_, _) => _onChange();
                watcher.EnableRaisingEvents = true;
                _watchers[root] = watcher;
            }
            catch
            {
                // A root that cannot be watched still gets covered by the poll.
                _watchers[root] = null;
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.FullPath)) _onChange();
    }

    internal static bool IsRelevant(string path)
    {
        if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(path);
        if (name.StartsWith("local_", StringComparison.Ordinal)) return true;
        // A hook's marker: the agent stopped to ask, and its transcript may
        // say nothing more until the user answers.
        var folder = Path.GetDirectoryName(path);
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(folder, ClaudeHooks.SignalDirectory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(folder, GrokHooks.Signals, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            if (watcher is null) continue;
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
            }
        }
        _watchers.Clear();
    }
}
