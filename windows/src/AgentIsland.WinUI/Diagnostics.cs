using AgentIsland.Core;

namespace AgentIsland.WinUI;

/// The app's crash log in %APPDATA%. A failure that repeats (a poll that
/// throws every half second, a binding that throws on every render) is
/// written once per launch, and a log past MostBytes moves to crash.old.log,
/// so a tray app that runs for weeks keeps it small and its first entries
/// in view.
internal static class CrashLog
{
    internal const long MostBytes = 1024 * 1024;
    private const int MostKept = 500;

    private static readonly object Gate = new();
    private static readonly HashSet<(string Source, string Type, string Message, string Where)> Seen = new();

    public static void Write(Exception? error, string source)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_HOVER_PREVIEW") == "1"
                && Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_TRACE") is { Length: > 0 } trace)
            {
                File.AppendAllText(trace, $"[{DateTimeOffset.Now:O}] {source}: {error}\n\n");
                return;
            }
            lock (Gate)
            {
                if (!FirstTime(error, source)) return;
                Directory.CreateDirectory(IslandPaths.AppSupportDir);
                Append(Path.Combine(IslandPaths.AppSupportDir, "crash.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] WinUI {source}: {error}\n\n");
            }
        }
        catch
        {
        }
    }

    /// False for a failure already written this launch: the same source,
    /// type, message and stack.
    internal static bool FirstTime(Exception? error, string source)
    {
        lock (Gate)
        {
            if (Seen.Count >= MostKept) Seen.Clear();
            return Seen.Add((source, error?.GetType().FullName ?? "", error?.Message ?? "", error?.StackTrace ?? ""));
        }
    }

    /// Appends an entry, first moving a log of MostBytes or more to
    /// crash.old.log (replacing the one before).
    internal static void Append(string path, string entry)
    {
        var log = new FileInfo(path);
        if (log.Exists && log.Length >= MostBytes) File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
        File.AppendAllText(path, entry);
    }

    /// Test seam: forget what was already written.
    internal static void Reset()
    {
        lock (Gate) Seen.Clear();
    }
}

/// AGENTISLAND_DEBUG_TRACE=<path>: verification breadcrumbs.
internal static class DebugTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_TRACE");

    public static void Write(string text)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        try
        {
            File.AppendAllText(Path, $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }
}
