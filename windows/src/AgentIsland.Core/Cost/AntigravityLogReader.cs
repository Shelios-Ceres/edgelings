using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Cost;

/// Token counts from Antigravity's conversation transcripts. Every model step
/// of a conversation is a PLANNER_RESPONSE line carrying input_tokens (the
/// uncached part of the prompt), cache_read_tokens and output_tokens
/// (thinking included), in
///
///   %USERPROFILE%\.gemini\&lt;antigravity | antigravity-ide | antigravity-cli&gt;\brain\&lt;conversation&gt;\.system_generated\logs\transcript_full.jsonl
///
/// The desktop app and the CLI write the same shape into their own roots.
/// The transcript names no model: AntigravityModelMap asks the language
/// servers, and a step nobody can name counts as tokens with no price.
public static class AntigravityLogReader
{
    /// The model of a step that no server named.
    internal const string UnknownModel = "antigravity-unknown";

    private static readonly string[] RootNames = { "antigravity", "antigravity-ide", "antigravity-cli" };
    private static readonly LogParseCache Cache = new(TriggerTool.Antigravity, "antigravity-parse-cache.v1.json");
    private static readonly AntigravityModelMap Models = new(Path.Combine(IslandPaths.CacheDir, "antigravity-models.v1.json"));

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var files = TranscriptFiles();
        if (files.Count == 0) return new List<TokenEvent>();
        var steps = Deduplicate(Cache.Walk(files, DateTimeOffset.Now.AddDays(-lookbackDays), ParseFile));
        return Models.Resolve(steps, AskServers);
    }

    /// What Antigravity calls the models its steps name, by price id. A step
    /// no server named is put into words where it is shown (NameOf), in the
    /// page's language, not here on the scan thread.
    public static IReadOnlyDictionary<string, string> ModelNames() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-4-argon"] = "Gemini 4 Argon",
        ["gemini-3.8-flash"] = "Gemini 3.8 Flash",
        ["claude-opus-5-5"] = "Claude Opus 5.5",
        ["claude-sonnet-5-5"] = "Claude Sonnet 5.5",
    };

    private static List<string> TranscriptFiles()
    {
        var files = new List<string>();
        foreach (var rootName in RootNames)
        {
            var brain = Path.Combine(IslandPaths.Home, ".gemini", rootName, "brain");
            if (!Directory.Exists(brain)) continue;
            foreach (var conversation in SessionScanner.SafeEnumerateDirectories(brain))
            {
                var path = Path.Combine(conversation, ".system_generated", "logs", "transcript_full.jsonl");
                if (File.Exists(path)) files.Add(path);
            }
        }
        return files;
    }

    /// Asks every running server, the desktop app's with its token and the
    /// CLI's with none, until one names the conversation's models.
    private static AntigravityModelMap.Generations AskServers(string conversation)
    {
        var servers = AntigravityLiveCascades.Servers(DateTimeOffset.Now);
        if (servers.Count == 0) return new AntigravityModelMap.Generations(false, null);
        var body = JsonSerializer.Serialize(new { cascadeId = conversation });
        foreach (var (port, token) in servers)
        {
            var reply = AntigravityLanguageServer.Call("GetCascadeTrajectoryGeneratorMetadata", port, body, token)
                .GetAwaiter().GetResult();
            if (reply is { Status: 200 }) return new AntigravityModelMap.Generations(true, reply.Body);
        }
        return new AntigravityModelMap.Generations(true, null);
    }

    internal static List<TokenEvent> ParseFile(string path)
    {
        var output = new List<TokenEvent>();
        // The conversation's id is its folder's name, three levels up.
        var conversation = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))) ?? "";
        // Thinking and tool output make some lines long; only model steps matter.
        foreach (var line in LogLines.Read(path, maxLength: 4_000_000, mustContain: "\"PLANNER_RESPONSE\""))
        {
            if (Parse(line, conversation) is { } step) output.Add(step);
        }
        return Deduplicate(output);
    }

    internal static TokenEvent? Parse(string line, string conversation)
    {
        using var doc = Jsonl.TryParseLine(line);
        if (doc is null) return null;
        var root = doc.RootElement;
        if (Jsonl.GetString(root, "source") != "MODEL" || Jsonl.GetString(root, "type") != "PLANNER_RESPONSE") return null;
        if (Jsonl.GetLong(root, "step_index") is not { } stepIndex) return null;
        if (Jsonl.ParseIso8601(Jsonl.GetString(root, "created_at")) is not { } at) return null;
        // A zero count is left out of the line.
        var input = Jsonl.GetLong(root, "input_tokens") ?? 0;
        var cacheRead = Jsonl.GetLong(root, "cache_read_tokens") ?? 0;
        var output = Jsonl.GetLong(root, "output_tokens") ?? 0;
        if (input == 0 && cacheRead == 0 && output == 0) return null;
        return new TokenEvent(TriggerTool.Antigravity, at, UnknownModel, input, output, 0, cacheRead)
        {
            ResponseKey = $"{conversation}|{stepIndex}",
        };
    }

    internal static List<TokenEvent> Deduplicate(List<TokenEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return events.Where(e => e.ResponseKey is null || seen.Add(e.ResponseKey)).ToList();
    }
}
