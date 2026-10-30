using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AgentIsland.Model;

/// Registry Run-key registration — the Windows counterpart of SMAppService,
/// which the Mac app calls from Swift (Core does nothing here on a Mac).
/// Windows keeps its own switch for each entry (Settings > Apps > Startup,
/// Task Manager) under StartupApproved and skips a switched-off entry at
/// sign-in even though the Run value is still there.
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Edgelings";
    /// What Agent Island registered itself as, before the rename.
    private const string LegacyValueName = "AgentIsland";

    /// Registered, and not switched off in Windows' startup settings.
    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string) return false;
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return IsApproved(approved?.GetValue(ValueName) as byte[]);
            }
            catch
            {
                return false;
            }
        }
    }

    /// Windows' switch: no value means on; otherwise the first byte is even
    /// when on (2, 6) and odd when switched off (1, 3, 7).
    internal static bool IsApproved(byte[]? state) => state is not { Length: > 0 } || state[0] % 2 == 0;

    /// Turning it on here is the user's own call, so it also clears a switch
    /// left off in Windows' startup settings — one set off for the WPF app
    /// kept this build from starting at sign-in.
    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (enabled)
            {
                Register();
            }
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    /// An enabled registration that starts another executable — the WPF app
    /// this one replaces, or a build that moved — moves to this one. Windows'
    /// own switch is left as the user set it.
    public static void Repoint()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.ProcessPath is not { Length: > 0 } exe) return;
        MoveLegacyEntry(exe);
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (key?.GetValue(ValueName) is not string registered) return;
                if (string.Equals(registered.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase)) return;
            }
            Register();
        }
        catch
        {
        }
    }

    /// An Agent Island registration becomes an Edgelings one, once: the old
    /// Run value is replaced by one that starts this executable, and Windows'
    /// own switch for it (StartupApproved) moves to the new name, so an entry
    /// the user had switched off stays off. Nothing happens without the old value.
    [SupportedOSPlatform("windows")]
    internal static void MoveLegacyEntry(string exe, string runKey = RunKey, string approvedKey = ApprovedKey)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (run?.GetValue(LegacyValueName) is not string) return;
            run.SetValue(ValueName, $"\"{exe}\"");
            using (var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true))
            {
                if (approved?.GetValue(LegacyValueName) is byte[] state)
                {
                    approved.SetValue(ValueName, state, RegistryValueKind.Binary);
                    approved.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                }
            }
            run.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Register()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\"");
    }
}
