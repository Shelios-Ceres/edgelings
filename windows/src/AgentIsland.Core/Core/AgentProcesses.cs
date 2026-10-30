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
        if (OperatingSystem.IsMacOS()) return GoneOnMac();
        if (!OperatingSystem.IsWindows()) return new HashSet<TriggerTool>();
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
                    var path = known.PathHint is null ? null : Hosts.Contains(name) ? CommandLineOf(process.Id) : PathOf(process);
                    // The memory plugin's headless runs are not Kimi being open.
                    if (KimiRunOf(process.Id, name, path).MemoryRun) continue;
                    running.Add((name, path));
                }
            }
            return Gone(running);
        }
        catch
        {
            return new HashSet<TriggerTool>();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static IReadOnlySet<TriggerTool> GoneOnMac()
    {
        try
        {
            var running = new List<(string Name, string? Path)>();
            foreach (var entry in MacProcesses.Snapshot())
            {
                var name = entry.Name;
                if (Known.All(k => !string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    var file = entry.Path is null ? null : System.IO.Path.GetFileNameWithoutExtension(entry.Path);
                    if (file is not null && Known.Any(k => string.Equals(k.Name, file, StringComparison.OrdinalIgnoreCase))) name = file;
                    else if (entry.Path is not null && entry.Path.Contains("/claude/versions/", StringComparison.Ordinal)) name = "claude";
                    else continue;
                }
                var known = Known.First(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
                var path = known.PathHint is null ? null : Hosts.Contains(name) ? CommandLineOf(entry.Id) : entry.Path;
                // The memory plugin's headless runs are not Kimi being open.
                if (KimiRunOf(entry.Id, name, path).MemoryRun) continue;
                running.Add((name, path));
            }
            return Gone(running);
        }
        catch
        {
            return new HashSet<TriggerTool>();
        }
    }

    /// The memory plugin's headless runs are left out first: they keep no
    /// session of the user's alive.
    internal static IReadOnlySet<TriggerTool> Gone(IEnumerable<(string Name, string? Path, KimiRun Run)> running) =>
        Gone(running.Where(p => !p.Run.MemoryRun).Select(p => (p.Name, p.Path)));

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

    /// What a Kimi Code process's command line says about its run, as two
    /// booleans: Print is a headless `kimi -p` run; MemoryRun is one of the
    /// memory plugin's. Nothing of the line is kept: the worker's prompt can
    /// hold the user's own memory text.
    internal readonly record struct KimiRun(bool Print, bool MemoryRun);

    /// Read only for a process that already matched the Kimi entry's path
    /// hint; a line that can't be read says nothing, so the process counts as
    /// it did before.
    internal static KimiRun KimiRunOf(int id, string name, string? path)
    {
        var known = Known.FirstOrDefault(k => k.Tool == TriggerTool.Kimi && k.PathHint is not null);
        return !Matches(known, name, path) || path is null ? default : KimiRunOf(CommandLineOf(id));
    }

    internal static KimiRun KimiRunOf(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return default;
        if (!HasPrintFlag(commandLine)) return default;
        // The worker starts each run in the plugin's memories folder with its
        // own agent profile from the plugin's runtime folder, and its
        // consolidation prompt names the memories folder.
        var line = commandLine.Replace('/', '\\');
        var memory = Names(line, KimiMemoryRuns.Folder) || Names(line, Path.Combine(IslandPaths.KimiHome, "memory", "runtime"));
        return new KimiRun(true, memory);
    }

    /// `-p` or `--prompt`, as a whole argument outside quotes.
    private static bool HasPrintFlag(string commandLine)
    {
        var token = new System.Text.StringBuilder();
        var quoted = false;
        var found = false;
        void End()
        {
            if (token.Length > 0)
            {
                var text = token.ToString();
                if (text == "-p" || text == "--prompt" || text.StartsWith("--prompt=", StringComparison.Ordinal)) found = true;
            }
            token.Clear();
        }
        for (var i = 0; i < commandLine.Length && !found; i++)
        {
            var c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                token.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                End();
            }
            else
            {
                token.Append(c);
            }
        }
        End();
        return found;
    }

    /// Whether the line names the folder (either slash, any case) as a path of its own:
    /// not as the start of a longer folder name.
    private static bool Names(string line, string folder)
    {
        var path = folder.Replace('/', '\\').TrimEnd('\\');
        for (var at = line.IndexOf(path, StringComparison.OrdinalIgnoreCase); at >= 0;
            at = line.IndexOf(path, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + path.Length;
            if (end == line.Length || !(char.IsLetterOrDigit(line[end]) || line[end] is '-' or '_' or '.')) return true;
        }
        return false;
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
        if (OperatingSystem.IsMacOS()) return MacProcesses.CommandLine(id);
        if (!OperatingSystem.IsWindows()) return null;
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
