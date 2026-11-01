using System.Diagnostics;

namespace AgentIsland.Usage;

/// Opens a visible terminal window running a CLI command — the Windows
/// counterpart of the macOS `.command`-file-in-Terminal trick. Prefers
/// Windows Terminal when installed, falls back to a plain cmd window.
public static class TerminalLauncher
{
    public static bool RunVisible(string binary, string arguments, string title)
    {
        if (OperatingSystem.IsMacOS()) return RunInMacTerminal(binary, arguments, title);
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

    /// On a Mac: the command goes into a .command file that Terminal opens and
    /// runs (the same trick the original macOS app used), so no Automation or
    /// Accessibility permission is asked for.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool RunInMacTerminal(string binary, string arguments, string title)
    {
        try
        {
            var folder = Path.Combine(Core.IslandPaths.AppSupportDir, "terminal");
            Directory.CreateDirectory(folder);
            var slug = new string(title.Where(char.IsAsciiLetterOrDigit).ToArray());
            var file = Path.Combine(folder, (slug.Length == 0 ? "command" : slug) + ".command");
            File.WriteAllText(file, MacScript(binary, arguments, title));
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var open = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
            open.ArgumentList.Add("-a");
            open.ArgumentList.Add("Terminal");
            open.ArgumentList.Add(file);
            using var process = Process.Start(open);
            return process is not null;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Edgelings: failed to open Terminal: {error.Message}");
            return false;
        }
    }

    /// The script Terminal runs: the title, the CLI, and a pause so the window
    /// is not closed under a message the user has not read. The binary is
    /// single-quoted for the shell; the title and arguments are the app's own
    /// fixed words.
    internal static string MacScript(string binary, string arguments, string title) =>
        "#!/bin/bash\n" +
        $"printf '\\033]0;%s\\007' {ShellQuote(title)}\n" +
        $"{ShellQuote(binary)} {arguments}\n" +
        "status=$?\n" +
        "echo\n" +
        "echo \"Finished (status $status). You can close this window.\"\n";

    internal static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    private static readonly char[] CmdSpecials = { '&', '<', '>', '(', ')', '@', '^', '|' };

    /// What follows `cmd /s /k` (or /c): the window's title, then the CLI.
    internal static string ShieldedCommand(string binary, string arguments, string title) =>
        $"\"title {title}& \"{binary}\" {arguments}\"";
}
