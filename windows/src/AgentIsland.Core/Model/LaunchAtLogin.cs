using Microsoft.Win32;

namespace AgentIsland.Model;

/// Registry Run-key registration — the Windows counterpart of SMAppService.
/// Windows keeps its own switch for each entry (Settings > Apps > Startup,
/// Task Manager) under StartupApproved and skips a switched-off entry at
/// sign-in even though the Run value is still there.
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "AgentIsland";

    /// Registered, and not switched off in Windows' startup settings.
    public static bool IsEnabled
    {
        get
        {
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
        if (Environment.ProcessPath is not { Length: > 0 } exe) return;
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

    private static void Register()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\"");
    }
}
