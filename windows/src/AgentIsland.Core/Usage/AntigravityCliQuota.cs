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
    private static bool? _sessionLocked;
    // The flag came from a lock or unlock event, not from asking Windows.
    private static bool _lockFromEvent;

    /// The lock state as read from Windows (at start, or by a test).
    internal static bool? SessionLocked
    {
        get { lock (WakeGate) return _sessionLocked; }
        set
        {
            lock (WakeGate)
            {
                _sessionLocked = value;
                _lockFromEvent = false;
            }
        }
    }

    /// A lock or unlock event. A lock it reports holds until the unlock
    /// event: Windows asked a moment later may not say locked yet.
    internal static void NoteSessionEvent(bool locked)
    {
        lock (WakeGate)
        {
            _sessionLocked = locked;
            _lockFromEvent = true;
        }
    }
    internal static Func<long> UptimeMilliseconds = () => Environment.TickCount64;
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

    /// The session's lock state as Windows reports it now. Null off Windows
    /// and when Windows does not say.
    internal static Func<bool?> QuerySessionLocked = () => OperatingSystem.IsWindows() ? WindowsSession.IsLocked() : null;

    private static bool SessionUnavailable() => UnavailableReason() is not null;

    /// Why the session cannot run agy now, or null when it can.
    private static string? UnavailableReason()
    {
        var now = Clock();
        lock (WakeGate)
        {
            if (OperatingSystem.IsWindows()) _sessionLocked ??= QuerySessionLocked();
            // Only an unlock event used to clear the flag, and signing in is
            // not one: a value read while the session was starting could hold
            // the CLI off for the whole session. A value Windows gave is asked
            // again; a lock event's is not.
            if (_sessionLocked != false && !_lockFromEvent && QuerySessionLocked() is { } current && current != _sessionLocked)
            {
                Diagnostics.Note($"session lock flag {Describe(_sessionLocked)}, Windows says {Describe(current)}");
                _sessionLocked = current;
            }
            if (now < _wakeGraceUntil) return "wake grace";
            if (_sessionLocked == true) return "session locked";
            if (_sessionLocked is null) return "session lock state unknown";
            if (UptimeMilliseconds() < 90_000) return "cold boot";
            return null;
        }
    }

    private static string Describe(bool? locked) => locked switch { true => "locked", false => "unlocked", null => "unknown" };

    private static bool LaunchIfAllowed(CancellationToken ct, Func<bool> launch)
    {
        lock (WakeGate) return !ct.IsCancellationRequested && !SessionUnavailable() && launch();
    }

    internal static bool ShouldDefer() => DeferReason() is not null;

    private static string? DeferReason()
    {
        if (UnavailableReason() is { } reason) return reason;
        if (!CheckOnline()) return "offline";
        if (!CheckReachable()) return "Google sign-in unreachable";
        return null;
    }

    /// Why each CLI poll went the way it did, one line each time the reason
    /// changes, so a quota that stops updating can be traced to the gate that
    /// held it. Reasons only: no token, account or command output is written.
    internal static class Diagnostics
    {
        private static readonly object Gate = new();
        private static string? _last;
        private const long MaxBytes = 64 * 1024;

        internal static string LogPath => Path.Combine(IslandPaths.CacheDir, "agy-quota-gate.log");

        /// Test seam: where lines go. False when the line could not be written.
        internal static Func<string, bool> Write = Append;

        /// A reason is remembered only once written, so a line lost to a
        /// busy file is tried again on the next poll.
        internal static void Note(string reason)
        {
            lock (Gate)
            {
                if (reason == _last) return;
                if (Write($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {reason}")) _last = reason;
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate) _last = null;
        }

        private static bool Append(string line)
        {
            try
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal static AntigravityUsageFetcher.Outcome? TryFetch(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Path.Combine(IslandPaths.LocalAppData, "agy", "bin", "agy.exe");
        if (!File.Exists(exe)) exe = CLILocator.Locate("agy");
        if (exe is null || !Path.IsPathFullyQualified(exe)
            || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;

        string? IneligibleReason() => ct.IsCancellationRequested ? "cancelled"
            : UnavailableReason() is { } reason ? reason
            : Model.DrawerStore.Shared.IsHidden(Model.DisplayProvider.Antigravity) ? "hidden"
            : null;
        bool NoLongerEligible() => IneligibleReason() is not null;
        AntigravityUsageFetcher.Outcome Deferred(string reason)
        {
            Diagnostics.Note("deferred: " + reason);
            return new AntigravityUsageFetcher.Outcome.Deferred();
        }
        AntigravityUsageFetcher.Outcome? Stopped(string reason, AntigravityUsageFetcher.Outcome? outcome)
        {
            Diagnostics.Note(IneligibleReason() is { } why ? "deferred: " + why : reason);
            return NoLongerEligible() ? new AntigravityUsageFetcher.Outcome.Deferred() : outcome;
        }

        // Skip CLI polling while offline, within wake grace, or unable to reach
        // Google OAuth: agy's silent refresh would fail and fall back to
        // interactive browser sign-in. Keep the cached snapshot untouched.
        if ((IneligibleReason() ?? DeferReason()) is { } held) return Deferred(held);

        // Before 1.1.11 an interactive slash command could be sent as a model
        // prompt in print mode. Check the actual executable once per binary
        // fingerprint instead of spawning --version on every poll.
        var home = EnsureIsolatedHome(IsolatedHomeDir);
        if (home is null) return Deferred("isolated home unavailable");
        if (ct.IsCancellationRequested) return Deferred("cancelled");
        if (!IsSupportedExecutable(exe, home, ct))
            return Stopped("agy --version failed or is older than 1.1.11", null);
        var logPath = Path.Combine(home, "last.log");
        try { File.Delete(logPath); } catch { }
        var output = Run(exe, $"--print /usage --output-format json --print-timeout 20s --log-file \"{logPath}\"", 25, home, ct);
        if (output is null)
            return Stopped("agy /usage failed", new AntigravityUsageFetcher.Outcome.Failed("Antigravity CLI quota unavailable"));
        if (AntigravityQuotaParser.ParseCliUsage(output) is not { } snapshot)
        {
            Diagnostics.Note("agy /usage reply unreadable");
            return new AntigravityUsageFetcher.Outcome.Failed("Antigravity CLI quota reply unreadable");
        }
        Diagnostics.Note("ok");
        return new AntigravityUsageFetcher.Outcome.Success(snapshot, null, FromCli: true);
    }

    internal static string IsolatedHomeDir => Path.Combine(IslandPaths.CacheDir, "agy-quota-home");

    internal static string? EnsureIsolatedHome(string home)
    {
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(home, "AppData", "Local"));
            Directory.CreateDirectory(Path.Combine(home, "AppData", "Roaming"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        return home;
    }

    internal static bool SupportsUsage(string? version) =>
        Version.TryParse(version?.Trim(), out var parsed) && parsed >= new Version(1, 1, 11);

    private static bool IsSupportedExecutable(string exe, string home, CancellationToken ct)
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

        var version = Run(exe, "--version", 5, home, ct);
        if (version is null) return false; // A deferred or failed launch is not a binary fingerprint.
        var supported = SupportsUsage(version);
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
            WorkingDirectory = home,
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

    private static string? Run(string exe, string arguments, int timeoutSeconds, string isolatedHome, CancellationToken ct)
    {
        var start = StartInfo(exe, arguments, isolatedHome);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        return OperatingSystem.IsWindows()
            ? RunInSingleProcessJob(start, timeout, launch => Model.DrawerStore.Shared.WhileVisible(
                Model.DisplayProvider.Antigravity, () => LaunchIfAllowed(ct, launch)))
            : AntigravityLanguageServer.Output(start, timeout);
    }

    /// Runs a Windows child inside a Job Object with ActiveProcessLimit = 1 and
    /// KILL_ON_JOB_CLOSE. The kernel itself rejects any attempt by agy.exe to
    /// spawn a grandchild process (rundll32, chrome.exe, updater, wt.exe).
    [SupportedOSPlatform("windows")]
    internal static string? RunInSingleProcessJob(ProcessStartInfo start, TimeSpan timeout, Func<Func<bool>, bool>? permitLaunch = null)
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

            return WindowsJobProcess.Run(job, start, timeout, permitLaunch);
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
    private static extern bool CloseHandle(IntPtr hObject);
}
