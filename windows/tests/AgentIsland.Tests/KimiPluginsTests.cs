using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Kimi Code's plugins: the data folder, the memory worker's background
/// runs and the mailbox. Each test runs against a temp KIMI_CODE_HOME.
public static class KimiPluginsTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("kimi home: KIMI_CODE_HOME wins, ~/.kimi-code otherwise", KimiHomeVariable),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS kimi plugins: " + name);
        }
        Console.WriteLine("KimiPluginsTests GREEN");
    }

    /// Runs `body` with KIMI_CODE_HOME pointing at a fresh temp folder.
    private static void WithHome(Action<string> body)
    {
        var home = Path.Combine(Path.GetTempPath(), "agentisland-kimi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var before = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
        try
        {
            body(home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", before);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    private static void KimiHomeVariable()
    {
        var before = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", null);
            Expect(IslandPaths.KimiHome, Path.Combine(IslandPaths.Home, ".kimi-code"));
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", "  ");
            Expect(IslandPaths.KimiHome, Path.Combine(IslandPaths.Home, ".kimi-code"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", before);
        }
        WithHome(home =>
        {
            Expect(IslandPaths.KimiHome, home);
            Expect(IslandPaths.KimiSessionsRoot, Path.Combine(home, "sessions"));
        });
    }
}
