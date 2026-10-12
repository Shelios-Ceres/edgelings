using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Model;

/// What a character says when its agent enters a phase that needs you:
/// a short line in a bubble beside it. Working phases stay quiet — only the
/// moments worth looking up for get a line.
public static class BotLines
{
    private static readonly Dictionary<AgentPhase, string[]> Lines = new()
    {
        [AgentPhase.Finished] = new[] { "Done — your turn!", "All set. Take a look?", "Finished. Waiting on you." },
        [AgentPhase.Approving] = new[] { "Can I run this? Needs your OK.", "Waiting for your approval." },
        [AgentPhase.Asking] = new[] { "Quick question for you.", "I need your input." },
        [AgentPhase.Failed] = new[] { "Something broke…", "Hit an error." },
        [AgentPhase.Stalled] = new[] { "I think I'm stuck.", "No progress for a while." },
        [AgentPhase.RateLimited] = new[] { "Out of quota for now.", "Taking a break — quota's used up." },
        [AgentPhase.AuthRequired] = new[] { "Please sign me in again." },
    };

    public static bool Speaks(AgentPhase phase) => Lines.ContainsKey(phase);

    /// A line for the phase, in the app's language, or null if it is quiet.
    public static string? For(AgentPhase phase, Random random) =>
        Lines.TryGetValue(phase, out var lines) ? L10n.Tr(lines[random.Next(lines.Length)]) : null;
}
