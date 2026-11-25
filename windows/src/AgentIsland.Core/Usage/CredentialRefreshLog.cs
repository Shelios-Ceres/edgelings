using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Why a token refresh is held back or was kept in memory, one line each time a
/// provider's reason changes. Provider names and fixed reason phrases only: no
/// token, fingerprint, account or file path is ever written.
internal static class CredentialRefreshLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Last = new();
    private const long MaxBytes = 64 * 1024;

    internal static string LogPath => Path.Combine(IslandPaths.CacheDir, "credential-refresh.log");

    /// Test seam: where lines go. False when the line could not be written.
    internal static Func<string, bool> Write = Append;

    /// A reason is remembered only once written, so a line lost to a busy file
    /// is tried again. Never throws: a diagnostic must not break a refresh.
    internal static void Note(string provider, string reason)
    {
        try
        {
            lock (Gate)
            {
                if (Last.TryGetValue(provider, out var last) && last == reason) return;
                if (Write($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {provider}: {reason}")) Last[provider] = reason;
            }
        }
        catch
        {
        }
    }

    /// A refresh worked again: say so once, so that the same trouble coming back
    /// later is not swallowed as "no change in reason".
    internal static void Recovered(string provider)
    {
        try
        {
            lock (Gate)
            {
                if (!Last.TryGetValue(provider, out var last) || last == "recovered") return;
                if (Write($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {provider}: recovered")) Last[provider] = "recovered";
            }
        }
        catch
        {
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate) Last.Clear();
    }

    private static bool Append(string line)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
