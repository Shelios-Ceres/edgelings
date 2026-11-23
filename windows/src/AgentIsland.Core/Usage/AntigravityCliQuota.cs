using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgentIsland.Core;
using AgentIsland.Trigger;

namespace AgentIsland.Usage;

internal static class AntigravityCliQuota
{
    private static readonly object VersionGate = new();
    private static (string Exe, long MtimeTicks, long Length, bool Supported)? _cachedVersion;

    private static readonly TimeSpan WakeGrace = TimeSpan.FromSeconds(45);
    private static readonly object WakeGate = new();
    private static DateTimeOffset _wakeGraceUntil = DateTimeOffset.MinValue;

    // Lightweight reachability probe using the system proxy (never sends tokens
    // or credentials). Any HTTP response proves TCP+TLS reachability to Google
    // OAuth; DNS/socket/timeout failure means we are offline or behind a captive
    // intranet and must NOT invoke agy (which would fall back to interactive
    // browser OAuth via rundll32).
    private static readonly HttpClient ProbeClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(2500),
    };

    // Test seams for deterministic offline/wake-grace verification.
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;
    internal static Func<bool> CheckOnline = InternetStatus.IsConnected;
    internal static Func<bool> CheckReachable = CanReachGoogleOAuth;

    internal static void NoteWake()
    {
        var until = Clock() + WakeGrace;
        lock (WakeGate)
        {
            if (until > _wakeGraceUntil) _wakeGraceUntil = until;
        }
    }

    internal static void ResetWakeGraceForTests()
    {
        lock (WakeGate) _wakeGraceUntil = DateTimeOffset.MinValue;
    }

    internal static bool ShouldDefer()
    {
        var now = Clock();
        lock (WakeGate)
        {
            if (now < _wakeGraceUntil) return true;
        }
        if (!CheckOnline()) return true;
        if (!CheckReachable()) return true;
        return false;
    }

    internal static AntigravityUsageFetcher.Outcome? TryFetch()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Path.Combine(IslandPaths.LocalAppData, "agy", "bin", "agy.exe");
        if (!File.Exists(exe)) exe = CLILocator.Locate("agy");
        if (exe is null || !Path.IsPathFullyQualified(exe)
            || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;

        // Skip CLI polling while offline, within wake grace, or unable to reach
        // Google OAuth: agy's silent refresh would fail and fall back to
        // interactive browser sign-in. Keep the cached snapshot untouched.
        if (ShouldDefer()) return new AntigravityUsageFetcher.Outcome.Deferred();

        // Before 1.1.11 an interactive slash command could be sent as a model
        // prompt in print mode. Check the actual executable once per binary
        // fingerprint instead of spawning --version on every poll.
        if (!IsSupportedExecutable(exe)) return null;
        var home = EnsureIsolatedHome();
        var logPath = Path.Combine(home, "last.log");
        try { File.Delete(logPath); } catch { }
        var output = Run(exe, $"--print /usage --output-format json --print-timeout 20s --log-file \"{logPath}\"", 25, home);
        if (output is null) return new AntigravityUsageFetcher.Outcome.Failed("Antigravity CLI quota unavailable");
        if (AntigravityQuotaParser.ParseCliUsage(output) is not { } snapshot)
            return new AntigravityUsageFetcher.Outcome.Failed("Antigravity CLI quota reply unreadable");
        return new AntigravityUsageFetcher.Outcome.Success(snapshot, null, FromCli: true);
    }

    internal static string IsolatedHomeDir => Path.Combine(IslandPaths.CacheDir, "agy-quota-home");

    private static string EnsureIsolatedHome()
    {
        var home = IsolatedHomeDir;
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(home, "AppData", "Local"));
            Directory.CreateDirectory(Path.Combine(home, "AppData", "Roaming"));
        }
        catch
        {
        }
        return home;
    }

    internal static bool SupportsUsage(string? version) =>
        Version.TryParse(version?.Trim(), out var parsed) && parsed >= new Version(1, 1, 11);

    private static bool IsSupportedExecutable(string exe)
    {
        long mtimeTicks = 0, length = 0;
        try
        {
            var info = new FileInfo(exe);
            if (!info.Exists) return false;
            mtimeTicks = info.LastWriteTimeUtc.Ticks;
            length = info.Length;
        }
        catch
        {
            return false;
        }

        lock (VersionGate)
        {
            if (_cachedVersion is { } cached
                && cached.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)
                && cached.MtimeTicks == mtimeTicks
                && cached.Length == length)
            {
                return cached.Supported;
            }
        }

        var supported = SupportsUsage(Run(exe, "--version", 5, EnsureIsolatedHome()));
        lock (VersionGate)
        {
            _cachedVersion = (exe, mtimeTicks, length, supported);
        }
        return supported;
    }

    internal static ProcessStartInfo StartInfo(string exe, string arguments, string? isolatedHome = null)
    {
        var home = isolatedHome ?? IsolatedHomeDir;
        var localAppData = Path.Combine(home, "AppData", "Local");
        var roamingAppData = Path.Combine(home, "AppData", "Roaming");
        var start = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = Directory.Exists(home) ? home : Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Suppress agy's 15-minute background auto-updater: on Windows its
        // detached updater subprocess allocates a visible Windows Terminal
        // window (~0.26s) and steals focus from the user's active app.
        // agy checks strict lowercase equality against "true" (not "1").
        start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
        // Keep print mode strictly non-interactive / headless.
        start.Environment["AGY_CLI_NONINTERACTIVE_HEADLESS"] = "true";
        // Isolate agy's entire Windows profile hierarchy (USERPROFILE, HOME,
        // LOCALAPPDATA, APPDATA, ANTIGRAVITY_GEMINI_DIR) into agy-quota-home so
        // /usage loads 0 user plugins/remote OAuth MCPs AND any descendant
        // process can never hit a split USERPROFILE vs LOCALAPPDATA mismatch
        // against the real user profile (e.g. Chromium 158 IsUsingDefaultDataDirectory).
        // Windows Credential Manager reads by logon session, so agy still reads
        // its own Google login token cleanly.
        start.Environment["USERPROFILE"] = home;
        start.Environment["HOME"] = home;
        start.Environment["LOCALAPPDATA"] = localAppData;
        start.Environment["APPDATA"] = roamingAppData;
        start.Environment["ANTIGRAVITY_GEMINI_DIR"] = Path.Combine(home, ".gemini");
        return start;
    }

    private static bool CanReachGoogleOAuth()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://oauth2.googleapis.com/tokeninfo");
            using var response = ProbeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            // Any HTTP status (including 400/405) confirms live TCP+TLS reachability.
            return (int)response.StatusCode > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? Run(string exe, string arguments, int timeoutSeconds, string isolatedHome)
    {
        var start = StartInfo(exe, arguments, isolatedHome);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        return OperatingSystem.IsWindows()
            ? RunInSingleProcessJob(start, timeout)
            : AntigravityLanguageServer.Output(start, timeout);
    }

    /// Runs a Windows child inside a Job Object with ActiveProcessLimit = 1 and
    /// KILL_ON_JOB_CLOSE. The kernel itself rejects any attempt by agy.exe to
    /// spawn a grandchild process (rundll32, chrome.exe, updater, wt.exe).
    [SupportedOSPlatform("windows")]
    internal static string? RunInSingleProcessJob(ProcessStartInfo start, TimeSpan timeout)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;
        try
        {
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitActiveProcess | JobObjectLimitKillOnJobClose,
                    ActiveProcessLimit = 1,
                },
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref info,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                return null;
            }

            using var process = Process.Start(start);
            if (process is null) return null;
            if (!AssignProcessToJobObject(job, process.Handle) && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (start.RedirectStandardInput)
            {
                try { process.StandardInput.Close(); } catch { }
            }
            var output = process.StandardOutput.ReadToEndAsync();
            var error = start.RedirectStandardError ? process.StandardError.ReadToEndAsync() : null;
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (process.ExitCode != 0 || !output.Wait(TimeSpan.FromSeconds(2))) return null;
            if (error is not null)
            {
                try { error.Wait(TimeSpan.FromSeconds(1)); } catch { }
            }
            return output.Result.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(job);
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int infoClass,
        ref JobObjectExtendedLimitInformation info,
        uint length);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
