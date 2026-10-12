namespace AgentIsland.Core;

/// What an agent is doing right now — finer than ActivityState, which stays
/// the source of truth for working/idle/attention. Live work phases come from
/// a transcript tail (AgentPhaseClassifier); the attention phases mirror
/// ActivityState so one value can drive a character's animation.
public enum AgentPhase
{
    Idle,
    Thinking,
    Streaming,
    Reading,
    Searching,
    WebSearching,
    Writing,
    Running,
    Controlling,
    Spawning,
    WaitingOnAgents,
    Sending,
    Asking,
    Compacting,
    Working,
    Finished,
    Failed,
    Stalled,
    RateLimited,
    AuthRequired,
    Approving,
}

/// One classification of a transcript tail. Detail is a tool name — never
/// message content.
public readonly record struct PhaseReading(AgentPhase Phase, string? Detail, DateTimeOffset? At);

public static class AgentPhases
{
    /// Lower wins. A higher-priority phase may interrupt a shown phase's
    /// minimum dwell; with several sessions the most urgent one is shown.
    public static int Priority(AgentPhase phase) => phase switch
    {
        AgentPhase.Asking or AgentPhase.Approving => 0,
        AgentPhase.Failed or AgentPhase.AuthRequired => 1,
        AgentPhase.Finished or AgentPhase.RateLimited or AgentPhase.Stalled => 2,
        AgentPhase.Idle => 4,
        _ => 3,
    };

    public static bool IsWork(AgentPhase phase) =>
        phase is not (AgentPhase.Idle or AgentPhase.Finished or AgentPhase.Stalled
            or AgentPhase.RateLimited or AgentPhase.AuthRequired);

    /// The scanner's state decides whether the agent is working at all; the
    /// transcript only refines HOW it works. A transcript lagging behind the
    /// scan (still idle or finished) falls back to plain Working.
    public static AgentPhase Combine(ActivityState state, PhaseReading? transcript, bool quotaExhausted) =>
        state switch
        {
            ActivityState.AuthRequired => AgentPhase.AuthRequired,
            ActivityState.RateLimited => AgentPhase.RateLimited,
            _ when quotaExhausted => AgentPhase.RateLimited,
            // An open permission prompt writes nothing, so the scan calls it
            // stalled after a few minutes; the hook knows better.
            ActivityState.Stalled when transcript is { Phase: AgentPhase.Approving or AgentPhase.Asking } reading => reading.Phase,
            ActivityState.Stalled => AgentPhase.Stalled,
            ActivityState.NeedsYou => AgentPhase.Finished,
            ActivityState.Working when transcript is { } reading && IsWork(reading.Phase) => reading.Phase,
            ActivityState.Working => AgentPhase.Working,
            _ => AgentPhase.Idle,
        };
}
