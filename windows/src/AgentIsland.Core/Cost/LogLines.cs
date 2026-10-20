using AgentIsland.Core;

namespace AgentIsland.Cost;

internal static class LogLines
{
    /// The lines of a log that another process may still be appending to,
    /// without the ones a parser would only throw away: longer than
    /// maxLength, or lacking the marker every wanted line carries. The file
    /// opens when the first line is asked for and closes with the loop.
    public static IEnumerable<string> Read(string path, int maxLength, string? mustContain = null)
    {
        using var stream = SharedFile.Open(path);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > maxLength) continue;
            if (mustContain is not null && !line.Contains(mustContain, StringComparison.Ordinal)) continue;
            yield return line;
        }
    }
}
