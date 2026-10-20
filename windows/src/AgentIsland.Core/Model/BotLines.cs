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
        [AgentPhase.NetworkTrouble] = new[] { "My connection dropped.", "Can't reach the server — retrying." },
        [AgentPhase.RateLimited] = new[] { "Out of quota for now.", "Taking a break — quota's used up." },
        [AgentPhase.AuthRequired] = new[] { "Please sign me in again." },
    };

    /// The phases a bot still speaks for as a whole agent. Finished, asking,
    /// approving and failed are said per session instead (see SessionEvent):
    /// the agent's busiest session would hide the others.
    public static bool Speaks(AgentPhase phase) =>
        Lines.ContainsKey(phase) && phase is not (AgentPhase.Finished or AgentPhase.Approving or AgentPhase.Asking or AgentPhase.Failed);

    /// The phase a session's event stands for in the stock lines.
    private static AgentPhase? PhaseOf(SessionEventKind kind) => kind switch
    {
        SessionEventKind.Done => AgentPhase.Finished,
        SessionEventKind.Question => AgentPhase.Asking,
        SessionEventKind.Approval => AgentPhase.Approving,
        SessionEventKind.Error => AgentPhase.Failed,
        _ => null,
    };

    /// The stock line for a session's event. A bubble says only this: never
    /// the session's name, never anything the agent wrote.
    public static string? For(SessionEvent item, Random random) => item.Kind switch
    {
        SessionEventKind.TaskSkipped => L10n.Tr("A scheduled task couldn't run."),
        _ => PhaseOf(item.Kind) is { } phase && Lines.TryGetValue(phase, out var lines)
            ? L10n.Tr(lines[random.Next(lines.Length)])
            : null,
    };

    /// The one word the card's list gives an event.
    public static string Word(SessionEventKind kind, bool classic = false) => L10n.Tr(kind switch
    {
        SessionEventKind.Done => "Done",
        SessionEventKind.Question => classic ? "Asking" : "Question",
        SessionEventKind.Approval => classic ? "Needs OK" : "Approval",
        _ => "Error",
    });

    /// A summary's lead: how many sessions wait on the bot's agent.
    public static string SummaryTitle(int count) => string.Format(L10n.Tr("{0} sessions need you."), count);

    /// A line for the phase, in the app's language, or null if it is quiet.
    public static string? For(AgentPhase phase, Random random) =>
        Lines.TryGetValue(phase, out var lines) ? L10n.Tr(lines[random.Next(lines.Length)]) : null;
}
