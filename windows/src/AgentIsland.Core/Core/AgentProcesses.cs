using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentIsland.Core;

/// Which agents have no process left. A session still mid-turn when its
/// agent's app is gone was abandoned (closed or crashed), so it reads as idle
/// at once instead of after half an hour of silence. Only agents whose process
/// names are known are checked; the rest are never declared gone.
internal static class AgentProcesses
{
    private const string ClaudeCodePackage = @"@anthropic-ai\claude-code";

    /// Process names per agent. A path hint tells a shared name apart: the
    /// Kimi chat app is also called Kimi. Kimi Code's desktop app runs its
    /// sessions in its own process; Antigravity's desktop app and its CLI's
    /// real binary are both "antigravity" (the CLI starts as agy); DeepSeek
    /// Harness runs its CLI through its own executable. Claude Code installed
    /// with npm or bun runs its cli.js under node or bun, so no process is
    /// named claude: for a script host (Hosts) the hint is looked for in its
    /// command line instead.
    internal static readonly (TriggerTool Tool, string Name, string? PathHint)[] Known =
    {
        (TriggerTool.Claude, "claude", null),
        (TriggerTool.Claude, "node", ClaudeCodePackage),
        (TriggerTool.Claude, "bun", ClaudeCodePackage),
        (TriggerTool.Codex, "codex", null),
        (TriggerTool.Grok, "grok", null),
        (TriggerTool.Kimi, "kimi", ".kimi-code"),
        (TriggerTool.Kimi, "Kimi Code", null),
        (TriggerTool.Cursor, "cursor", null),
        (TriggerTool.Antigravity, "antigravity", null),
        (TriggerTool.Antigravity, "agy", null),
        (TriggerTool.DeepSeek, "DeepSeek Harness", null),
    };

    /// Apps that only count toward whether an agent is on screen. Grok Bot runs
    /// none of the grok CLI's sessions, so it stays out of Gone(): a CLI turn
    /// whose process died is still idled at once while Grok Bot is open.
    internal static readonly (TriggerTool Tool, string Name)[] PresenceOnly =
    {
        (TriggerTool.Grok, GrokBot.ProcessName),
    };

    /// Runtimes that run an agent as a script: their own path says nothing.
    internal static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "node", "bun" };

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
                    running.Add((name, known.PathHint is null ? null : Hosts.Contains(name) ? CommandLineOf(process.Id) : PathOf(process)));
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
                if (Matches(known, name, path)) alive.Add(known.Tool);
            }
        }
        return Known.Select(k => k.Tool).Where(tool => !alive.Contains(tool)).ToHashSet();
    }

    /// Whether a process is this entry's: by name, and by its path (a script
    /// host's command line, either slash) where the name is shared. A path
    /// that can't be read counts.
    internal static bool Matches((TriggerTool Tool, string Name, string? PathHint) known, string name, string? path) =>
        string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase)
        && (known.PathHint is null || path is null || path.Replace('/', '\\').Contains(known.PathHint, StringComparison.OrdinalIgnoreCase));

    /// A process's command line, from one query that needs no access to its
    /// memory (ProcessCommandLineInformation, Windows 8.1 and later); null
    /// when it can't be read.
    internal static string? CommandLineOf(int id)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)id);
        if (process == IntPtr.Zero) return null;
        var buffer = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(process, CommandLineInformation, IntPtr.Zero, 0, out var size);
            if (size <= 0 || size > 1 << 20) return null;
            buffer = Marshal.AllocHGlobal(size);
            if (NtQueryInformationProcess(process, CommandLineInformation, buffer, size, out _) != 0) return null;
            // A UNICODE_STRING: its length in bytes, then a pointer into the same buffer.
            var bytes = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(process);
        }
    }

    private const uint QueryLimitedInformation = 0x1000;
    private const int CommandLineInformation = 60;

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returned);

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
