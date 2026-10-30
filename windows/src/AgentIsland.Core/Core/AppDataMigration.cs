namespace AgentIsland.Core;

/// The app was Agent Island and kept its settings in %APPDATA%\AgentIsland and
/// its caches in %LOCALAPPDATA%\AgentIsland\cache. As Edgelings it uses
/// %APPDATA%\Edgelings and %LOCALAPPDATA%\Edgelings\cache. On first start the
/// old files are copied to the new folders, once. The old folders are never
/// changed, moved or deleted: the Claude and Grok hooks keep their signal
/// folders there, and an Agent Island build that still runs keeps using it.
public static class AppDataMigration
{
    /// Files that belong to the hooks and stay where they are.
    private static bool IsAppFile(string name) =>
        !name.Equals("grok-hook.ps1", StringComparison.OrdinalIgnoreCase)
        && !name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase);

    /// Copies settings and the crash log, then the caches. Safe to call on every start.
    public static void Run()
    {
        // The Mac app is new: it has no Agent Island folders to bring across.
        if (OperatingSystem.IsMacOS()) return;
        Migrate(IslandPaths.LegacyAppSupportDir, IslandPaths.AppSupportDir, IsAppFile);
        Migrate(IslandPaths.LegacyCacheDir, IslandPaths.CacheDir, _ => true);
    }

    /// Copies the files directly in oldDir (not its folders) to newDir, only
    /// when newDir doesn't exist and oldDir does. The copy is made in a
    /// sibling folder and renamed into place, so newDir is either absent or
    /// whole; a copy that was cut short leaves only a leftover that the next
    /// start removes. Returns whether it copied.
    internal static bool Migrate(string oldDir, string newDir, Func<string, bool> include)
    {
        string? temp = null;
        try
        {
            if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return false;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(newDir));
            if (string.IsNullOrEmpty(parent)) return false;
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(newDir));
            Directory.CreateDirectory(parent);
            foreach (var leftover in Directory.EnumerateDirectories(parent, leaf + ".migrating-*"))
            {
                try { Directory.Delete(leftover, recursive: true); } catch { }
            }
            temp = Path.Combine(parent, leaf + ".migrating-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            foreach (var file in Directory.EnumerateFiles(oldDir))
            {
                var name = Path.GetFileName(file);
                if (!include(name)) continue;
                File.Copy(file, Path.Combine(temp, name));
            }
            Directory.Move(temp, newDir);
            temp = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another start got there first, or a file is held open: whatever is
            // left of this copy goes, and the next start tries again.
            if (temp is not null) try { Directory.Delete(temp, recursive: true); } catch { }
            return false;
        }
    }
}
