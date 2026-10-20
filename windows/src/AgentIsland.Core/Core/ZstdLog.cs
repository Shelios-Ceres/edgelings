using System.Text;
using ZstdSharp;

namespace AgentIsland.Core;

/// DeepSeek Harness's session log: JSONL appended as a run of independent
/// zstd frames, one or two events per frame. A window cut from the end of
/// the file is decoded from its first frame boundary, and a frame the writer
/// is still appending simply ends the read.
public static class ZstdLog
{
    private static readonly byte[] Magic = { 0x28, 0xB5, 0x2F, 0xFD };

    /// The last complete lines of the log.
    public static List<string> TailLines(string path, long bytes = 262_144, int keep = 200)
    {
        byte[] data;
        try
        {
            using var stream = Open(path);
            var start = Math.Max(0, stream.Length - bytes);
            stream.Seek(start, SeekOrigin.Begin);
            data = new byte[stream.Length - start];
            stream.ReadExactly(data);
        }
        catch
        {
            return new List<string>();
        }
        var lines = Decode(data).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Skip(Math.Max(0, lines.Length - keep)).Select(line => line.TrimEnd('\r')).ToList();
    }

    /// Every line of the log, for the cost reader.
    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = Open(path);
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return Decode(data).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// Decodes from the first frame boundary that yields output. Compressed
    /// bytes can contain the magic by chance; a false start fails at once
    /// and the next candidate is tried.
    internal static string Decode(byte[] data)
    {
        for (var at = data.AsSpan().IndexOf(Magic); at >= 0;)
        {
            if (DecodeFrom(data, at) is { Length: > 0 } text) return text;
            var next = data.AsSpan(at + 1).IndexOf(Magic);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return string.Empty;
    }

    private static string DecodeFrom(byte[] data, int at)
    {
        using var output = new MemoryStream();
        try
        {
            using var input = new MemoryStream(data, at, data.Length - at, writable: false);
            using var zstd = new DecompressionStream(input);
            var buffer = new byte[65_536];
            int read;
            while ((read = zstd.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        }
        catch
        {
            // The frame being written (or a false start) ends the read; what
            // decoded before it stands.
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
