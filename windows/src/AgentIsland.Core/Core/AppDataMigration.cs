namespace AgentIsland.Core;

/// The app was Agent Island (%APPDATA%\AgentIsland, %LOCALAPPDATA%\AgentIsland\cache)
/// and then Edgelings (%APPDATA%\Edgelings, %LOCALAPPDATA%\Edgelings\cache). As
/// Limen it uses %APPDATA%\Limen and %LOCALAPPDATA%\Limen\cache (on macOS,
/// ~/Library/Application Support/Limen and ~/Library/Caches/Limen). On first
/// start the newest prior folder's files are copied to the new folders, once.
/// Old folders are never changed, moved or deleted: the Claude and Grok hooks
/// keep their signal folders in AgentIsland on Windows, and an older build that
/// still runs keeps using its own folder.
public static class AppDataMigration
{
    /// Files that belong to the hooks and stay where they are.
    private static bool IsAppFile(string name) =>
        !name.Equals("grok-hook.ps1", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("grok-hook.sh", StringComparison.OrdinalIgnoreCase)
        && !name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase);

    /// Copies settings and the crash log, then the caches. Safe to call on every start.
    public static void Run()
    {
        MigrateFirst(
            OperatingSystem.IsMacOS()
                ? new[] { IslandPaths.PreviousAppSupportDir }
                : new[] { IslandPaths.PreviousAppSupportDir, IslandPaths.LegacyAppSupportDir },
            IslandPaths.AppSupportDir,
            IsAppFile);
        MigrateFirst(
            OperatingSystem.IsMacOS()
                ? new[] { IslandPaths.PreviousCacheDir }
                : new[] { IslandPaths.PreviousCacheDir, IslandPaths.LegacyCacheDir },
            IslandPaths.CacheDir,
            _ => true);
    }

    internal static bool MigrateFirst(IEnumerable<string> oldDirs, string newDir, Func<string, bool> include)
    {
        foreach (var oldDir in oldDirs)
        {
            if (Directory.Exists(oldDir)) return Migrate(oldDir, newDir, include);
        }
        return false;
    }

    /// Publishes a fresh directory as a unit, or backfills absent files in an
    /// existing migration. A file already at the destination always wins.
    internal static bool Migrate(string oldDir, string newDir, Func<string, bool> include)
    {
        string? temp = null;
        try
        {
            if (!Directory.Exists(oldDir)) return false;
            if (Directory.Exists(newDir))
            {
                var copied = false;
                foreach (var file in Directory.EnumerateFiles(oldDir))
                {
                    var name = Path.GetFileName(file);
                    var destination = Path.Combine(newDir, name);
                    if (!include(name) || File.Exists(destination)) continue;
                    var staging = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(file, staging);
                        File.Move(staging, destination);
                        copied = true;
                    }
                    catch (IOException) when (File.Exists(destination)) { }
                    finally { File.Delete(staging); }
                }
                return copied;
            }
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
