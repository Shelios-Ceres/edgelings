using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Which model made each step of an Antigravity conversation. A transcript
/// counts a step's tokens and names no model; the language server does, one
/// generation at a time, with the same three counts. A step is matched on
/// (input, cache read, output) within its conversation, and the answer is
/// kept in a file so a conversation the server no longer holds still has
/// its models after a restart.
internal sealed class AntigravityModelMap
{
    /// What asking the servers about one conversation came to: Asked is false
    /// when no server was running to ask, and Body is null when every server
    /// that was asked refused.
    internal readonly record struct Generations(bool Asked, byte[]? Body);

    private readonly string _path;
    private readonly object _gate = new();
    // conversation -> "input|cacheRead|output" -> model id ("" when two
    // generations with those counts name different models).
    private Dictionary<string, Dictionary<string, string>>? _models;
    // How many steps a conversation had when it was last asked about: it is
    // asked again when it has grown, or after a restart, never on every scan.
    private readonly Dictionary<string, int> _asked = new(StringComparer.Ordinal);

    public AntigravityModelMap(string path) => _path = path;

    /// Sets each event's model from the map, asking the servers about the
    /// conversations that still have steps with none. A step nobody names
    /// keeps the placeholder model, which has no price.
    public List<TokenEvent> Resolve(List<TokenEvent> events, Func<string, Generations> ask)
    {
        lock (_gate)
        {
            Load();
            var dirty = false;
            foreach (var conversation in events.GroupBy(Conversation))
            {
                var steps = conversation.ToList();
                if (!_models!.TryGetValue(conversation.Key, out var known))
                {
                    known = new Dictionary<string, string>(StringComparer.Ordinal);
                    _models[conversation.Key] = known;
                    dirty = true;
                }
                if (steps.All(step => known.ContainsKey(Counts(step)))) continue;
                if (_asked.TryGetValue(conversation.Key, out var count) && count == steps.Count) continue;
                var reply = ask(conversation.Key);
                if (!reply.Asked) continue;
                _asked[conversation.Key] = steps.Count;
                if (reply.Body is { } body && Merge(known, body, steps.Select(Counts).ToHashSet(StringComparer.Ordinal))) dirty = true;
            }
            var seen = events.Select(Conversation).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _models!.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _models.Remove(gone);
                dirty = true;
            }
            if (dirty) Save();
            return events.Select(step => step with { Model = ModelOf(step) }).ToList();
        }
    }

    /// The conversation a step belongs to: the front of its response key.
    internal static string Conversation(TokenEvent step) => step.ResponseKey?.Split('|')[0] ?? "";

    private static string Counts(TokenEvent step) => $"{step.InputTokens}|{step.CacheReadTokens}|{step.OutputTokens}";

    private string ModelOf(TokenEvent step) =>
        _models!.TryGetValue(Conversation(step), out var known) && known.TryGetValue(Counts(step), out var model) && model.Length > 0
            ? Pricing.Family(model)
            : AntigravityLogReader.UnknownModel;

    /// GetCascadeTrajectoryGeneratorMetadata: { generatorMetadata: [{ chatModel:
    /// { responseModel, usage: { inputTokens, cacheReadTokens, outputTokens } } }] },
    /// the counts as strings and a zero count left out. Only the counts a
    /// transcript step has are kept.
    internal static bool Merge(Dictionary<string, string> known, byte[] body, ISet<string> wanted)
    {
        var changed = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("generatorMetadata", out var list) || list.ValueKind != JsonValueKind.Array) return false;
            foreach (var entry in list.EnumerateArray())
            {
                if (Jsonl.GetObject(entry, "chatModel") is not { } chat) continue;
                if (Jsonl.GetString(chat, "responseModel") is not { Length: > 0 } model) continue;
                if (Jsonl.GetObject(chat, "usage") is not { } usage) continue;
                var counts = $"{Count(usage, "inputTokens")}|{Count(usage, "cacheReadTokens")}|{Count(usage, "outputTokens")}";
                if (!wanted.Contains(counts)) continue;
                if (!known.TryGetValue(counts, out var before)) { known[counts] = model; changed = true; }
                else if (before != model && before.Length > 0) { known[counts] = ""; changed = true; }
            }
        }
        catch (JsonException)
        {
        }
        return changed;
    }

    private static long Count(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number : 0,
            JsonValueKind.String => long.TryParse(value.GetString(), out var parsed) ? parsed : 0,
            _ => 0,
        };
    }

    private void Load()
    {
        if (_models is not null) return;
        _models = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize(File.ReadAllText(_path), CoreJson.Default.ModelMaps) is { } saved)
            {
                foreach (var (id, models) in saved) _models[id] = new Dictionary<string, string>(models, StringComparer.Ordinal);
            }
        }
        catch
        {
            _models.Clear();
        }
    }

    private void Save()
    {
        // Per-write temp name, as the parse cache does.
        var tmp = string.Empty;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(_models, CoreJson.Default.ModelMaps));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { if (tmp.Length > 0) File.Delete(tmp); } catch { }
        }
    }
}
