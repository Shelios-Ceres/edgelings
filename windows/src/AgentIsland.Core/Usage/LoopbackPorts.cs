using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentIsland.Usage;

/// Which process listens on a loopback port. Kimi Code's local server is
/// trusted with its token only when the process that registered it is the one
/// that owns the port.
public static class LoopbackPorts
{
    private static readonly TimeSpan CommandLineTimeout = TimeSpan.FromSeconds(10);

    /// Test seam.
    internal static Func<int, int?> ResolvePortOwner = PortOwner;

    // MARK: - Listening-port enumeration (iphlpapi)

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private static readonly uint LoopbackIpv4 = BitConverter.ToUInt32(new byte[] { 127, 0, 0, 1 });

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool order,
        int ipVersion,
        int tableClass,
        uint reserved);

    /// The process listening on a loopback port, if any.
    internal static int? PortOwner(int port)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return null;
        return OwnerOf(ListenerRows(), port);
    }

    /// The one process that owns the port on 127.0.0.1. A listener bound to
    /// 127.0.0.1 itself wins over a wildcard one (Windows delivers to the more
    /// specific bind); several different processes at the winning level mean
    /// the owner is unknown, so nothing is trusted with a token.
    internal static int? OwnerOf(IEnumerable<(int Pid, int Port, bool Exact)> rows, int port)
    {
        var onPort = rows.Where(row => row.Port == port).ToList();
        var level = onPort.Any(row => row.Exact) ? onPort.Where(row => row.Exact) : onPort;
        var owners = level.Select(row => row.Pid).Distinct().Take(2).ToList();
        return owners.Count == 1 ? owners[0] : null;
    }

    private static List<(int Pid, int Port, bool Exact)> ListenerRows()
    {
        // lsof ships with macOS; its field output (-F) is stable and needs no parsing of columns.
        if (OperatingSystem.IsMacOS()) return ParseLsofRows(Output(new ProcessStartInfo
        {
            FileName = "/usr/sbin/lsof",
            Arguments = "-nP -iTCP -sTCP:LISTEN -Fpn",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, CommandLineTimeout) ?? "");
        var listeners = new List<(int, int, bool)>();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0) return listeners;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
            {
                return listeners;
            }
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
                // LocalPort is in network byte order, low 16 bits.
                var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                // Keep only listeners reachable on 127.0.0.1 (loopback or wildcard), matching ParseLsof.
                if (port > 0 && (row.LocalAddr == 0 || row.LocalAddr == LoopbackIpv4))
                {
                    listeners.Add(((int)row.OwningPid, port, row.LocalAddr == LoopbackIpv4));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return listeners;
    }

    /// `lsof -Fpn` prints "p<pid>" and then one "n<address>:<port>" per socket.
    /// Only sockets that 127.0.0.1 reaches are kept (that address and the
    /// wildcard): a listener on ::1 or on a network address must not make
    /// another local process's port on 127.0.0.1 look like someone else's.
    internal static List<(int Pid, int Port)> ParseLsof(string output) =>
        ParseLsofRows(output).Select(row => (row.Pid, row.Port)).ToList();

    /// ParseLsof's rows, each saying whether it was bound to 127.0.0.1 itself.
    internal static List<(int Pid, int Port, bool Exact)> ParseLsofRows(string output)
    {
        var listeners = new List<(int, int, bool)>();
        var pid = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;
            if (line[0] == 'p') int.TryParse(line.AsSpan(1), out pid);
            else if (line[0] == 'n' && pid > 0 && line.LastIndexOf(':') is var colon and > 1
                && line[1..colon] is "127.0.0.1" or "*"
                && int.TryParse(line.AsSpan(colon + 1), out var port) && port > 0)
            {
                listeners.Add((pid, port, line[1..colon] == "127.0.0.1"));
            }
        }
        return listeners;
    }

    /// A helper's trimmed output, or null if it failed or outran the timeout
    /// (then it is killed). Output is read while waiting: reading it to the
    /// end first would wait as long as the helper hangs.
    internal static string? Output(ProcessStartInfo start, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (process.ExitCode != 0 || !output.Wait(TimeSpan.FromSeconds(2))) return null;
            return output.Result.Trim();
        }
        catch
        {
            return null;
        }
    }
}
