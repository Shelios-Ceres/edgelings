using System.Globalization;
using System.Text.RegularExpressions;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Token counts from Kimi Code's session logs. Every model call writes a
/// `usage.record` line to its agent's wire.jsonl:
///
///   %USERPROFILE%\.kimi-code\sessions\&lt;workdir&gt;\session_&lt;uuid&gt;\agents\&lt;agent&gt;\wire.jsonl
///
/// with usage {inputOther, output, inputCacheRead, inputCacheCreation} and the
/// model id. Subagents count too — they spend the same plan. Kimi Code is a
/// subscription, so the dollars are API list-price estimates (Pricing); a
/// model with no price shows as tokens only.
public static class KimiLogReader
{
    // v3 carries usage identity so copied session histories count only once.
    private static readonly LogParseCache Cache = new(TriggerTool.Kimi, "kimi-parse-cache.v3.json");

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var root = IslandPaths.KimiSessionsRoot;
        if (!Directory.Exists(root)) return new List<TokenEvent>();
        var files = SessionScanner.SafeEnumerateFiles(root, "wire.jsonl");
        return Deduplicate(Cache.Walk(files, DateTimeOffset.Now.AddDays(-lookbackDays), ParseFile));
    }

    /// What Kimi Code calls its plan's models, by id, from the
    /// [models."<id>"] tables of its settings: "kimi-code/kimi-for-coding"
    /// is "K2.8 Preview". Read on every scan, so a name follows Kimi when it
    /// points an alias at a new model.
    public static IReadOnlyDictionary<string, string> ModelNames()
    {
        try
        {
            return ModelNames(File.ReadAllText(Path.Combine(IslandPaths.Home, ".kimi-code", "config.toml")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static readonly Regex ModelTable = new(@"^\[\s*models\s*\.\s*""(?<id>[^""\\]+)""\s*\]\s*(#.*)?$");
    private static readonly Regex DisplayName = new(@"^display_name\s*=\s*""(?<name>(?:[^""\\]|\\.)*)""\s*(#.*)?$");

    internal static Dictionary<string, string> ModelNames(string config)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? model = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var table = ModelTable.Match(line);
                model = table.Success ? table.Groups["id"].Value : null;
                continue;
            }
            if (model is null || DisplayName.Match(line) is not { Success: true } match) continue;
            string name;
            try { name = Regex.Unescape(match.Groups["name"].Value).Trim(); }
            catch (ArgumentException) { continue; }
            if (name.Length > 0) names[model] = name;
        }
        return names;
    }

    internal static List<TokenEvent> ParseFile(string path)
    {
        var output = new List<TokenEvent>();
        // Tool results and appended messages dominate the log; only usage
        // records matter, and the substring guard skips the rest before any
        // JSON parse.
        foreach (var line in LogLines.Read(path, maxLength: 100_000, mustContain: "\"usage.record\""))
        {
            if (Parse(line) is { } tokenEvent) output.Add(tokenEvent);
        }
        return Deduplicate(output);
    }

    internal static TokenEvent? Parse(string line)
    {
        using var doc = Jsonl.TryParseLine(line);
        if (doc is null) return null;
        var root = doc.RootElement;
        if (Jsonl.GetString(root, "type") != "usage.record") return null;
        if (Jsonl.GetObject(root, "usage") is not { } usage) return null;
        if (Jsonl.GetDouble(root, "time") is not { } ms || ms <= 0 || ms > 253_402_300_799_999) return null;
        var input = Jsonl.GetLong(usage, "inputOther") ?? 0;
        var output = Jsonl.GetLong(usage, "output") ?? 0;
        var cacheRead = Jsonl.GetLong(usage, "inputCacheRead") ?? 0;
        var cacheCreation = Jsonl.GetLong(usage, "inputCacheCreation") ?? 0;
        if (input == 0 && output == 0 && cacheRead == 0 && cacheCreation == 0) return null;
        var model = Jsonl.GetString(root, "model") ?? "kimi";
        var agent = Jsonl.GetString(root, "agentId");
        var scope = Jsonl.GetString(root, "usageScope");
        var key = agent is { Length: > 0 } && scope is { Length: > 0 }
            ? $"{agent.Length}:{agent}{scope.Length}:{scope}|{ms.ToString("R", CultureInfo.InvariantCulture)}|{model}|{input}|{output}|{cacheCreation}|{cacheRead}"
            : null;
        return new TokenEvent(
            TriggerTool.Kimi,
            DateTimeOffset.FromUnixTimeMilliseconds((long)ms),
            model,
            input, output, cacheCreation, cacheRead)
        {
            ResponseKey = key,
        };
    }

    internal static List<TokenEvent> Deduplicate(List<TokenEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return events.Where(e => e.ResponseKey is null || seen.Add(e.ResponseKey)).ToList();
    }
}
