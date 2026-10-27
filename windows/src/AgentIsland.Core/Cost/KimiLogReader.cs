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
        var events = Deduplicate(Cache.Walk(files, DateTimeOffset.Now.AddDays(-lookbackDays), ParseFile));
        return PriceByProvider(events, ProviderModels());
    }

    /// What Kimi Code calls its plan's models, by id, from the
    /// [models."<id>"] tables of its settings: "kimi-code/kimi-for-coding"
    /// is "K2.8 Preview". Read on every scan, so a name follows Kimi when it
    /// points an alias at a new model.
    public static IReadOnlyDictionary<string, string> ModelNames()
    {
        try
        {
            return ModelNames(ReadConfig());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string ReadConfig() =>
        File.ReadAllText(Path.Combine(IslandPaths.Home, ".kimi-code", "config.toml"));

    /// Kimi's own provider; any other [models] table points at a provider the
    /// user connected, whose price list is the model's own.
    private const string OwnProvider = "managed:kimi-code";

    private static readonly Regex ModelTable = new(@"^\[\s*models\s*\.\s*""(?<id>[^""\\]+)""\s*\]\s*(#.*)?$");
    private static readonly Regex ModelKey = new(@"^(?<key>display_name|provider|model)\s*=\s*""(?<value>(?:[^""\\]|\\.)*)""\s*(#.*)?$");

    private sealed record ModelEntry(string? Name, string? Provider, string? Model);

    /// One pass over the settings. Only the display_name, provider and model
    /// keys of the [models."<id>"] tables are read: a [providers] table holds
    /// API keys, and nothing else of the file is looked at or kept.
    private static Dictionary<string, ModelEntry> ReadModelTables(string config)
    {
        var tables = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
        string? id = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var table = ModelTable.Match(line);
                id = table.Success ? table.Groups["id"].Value : null;
                continue;
            }
            if (id is null || ModelKey.Match(line) is not { Success: true } match) continue;
            string value;
            try { value = Regex.Unescape(match.Groups["value"].Value).Trim(); }
            catch (ArgumentException) { continue; }
            if (value.Length == 0) continue;
            tables.TryGetValue(id, out var entry);
            entry ??= new ModelEntry(null, null, null);
            tables[id] = match.Groups["key"].Value switch
            {
                "display_name" => entry with { Name = value },
                "provider" => entry with { Provider = value },
                _ => entry with { Model = value },
            };
        }
        return tables;
    }

    internal static Dictionary<string, string> ModelNames(string config)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in ReadModelTables(config))
        {
            if (entry.Name is { } name) names[id] = name;
        }
        return names;
    }

    /// The provider's own model id of each table that points at another
    /// provider ("deepseek/deepseek-flash" is "deepseek-flash"), which is what
    /// Pricing knows it by.
    internal static Dictionary<string, string> ProviderModels(string config)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in ReadModelTables(config))
        {
            if (entry.Provider is { } provider && !provider.Equals(OwnProvider, StringComparison.OrdinalIgnoreCase)
                && entry.Model is { } model)
            {
                models[id] = model;
            }
        }
        return models;
    }

    private static IReadOnlyDictionary<string, string> ProviderModels()
    {
        try
        {
            return ProviderModels(ReadConfig());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// Kimi's K2 models are being retired, so the Usage & cost page leaves them
    /// out of its model rows (their tokens still count in every total). A model
    /// is K2 by the name Kimi gives it ("K2.8 Preview"); one with no name is K2
    /// by its id: the two plan aliases that were K2, or any `kimi-k2…` id.
    /// Kimi pointing an alias at a K3 model, under a K3 name, lists it again.
    public static bool IsK2(string model, string? displayName)
    {
        if (displayName is { Length: > 0 })
        {
            return displayName.StartsWith("K2", StringComparison.OrdinalIgnoreCase)
                && (displayName.Length == 2 || !char.IsAsciiDigit(displayName[2]));
        }
        var name = model.StartsWith("kimi-code/", StringComparison.OrdinalIgnoreCase) ? model["kimi-code/".Length..] : model;
        return model.Equals("kimi-code/kimi-for-coding", StringComparison.OrdinalIgnoreCase)
            || model.Equals("kimi-code/kimi-for-coding-highspeed", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("kimi-k2", StringComparison.OrdinalIgnoreCase);
    }

    /// An event of another provider's model that has no price under the
    /// table's id is priced under the provider's own id; the event keeps the
    /// table id, so names and the parse cache are unaffected.
    internal static List<TokenEvent> PriceByProvider(List<TokenEvent> events, IReadOnlyDictionary<string, string> providerModels)
    {
        if (providerModels.Count == 0) return events;
        return events.Select(e => providerModels.TryGetValue(e.Model, out var model) && !Pricing.IsKnown(e.Model)
            ? e with { PricedAs = model }
            : e).ToList();
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
