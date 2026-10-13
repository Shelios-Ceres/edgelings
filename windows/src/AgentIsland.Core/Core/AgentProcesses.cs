using System.Diagnostics;

namespace AgentIsland.Core;

/// Which agents have no process left. A session still mid-turn when its
/// agent's app is gone was abandoned (closed or crashed), so it reads as idle
/// at once instead of after half an hour of silence. Only agents whose process
/// names are known are checked; the rest are never declared gone.
internal static class AgentProcesses
{
    /// Process names per agent. A path hint tells a shared name apart: the
    /// Kimi desktop app is also called Kimi.
    private static readonly (TriggerTool Tool, string Name, string? PathHint)[] Known =
    {
        (TriggerTool.Claude, "claude", null),
        (TriggerTool.Codex, "codex", null),
        (TriggerTool.Grok, "grok", null),
        (TriggerTool.Kimi, "kimi", ".kimi-code"),
        (TriggerTool.Cursor, "cursor", null),
    };

    public static IReadOnlySet<TriggerTool> Gone()
    {
        try
        {
            var running = new List<(string Name, string? Path)>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var name = process.ProcessName;
                    var known = Known.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (known.Name is null) continue;
                    running.Add((name, known.PathHint is null ? null : PathOf(process)));
                }
            }
            return Gone(running);
        }
        catch
        {
            return new HashSet<TriggerTool>();
        }
    }

    /// An unreadable path counts as alive: a session is never idled on a guess.
    internal static IReadOnlySet<TriggerTool> Gone(IEnumerable<(string Name, string? Path)> running)
    {
        var alive = new HashSet<TriggerTool>();
        foreach (var (name, path) in running)
        {
            foreach (var known in Known)
            {
                if (!string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (known.PathHint is null || path is null || path.Contains(known.PathHint, StringComparison.OrdinalIgnoreCase))
                    alive.Add(known.Tool);
            }
        }
        return Known.Select(k => k.Tool).Where(tool => !alive.Contains(tool)).ToHashSet();
    }

    private static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
