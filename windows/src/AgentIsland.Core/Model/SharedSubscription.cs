using AgentIsland.Core;

namespace AgentIsland.Model;

/// Grok 和 Cursor 共用 Grok 的角色（DisplayProviderExtensions.Character）。
public static class SharedSubscription
{
    /// The character plays the more urgent of the two apps' phases, Grok's on
    /// a tie, its detail without naming the app. Only the task phases decide:
    /// quota and billing play no part.
    public static (AgentPhase Phase, string? Detail) Pick(
        (AgentPhase Phase, string? Detail) grok, (AgentPhase Phase, string? Detail) cursor)
    {
        var fromCursor = AgentPhases.Priority(cursor.Phase) < AgentPhases.Priority(grok.Phase);
        var (phase, detail) = fromCursor ? cursor : grok;
        // Grok's card names no other app (the user's call, 2026-11-26).
        return phase == AgentPhase.Idle ? (phase, null) : (phase, detail);
    }
}
