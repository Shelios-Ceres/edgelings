using System.Diagnostics;

namespace AgentIsland.Core;

/// 登记 Limen 自己起的子进程（读额度的探测进程之类）。它们活着的那几秒，
/// 不是用户的 agent 开着，也不是在跑的任务：进程判定把登记的 PID 和它们的
/// 子孙进程一并排除。同一个 PID 可以登记多次，注销要配对。
internal static class OwnProcesses
{
    private sealed class Entry(DateTime? started)
    {
        public int Count = 1;
        public readonly DateTime? Started = started;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Entry> Entries = new();

    /// 进程的创建时间（UTC）；读不到就是 null。测试可以换掉。
    internal static Func<int, DateTime?> StartOf { get; set; } = ReadStart;

    private static DateTime? ReadStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    /// 登记一个 PID；Dispose 时注销（重复 Dispose 只注销一次）。登记时记下它的
    /// 创建时间：没有它，就认不出被复用的 PID。
    public static IDisposable Register(int pid) => Register(pid, StartOf(pid));

    internal static IDisposable Register(int pid, DateTime? started)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(pid, out var entry)) entry.Count++;
            else Entries[pid] = new Entry(started);
        }
        return new Registration(pid);
    }

    public static void Unregister(int pid)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(pid, out var entry)) return;
            if (--entry.Count <= 0) Entries.Remove(pid);
        }
    }

    public static bool Contains(int pid)
    {
        lock (Gate) return Entries.ContainsKey(pid);
    }

    public static IReadOnlySet<int> Snapshot()
    {
        lock (Gate) return Entries.Keys.ToHashSet();
    }

    private static Dictionary<int, DateTime?> Roots()
    {
        lock (Gate) return Entries.ToDictionary(e => e.Key, e => e.Value.Started);
    }

    /// 登记的 PID 加上它们在进程表里的全部子孙。登记表是空的就不读进程表。
    public static HashSet<int> Excluded(Func<IReadOnlyList<AgentPresences.Running>> table)
    {
        var roots = Roots();
        return roots.Count == 0 ? new HashSet<int>() : Excluded(roots, table(), StartOf);
    }

    /// 纯函数：roots（PID 和创建时间）和它们的子孙。Windows 上父进程退出后，子进程的
    /// ParentId 不会更新，PID 被复用后会指向毫不相干的新进程，所以子进程要和它的父进程
    /// 比创建时间：只认不早于父进程的；任何一边读不到创建时间，就不算子孙。
    /// 父子成环也不会死循环。
    internal static HashSet<int> Excluded(IReadOnlyDictionary<int, DateTime?> roots,
        IReadOnlyList<AgentPresences.Running> processes, Func<int, DateTime?>? startOf = null)
    {
        var excluded = new HashSet<int>(roots.Keys);
        if (excluded.Count == 0) return excluded;
        var children = processes.ToLookup(p => p.ParentId);
        var queue = new Queue<(int Id, DateTime? Started)>(roots.Select(r => (r.Key, r.Value)));
        while (queue.Count > 0)
        {
            var (parent, parentStarted) = queue.Dequeue();
            if (parentStarted is not { } since) continue;
            foreach (var child in children[parent])
            {
                if (excluded.Contains(child.Id)) continue;
                var started = child.Started ?? startOf?.Invoke(child.Id);
                if (started is not { } at || at < since) continue;
                excluded.Add(child.Id);
                queue.Enqueue((child.Id, started));
            }
        }
        return excluded;
    }

    /// 去掉登记进程和它们的子孙后的进程表。
    public static IReadOnlyList<AgentPresences.Running> Without(IReadOnlyList<AgentPresences.Running> processes)
    {
        var roots = Roots();
        if (roots.Count == 0) return processes;
        var excluded = Excluded(roots, processes, StartOf);
        return processes.Where(p => !excluded.Contains(p.Id)).ToList();
    }

    private sealed class Registration(int pid) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Unregister(pid);
        }
    }
}
