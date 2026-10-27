using AgentIsland.Core;
using ZstdSharp;

namespace AgentIsland.Cost;

internal static class LogLines
{
    /// The lines of a log that another process may still be appending to,
    /// without the ones a parser would only throw away: longer than
    /// maxLength, or lacking the marker every wanted line carries. The file
    /// opens when the first line is asked for and closes with the loop.
    /// A `.zst` log is decoded as it is read, so a rollout that is hundreds
    /// of MB of JSON never sits in memory; a torn or corrupt frame ends the
    /// read and what decoded before it stands.
    public static IEnumerable<string> Read(string path, int maxLength, string? mustContain = null)
    {
        var compressed = path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
        using var stream = SharedFile.Open(path);
        using var decoded = compressed ? new DecompressionStream(stream, leaveOpen: true) : null;
        using var reader = new StreamReader(decoded ?? (Stream)stream);
        while (NextLine(reader, compressed) is { } line)
        {
            if (line.Length > maxLength) continue;
            if (mustContain is not null && !line.Contains(mustContain, StringComparison.Ordinal)) continue;
            yield return line;
        }
    }

    private static string? NextLine(StreamReader reader, bool compressed)
    {
        try
        {
            return reader.ReadLine();
        }
        catch (Exception error) when (compressed && error is ZstdException or EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    }
}
