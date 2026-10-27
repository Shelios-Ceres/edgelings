using System.Diagnostics;

namespace AgentIsland.Usage;

/// Opens a visible terminal window running a CLI command — the Windows
/// counterpart of the macOS `.command`-file-in-Terminal trick. Prefers
/// Windows Terminal when installed, falls back to a plain cmd window.
public static class TerminalLauncher
{
    public static bool RunVisible(string binary, string arguments, string title)
    {
        var command = $"\"{binary}\" {arguments}";
        try
        {
            // cmd /k keeps the quotes around a path only when nothing between
            // them is one of & < > ( ) @ ^ |; otherwise it strips them, and a
            // CLI under C:\Users\R&D Team\ runs as "C:\Users\R" and "D Team…".
            // Such a path gets a console of its own, its command wrapped in
            // one more pair of quotes that /s strips instead.
            if (binary.IndexOfAny(CmdSpecials) >= 0)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/s /k " + ShieldedCommand(binary, arguments, title),
                    UseShellExecute = false,
                    WorkingDirectory = Core.IslandPaths.Home,
                });
                return true;
            }
            var wt = Trigger.CLILocator.Locate("wt");
            if (wt is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = wt,
                    Arguments = $"new-tab --title \"{title}\" cmd /k {command}",
                    UseShellExecute = false,
                });
                return true;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"{title}\" cmd /k {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Core.IslandPaths.Home,
            });
            return true;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"AgentIsland: failed to open terminal: {error.Message}");
            return false;
        }
    }

    private static readonly char[] CmdSpecials = { '&', '<', '>', '(', ')', '@', '^', '|' };

    /// What follows `cmd /s /k` (or /c): the window's title, then the CLI.
    internal static string ShieldedCommand(string binary, string arguments, string title) =>
        $"\"title {title}& \"{binary}\" {arguments}\"";
}
