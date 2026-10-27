using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentIsland.Usage;

/// Client for Antigravity's local language server — the only place its quota
/// is actually readable.
///
/// The cloud path other tools document is a dead end (verified on a real
/// signed-in account, 2026-08-08): `retrieveUserQuota` answers with legacy
/// Gemini Code Assist buckets, `loadCodeAssist` says UNSUPPORTED_CLIENT, and
/// `retrieveUserQuotaSummary` 403s for a plain Bearer caller. The CLI
/// reaches the same method through its own language server, which does hold
/// the real numbers.
///
/// That server is embedded in the `agy` process rather than spawned
/// separately, so quota is readable exactly while Antigravity is running.
/// There is no on-disk cache to fall back on, so the store keeps the last
/// good snapshot and the UI dates it.
public static class AntigravityLanguageServer
{
    private const string ServicePrefix = "/exa.language_server_pb.LanguageServerService/";
    private const string Host = "127.0.0.1";

    public sealed record Reply(int Status, byte[] Body);

    // The server speaks HTTPS with a self-signed certificate. The trust
    // override is scoped to loopback so it can never soften validation for
    // a real host.
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, _) =>
            request.RequestUri?.Host == Host,
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private static int _cachedPort;
    private static int _cachedPid;

    public static async Task<Reply?> Call(
        string method,
        int port,
        string body = "{}",
        string? csrfToken = null,
        double timeoutSeconds = 6)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{Host}:{port}{ServicePrefix}{method}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        // The CLI's own server rejects nothing and wants no token; the
        // desktop IDE gates on one. Sending an empty header to the CLI would
        // be worse than sending none, so it stays absent unless we have one.
        if (!string.IsNullOrEmpty(csrfToken))
        {
            request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            return new Reply((int)response.StatusCode, data);
        }
        catch
        {
            return null;
        }
    }

    /// Ports are assigned at launch and written nowhere, so they are found
    /// by walking the running Antigravity processes' listening sockets —
    /// GetExtendedTcpTable in-process; shelling out to netstat on every
    /// refresh tick would fork twice a minute for the life of the app.
    public static async Task<int?> Discover()
    {
        var cached = _cachedPort;
        var cachedPid = _cachedPid;
        // Still the same server's port: one Antigravity let go of can be taken
        // by another product's language server, which answers just the same.
        if (cached > 0 && PortOwner(cached) == cachedPid && await IsAlive(cached).ConfigureAwait(false)) return cached;
        foreach (var pid in AntigravityProcessIds())
        {
            foreach (var port in ListeningPorts(pid))
            {
                if (port == cached && pid == cachedPid) continue;
                if (await IsAlive(port).ConfigureAwait(false))
                {
                    _cachedPort = port;
                    _cachedPid = pid;
                    return port;
                }
            }
        }
        _cachedPort = 0;
        _cachedPid = 0;
        return null;
    }

    /// `GetUnleashData` is the cheapest method that proves this is the RPC
    /// port rather than a sibling plain-HTTP port the process also opens.
    /// 401 counts as alive: the port is right and only the token is missing.
    private static async Task<bool> IsAlive(int port)
    {
        var reply = await Call(
            "GetUnleashData", port, "{\"wrapper_data\":{}}", timeoutSeconds: 2)
            .ConfigureAwait(false);
        return reply is { Status: 200 or 401 };
    }

    /// Matches the CLI (whose real binary is `antigravity`, reached through
    /// an `agy` shim) and the desktop app. Process names on Windows drop the
    /// .exe extension.
    public static bool IsAntigravityName(string name) =>
        name.Equals("agy", StringComparison.OrdinalIgnoreCase)
        || name.Equals("antigravity", StringComparison.OrdinalIgnoreCase)
        || name.Equals("antigravity-cli", StringComparison.OrdinalIgnoreCase);

    /// Antigravity's own processes, and a separately spawned
    /// `language_server*` only when it is Antigravity's: Windsurf and Codeium
    /// ship a language server of the same name and the same RPC, so the name
    /// alone would query theirs and hand them Antigravity's token. Theirs live
    /// in their own install folders and are started by their own editors;
    /// Antigravity's sits under an Antigravity folder, or Antigravity started it.
    internal static bool IsAntigravityProcess(string name, string? path, string? parentName)
    {
        if (IsAntigravityName(name)) return true;
        if (!name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase)) return false;
        if (parentName is not null && IsAntigravityName(parentName)) return true;
        return Path.GetDirectoryName(path) is { } folder
            && folder.Split('\\', '/').Any(segment => segment.StartsWith("antigravity", StringComparison.OrdinalIgnoreCase));
    }

    public static List<int> AntigravityProcessIds()
    {
        var ids = new List<int>();
        var processes = Processes();
        var names = new Dictionary<int, string>();
        foreach (var (id, _, name) in processes) names.TryAdd(id, name);
        foreach (var (id, parent, name) in processes)
        {
            var path = name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase) ? ImagePath(id) : null;
            if (IsAntigravityProcess(name, path, names.GetValueOrDefault(parent))) ids.Add(id);
        }
        return ids;
    }

    // MARK: - Processes (toolhelp)

    /// Every process as (id, parent id, image name without ".exe"). Empty if
    /// the system won't give a snapshot.
    internal static List<(int Id, int ParentId, string Name)> Processes()
    {
        var list = new List<(int, int, string)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return list;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                list.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, Path.GetFileNameWithoutExtension(entry.ExeFile)));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return list;
    }

    private static string? ImagePath(int id)
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

    // MARK: - Listening-port enumeration (iphlpapi)

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

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

    public static List<int> ListeningPorts(int pid)
    {
        var ports = new List<int>();
        foreach (var (owner, port) in Listeners())
        {
            if (owner == pid && !ports.Contains(port)) ports.Add(port);
        }
        return ports;
    }

    /// The process listening on a loopback port, if any.
    internal static int? PortOwner(int port)
    {
        foreach (var (owner, listening) in Listeners())
        {
            if (listening == port) return owner;
        }
        return null;
    }

    private static List<(int Pid, int Port)> Listeners()
    {
        var listeners = new List<(int, int)>();
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
                if (port > 0) listeners.Add(((int)row.OwningPid, port));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return listeners;
    }

    // MARK: - CSRF token

    private static readonly object TokenGate = new();

    /// What each process's command line said, by pid and start time (a pid
    /// can be reused). A token is kept for the process's life; a read that
    /// found none is tried again only after a while.
    private static readonly Dictionary<int, (DateTime? Started, string? Token, DateTimeOffset? RetryAt)> TokenByPid = new();

    private static readonly TimeSpan CommandLineTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LookAgainAfter = TimeSpan.FromMinutes(5);

    /// Test seams: the command-line read, and forgetting what it found.
    internal static Func<int, string?> ReadCommandLine = ProcessCommandLine;
    internal static void ForgetCsrfTokens()
    {
        lock (TokenGate) TokenByPid.Clear();
    }

    /// The CLI needs no token and must not be sent one. The desktop IDE
    /// passes its own on the command line, so it is lifted from the process
    /// command line only after a 401 says the plain call was refused —
    /// a strictly additive retry on a path that already failed. Reading it
    /// starts PowerShell, so it is read once per process.
    public static string? CsrfToken(int pid)
    {
        var started = StartTime(pid);
        var now = DateTimeOffset.Now;
        lock (TokenGate)
        {
            if (TokenByPid.TryGetValue(pid, out var known) && known.Started == started
                && (known.RetryAt is not { } retryAt || now < retryAt))
            {
                return known.Token;
            }
        }
        var args = ReadCommandLine(pid);
        string? token = null;
        if (args is not null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                args, "--csrf_token[=\\s]+([^\\s\"]+)");
            token = match.Success ? match.Groups[1].Value : null;
        }
        lock (TokenGate)
        {
            if (TokenByPid.Count >= 64) TokenByPid.Clear();
            TokenByPid[pid] = (started, token, token is null ? now + LookAgainAfter : null);
        }
        return token;
    }

    private static DateTime? StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ProcessCommandLine(int pid)
    {
        // WMI without a System.Management dependency: wmic is gone on
        // modern Windows, so query via PowerShell's CIM cmdlet. This runs
        // only on the rare desktop-IDE 401 retry path, never per tick.
        return Output(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter \\\"ProcessId="
                + pid + "\\\").CommandLine\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, CommandLineTimeout);
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
