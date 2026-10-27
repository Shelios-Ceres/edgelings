using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Token counts from DeepSeek Harness's session logs (zstd-framed JSONL):
///
///   %USERPROFILE%\.dsh\sessions\&lt;workspace&gt;\&lt;session&gt;\session.v4.jsonl.zstd
///
/// Each completed model step writes an assistant/message line whose
/// data.usage splits input, output, cache reads and cache writes (their sum
/// is totalTokens, so nothing double counts). The model comes from the most
/// recent request/header. Signed-in accounts carry no per-token price here,
/// so the dollars are list-price estimates from Pricing.
public static class DeepSeekLogReader
{
    private static readonly LogParseCache Cache = new(TriggerTool.DeepSeek, "deepseek-parse-cache.v2.json");

    private const string FallbackModel = "deepseek";

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var root = IslandPaths.DeepSeekSessionsRoot;
        if (!Directory.Exists(root)) return new List<TokenEvent>();
        var files = SessionScanner.SafeEnumerateFiles(root, "session.v4.jsonl.zstd");
        return Cache.Walk(files, DateTimeOffset.Now.AddDays(-lookbackDays), ParseFile);
    }

    /// The log is decoded as it is read, never whole: an active session's
    /// log can be tens of MB compressed and many times that as text.
    internal static List<TokenEvent> ParseFile(string path) => ParseLines(LogLines.Read(path, maxLength: 2_000_000));

    internal static List<TokenEvent> ParseLines(IEnumerable<string> lines)
    {
        var output = new List<TokenEvent>();
        var model = FallbackModel;
        foreach (var line in lines)
        {
            if (line.Length > 2_000_000) continue;
            var header = line.Contains("\"request/header\"", StringComparison.Ordinal);
            if (!header && !line.Contains("\"assistant/message\"", StringComparison.Ordinal)) continue;
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (Jsonl.GetObject(root, "data") is not { } data) continue;
            switch (Jsonl.GetString(root, "type"))
            {
                case "request/header":
                    if (Jsonl.GetObject(data, "header") is { } h
                        && Jsonl.GetObject(h, "config") is { } config
                        && Jsonl.GetString(config, "model") is { Length: > 0 } named)
                    {
                        model = named;
                    }
                    continue;
                case "assistant/message":
                    if (Jsonl.GetObject(data, "usage") is not { } usage) continue;
                    if (Jsonl.GetDouble(root, "time") is not { } ms || ms <= 0 || ms > 253_402_300_799_999) continue;
                    var input = Jsonl.GetLong(usage, "inputTokens") ?? 0;
                    var outputTokens = Jsonl.GetLong(usage, "outputTokens") ?? 0;
                    var cacheRead = Jsonl.GetLong(usage, "cacheReadTokens") ?? 0;
                    var cacheWrite = Jsonl.GetLong(usage, "cacheWriteTokens") ?? 0;
                    if (input == 0 && outputTokens == 0 && cacheRead == 0 && cacheWrite == 0) continue;
                    output.Add(new TokenEvent(
                        TriggerTool.DeepSeek,
                        DateTimeOffset.FromUnixTimeMilliseconds((long)ms),
                        model,
                        input, outputTokens, cacheWrite, cacheRead));
                    continue;
            }
        }
        return output;
    }
}
