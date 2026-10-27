namespace AgentIsland.Core;

public enum SessionLaunchTarget
{
    Cli,
    ClaudeDesktop,
}

/// A resumable session discovered on disk, for the activity monitor.
public sealed record ScannedSession(
    TriggerTool Tool,
    string SessionId,
    string Cwd,
    string Label,
    DateTimeOffset Modified,
    ActivityState Status,
    string? TranscriptPath,
    string? TurnKey,
    SessionLaunchTarget LaunchTarget)
{
    public string Id => Tool.RawValue() + ":" + SessionId;
    public PhaseReading? PendingPrompt { get; init; }

    /// The scan read the session's turn as over: a NetworkTrouble status is
    /// then how the turn ended, not a retry still under way.
    public bool TurnEnded { get; init; }
}
