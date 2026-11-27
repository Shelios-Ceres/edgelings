using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;

namespace AgentIsland.Model;

/// 卡上一行任务的视觉状态（设计稿 §3.1）：只有等你、出错、刚完成上色。
public enum StatusTaskState
{
    Running,
    NeedsYou,
    Error,
    Done,
}

public enum StatusRowKind
{
    Task,
    /// 折叠行：“+3 个子任务 · 2 个在跑”。
    MoreSubtasks,
    /// 主对话超过上限：“+2 个对话”。
    MoreConversations,
}

/// 悬浮卡任务列表里的一行。Depth 0 是主对话（占两行：名字，和 Activity），1、2 是子任务
/// （名字和右侧的短状态词 Word）。折叠行只有 Name。
public sealed record StatusTaskRow(string Id, StatusRowKind Kind, int Depth, string Name, StatusTaskState State, string Word)
{
    /// 主对话第二行的完整说法；放不下时界面改用 Word（短词）。
    public string? Activity { get; init; }

    /// 主对话第二行的对象（工具名、重连第几次），接在说法后面，从末尾截断。
    public string? Object { get; init; }

    public bool IsMain => Kind == StatusRowKind.Task && Depth == 0;

    /// “+N 个对话”这一行：被收起的对话里有几个在等你、出错、在跑，面板头的汇总要算上它们。
    public StatusTally Hidden { get; init; }
}

/// 面板头汇总用的计数：等你和出错的算所有层级，在跑的只算主对话。
public readonly record struct StatusTally(int Waiting, int Errors, int Running)
{
    public static StatusTally operator +(StatusTally a, StatusTally b) =>
        new(a.Waiting + b.Waiting, a.Errors + b.Errors, a.Running + b.Running);
}

/// 头部的紧凑额度：一个窗口的短标签和读数。Used 为 null 表示在等同步。
public sealed record StatusQuotaWindow(string Label, string Value, double? Used)
{
    public string Text => $"{Label} {Value}";
    /// Used 是 0–1 的比例（和 StatusMeter.Used 一样）。
    public bool Warn => Used >= 0.9;
}

/// 头部右侧的额度行，最多两个窗口。Stale：读数不是最新的（数字改用三级墨色）。
public sealed record StatusQuota(IReadOnlyList<StatusQuotaWindow> Windows, bool Stale)
{
    public string Text => string.Join(" · ", Windows.Select(w => w.Text));

    /// 放不下时只留用得更多的那个窗口（设计稿 §2.6）。
    public StatusQuotaWindow? Busiest => Windows.OrderByDescending(w => w.Used ?? -1).FirstOrDefault();
}

public static class StatusTaskRows
{
    /// 每个 agent 分段最多显示的主对话（设计稿 §2.3）。
    public const int MaxConversations = 3;
    public const int ClassicMaxLines = 10;
    public const int PixelMaxLines = 8;
    /// 子项不超过这么多就全部显示；超过时只露 FoldShown 条（等你和出错的一定露出）。
    public const int FoldAbove = 3;
    public const int FoldShown = 2;
    /// 刚完成的子任务显示多久。
    public static readonly TimeSpan DoneShownFor = TimeSpan.FromSeconds(60);
    /// 主对话的“等你回复”和出错显示多久（和 StatusWaitingList.ClassicFor 一致）。
    public static readonly TimeSpan EndedShownFor = StatusWaitingList.ClassicFor;

    /// 一个角色的任务列表。sessions 是这个角色名下的平铺会话；phases 是各会话（按 ScannedSession.Id）
    /// 此刻在做什么，角色自己的头号会话可以传角色正在演的 phase，好和动画一致。
    public static IReadOnlyList<StatusTaskRow> Build(
        IEnumerable<ScannedSession> sessions, IReadOnlyDictionary<string, PhaseReading> phases, DateTimeOffset now, bool pixel)
    {
        var latest = ActivityMonitor.LatestSessions(sessions).ToList();
        var mains = latest
            .Select(session => Main(session, phases.TryGetValue(session.Id, out var reading) ? reading : null, now))
            .OfType<MainTask>()
            .ToList();
        // 挂在父会话下的侧边对话：状态取它在平铺列表里那一行（一轮做完是“等你回复”，D4），
        // 不用节点自己的“完成”。
        var conversations = mains.ToDictionary(main => main.Session.Id, main => main.Row, StringComparer.Ordinal);
        // 侧边会话已经挂在父会话下面，不再单列。
        var nested = mains
            .SelectMany(main => TaskTrees.Flatten(main.Session.Children))
            .Select(node => node.ConversationId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        mains = mains.Where(main => !nested.Contains(main.Session.Id)).ToList();
        // 挑哪几个对话露出来，按它连同子任务里最紧急的状态：子任务在等你或出错的对话不会被收进“+N 个对话”。
        var all = mains.Select(main => new Tree(main, Visible(main.Session.Children, now, conversations, 1))).ToList();
        var shown = all
            .OrderBy(tree => tree.Rank)
            .ThenByDescending(tree => tree.Main.Session.Modified)
            .Take(MaxConversations)
            .OrderBy(tree => tree.Rank)
            .ThenBy(tree => tree.Main.Session.Id, StringComparer.Ordinal)
            .ToList();
        var hidden = all.Except(shown).ToList();
        var budget = (pixel ? PixelMaxLines : ClassicMaxLines) - (hidden.Count > 0 ? 1 : 0);
        // 超出总行数：从子项最多的那个对话开始，一次少露一棵层级 1 的子树。
        while (shown.Sum(tree => tree.Lines()) > budget)
        {
            var tree = shown.Where(t => t.CanShrink()).OrderByDescending(t => t.Lines()).FirstOrDefault();
            if (tree is null) break;
            tree.Shrink();
        }
        var rows = new List<StatusTaskRow>();
        foreach (var tree in shown) rows.AddRange(tree.Rows());
        if (hidden.Count > 0)
        {
            rows.Add(new StatusTaskRow("more-conversations", StatusRowKind.MoreConversations, 0,
                L10n.TrFormat("+{0} more conversations", hidden.Count), StatusTaskState.Running, "")
            {
                Hidden = hidden.Aggregate(default(StatusTally), (sum, tree) => sum + tree.Tally()),
            });
        }
        return rows;
    }

    /// 汇总：等你和出错的算所有层级，在跑的只算主对话；收进“+N 个对话”的也算上。
    public static StatusTally Tally(IEnumerable<StatusTaskRow> rows)
    {
        var tally = default(StatusTally);
        foreach (var row in rows)
        {
            if (row.Kind == StatusRowKind.MoreConversations)
            {
                tally += row.Hidden;
                continue;
            }
            if (row.Kind != StatusRowKind.Task) continue;
            tally += new StatusTally(
                row.State == StatusTaskState.NeedsYou ? 1 : 0,
                row.State == StatusTaskState.Error ? 1 : 0,
                row.IsMain && row.State == StatusTaskState.Running ? 1 : 0);
        }
        return tally;
    }

    /// 面板头左边的汇总：“1 个在等你 · 1 个出错 · 2 个在跑”，都没有就“都空闲”。
    /// urgentOnly：放不下时只留等你和出错的（没有的话留在跑的）。
    public static string Summary(IEnumerable<StatusTaskRow> rows, bool urgentOnly = false)
    {
        var tally = Tally(rows);
        var urgent = new List<string>();
        if (tally.Waiting > 0) urgent.Add(L10n.TrFormat("{0} need you", tally.Waiting));
        if (tally.Errors > 0) urgent.Add(L10n.TrFormat("{0} failed (tasks)", tally.Errors));
        var running = tally.Running > 0 ? L10n.TrFormat("{0} running (tasks)", tally.Running) : null;
        if (urgent.Count == 0) return running ?? L10n.Tr("All idle");
        if (!urgentOnly && running is not null) urgent.Add(running);
        return string.Join(" · ", urgent);
    }

    /// 出错 > 等你 > 在跑 > 刚完成（设计稿 §2.3）。
    internal static int Rank(StatusTaskState state) => state switch
    {
        StatusTaskState.Error => 0,
        StatusTaskState.NeedsYou => 1,
        StatusTaskState.Running => 2,
        _ => 3,
    };

    private sealed record MainTask(ScannedSession Session, StatusTaskRow Row);

    /// 主对话一行：D4，主对话一轮做完算“等你回复”；出错和做完的留 15 分钟；有未读消息的算等你。
    private static MainTask? Main(ScannedSession session, PhaseReading? reading, DateTimeOffset now)
    {
        var prompt = session.PendingPrompt ?? reading;
        var phase = AgentPhases.Combine(session.Status, prompt, session.TurnFailed);
        var ended = now - session.Modified > EndedShownFor;
        StatusTaskState state;
        switch (phase)
        {
            case AgentPhase.Asking or AgentPhase.Approving:
                state = StatusTaskState.NeedsYou;
                break;
            case AgentPhase.Failed or AgentPhase.AuthRequired:
                if (ended) return session.UnreadMail.Count > 0 ? Mail(session) : null;
                state = StatusTaskState.Error;
                break;
            case AgentPhase.NetworkTrouble:
                if (session.TurnEnded && ended) return session.UnreadMail.Count > 0 ? Mail(session) : null;
                state = StatusTaskState.Error;
                break;
            case AgentPhase.Finished:
                if (ended) return session.UnreadMail.Count > 0 ? Mail(session) : null;
                state = StatusTaskState.NeedsYou;
                break;
            case AgentPhase.Idle:
                return session.UnreadMail.Count > 0 ? Mail(session) : null;
            default:
                state = StatusTaskState.Running;
                break;
        }
        var detail = prompt is { } r && r.Phase == phase ? r.Detail : null;
        return new MainTask(session, new StatusTaskRow(session.Id, StatusRowKind.Task, 0, MainName(session.Label), state, Short(phase))
        {
            Activity = Full(phase),
            Object = string.IsNullOrWhiteSpace(detail) ? null : detail,
        });
    }

    private static MainTask Mail(ScannedSession session)
    {
        var count = session.UnreadMail.Count;
        return new MainTask(session, new StatusTaskRow(session.Id, StatusRowKind.Task, 0, MainName(session.Label), StatusTaskState.NeedsYou,
            L10n.Tr("Mail (short)"))
        {
            Activity = count > 1 ? L10n.TrFormat("{0} new messages", count) : L10n.Tr("New mail"),
        });
    }

    internal static string MainName(string label) => Clean(label) is { Length: > 0 } name ? name : L10n.Tr("Untitled conversation");

    private static string NodeName(TaskNode node)
    {
        if (Clean(node.Name) is { Length: > 0 } name) return name;
        var kind = node.AgentType is { Length: > 0 } type ? type : node.Kind.ToString();
        return L10n.TrFormat("Subtask · {0}", kind);
    }

    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    /// 任务名只占一行：换行和连续空白并成一个空格，去掉开头的 / # > 和 Markdown 的 ** 与 `（设计稿 §2.1）。
    internal static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = Space.Replace(text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal), " ").Trim();
        return flat.TrimStart('/', '#', '>', ' ');
    }

    internal static StatusTaskState StateOf(TaskNodeState state) => state switch
    {
        TaskNodeState.WaitingOnYou => StatusTaskState.NeedsYou,
        TaskNodeState.Error => StatusTaskState.Error,
        TaskNodeState.Done => StatusTaskState.Done,
        _ => StatusTaskState.Running,
    };

    /// 一个子项（连同它的子树）还要不要显示：刚完成的只留 DoneShownFor，其余交给 Core 的保留规则。
    private static bool Shows(StatusTaskState state, TaskNode node, DateTimeOffset now) =>
        state != StatusTaskState.Done
        || (node.UpdatedAt ?? node.StartedAt) is not { } at
        || now - at <= DoneShownFor;

    /// 一个子项：它的状态和右侧的短状态词，露出来的子项，以及子树里最紧急的状态（折叠时按它挑）。
    private sealed record Node(TaskNode Task, StatusTaskState State, string Word, IReadOnlyList<Node> Kids, int EffectiveRank)
    {
        public int Size => 1 + Kids.Sum(kid => kid.Size);
        public int Running => (State == StatusTaskState.Running ? 1 : 0) + Kids.Sum(kid => kid.Running);
    }

    /// 露出来的子项。第 MaxLevels 层及更深的压平到第 MaxLevels 层（设计稿 §2.2）：Core 已经压过，
    /// 这里再兜一次底，界面无论拿到多深的树都不画第 3 层以下的缩进。
    private static List<Node> Visible(IReadOnlyList<TaskNode> nodes, DateTimeOffset now, IReadOnlyDictionary<string, StatusTaskRow> conversations, int level)
    {
        var output = new List<Node>();
        foreach (var task in level >= TaskTrees.MaxLevels ? Deep(nodes) : nodes)
        {
            var kids = level >= TaskTrees.MaxLevels ? new List<Node>() : Visible(task.Children, now, conversations, level + 1);
            var (state, word) = task.ConversationId is { } id && conversations.TryGetValue(id, out var flat)
                ? (flat.State, flat.Word)
                : Own(task);
            if (!Shows(state, task, now) && kids.Count == 0) continue;
            var rank = kids.Select(kid => kid.EffectiveRank).Append(Rank(state)).Min();
            output.Add(new Node(task, state, word, kids, rank));
        }
        return output
            .OrderBy(node => node.Task.StartedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(node => node.Task.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<TaskNode> Deep(IReadOnlyList<TaskNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var deeper in Deep(node.Children)) yield return deeper;
        }
    }

    private static (StatusTaskState State, string Word) Own(TaskNode node)
    {
        var state = StateOf(node.State);
        return (state, state switch
        {
            StatusTaskState.Done => L10n.Tr("Done (short)"),
            StatusTaskState.Error => Short(AgentPhase.Failed),
            StatusTaskState.NeedsYou => node.Phase?.Phase is AgentPhase.Asking ? Short(AgentPhase.Asking) : Short(AgentPhase.Approving),
            _ => node.Phase is { } reading && AgentPhases.IsWork(reading.Phase) ? Short(reading.Phase) : Short(AgentPhase.Working),
        });
    }

    /// 一个列表里露哪几条：不超过 limit 条全露；否则露 limit 条（等你和出错的一定露），
    /// 按“出错 > 等你 > 在跑 > 完成、同级开始早的优先”挑，再按开始时间排。
    /// 折叠行不只替代 1 条（设计稿 §2.3）：只藏下 1 条、又没有别的省掉的，就直接露出来。
    private static (List<Node> Shown, List<Node> Hidden) Fold(List<Node> nodes, int limit, int omitted)
    {
        var urgent = nodes.Count(node => node.EffectiveRank <= 1);
        if (nodes.Count <= limit) return (nodes, new List<Node>());
        var count = Math.Max(limit, urgent);
        if (nodes.Count - count == 1 && omitted == 0) return (nodes, new List<Node>());
        var picked = nodes
            .Select((node, index) => (node, index))
            .OrderBy(item => item.node.EffectiveRank)
            .ThenBy(item => item.index)
            .Take(count)
            .Select(item => item.node)
            .ToHashSet();
        return (nodes.Where(picked.Contains).ToList(), nodes.Where(node => !picked.Contains(node)).ToList());
    }

    private static int DefaultLimit(int count) => count <= FoldAbove ? count : FoldShown;

    /// 一个主对话和它露出来的子任务。Limit 是层级 1 列表还能露几条，超出总行数时逐条减少。
    private sealed class Tree(MainTask main, List<Node> kids)
    {
        private int _limit = DefaultLimit(kids.Count);
        private readonly int _urgent = kids.Count(kid => kid.EffectiveRank <= 1);

        public MainTask Main => main;

        /// 这个对话连同子任务里最紧急的状态。
        public int Rank => kids.Select(kid => kid.EffectiveRank).Append(StatusTaskRows.Rank(main.Row.State)).Min();

        /// 整个对话收起来时，它在汇总里占的份：主对话自己，加上子任务里等你和出错的。
        public StatusTally Tally() => StatusTaskRows.Tally(new[] { main.Row }) + Urgent(kids);

        private static StatusTally Urgent(IEnumerable<Node> nodes) => nodes.Aggregate(default(StatusTally), (sum, node) => sum
            + new StatusTally(node.State == StatusTaskState.NeedsYou ? 1 : 0, node.State == StatusTaskState.Error ? 1 : 0, 0)
            + Urgent(node.Kids));

        public bool CanShrink() => _limit > Math.Max(0, _urgent) && kids.Count > 0;

        public void Shrink() => _limit--;

        public int Lines() => Rows().Sum(row => row.IsMain ? 2 : 1);

        public IEnumerable<StatusTaskRow> Rows()
        {
            yield return main.Row;
            var session = main.Session;
            foreach (var row in List(session.Id, kids, _limit, 1, session.ChildrenOmitted, session.ChildrenOmittedRunning)) yield return row;
        }

        private static IEnumerable<StatusTaskRow> List(string parent, List<Node> nodes, int limit, int depth, int omitted, int omittedRunning)
        {
            // 第 2 层的节点下面被 Core 省掉的子项（已压平到这一层）并进这一层的折叠行。
            if (depth >= TaskTrees.MaxLevels)
            {
                omitted += nodes.Sum(node => node.Task.Omitted);
                omittedRunning += nodes.Sum(node => node.Task.OmittedRunning);
            }
            var (shown, hidden) = Fold(nodes, limit, omitted);
            foreach (var node in shown)
            {
                // 压平到第 2 层的更深节点用第 2 层的缩进。
                yield return new StatusTaskRow(node.Task.Id, StatusRowKind.Task, Math.Min(depth, TaskTrees.MaxLevels), NodeName(node.Task), node.State, node.Word);
                if (depth >= TaskTrees.MaxLevels) continue;
                var kids = node.Kids.ToList();
                foreach (var row in List(node.Task.Id, kids, DefaultLimit(kids.Count), depth + 1, node.Task.Omitted, node.Task.OmittedRunning)) yield return row;
            }
            var count = hidden.Sum(node => node.Size) + omitted;
            if (count <= 0) yield break;
            var running = hidden.Sum(node => node.Running) + omittedRunning;
            yield return new StatusTaskRow(parent + "/more", StatusRowKind.MoreSubtasks, depth,
                running > 0 ? L10n.TrFormat("+{0} subtasks · {1} running", count, running) : L10n.TrFormat("+{0} subtasks", count),
                StatusTaskState.Running, "");
        }
    }

    /// 状态词的完整说法（设计稿 §5.1 的完整列）：主对话第二行用。卡有自己的一套键，不动 Settings 等处的说法。
    public static string Full(AgentPhase phase) => L10n.Tr(phase switch
    {
        AgentPhase.Thinking => "Thinking (card)",
        AgentPhase.Streaming => "Writing (card)",
        AgentPhase.Reading => "Reading files (card)",
        AgentPhase.Searching => "Searching code (card)",
        AgentPhase.WebSearching => "Searching the web (card)",
        AgentPhase.Writing => "Editing code (card)",
        AgentPhase.Running => "Running commands (card)",
        AgentPhase.Controlling => "Operating apps (card)",
        AgentPhase.Spawning => "Starting a subagent (card)",
        AgentPhase.WaitingOnAgents => "Waiting on subagents (card)",
        AgentPhase.Sending => "Sending a message (card)",
        AgentPhase.Compacting => "Compacting context (card)",
        AgentPhase.Asking => "Asking you (card)",
        AgentPhase.Approving => "Waiting for your approval (card)",
        AgentPhase.Finished => "Waiting for your reply (card)",
        AgentPhase.Failed => "Something went wrong (card)",
        AgentPhase.NetworkTrouble => "Network trouble (card)",
        AgentPhase.AuthRequired => "Sign-in needed (card)",
        AgentPhase.Idle => "Idle",
        _ => "Working (card)",
    });

    /// 状态词的短形式（设计稿 §5.1 的短词列）：子任务行右侧和第二行放不下时用。
    public static string Short(AgentPhase phase) => L10n.Tr(phase switch
    {
        AgentPhase.Thinking => "Thinking (short)",
        AgentPhase.Streaming => "Writing (short)",
        AgentPhase.Reading => "Reading (short)",
        AgentPhase.Searching => "Searching (short)",
        AgentPhase.WebSearching => "Web (short)",
        AgentPhase.Writing => "Editing (short)",
        AgentPhase.Running => "Running commands (short)",
        AgentPhase.Controlling => "Apps (short)",
        AgentPhase.Spawning => "Spawning (short)",
        AgentPhase.WaitingOnAgents => "Waiting on subagents (short)",
        AgentPhase.Sending => "Sending (short)",
        AgentPhase.Compacting => "Compacting (short)",
        AgentPhase.Asking => "Asking you (short)",
        AgentPhase.Approving => "Needs OK (short)",
        AgentPhase.Finished => "Your turn (short)",
        AgentPhase.Failed => "Failed (short)",
        AgentPhase.NetworkTrouble => "Offline (short)",
        AgentPhase.AuthRequired => "Sign in (short)",
        AgentPhase.Idle => "Idle",
        _ => "Working (short)",
    });

    /// 头部的紧凑额度（设计稿 §5A.1）：5 小时和周两个窗口，卡上用短标签；月等其余窗口不上卡。
    public static StatusQuota? Quota(DisplayProvider provider) => QuotaOf(StatusCards.Meters(provider), StatusCards.QuotaStale(provider));

    /// 按窗口自己的标签认，不按位置：Kimi 缺 5 小时窗口时列表是“周、月”。
    internal static StatusQuota? QuotaOf(IEnumerable<StatusMeter> meters, bool stale)
    {
        var fiveHours = L10n.Tr("5h");
        var week = L10n.Tr("Week");
        var windows = new List<(string Label, StatusMeter Meter)>();
        foreach (var meter in meters)
        {
            if (meter.Label == fiveHours) windows.Add((L10n.Tr("5h (card)"), meter));
            else if (meter.Label == week) windows.Add((L10n.Tr("Wk"), meter));
        }
        return Quota(windows, stale);
    }

    /// 读不到的窗口不显示（不写“—”）；等同步的写“同步中”。
    internal static StatusQuota? Quota(IEnumerable<(string Label, StatusMeter Meter)> windows, bool stale)
    {
        var list = new List<StatusQuotaWindow>();
        foreach (var (label, meter) in windows)
        {
            if (meter.Used is { } used) list.Add(new StatusQuotaWindow(label, meter.Value, used));
            else if (meter.Resets is not null) list.Add(new StatusQuotaWindow(label, L10n.Tr("syncing"), null));
        }
        return list.Count == 0 ? null : new StatusQuota(list, stale);
    }

    /// 全员卡的合计：各家今天和本月的 token 加起来。某家的数字停在昨天（或上个月），
    /// 这一项就不算它，免得一家过期把整行变成“—”；一家都没有当天的数字才写“—”。
    public static StatusTokens Total(IEnumerable<TokenTotals?> totals, TokenCountMode mode, DateTimeOffset now)
    {
        var list = totals.OfType<TokenTotals>().ToList();
        var label = L10n.Tr("Total");
        if (list.Count == 0) return new StatusTokens(label, L10n.Tr("Not scanned yet"), "", "—", "");
        var billable = mode == TokenCountMode.Billable;
        var local = now.ToLocalTime();
        var todays = list.Where(t => t.AsOf.ToLocalTime().Date == local.Date).ToList();
        var months = list.Where(t => t.AsOf.ToLocalTime() is var at && at.Year == local.Year && at.Month == local.Month).ToList();
        var today = todays.Count == 0 ? "—" : Formatting.CompactTokens(todays.Sum(t => billable ? t.TodayBillable : t.Today));
        var month = months.Count == 0 ? "—" : Formatting.CompactTokens(months.Sum(t => billable ? t.MonthBillable : t.Month));
        return new StatusTokens(label, $"{L10n.Tr("today")} {today}", today, L10n.TrFormat("{0} this month", month), month);
    }

    /// 一个角色的 token（Grok 含 Cursor），给全员卡的合计用。
    public static TokenTotals? Totals(DisplayProvider character) => character == DisplayProvider.Grok
        ? TokenTotals.Sum(CostStore.Shared.Totals(DisplayProvider.Grok), CostStore.Shared.Totals(DisplayProvider.Cursor))
        : CostStore.Shared.Totals(character);

    /// 内容 key 用：行的全部可见文字和状态，不含时间。
    public static string Key(IEnumerable<StatusTaskRow> rows)
    {
        var key = new StringBuilder();
        foreach (var row in rows)
        {
            key.Append(row.Id).Append('|').Append((int)row.Kind).Append('|').Append(row.Depth.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(row.Name).Append('|').Append((int)row.State).Append('|').Append(row.Word)
                .Append('|').Append(row.Activity).Append('|').Append(row.Object).Append('\n');
        }
        return key.ToString();
    }
}
