using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Where the app and every agent keep things, and the root that moves all of
/// it for tests and benches. Temp folders only.
public static class IslandPathsTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("paths: a root override puts the home, the app's folders and every agent beneath it", RootMovesEverything),
            ("paths: the app's own folders are the platform's (Application Support and Caches on a Mac)", PlatformFolders),
            ("paths: Cursor's database and the Claude Desktop folder follow the platform", AgentFolders),
            ("paths: the hooks' signal folders stay at Agent Island's folder on Windows, Edgelings' on a Mac", HooksFolder),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        Console.WriteLine("IslandPathsTests GREEN");
    }

    private static void WithRoot(Action<string> body)
    {
        var before = IslandPaths.RootOverride;
        var root = Path.Combine(Path.GetTempPath(), "edgelings-paths-" + Guid.NewGuid().ToString("N"));
        IslandPaths.RootOverride = root;
        try
        {
            body(Path.GetFullPath(root));
        }
        finally
        {
            IslandPaths.RootOverride = before;
        }
    }

    private static bool Inside(string root, string path) =>
        Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void RootMovesEverything()
    {
        WithRoot(root =>
        {
            var all = new List<string>
            {
                IslandPaths.Home, IslandPaths.RoamingAppData, IslandPaths.LocalAppData, IslandPaths.AppSupportDir,
                IslandPaths.SettingsFile, IslandPaths.CacheDir, IslandPaths.CodexSessionsRoot, IslandPaths.CodexArchivedSessionsRoot,
                IslandPaths.CodexAuthFile, IslandPaths.GrokSessionsRoot, IslandPaths.KimiSessionsRoot, IslandPaths.DeepSeekSessionsRoot,
                IslandPaths.CursorStateDb, IslandPaths.ClaudeCredentialsFile, IslandPaths.HooksDir,
            };
            all.AddRange(IslandPaths.ClaudeConfigRoots);
            all.AddRange(IslandPaths.ClaudeProjectRoots);
            all.AddRange(IslandPaths.ClaudeDesktopDataRoots);
            foreach (var path in all) Expect(Inside(root, path), "outside the root: " + path);
        });
    }

    private static void PlatformFolders()
    {
        WithRoot(root =>
        {
            Expect(Path.GetFileName(IslandPaths.AppSupportDir), "Edgelings");
            Expect(IslandPaths.SettingsFile, Path.Combine(IslandPaths.AppSupportDir, "settings.json"));
            if (OperatingSystem.IsMacOS())
            {
                Expect(IslandPaths.AppSupportDir, Path.Combine(root, "home", "Library", "Application Support", "Edgelings"));
                Expect(IslandPaths.CacheDir, Path.Combine(root, "home", "Library", "Caches", "Edgelings"));
            }
            else
            {
                Expect(IslandPaths.AppSupportDir, Path.Combine(root, "home", "AppData", "Roaming", "Edgelings"));
                Expect(IslandPaths.CacheDir, Path.Combine(root, "home", "AppData", "Local", "Edgelings", "cache"));
            }
        });
    }

    private static void AgentFolders()
    {
        WithRoot(root =>
        {
            var roaming = IslandPaths.RoamingAppData;
            Expect(IslandPaths.CursorStateDb, Path.Combine(roaming, "Cursor", "User", "globalStorage", "state.vscdb"));
            if (OperatingSystem.IsMacOS())
            {
                Expect(IslandPaths.ClaudeDesktopDataRoots.Count, 1);
                Expect(IslandPaths.ClaudeDesktopDataRoots[0], Path.Combine(root, "home", "Library", "Application Support", "Claude"));
            }
            else
            {
                Expect(IslandPaths.ClaudeDesktopDataRoots[0], Path.Combine(roaming, "Claude"));
            }
            Expect(IslandPaths.ClaudeConfigRoots[0], Path.Combine(root, "home", ".claude"));
            Expect(IslandPaths.ClaudeConfigRoots[1], Path.Combine(root, "home", ".config", "claude"));
        });
    }

    private static void HooksFolder()
    {
        WithRoot(_ =>
        {
            Expect(IslandPaths.HooksDir, OperatingSystem.IsMacOS() ? IslandPaths.AppSupportDir : IslandPaths.LegacyAppSupportDir);
        });
    }
}
