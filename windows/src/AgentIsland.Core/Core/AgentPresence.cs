using System.Runtime.InteropServices;
using AgentIsland.Model;

namespace AgentIsland.Core;

/// Whether an agent's app is on screen. An agent's windows are its own
/// processes' and its hosts': the Claude app around Claude Code, the Codex
/// app around its CLI, the terminal a CLI runs in (or that terminal's console
/// host). A window covered by another still counts as open. In order, from
/// the most present: an open window, only minimized ones, none at all (in
/// the tray), no process left.
public enum AgentPresence
{
    Open,
    Minimized,
    Hidden,
    Closed,
}

public static class AgentPresences
{
    /// A process: its id, its parent's, its image name without ".exe", and
    /// its path (a script host's command line) where a name alone is ambiguous.
    internal sealed record Running(int Id, int ParentId, string Name, string? Path = null)
    {
        /// A headless `kimi -p` run: no window, so never a sign that Kimi is open.
        public bool Headless { get; init; }
    }

    /// A top-level app window: whose it is, and whether it is minimized.
    internal sealed record AppWindow(int ProcessId, bool Minimized);

    // Above these, a host chain has left the agent: the shell's own windows
    // (the desktop, the taskbar) say nothing about it.
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "svchost", "services", "wininit", "winlogon", "userinit", "csrss", "smss",
        "System", "taskhostw", "RuntimeBroker", "ApplicationFrameHost", "dwm",
    };

    // A console's window belongs to its host, a child of the shell it serves.
    private static readonly HashSet<string> ConsoleHosts = new(StringComparer.OrdinalIgnoreCase) { "conhost", "OpenConsole" };

    private const string Terminal = "WindowsTerminal";

    /// Every agent with a known process name, as it stands now. An agent whose
    /// processes can't be read counts as open: it never rests on a guess.
    public static IReadOnlyDictionary<TriggerTool, AgentPresence> Now()
    {
        // Until the macOS platform layer reads processes and windows, every agent counts as open.
        if (!OperatingSystem.IsWindows()) return AgentProcesses.Known.Select(k => k.Tool).Distinct().ToDictionary(tool => tool, _ => AgentPresence.Open);
        try
        {
            var processes = Processes();
            if (processes.Any(p => string.Equals(p.Name, GrokBot.ProcessName, StringComparison.OrdinalIgnoreCase))) GrokBot.NoteRunning();
            return Of(processes, Windows(), id => PathOf(id) is { } path && IsConsoleImage(path));
        }
        catch
        {
            return AgentProcesses.Known.Select(k => k.Tool).Distinct().ToDictionary(tool => tool, _ => AgentPresence.Open);
        }
    }

    /// isConsole says whether a process is a console program; without it no
    /// chain counts as a console handed off to Windows Terminal.
    internal static Dictionary<TriggerTool, AgentPresence> Of(IReadOnlyList<Running> processes, IReadOnlyList<AppWindow> windows,
        Func<int, bool>? isConsole = null)
    {
        var byId = new Dictionary<int, Running>();
        foreach (var process in processes) byId.TryAdd(process.Id, process);
        var children = processes.ToLookup(p => p.ParentId);
        var shown = windows.ToLookup(w => w.ProcessId);
        AgentPresence Most(IEnumerable<AppWindow> candidates)
        {
            var most = AgentPresence.Hidden;
            foreach (var window in candidates)
            {
                if (!window.Minimized) return AgentPresence.Open;
                most = AgentPresence.Minimized;
            }
            return most;
        }
        // The most a process shows, through its host chain.
        AgentPresence Shows(Running process)
        {
            var most = AgentPresence.Hidden;
            var seen = new HashSet<int>();
            Running? top = null;
            for (var current = process; current is not null && seen.Add(current.Id) && seen.Count <= 8 && !Shell.Contains(current.Name);
                current = byId.GetValueOrDefault(current.ParentId))
            {
                top = current;
                var hosts = children[current.Id].Where(child => ConsoleHosts.Contains(child.Name)).Select(child => child.Id).Prepend(current.Id);
                var here = Most(hosts.SelectMany(id => shown[id]));
                if (here == AgentPresence.Open) return here;
                if (here == AgentPresence.Minimized) most = here;
            }
            // Windows 11 hands a console started from the Start menu or Run
            // to Windows Terminal: its tab is in a terminal window outside
            // the chain. Only a chain started by a console program is one; a
            // CLI under a desktop app in the tray stays in the tray.
            if (most == AgentPresence.Hidden && top is not null && isConsole?.Invoke(top.Id) == true)
            {
                return Most(processes.Where(p => string.Equals(p.Name, Terminal, StringComparison.OrdinalIgnoreCase)).SelectMany(p => shown[p.Id]));
            }
            return most;
        }
        var presence = new Dictionary<TriggerTool, AgentPresence>();
        foreach (var tool in AgentProcesses.Known.Select(k => k.Tool).Distinct())
        {
            var mine = processes.Where(p => !p.Headless).Where(p => AgentProcesses.Known.Any(k => k.Tool == tool && AgentProcesses.Matches(k, p.Name, p.Path))
                || AgentProcesses.PresenceOnly.Any(k => k.Tool == tool && string.Equals(k.Name, p.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            presence[tool] = mine.Count == 0 ? AgentPresence.Closed : mine.Select(Shows).Min();
        }
        return presence;
    }

    /// The line for a character whose every app is closed. Grok's shared
    /// character names its apps, Grok Bot only where it is installed. A
    /// no-break space keeps Grok Bot's name whole where the line wraps.
    public static string ClosedLine(DisplayProvider character, bool grokBotInstalled) => character switch
    {
        DisplayProvider.Grok when grokBotInstalled => "Grok, Grok\u00A0Bot and Cursor closed",
        DisplayProvider.Grok => "Grok and Cursor closed",
        _ => "App closed",
    };

    // MARK: - The system's view

    private static List<Running> Processes()
    {
        var list = new List<Running>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) throw new InvalidOperationException("no process snapshot");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                var name = Path.GetFileNameWithoutExtension(entry.ExeFile);
                // Only a name a path tells apart needs its path.
                var path = !AgentProcesses.Known.Any(k => k.PathHint is not null && string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) ? null
                    : AgentProcesses.Hosts.Contains(name) ? AgentProcesses.CommandLineOf((int)entry.ProcessId)
                    : PathOf((int)entry.ProcessId);
                list.Add(new Running((int)entry.ProcessId, (int)entry.ParentProcessId, name, path)
                {
                    Headless = AgentProcesses.KimiRunOf((int)entry.ProcessId, name, path).Print,
                });
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return list;
    }

    /// Visible, titled top-level windows that stand for an app: not a tool
    /// window, not owned by another window.
    private static List<AppWindow> Windows()
    {
        var list = new List<AppWindow>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, OwnerWindow) != IntPtr.Zero || GetWindowTextLengthW(window) == 0) return true;
            if (((long)GetWindowLongPtrW(window, ExtendedStyle) & ToolWindow) != 0) return true;
            GetWindowThreadProcessId(window, out var process);
            list.Add(new AppWindow((int)process, IsIconic(window)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ConsoleImages = new(StringComparer.OrdinalIgnoreCase);

    /// Whether an executable is a console program: its PE header's subsystem
    /// is IMAGE_SUBSYSTEM_WINDOWS_CUI. Read once per path.
    internal static bool IsConsoleImage(string path) => ConsoleImages.GetOrAdd(path, static image =>
    {
        try
        {
            using var stream = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3C;
            var header = reader.ReadInt32();
            stream.Position = header;
            if (reader.ReadUInt32() != 0x4550) return false;
            stream.Position = header + 4 + 20 + 68;
            return reader.ReadUInt16() == 3;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    });

    private static string? PathOf(int id)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)id);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint SnapProcess = 0x2;
    private const uint OwnerWindow = 4;
    private const int ExtendedStyle = -20;
    private const long ToolWindow = 0x80;
    private const uint QueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
