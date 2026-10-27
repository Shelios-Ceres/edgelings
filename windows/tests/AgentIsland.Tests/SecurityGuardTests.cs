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
            ("account labels cannot escape the store directory", TestAccountLabelSanitizing),
            ("a CLI under a folder with & or ( ) in its name runs whole", TestShieldedCommand),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SecurityGuardTests GREEN");
    }

    /// A saved-account label becomes a FILENAME holding a live credential
    /// pair, so it must never carry a separator, a traversal segment, or a
    /// reserved DOS device name. Unlike a session id it is cosmetic, so this
    /// one strips rather than rejects.
    private static void TestAccountLabelSanitizing()
    {
        var cases = new (string? Raw, string Expected)[]
        {
            ("work", "work"),
            ("work / personal", "work  personal"),
            (@"..\..\etc", "etc"),
            (@"a/b\c", "abc"),
            ("CON", "CON_"),
            ("com1", "com1_"),
            ("  ", ""),
            ("", ""),
            (null, ""),
        };
        foreach (var (raw, expected) in cases)
        {
            var actual = CodexAccountSwitcher.Sanitize(raw);
            Expect(actual == expected,
                $"Sanitize(\"{raw}\") = \"{actual}\", expected \"{expected}\"");
        }

        // The 40-character cap runs BEFORE the trim, so a label that ends on
        // a space at the boundary must not keep it: Win32 drops a trailing
        // space from a filename, and two labels would then fight over one file.
        var long40 = CodexAccountSwitcher.Sanitize(new string('a', 60));
        Expect(long40.Length == 40, $"label cap not applied: {long40.Length} chars");
        var boundarySpace = CodexAccountSwitcher.Sanitize(new string('a', 39) + "   tail");
        Expect(boundarySpace == new string('a', 39),
            $"a trailing space survived the cap: \"{boundarySpace}\"");
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
