namespace AgentIsland.Core;

/// What Kimi Code's wire.jsonl says about waiting on the user. When the agent
/// needs an approval (or an answer) it writes `interaction.request` with an
/// id and a kind, and when the user decides it writes `interaction.resolved`
/// with the same id. A request with no later resolution, inside the turn that
/// is still open, is a session waiting on the user. The Kimi Code CLI writes
/// these as well as the desktop app, so the wait shows where the desktop
/// app's local server (KimiLiveSessions) is not reachable.
///
/// Only the line's type, its kind, its id and its time are read. The request
/// also carries `display` (the command and the working folder): it is never
/// looked at, kept or logged.
internal static class KimiInteractions
{
    internal readonly record struct Wait(string Kind, string Id, DateTimeOffset? At);

    /// The newest unresolved request of the open turn, or null. A line that
    /// closes the turn (a turn end, a completed or aborted prompt) stops the
    /// search: a request left behind it was dropped with the turn.
    internal static Wait? Pending(IReadOnlyList<string> lines)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            // Most lines are tool output: skip them before any JSON parse.
            if (!line.Contains("interaction.", StringComparison.Ordinal)
                && !line.Contains("turn.ended", StringComparison.Ordinal)
                && !line.Contains("prompt.completed", StringComparison.Ordinal)
                && !line.Contains("prompt.aborted", StringComparison.Ordinal))
            {
                continue;
            }
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            switch (Jsonl.GetString(root, "type"))
            {
                case "interaction.resolved":
                    if (Jsonl.GetString(root, "id") is { Length: > 0 } done) resolved.Add(done);
                    continue;
                case "interaction.request":
                    if (Jsonl.GetString(root, "id") is not { Length: > 0 } id || resolved.Contains(id)) continue;
                    return new Wait(Jsonl.GetString(root, "kind") ?? "approval", id, At(root));
                case "agent.turn.ended" or "turn.ended" or "prompt.completed" or "prompt.aborted":
                    return null;
                default:
                    continue;
            }
        }
        return null;
    }

    /// The phase a wait puts the session in: an approval is Approving; any
    /// other kind of request (a question) is Asking.
    internal static AgentPhase PhaseOf(Wait wait) =>
        wait.Kind.Equals("approval", StringComparison.OrdinalIgnoreCase) ? AgentPhase.Approving : AgentPhase.Asking;

    private static DateTimeOffset? At(System.Text.Json.JsonElement root) =>
        Jsonl.GetDouble(root, "time") is { } ms and > 0 and <= 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : null;
}
