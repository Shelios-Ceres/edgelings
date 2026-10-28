using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentIsland.Core;

/// Kimi Code's memory plugin runs headless `kimi -p` jobs with the plugin's
/// own folder as their working directory. Kimi stores each as an ordinary
/// session, so the scan would take it for the user's work. A run is told by
/// its state.json cwd, never by its workdir folder's name: a user project
/// called "memories" gets a `wd_memories_…` folder too.
internal static class KimiMemoryRuns
{
    public static string Folder => Path.Combine(IslandPaths.KimiHome, "memories");

    // A session's cwd never changes, but the file is rewritten as the session
    // goes on, so a read is kept only for the write time it was taken at.
    private static readonly ConcurrentDictionary<string, (long Ticks, string Cwd)> Cwds = new(StringComparer.OrdinalIgnoreCase);

    /// The cwd a session's state.json records, "" while it is missing or
    /// caught mid-write (read again next time).
    public static string SessionCwd(string statePath)
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(statePath).Ticks;
            if (Cwds.TryGetValue(statePath, out var known) && known.Ticks == ticks) return known.Cwd;
            using var stream = SharedFile.Open(statePath);
            using var doc = JsonDocument.Parse(stream);
            var cwd = Jsonl.GetString(doc.RootElement, "cwd") ?? "";
            Cwds[statePath] = (ticks, cwd);
            return cwd;
        }
        catch
        {
            return "";
        }
    }

    /// The whole path, normalised, case-insensitive, trailing separator ignored.
    public static bool IsMemoryFolder(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        try
        {
            return string.Equals(Normalize(cwd), Normalize(Folder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// A wire.jsonl is …\session_<id>\agents\<agent>\wire.jsonl, state.json
    /// beside the agents folder.
    public static bool IsMemoryRunLog(string wirePath)
    {
        var agents = Path.GetDirectoryName(Path.GetDirectoryName(wirePath));
        var session = agents is null ? null : Path.GetDirectoryName(agents);
        return session is not null && IsMemoryFolder(SessionCwd(Path.Combine(session, "state.json")));
    }
}
