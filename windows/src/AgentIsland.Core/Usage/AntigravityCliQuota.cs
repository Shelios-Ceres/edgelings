using System.Diagnostics;
using System.Text;
using AgentIsland.Core;
using AgentIsland.Trigger;

namespace AgentIsland.Usage;

internal static class AntigravityCliQuota
{
    private static readonly object VersionGate = new();
    private static (string Exe, long MtimeTicks, long Length, bool Supported)? _cachedVersion;

    internal static AntigravityUsageFetcher.Outcome? TryFetch()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Path.Combine(IslandPaths.LocalAppData, "agy", "bin", "agy.exe");
        if (!File.Exists(exe)) exe = CLILocator.Locate("agy");
        if (exe is null || !Path.IsPathFullyQualified(exe)
            || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;

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
        try { Directory.CreateDirectory(home); } catch { }
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
        // Isolate agy's config/plugin/MCP home away from the real %USERPROFILE%
        // so /usage never loads user plugins or cold-connects remote OAuth MCPs
        // (Notion, XMind, Google Workspace), preventing OAuth 2.1 refresh-token
        // rotation races against the desktop Antigravity language server while
        // still reading Antigravity's own login from Windows Credential Manager.
        start.Environment["USERPROFILE"] = home;
        start.Environment["HOME"] = home;
        start.Environment["ANTIGRAVITY_GEMINI_DIR"] = Path.Combine(home, ".gemini");
        return start;
    }

    private static string? Run(string exe, string arguments, int timeoutSeconds, string isolatedHome) =>
        AntigravityLanguageServer.Output(StartInfo(exe, arguments, isolatedHome), TimeSpan.FromSeconds(timeoutSeconds));
}
