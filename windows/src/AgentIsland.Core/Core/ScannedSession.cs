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

    /// The turn ended on an error the user has to see (a rate-limit
    /// interruption included) and the scan reads it as NeedsYou. The scan
    /// keeps it that way for up to NeedsYouCap, and so must whatever shows it.
    public bool TurnFailed { get; init; }

    /// The session runs in the agent's desktop app (Kimi Work), not in its CLI:
    /// it is abandoned when that app has no process left (AgentProcesses.DesktopGone).
    public bool DesktopHosted { get; init; }

    /// 这个会话下的子任务（subagent、workflow、swarm、子对话）。子任务不进平铺的会话列表，
    /// 所以运行数、“轮到你”和 LatestSessions 都看不到它们。只有不在 Idle 的会话才展开，
    /// 没有子任务或读不到时为空。
    public IReadOnlyList<TaskNode> Children { get; init; } = Array.Empty<TaskNode>();

    /// 没有展开的子任务数：超过每层上限（TaskTrees.MaxNodesPerList）或读取上限的部分。
    public int ChildrenOmitted { get; init; }

    /// 没展开的里面确定在跑或在等你的数量（下限）。
    public int ChildrenOmittedRunning { get; init; }

    /// Files waiting unread in the session's mailbox, newest last.
    public IReadOnlyList<MailFile> UnreadMail { get; init; } = Array.Empty<MailFile>();
}
