using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Model;

namespace AgentIsland.Bridge;

/// The work-state groups for the Mac drawer: Core's GroupRotation, one per
/// bot, answering Swift's per-tick question "which engine state now?". The
/// character engines live in JavaScriptCore on the Swift side, so Swift reads
/// each state's cycle length from its engine (cycleOf over the bot's tempo)
/// and hands the numbers over once per look; the scheduler, its tables and its
/// clock rules stay the Windows app's too.
///
/// Calls come from the drawer's loop thread while a poll or a scan may run on
/// Core's queue, so this class keeps to its own lock and touches nothing else.
public static class RotationApi
{
    private static readonly object Gate = new();
    private sealed record Entry(GroupRotation Rotation, string Kind, string Look);

    private static readonly Dictionary<string, Entry> Rotations = new();

    /// {"method":"rotationStates","theme":"pixel","id":"claude","kind":"claude","look":"clawd"}: every state the
    /// look's groups can play, so Swift can read their cycle lengths, and the character's tempo.
    public static string States(JsonElement request)
    {
        var theme = Theme(request);
        var kind = request.GetProperty("kind").GetString() ?? "";
        var look = request.TryGetProperty("look", out var l) ? l.GetString() ?? "" : "";
        var states = new SortedSet<string>(StringComparer.Ordinal);
        if (StateGroups.For(theme, kind, look) is { } groups)
        {
            foreach (var group in new[] { WorkGroup.Think, WorkGroup.Do, WorkGroup.Team, WorkGroup.Busy })
            {
                if (groups.Of(group) is { } pair)
                {
                    states.Add(pair.Main);
                    states.Add(pair.Second);
                }
            }
            // A tool's own state can take the second slot.
            foreach (var phase in Enum.GetValues<AgentPhase>()) states.Add(DrawerLooks.Entry(phase, theme));
        }
        var tempo = request.TryGetProperty("id", out var idElement) && DisplayProviders.Parse(idElement.GetString()) is { } provider ? DrawerLooks.TempoOf(provider) : 1.0;
        return Json.Write(json =>
        {
            json.WriteNumber("tempo", tempo);
            json.WriteStartArray("states");
            foreach (var state in states) json.WriteStringValue(state);
            json.WriteEndArray();
        });
    }

    /// {"method":"rotationSetup","theme":"pixel","bots":[{"id":"claude","kind":"claude","look":"clawd","cycles":{"thinking":3.2}}]}:
    /// a rotation for each bot, replacing any it had (a new look, a new engine).
    public static string Setup(JsonElement request)
    {
        var theme = Theme(request);
        lock (Gate)
        {
            foreach (var bot in request.GetProperty("bots").EnumerateArray())
            {
                var id = bot.GetProperty("id").GetString() ?? "";
                var kind = bot.GetProperty("kind").GetString() ?? "";
                var look = bot.TryGetProperty("look", out var l) ? l.GetString() ?? "" : "";
                var cycles = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var cycle in bot.GetProperty("cycles").EnumerateObject()) cycles[cycle.Name] = cycle.Value.GetDouble();
                Rotations[id] = new Entry(new GroupRotation(theme, kind, look, state => cycles.TryGetValue(state, out var seconds) ? seconds : 3), kind, look);
            }
        }
        return Json.Write(json => json.WriteBoolean("ok", true));
    }

    /// {"method":"rotate","theme":"pixel","now":123.4,"reduced":false,"bots":[{"id":"claude","phase":"Running"}]}: for
    /// each bot the state to play and whether the phase plays in a group. A phase outside every group takes the
    /// rotation out of its group at once (the dwell is not waited out, as on Windows) and answers its own entry
    /// state; the drawer keeps its entry and hold logic there, so only grouped answers are to be applied.
    /// "today":true answers every phase's own entry state: the drawer before the groups, for the films.
    public static string Rotate(JsonElement request)
    {
        var theme = Theme(request);
        var today = request.TryGetProperty("today", out var td) && td.ValueKind == JsonValueKind.True;
        var now = request.GetProperty("now").GetDouble();
        var reduced = request.TryGetProperty("reduced", out var r) && r.ValueKind == JsonValueKind.True;
        return Json.Write(json =>
        {
            json.WriteStartArray("bots");
            lock (Gate)
            {
                foreach (var bot in request.GetProperty("bots").EnumerateArray())
                {
                    var id = bot.GetProperty("id").GetString() ?? "";
                    if (!Rotations.TryGetValue(id, out var entry) || !Enum.TryParse<AgentPhase>(bot.GetProperty("phase").GetString(), out var phase)) continue;
                    string state;
                    bool grouped;
                    if (!today && StateGroups.GroupOf(theme, entry.Kind, entry.Look, phase) is not null)
                    {
                        state = entry.Rotation.State(now, phase, reduced);
                        grouped = true;
                    }
                    else
                    {
                        if (!today) entry.Rotation.Leave();
                        state = DrawerLooks.Entry(phase, theme);
                        grouped = false;
                    }
                    json.WriteStartObject();
                    json.WriteString("id", id);
                    json.WriteString("state", state);
                    json.WriteBoolean("grouped", grouped);
                    json.WriteEndObject();
                }
            }
            json.WriteEndArray();
        });
    }

    private static CharacterTheme Theme(JsonElement request) =>
        request.TryGetProperty("theme", out var t) && t.GetString() == "classic" ? CharacterTheme.Classic : CharacterTheme.Pixel;
}
