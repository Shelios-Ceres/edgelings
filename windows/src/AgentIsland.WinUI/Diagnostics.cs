using AgentIsland.Core;

namespace AgentIsland.WinUI;

internal static class CrashLog
{
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
            Directory.CreateDirectory(IslandPaths.AppSupportDir);
            File.AppendAllText(Path.Combine(IslandPaths.AppSupportDir, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] WinUI {source}: {error}\n\n");
        }
        catch
        {
        }
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
