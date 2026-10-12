using Microsoft.Win32;

namespace AgentIsland.Model;

/// Registry Run-key registration — the Windows counterpart of SMAppService.
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentIsland";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
        }
    }

    /// An enabled registration that starts another executable — the WPF app
    /// this one replaces, or a build that moved — moves to this one.
    public static void Repoint()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string registered) return;
            if (string.Equals(registered.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch
        {
            return;
        }
        SetEnabled(true);
    }
}
