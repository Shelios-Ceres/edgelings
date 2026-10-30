using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentIsland.Core;

/// The process table on macOS, read in process through libproc and sysctl (both
/// live in libSystem): ids, parents, names, image paths and, for a script
/// host, the command line. No window titles and no environments are ever
/// read. Windows come from Swift, which owns AppKit (see MacWindowReport).
///
/// Every call degrades to "nothing read" when a process is gone or off
/// limits: another user's process answers EPERM, and that is not an error.
/// One process: id, parent, name, image path.
internal sealed record MacProcessEntry(int Id, int ParentId, string Name, string? Path);

[SupportedOSPlatform("macos")]
internal static class MacProcesses
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private const int ProcPidTbsdInfo = 3;
    private const int BsdInfoSize = 136;
    private const int PathMax = 4096;
    private const int CtlKern = 1;
    private const int KernProcArgs2 = 49;
    private const int ArgBytes = 128 * 1024;

    // proc_bsdinfo: pbi_ppid at 16, pbi_comm[16] at 48, pbi_name[32] at 64.
    private const int PpidOffset = 16;
    private const int CommOffset = 48;
    private const int NameOffset = 64;

    [DllImport(LibSystem)]
    private static extern int proc_listallpids(int[]? buffer, int size);

    [DllImport(LibSystem)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong argument, byte[] buffer, int size);

    [DllImport(LibSystem)]
    private static extern int proc_pidpath(int pid, byte[] buffer, uint size);

    [DllImport(LibSystem)]
    private static extern int sysctl(int[] name, uint nameLength, byte[]? old, ref nuint oldLength, IntPtr replacement, nuint replacementLength);

    /// Every process the user may inspect. Empty if the table can't be read.
    internal static List<MacProcessEntry> Snapshot()
    {
        var list = new List<MacProcessEntry>();
        var count = proc_listallpids(null, 0);
        if (count <= 0) return list;
        var ids = new int[count + 64];
        count = proc_listallpids(ids, ids.Length * sizeof(int));
        var info = new byte[BsdInfoSize];
        var path = new byte[PathMax];
        for (var i = 0; i < count && i < ids.Length; i++)
        {
            var id = ids[i];
            if (id <= 0) continue;
            if (proc_pidinfo(id, ProcPidTbsdInfo, 0, info, info.Length) < BsdInfoSize) continue;
            var parent = BitConverter.ToInt32(info, PpidOffset);
            var name = CString(info, NameOffset, 32);
            if (name.Length == 0) name = CString(info, CommOffset, 16);
            var length = proc_pidpath(id, path, (uint)path.Length);
            var image = length > 0 ? Encoding.UTF8.GetString(path, 0, Math.Min(length, path.Length)) : null;
            list.Add(new MacProcessEntry(id, parent, name, image));
        }
        return list;
    }

    /// The process's arguments, space-joined: the argv the program was started
    /// with, never its environment. Null when they can't be read.
    internal static string? CommandLine(int id)
    {
        var buffer = new byte[ArgBytes];
        var length = (nuint)buffer.Length;
        if (sysctl(new[] { CtlKern, KernProcArgs2, id }, 3, buffer, ref length, IntPtr.Zero, 0) != 0) return null;
        return ProcessArguments.Parse(buffer, (int)Math.Min(length, (nuint)buffer.Length));
    }

    private static string CString(byte[] buffer, int offset, int max)
    {
        var end = offset;
        while (end < offset + max && end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }
}

/// KERN_PROCARGS2 is parsed apart from the system call, so the layout is
/// tested on every machine.
internal static class ProcessArguments
{
    /// KERN_PROCARGS2's layout: an int argc, the executable's path, NUL
    /// padding, then argc NUL-terminated arguments, and then the environment,
    /// which is not looked at. A truncated buffer yields the arguments it holds.
    internal static string? Parse(byte[] buffer, int length)
    {
        if (length < sizeof(int)) return null;
        var argc = BitConverter.ToInt32(buffer, 0);
        if (argc <= 0) return null;
        var at = sizeof(int);
        while (at < length && buffer[at] != 0) at++;
        while (at < length && buffer[at] == 0) at++;
        var arguments = new List<string>();
        while (arguments.Count < argc && at < length)
        {
            var end = at;
            while (end < length && buffer[end] != 0) end++;
            arguments.Add(Encoding.UTF8.GetString(buffer, at, end - at));
            at = end + 1;
        }
        return arguments.Count == 0 ? null : string.Join(' ', arguments);
    }
}

/// What Swift reports about the user's windows, held for the presence check.
/// A report older than MaxAge is ignored: the app's own loop is not running,
/// and a stale picture would keep an agent "minimized" for ever.
public static class MacWindowReport
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(20);

    private static readonly object Gate = new();
    private static (DateTimeOffset At, IReadOnlyList<AgentPresences.AppWindow> Windows)? _latest;

    /// One entry per window of an app: its owner's process id, and whether it
    /// is off screen (minimized, or its app hidden).
    public static void Report(IEnumerable<AgentPresences.AppWindow> windows) => Report(windows, DateTimeOffset.UtcNow);

    internal static void Report(IEnumerable<AgentPresences.AppWindow> windows, DateTimeOffset now)
    {
        var copy = windows.ToList();
        lock (Gate) _latest = (now, copy);
    }

    internal static IReadOnlyList<AgentPresences.AppWindow>? Current(DateTimeOffset now)
    {
        lock (Gate) return _latest is { } latest && now - latest.At <= MaxAge ? latest.Windows : null;
    }

    /// How many windows the latest fresh report holds, or -1 when there is none.
    public static int ReportedCount() => Current(DateTimeOffset.UtcNow)?.Count ?? -1;

    internal static void Forget()
    {
        lock (Gate) _latest = null;
    }
}
