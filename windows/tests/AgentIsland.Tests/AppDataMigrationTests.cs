using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The move from Agent Island's folders to Edgelings'. Temp folders only.
public static class AppDataMigrationTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("migration: copies the app's files once and leaves the hooks' alone", CopiesOnce),
            ("migration: a second start changes nothing", Idempotent),
            ("migration: the old folder is untouched", OldFolderUntouched),
            ("migration: a copy that was cut short is redone and its leftover removed", HalfDoneRecovers),
            ("migration: nothing to do without an old folder, or when the new one exists", NothingToDo),
            ("migration: the cache folder is copied the same way, creating its parent", CacheFolder),
            ("migration: the hooks still use the old signal folders", HooksStayPut),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        Console.WriteLine("AppDataMigrationTests GREEN");
    }

    private static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agentisland-migration-" + Guid.NewGuid().ToString("N"))).FullName;

    private static bool App(string name) =>
        !name.Equals("grok-hook.ps1", StringComparison.OrdinalIgnoreCase) && !name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase);

    /// An Agent Island app folder: settings, a crash log, a half-written temp file,
    /// Grok's hook script and the signal folders.
    private static string OldFolder(string root)
    {
        var old = Path.Combine(root, "AgentIsland");
        Directory.CreateDirectory(Path.Combine(old, "claude-hooks"));
        Directory.CreateDirectory(Path.Combine(old, "grok-hooks"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "{\"AgentIsland.drawer.side\":\"Left\"}");
        File.WriteAllText(Path.Combine(old, "crash.log"), "an old crash");
        File.WriteAllText(Path.Combine(old, "settings.json.tmp-1234"), "half");
        File.WriteAllText(Path.Combine(old, "grok-hook.ps1"), "# hook");
        File.WriteAllText(Path.Combine(old, "claude-hooks", "s1.json"), "{}");
        File.WriteAllText(Path.Combine(old, "grok-hooks", "s2.json"), "{}");
        return old;
    }

    private static string Snapshot(string folder) => string.Join(";", Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(folder, path) + "@" + (File.Exists(path) ? new FileInfo(path).Length + "/" + File.GetLastWriteTimeUtc(path).Ticks : "dir")));

    private static void CopiesOnce()
    {
        var root = Temp();
        try
        {
            var old = OldFolder(root);
            var fresh = Path.Combine(root, "Edgelings");
            Expect(AppDataMigration.Migrate(old, fresh, App), true);
            Expect(File.ReadAllText(Path.Combine(fresh, "settings.json")), "{\"AgentIsland.drawer.side\":\"Left\"}");
            Expect(File.ReadAllText(Path.Combine(fresh, "crash.log")), "an old crash");
            Expect(string.Join(",", Directory.EnumerateFileSystemEntries(fresh).Select(Path.GetFileName).Order(StringComparer.Ordinal)), "crash.log,settings.json");
            Expect(Directory.EnumerateDirectories(root, "Edgelings.migrating-*").Any(), false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Idempotent()
    {
        var root = Temp();
        try
        {
            var old = OldFolder(root);
            var fresh = Path.Combine(root, "Edgelings");
            Expect(AppDataMigration.Migrate(old, fresh, App), true);
            File.WriteAllText(Path.Combine(fresh, "settings.json"), "{\"changed\":true}");
            Expect(AppDataMigration.Migrate(old, fresh, App), false);
            Expect(File.ReadAllText(Path.Combine(fresh, "settings.json")), "{\"changed\":true}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void OldFolderUntouched()
    {
        var root = Temp();
        try
        {
            var old = OldFolder(root);
            var before = Snapshot(old);
            AppDataMigration.Migrate(old, Path.Combine(root, "Edgelings"), App);
            AppDataMigration.Migrate(old, Path.Combine(root, "Edgelings"), App);
            Expect(Snapshot(old), before);
            Expect(File.Exists(Path.Combine(old, "claude-hooks", "s1.json")) && File.Exists(Path.Combine(old, "grok-hook.ps1")), "the hooks' files are still there");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void HalfDoneRecovers()
    {
        var root = Temp();
        try
        {
            var old = OldFolder(root);
            // What a copy cut short leaves: the sibling folder with one file, no new folder.
            var leftover = Directory.CreateDirectory(Path.Combine(root, "Edgelings.migrating-deadbeef")).FullName;
            File.WriteAllText(Path.Combine(leftover, "settings.json"), "{\"half");
            var fresh = Path.Combine(root, "Edgelings");
            Expect(Directory.Exists(fresh), false);
            Expect(AppDataMigration.Migrate(old, fresh, App), true);
            Expect(File.ReadAllText(Path.Combine(fresh, "settings.json")), "{\"AgentIsland.drawer.side\":\"Left\"}");
            Expect(Directory.Exists(leftover), false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void NothingToDo()
    {
        var root = Temp();
        try
        {
            var fresh = Path.Combine(root, "Edgelings");
            Expect(AppDataMigration.Migrate(Path.Combine(root, "AgentIsland"), fresh, App), false);
            Expect(Directory.Exists(fresh), false);
            var old = OldFolder(root);
            Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(fresh, "settings.json"), "{\"new\":1}");
            Expect(AppDataMigration.Migrate(old, fresh, App), false);
            Expect(File.ReadAllText(Path.Combine(fresh, "settings.json")), "{\"new\":1}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CacheFolder()
    {
        var root = Temp();
        try
        {
            var old = Path.Combine(root, "local", "AgentIsland", "cache");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "kimi-parse-cache.v4.json"), "{}");
            File.WriteAllText(Path.Combine(old, "antigravity-models.v1.json"), "{}");
            var fresh = Path.Combine(root, "local", "Edgelings", "cache");
            Expect(AppDataMigration.Migrate(old, fresh, _ => true), true);
            Expect(Directory.EnumerateFiles(fresh).Count(), 2);
            Expect(Directory.EnumerateFiles(old).Count(), 2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void HooksStayPut()
    {
        var legacy = IslandPaths.LegacyAppSupportDir;
        Expect(Path.GetFileName(legacy), "AgentIsland");
        Expect(Path.GetFileName(IslandPaths.AppSupportDir), "Edgelings");
        // Windows keeps the hooks in Agent Island's folder; a Mac install never had one.
        var hooks = OperatingSystem.IsMacOS() ? IslandPaths.AppSupportDir : legacy;
        Expect(ClaudeHooks.SignalDirectory, Path.Combine(hooks, "claude-hooks"));
        Expect(GrokHooks.Signals, Path.Combine(hooks, "grok-hooks"));
        Expect(GrokHooks.Scripts, hooks);
        // Settings and caches moved; the hooks did not.
        Expect(IslandPaths.SettingsFile, Path.Combine(IslandPaths.AppSupportDir, "settings.json"));
        Expect(Path.GetFileName(OperatingSystem.IsMacOS() ? IslandPaths.CacheDir : Path.GetDirectoryName(IslandPaths.CacheDir)), "Edgelings");
    }
}
