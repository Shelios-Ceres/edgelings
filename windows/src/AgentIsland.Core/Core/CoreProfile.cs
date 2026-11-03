using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentIsland.Core;

/// Where Core spends its time, by subsystem: the wall time of each named piece of
/// work (a scan of one agent's sessions, a cost scan, the process table, the
/// frame the Mac app asks for twice a second) and how many times it ran. Those
/// pieces are CPU bound, so wall time is the CPU they cost. The diagnostic prints
/// the totals as milliseconds per minute, so a remote report says where the time
/// goes. Names only: nothing a file holds, no path.
public static class CoreProfile
{
    private sealed class Cell
    {
        public long Ticks;
        public long Count;
        public long Worst;
    }

    private static readonly ConcurrentDictionary<string, Cell> Cells = new(StringComparer.Ordinal);
    private static readonly long StartedAt = Stopwatch.GetTimestamp();

    public static T Time<T>(string name, Func<T> work)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return work();
        }
        finally
        {
            Add(name, Stopwatch.GetTimestamp() - start);
        }
    }

    public static void Time(string name, Action work)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            work();
        }
        finally
        {
            Add(name, Stopwatch.GetTimestamp() - start);
        }
    }

    /// Adds a duration that was timed elsewhere (a stopwatch tick count).
    public static void Add(string name, long ticks)
    {
        var cell = Cells.GetOrAdd(name, _ => new Cell());
        Interlocked.Add(ref cell.Ticks, ticks);
        Interlocked.Increment(ref cell.Count);
        long seen;
        while (ticks > (seen = Interlocked.Read(ref cell.Worst)) && Interlocked.CompareExchange(ref cell.Worst, ticks, seen) != seen) { }
    }

    /// One row per name: how many times it ran, its total and its slowest run in milliseconds, and its cost
    /// in milliseconds per minute of the app's life. The longest-running first.
    public sealed record Row(string Name, long Count, double TotalMs, double WorstMs, double MsPerMinute);

    public static IReadOnlyList<Row> Snapshot()
    {
        var minutes = Math.Max((Stopwatch.GetTimestamp() - StartedAt) / (double)Stopwatch.Frequency / 60.0, 1.0 / 60.0);
        return Cells.Select(pair =>
        {
            var total = pair.Value.Ticks * 1000.0 / Stopwatch.Frequency;
            return new Row(pair.Key, Interlocked.Read(ref pair.Value.Count), total, pair.Value.Worst * 1000.0 / Stopwatch.Frequency, total / minutes);
        }).OrderByDescending(r => r.TotalMs).ToList();
    }

    /// Seconds since the first use of this class (the app's start, in practice).
    public static double UptimeSeconds => (Stopwatch.GetTimestamp() - StartedAt) / (double)Stopwatch.Frequency;

    public static void Reset()
    {
        Cells.Clear();
    }
}
