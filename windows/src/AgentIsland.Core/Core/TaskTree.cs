namespace AgentIsland.Core;

/// 任务树节点的种类。Conversation 是另一个完整的对话（Claude 桌面应用的侧边会话、
/// Codex 派生的线程），其余是父会话自己起的工作单元。
public enum TaskNodeKind
{
    Subagent,
    Workflow,
    Swarm,
    Conversation,
    /// Kimi 的后台进程任务（task.started 里 kind 为 process 的那些）。
    Task,
}

public enum TaskNodeState
{
    Running,
    /// 在等用户批准或回答。
    WaitingOnYou,
    Error,
    Done,
}

/// 父会话下的一个子任务。只有名字、种类、状态、正在做什么和时间，不含任何对话内容。
public sealed record TaskNode(
    string Id,
    string Name,
    TaskNodeKind Kind,
    TaskNodeState State,
    DateTimeOffset? StartedAt)
{
    /// 子任务的类型名（Explore、coder 等），名字缺失时界面用它拼“子任务 · 类型”。
    public string? AgentType { get; init; }

    /// 复用现有 phase 识别：这个子任务此刻在做什么。完成的节点没有。
    public PhaseReading? Phase { get; init; }

    /// 最后一次有动静的时间；完成的节点就是结束时间，界面据此在完成一段时间后收起它。
    public DateTimeOffset? UpdatedAt { get; init; }

    /// 真实层级：1 是父会话的直接子任务。超过 TaskTrees.MaxLevels 的节点已被压平到
    /// 第 MaxLevels 层的缩进上（挂在第 MaxLevels - 1 层的节点下面），这里保留它原来的层级。
    public int Level { get; init; } = 1;

    /// Conversation 节点：它在平铺会话列表里的 ScannedSession.Id，界面用它去重、取更详细的状态。
    public string? ConversationId { get; init; }

    public IReadOnlyList<TaskNode> Children { get; init; } = Array.Empty<TaskNode>();

    /// 限制展开数量时，这个节点下被省掉的子节点数。
    public int Omitted { get; init; }

    /// 被省掉的里面确定在跑或在等你的数量（下限：没读它们的记录就不知道，不算在内）。
    public int OmittedRunning { get; init; }
}

public static class TaskTrees
{
    /// 主对话为第 0 层，子任务最多画到第 2 层（设计稿 §2.2）。
    public const int MaxLevels = 2;

    /// 每个父会话每一层列表最多展开的节点数。读完状态后按“等你、出错、在跑、完成”的次序留，
    /// 再按新旧：等你和出错的先留，在跑的其次，被省掉的是排在最后的（较旧的已完成节点，
    /// 或在跑的超过 20 个以后最旧的那些，后者记在 OmittedRunning 里）。
    public const int MaxNodesPerList = 20;

    /// 每个父会话最多读最近的几个 workflow 运行。
    public const int MaxRunsPerSession = 5;

    /// 一层里最多读状态的记录数（按修改时间取最新）。再多的不读，只计入 Omitted：
    /// 一次几百个 agent 的 workflow 冷启动时不至于读几百份记录。
    public const int MaxParsedPerList = 100;

    /// 已完成的节点保留多久：过了这段时间的不再读它的记录，
    /// 也不进树。设计稿里“刚完成”显示 60 秒，这里多留一些，界面自己决定何时收起。
    public static readonly TimeSpan DoneKeepFor = TimeSpan.FromMinutes(10);

    /// 出错的节点保留多久（设计稿 §3.1：15 分钟）。
    public static readonly TimeSpan ErrorKeepFor = TimeSpan.FromMinutes(15);

    public static int Count(IReadOnlyList<TaskNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes) total += 1 + Count(node.Children);
        return total;
    }

    public static IEnumerable<TaskNode> Flatten(IReadOnlyList<TaskNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    /// 在跑和出错、等你的排前面，其次按最后动静从新到旧。
    internal static int Rank(TaskNodeState state) => state switch
    {
        TaskNodeState.WaitingOnYou => 0,
        TaskNodeState.Error => 1,
        TaskNodeState.Running => 2,
        _ => 3,
    };

    internal static List<TaskNode> Order(IEnumerable<TaskNode> nodes) =>
        nodes.OrderBy(node => Rank(node.State))
            .ThenByDescending(node => node.UpdatedAt ?? node.StartedAt ?? DateTimeOffset.MinValue)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToList();

    /// 层级压平：第 MaxLevels 层以下的节点放到它们第 MaxLevels 层祖先所在的同一列表里，
    /// 紧跟在该祖先后面，保留各自的 Level。层级关系有一点损失，但任务不会丢。
    internal static List<TaskNode> Cap(IReadOnlyList<TaskNode> nodes, int level = 1)
    {
        var output = new List<TaskNode>(nodes.Count);
        foreach (var node in nodes)
        {
            var leveled = node with { Level = level };
            if (level < MaxLevels)
            {
                // 压平之后第 2 层可能装不下：再留一次最要紧的，被省掉的计入这个节点。
                var kids = Cap(node.Children, level + 1);
                var omitted = node.Omitted;
                var omittedRunning = node.OmittedRunning;
                if (kids.Count > MaxNodesPerList)
                {
                    var trimmed = Trim(kids);
                    kids = trimmed.Kept;
                    omitted += trimmed.Omitted;
                    omittedRunning += trimmed.OmittedRunning;
                }
                output.Add(leveled with { Children = kids, Omitted = omitted, OmittedRunning = omittedRunning });
                continue;
            }
            output.Add(leveled with { Children = Array.Empty<TaskNode>() });
            output.AddRange(Deeper(node.Children, level + 1));
        }
        return output;
    }

    private static IEnumerable<TaskNode> Deeper(IReadOnlyList<TaskNode> nodes, int level)
    {
        foreach (var node in nodes)
        {
            yield return node with { Level = level, Children = Array.Empty<TaskNode>() };
            foreach (var deeper in Deeper(node.Children, level + 1)) yield return deeper;
        }
    }

    /// 一个节点要不要进树：在跑和等你的都留，完成的只留 DoneKeepFor 之内，出错的留 ErrorKeepFor 之内。
    internal static bool Keep(TaskNodeState state, DateTimeOffset? at, DateTimeOffset now) => state switch
    {
        TaskNodeState.Done => at is not { } ended || now - ended <= DoneKeepFor,
        TaskNodeState.Error => at is not { } failed || now - failed <= ErrorKeepFor,
        _ => true,
    };

    internal static bool KeepDone(DateTimeOffset? at, DateTimeOffset now) => Keep(TaskNodeState.Done, at, now);

    /// 读完状态之后留下最要紧的 MaxNodesPerList 个：先按 Order 的次序选，
    /// 留下的再按开始时间排（设计稿 §2.3：折叠按开始时间早的优先，露出的行不换人）。
    internal static (List<TaskNode> Kept, int Omitted, int OmittedRunning) Trim(IEnumerable<TaskNode> nodes, int alreadyOmitted = 0)
    {
        var ordered = Order(nodes);
        var cut = ordered.Skip(MaxNodesPerList).ToList();
        var kept = ordered.Take(MaxNodesPerList)
            .OrderBy(node => node.StartedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToList();
        return (kept, alreadyOmitted + cut.Count, cut.Count(node => node.State is TaskNodeState.Running or TaskNodeState.WaitingOnYou));
    }
}
