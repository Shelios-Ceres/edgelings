using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Pins the guard that keeps a saved Codex account label from escaping the
/// account store directory or naming a reserved DOS device, and the quoting
/// that keeps a CLI path whole on cmd's command line.
public static class SecurityGuardTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a CLI under a folder with & or ( ) in its name runs whole", TestShieldedCommand),
            ("on a Mac the login script quotes a CLI path with a space or a quote", TestMacScript),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SecurityGuardTests GREEN");
    }

    private static void TestMacScript()
    {
        var script = TerminalLauncher.MacScript("/Users/o'neil/My Tools/claude", "/login", "Limen Claude login");
        Expect(script.StartsWith("#!/bin/bash\n", StringComparison.Ordinal), "a bash script");
        Expect(script.Contains("'/Users/o'\\''neil/My Tools/claude' /login\n", StringComparison.Ordinal), "the path is one shell word, its quote escaped: " + script);
        Expect(script.Contains("You can close this window", StringComparison.Ordinal), "the window says when it is done");
    }

    /// The login window's command, run with /c instead of /k and no window,
    /// against a stand-in CLI that records the arguments it got.
    private static void TestShieldedCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentisland-launch-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "R&D Team (x)");
        Directory.CreateDirectory(folder);
        try
        {
            var cli = Path.Combine(folder, "claude.cmd");
            File.WriteAllText(cli, "@echo %*> \"%~dp0args.txt\"\r\n");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/s /c " + TerminalLauncher.ShieldedCommand(cli, "/login", "Agent Island login"),
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            Expect(process.WaitForExit(TimeSpan.FromSeconds(10)), "the command finished");
            var args = Path.Combine(folder, "args.txt");
            Expect(File.Exists(args) && File.ReadAllText(args).Trim() == "/login",
                "the CLI ran from its own folder with its argument, not split at the &");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
