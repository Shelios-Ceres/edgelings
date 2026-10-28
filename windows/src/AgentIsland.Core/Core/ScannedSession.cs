namespace AgentIsland.Core;

public enum SessionLaunchTarget
{
    Cli,
    ClaudeDesktop,
}

/// An unread mailbox message of a Kimi session: its file's name and when the
/// file was written. Nothing inside the file is ever read.
public readonly record struct MailFile(string Name, DateTimeOffset At);

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

    /// Files waiting unread in the session's mailbox, newest last.
    public IReadOnlyList<MailFile> UnreadMail { get; init; } = Array.Empty<MailFile>();
}
