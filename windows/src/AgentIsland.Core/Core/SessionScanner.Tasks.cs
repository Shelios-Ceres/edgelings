using System.Text;
using System.Text.Json;

namespace AgentIsland.Core;

// 任务树：父会话下面的 subagent、workflow、swarm 和子对话。
// 子任务只挂在父会话的 Children 上，不进平铺的会话列表，
// 所以运行数、“轮到你”事件和 LatestSessions 都和以前一样。
// 只展开不在 Idle 的父会话；一个子任务读不到只是少一个节点，父会话照常显示。
public static partial class SessionScanner
{
    /// 展开时一路往下建的层数上限（只防环和失控；画几层由 TaskTrees.Cap 决定）。
    private const int BuildDepth = 4;

    /// 节点读状态时，开着的回合静默多久当作已经放弃。
    private static readonly TimeSpan ChildAbandonedAfter = AttentionWindow;

    /// 测试和耗时对比用的开关：关掉后不读主会话名的尾部、不展开任务树，行为和加入任务树之前一致。
    internal static bool TaskTreesEnabled = true;

    /// 测试用：读过状态的子记录个数（Claude 的 agent 记录），用来核对每层读取上限。
    internal static int ParsedChildren;

    // MARK: - 共用

    private static TaskNodeState NodeStateOf(
        SessionTurnStatus turn, DateTimeOffset updated, PhaseReading? phase, DateTimeOffset now)
    {
        if (turn.IsDone) return turn.Trouble == TurnTrouble.None ? TaskNodeState.Done : TaskNodeState.Error;
        if (turn.Idle) return TaskNodeState.Done;
        if (now - updated > ChildAbandonedAfter) return TaskNodeState.Done;
        return phase?.Phase switch
        {
            AgentPhase.Asking or AgentPhase.Approving => TaskNodeState.WaitingOnYou,
            AgentPhase.Failed or AgentPhase.AuthRequired => TaskNodeState.Error,
            _ => TaskNodeState.Running,
        };
    }

    /// 读状态之前的开销上限：最多读最近的 MaxParsedPerList 个，其余不读，只计入省掉的个数。
    /// 哪些节点最终显示由读完状态后的 TaskTrees.Trim 决定，不是这里按时间挤的。
    private static (List<T> ToParse, int NotParsed) NewestToParse<T>(List<T> items, Func<T, DateTimeOffset> modified)
    {
        if (items.Count <= TaskTrees.MaxParsedPerList) return (items, 0);
        var kept = items.OrderByDescending(modified).Take(TaskTrees.MaxParsedPerList).ToList();
        return (kept, items.Count - kept.Count);
    }

    /// 一批节点读完之后的汇总：显示的节点、省掉的个数、省掉里在跑的个数，
    /// 以及整批（含省掉的）里有没有在跑、在等你的——运行和 swarm 的状态要看整批，不是只看留下的。
    private sealed record Batch(List<TaskNode> Kept, int Omitted, int OmittedRunning, bool Live, bool Waiting)
    {
        public static Batch Of(IReadOnlyCollection<TaskNode> all, int notParsed)
        {
            var (kept, omitted, omittedRunning) = TaskTrees.Trim(all, notParsed);
            return new Batch(kept, omitted, omittedRunning,
                all.Any(n => n.State is TaskNodeState.Running or TaskNodeState.WaitingOnYou),
                all.Any(n => n.State == TaskNodeState.WaitingOnYou));
        }
    }

    private static DateTimeOffset Created(string path)
    {
        try
        {
            var utc = File.GetCreationTimeUtc(path);
            return utc.Year < 1700 ? DateTimeOffset.MinValue : new DateTimeOffset(utc);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// 毫秒时间戳转时间；写坏了的（负数、越界、不是数）当作没有，不抛异常。
    private static DateTimeOffset? FromMilliseconds(double? ms) =>
        ms is { } m and > 0 and < 253_402_300_799_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)m) : null;

    private static readonly System.Text.RegularExpressions.Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static readonly System.Text.RegularExpressions.Regex SecretPrefixPattern = new(
        @"(?<![A-Za-z0-9])(?:sk-ant-|sk-|ghp_|github_pat_|xox|AKIA|Bearer\s+|eyJ)[A-Za-z0-9._\-+/=]*",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static readonly System.Text.RegularExpressions.Regex LongTokenPattern = new(
        @"[A-Za-z0-9+/]{24,}={0,2}",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// 标题可能是用户的一句话，里面会夹着密钥、令牌或邮箱：常见前缀开头的串、
    /// 24 位以上连续的 base64 或 hex 串、邮箱都换成 …。匹配超时（异常长的输入）时整句不用。
    internal static string RedactTitle(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            var result = EmailPattern.Replace(text, "…");
            result = SecretPrefixPattern.Replace(result, "…");
            return LongTokenPattern.Replace(result, "…");
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return "";
        }
    }

    /// 进标题的文字一律先遮蔽再截断（遮蔽在前，免得截断把一个令牌切成认不出的半截）。
    internal static string SafeTitle(string? text, int limit = 40) => ShortTitle(RedactTitle(text), limit);

    /// 标题用的一句话：折掉空白，太长就截断。
    internal static string ShortTitle(string? text, int limit = 40)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= limit) return collapsed;
        // 不把一个代理对（emoji）从中间切开。
        var cut = char.IsHighSurrogate(collapsed[limit - 1]) ? limit - 1 : limit;
        return collapsed[..cut] + "…";
    }

    private static void ClearTaskCaches()
    {
        ClaudeTitles.Clear();
        ClaudeAgentMetas.Clear();
        ClaudeWorkflowNames.Clear();
        KimiStates.Clear();
        KimiWires.Clear();
    }

    // MARK: - Claude：主会话名

    private static readonly ScanCache<(long Ticks, long Size, string Title)> ClaudeTitles = new();
    private const long TitleTailBytes = 131_072;

    /// 记录里的名字：custom-title → agent-name → last-prompt。这几种记录在会话进行中
    /// 反复写在末尾（实测都在末尾 40K 以内），所以只读尾部；尾部里一个也没有时
    /// 保留上一次读到的，不退回目录名。
    internal static string ClaudeTranscriptTitle(string path)
    {
        long ticks, size;
        try
        {
            var info = new FileInfo(path);
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
        var known = "";
        if (ClaudeTitles.TryGet(path, out var cached))
        {
            if (cached.Ticks == ticks && cached.Size == size) return cached.Title;
            known = cached.Title;
        }
        string? custom = null, agent = null, prompt = null;
        foreach (var line in TailLines(path, TitleTailBytes, keep: int.MaxValue))
        {
            if (!line.StartsWith("{\"type\":\"", StringComparison.Ordinal)) continue;
            var which = line.StartsWith("{\"type\":\"custom-title\"", StringComparison.Ordinal) ? 1
                : line.StartsWith("{\"type\":\"agent-name\"", StringComparison.Ordinal) ? 2
                : line.StartsWith("{\"type\":\"last-prompt\"", StringComparison.Ordinal) ? 3
                : 0;
            if (which == 0) continue;
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            switch (which)
            {
                case 1: custom = NonBlank(SafeTitle(Jsonl.GetString(doc.RootElement, "customTitle"))) ?? custom; break;
                case 2: agent = NonBlank(SafeTitle(Jsonl.GetString(doc.RootElement, "agentName"))) ?? agent; break;
                default: prompt = NonBlank(Jsonl.GetString(doc.RootElement, "lastPrompt")) ?? prompt; break;
            }
        }
        var title = custom ?? agent ?? (prompt is null ? null : SafeTitle(prompt));
        if (string.IsNullOrEmpty(title)) title = known;
        ClaudeTitles.Set(path, (ticks, size, title));
        return title;
    }

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // MARK: - Claude：subagent、workflow、桌面子对话

    private sealed record AgentMeta(string Description, string AgentType, bool Finished, string? WorkflowPhase);

    private static readonly ScanCache<(long Ticks, long Size, AgentMeta Meta)> ClaudeAgentMetas = new();

    /// agent-<id>.meta.json：名字（description）、类型和是否已完成。meta 只写几行，
    /// 写完后随 finished 变一次，所以按（修改时间，长度）缓存。读不到当作没有。
    private static AgentMeta ClaudeAgentMeta(string transcript)
    {
        var path = transcript[..^".jsonl".Length] + ".meta.json";
        long ticks, size;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new AgentMeta("", "", false, null);
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AgentMeta("", "", false, null);
        }
        if (ClaudeAgentMetas.TryGet(path, out var cached) && cached.Ticks == ticks && cached.Size == size) return cached.Meta;
        try
        {
            using var stream = SharedFile.Open(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var meta = new AgentMeta(
                (Jsonl.GetString(root, "description") ?? "").Trim(),
                (Jsonl.GetString(root, "agentType") ?? "").Trim(),
                Jsonl.GetBool(root, "finished") == true,
                Jsonl.GetString(root, "workflowPhase"));
            ClaudeAgentMetas.Set(path, (ticks, size, meta));
            return meta;
        }
        catch
        {
            // 正在写：下次再读。
            return new AgentMeta("", "", false, null);
        }
    }

    private sealed class WorkflowNames
    {
        public readonly Dictionary<string, string> ByRun = new(StringComparer.Ordinal);
    }

    /// 父记录里 Workflow 工具结果的 toolUseResult.workflowName（带 runId）：启动时写下，
    /// 可能离末尾很远，所以按关键字增量读，不重读整份记录。
    private static readonly LineIndex<WorkflowNames> ClaudeWorkflowNames = new(
        ["\"workflowName\""],
        () => new WorkflowNames(),
        (state, line) =>
        {
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (Jsonl.GetObject(doc.RootElement, "toolUseResult") is not { } result) return;
            if (Jsonl.GetString(result, "runId") is not { Length: > 0 } run) return;
            if (NonBlank(Jsonl.GetString(result, "workflowName")) is { } name) state.ByRun[run] = name;
        });

    private sealed record AgentFile(string Path, DateTimeOffset Modified);

    private static List<AgentFile> AgentFilesIn(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists) return new List<AgentFile>();
            return info.EnumerateFiles("agent-*.jsonl")
                .Select(file => new AgentFile(file.FullName, new DateTimeOffset(file.LastWriteTimeUtc)))
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new List<AgentFile>();
        }
    }

    /// 一个 agent 记录读成节点；已经不值得显示（完成太久）的返回 null。
    private static TaskNode? ClaudeAgentNode(AgentFile file, TaskNodeKind kind, DateTimeOffset now)
    {
        Interlocked.Increment(ref ParsedChildren);
        var meta = ClaudeAgentMeta(file.Path);
        var id = Path.GetFileNameWithoutExtension(file.Path);
        TaskNodeState state;
        PhaseReading? phase = null;
        var updated = file.Modified;
        if (meta.Finished)
        {
            state = TaskNodeState.Done;
        }
        else
        {
            // 没写完成标记的：看记录自己。读不到记录就当它刚被起来。
            phase = AgentPhaseClassifier.ClassifyClaudeAgent(file.Path, now);
            var (turn, modified) = ReadTurn(file.Path, SessionTurnState.ClaudeAgent, p => TailLines(p));
            updated = turn.ActivityDate is { } at && at > modified ? at : modified;
            state = NodeStateOf(turn, updated, phase, now);
        }
        if (state == TaskNodeState.Done) phase = null;
        if (!TaskTrees.Keep(state, updated, now)) return null;
        return new TaskNode(id, meta.Description, kind, state, Created(file.Path))
        {
            AgentType = meta.AgentType.Length > 0 ? meta.AgentType : null,
            Phase = state is TaskNodeState.Done ? null : phase,
            UpdatedAt = updated,
        };
    }

    /// 读一批 agent 记录：先按修改时间筛（开着的回合一定在放弃窗口之内），
    /// 最多读最近的 MaxParsedPerList 个，读完状态再由 Batch 按优先级留下 MaxNodesPerList 个。
    private static Batch ClaudeAgentNodes(List<AgentFile> files, TaskNodeKind kind, DateTimeOffset now)
    {
        var live = files.Where(file => now - file.Modified <= ChildAbandonedAfter).ToList();
        var (toParse, notParsed) = NewestToParse(live, file => file.Modified);
        var nodes = new List<TaskNode>();
        foreach (var file in toParse)
        {
            if (ClaudeAgentNode(file, kind, now) is { } node) nodes.Add(node);
        }
        return Batch.Of(nodes, notParsed);
    }

    /// 一个 Claude 会话自己的子任务：subagent 和 workflow 运行（运行下面是各 agent）。
    internal static (List<TaskNode> Nodes, int Omitted, int OmittedRunning) ClaudeOwnChildren(string transcriptPath, DateTimeOffset now)
    {
        var all = new List<TaskNode>();
        var notParsed = 0;
        if (!transcriptPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return (all, 0, 0);
        var root = Path.Combine(transcriptPath[..^".jsonl".Length], "subagents");
        try
        {
            if (!Directory.Exists(root)) return (all, 0, 0);
            var direct = ClaudeAgentNodes(AgentFilesIn(root), TaskNodeKind.Subagent, now);
            // 直属的 subagent 先全部汇总，和 workflow 运行一起再留 MaxNodesPerList 个。
            all.AddRange(direct.Kept);
            notParsed += direct.Omitted;
            var extraRunning = direct.OmittedRunning;

            var workflows = Path.Combine(root, "workflows");
            if (Directory.Exists(workflows))
            {
                Dictionary<string, string>? names = null;
                // 只读最近的 MaxRunsPerSession 个运行，更早的不读，计入省掉的个数。
                var runs = Directory.EnumerateDirectories(workflows)
                    .Where(d => Path.GetFileName(d).StartsWith("wf", StringComparison.Ordinal))
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .ToList();
                notParsed += Math.Max(0, runs.Count - TaskTrees.MaxRunsPerSession);
                foreach (var run in runs.Take(TaskTrees.MaxRunsPerSession))
                {
                    var files = AgentFilesIn(run);
                    if (files.Count == 0) continue;
                    var newest = files.Max(file => file.Modified);
                    // 整个运行早就停了：不为它读任何记录。
                    if (now - newest > ChildAbandonedAfter && !TaskTrees.KeepDone(newest, now)) continue;
                    var agents = ClaudeAgentNodes(files, TaskNodeKind.Subagent, now);
                    names ??= ClaudeWorkflowNames.Read(transcriptPath, state => new Dictionary<string, string>(state.ByRun), new Dictionary<string, string>());
                    var runId = Path.GetFileName(run);
                    names.TryGetValue(runId, out var runName);
                    // 运行的状态看整批（含被省掉的），不是只看留下的那些。
                    var state = agents.Waiting ? TaskNodeState.WaitingOnYou : agents.Live ? TaskNodeState.Running : TaskNodeState.Done;
                    if (!TaskTrees.Keep(state, newest, now)) continue;
                    all.Add(new TaskNode(runId, runName ?? "", TaskNodeKind.Workflow, state, Created(run))
                    {
                        UpdatedAt = newest,
                        Children = agents.Kept,
                        Omitted = agents.Omitted,
                        OmittedRunning = agents.OmittedRunning,
                    });
                }
            }
            var top = TaskTrees.Trim(all, notParsed);
            return (top.Kept, top.Omitted, top.OmittedRunning + extraRunning);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 目录在扫描中被轮换：留下已经读到的。
            var top = TaskTrees.Trim(all, notParsed);
            return (top.Kept, top.Omitted, top.OmittedRunning);
        }
    }

    private static TaskNodeState ConversationState(ScannedSession session, PhaseReading? phase) =>
        session.Status switch
        {
            ActivityState.Working => phase?.Phase is AgentPhase.Asking or AgentPhase.Approving
                ? TaskNodeState.WaitingOnYou : TaskNodeState.Running,
            ActivityState.NeedsYou => session.TurnFailed ? TaskNodeState.Error : TaskNodeState.Done,
            ActivityState.NetworkTrouble or ActivityState.AuthRequired => TaskNodeState.Error,
            _ => TaskNodeState.Done,
        };

    /// 给 Claude 的平铺会话挂上子任务：自己的 subagent / workflow，
    /// 以及桌面应用里 spawnedFrom 指向它的子会话（那些会话仍留在平铺列表里）。
    private static void AttachClaudeChildren(
        List<ScannedSession> sessions, IReadOnlyDictionary<string, DesktopSession> desktops, DateTimeOffset now)
    {
        var bySession = new Dictionary<string, ScannedSession>(StringComparer.Ordinal);
        foreach (var session in sessions) bySession.TryAdd(session.SessionId, session);
        var flatOfDesktop = new Dictionary<string, string>(StringComparer.Ordinal);
        var spawned = new Dictionary<string, List<ScannedSession>>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!desktops.TryGetValue(session.SessionId, out var desktop)) continue;
            if (desktop.SessionId.Length > 0) flatOfDesktop.TryAdd(desktop.SessionId, session.SessionId);
            if (desktop.SpawnedFromSessionId.Length == 0) continue;
            if (!spawned.TryGetValue(desktop.SpawnedFromSessionId, out var list)) spawned[desktop.SpawnedFromSessionId] = list = new();
            list.Add(session);
        }
        var memo = new Dictionary<string, (List<TaskNode> Nodes, int Omitted, int OmittedRunning)>(StringComparer.Ordinal);

        (List<TaskNode> Nodes, int Omitted, int OmittedRunning) Expand(ScannedSession session, int depth, HashSet<string> path)
        {
            if (memo.TryGetValue(session.SessionId, out var done)) return done;
            var all = new List<TaskNode>();
            var omitted = 0;
            var omittedRunning = 0;
            if (session.TranscriptPath is { } transcript)
            {
                var own = ClaudeOwnChildren(transcript, now);
                all.AddRange(own.Nodes);
                omitted += own.Omitted;
                omittedRunning += own.OmittedRunning;
            }
            if (depth < BuildDepth
                && desktops.TryGetValue(session.SessionId, out var desktop)
                && desktop.SessionId.Length > 0
                && spawned.TryGetValue(desktop.SessionId, out var kids)
                && path.Add(session.SessionId))
            {
                var (toBuild, notBuilt) = NewestToParse(kids, kid => kid.Modified);
                omitted += notBuilt;
                foreach (var kid in toBuild)
                {
                    if (path.Contains(kid.SessionId)) continue;
                    var phase = kid.Status == ActivityState.Idle || kid.TranscriptPath is null
                        ? null
                        : AgentPhaseClassifier.Classify(TriggerTool.Claude, kid.TranscriptPath, now);
                    var state = ConversationState(kid, phase);
                    if (!TaskTrees.Keep(state, kid.Modified, now)) continue;
                    var inner = kid.Status == ActivityState.Idle
                        ? (Nodes: new List<TaskNode>(), Omitted: 0, OmittedRunning: 0)
                        : Expand(kid, depth + 1, path);
                    all.Add(new TaskNode(kid.SessionId, kid.Label, TaskNodeKind.Conversation, state,
                        desktops.TryGetValue(kid.SessionId, out var kidDesktop) ? kidDesktop.CreatedAt : null)
                    {
                        Phase = state == TaskNodeState.Done ? null : phase,
                        UpdatedAt = kid.Modified,
                        ConversationId = kid.Id,
                        Children = inner.Nodes,
                        Omitted = inner.Omitted,
                        OmittedRunning = inner.OmittedRunning,
                    });
                }
                path.Remove(session.SessionId);
            }
            var top = TaskTrees.Trim(all, omitted);
            return memo[session.SessionId] = (top.Kept, top.Omitted, top.OmittedRunning + omittedRunning);
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.Status == ActivityState.Idle) continue;
            try
            {
                var (nodes, omitted, omittedRunning) = Expand(session, 1, new HashSet<string>(StringComparer.Ordinal));
                if (nodes.Count == 0 && omitted == 0) continue;
                sessions[i] = session with
                {
                    Children = TaskTrees.Cap(nodes),
                    ChildrenOmitted = omitted,
                    ChildrenOmittedRunning = omittedRunning,
                };
            }
            catch (Exception error) when (IsExpansionFault(error))
            {
                // 子任务读不到：父会话照常显示，只是没有子节点。
            }
        }
    }

    /// 展开一个父会话的子任务时的任何读取或解析故障（文件被轮换、没权限、写了一半、
    /// 时间戳写坏）都只让这个父会话少子节点，不能把整次扫描带下去。
    /// 只放过内存耗尽这类不是数据问题的。
    private static bool IsExpansionFault(Exception error) => error is not (OutOfMemoryException or StackOverflowException);

    // MARK: - Kimi

    internal sealed record KimiAgentRef(string Id, string ParentId, bool Swarm);

    internal sealed record KimiStateInfo(string Title, string LastPrompt, IReadOnlyList<KimiAgentRef> Agents);

    private static readonly ScanCache<(string Stamp, KimiStateInfo Info)> KimiStates = new();

    /// state.json：标题（title / lastPrompt）和 agents 的父子关系。按写入时间和长度缓存。
    internal static KimiStateInfo? KimiState(string statePath)
    {
        var stamp = FileStamp(statePath);
        if (stamp.Length == 0) return null;
        if (KimiStates.TryGet(statePath, out var known) && known.Stamp == stamp) return known.Info;
        try
        {
            using var stream = SharedFile.Open(statePath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var agents = new List<KimiAgentRef>();
            if (root.TryGetProperty("agents", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (Jsonl.GetString(entry.Value, "parentAgentId") is not { Length: > 0 } parent) continue;
                    var swarm = Jsonl.GetObject(entry.Value, "labels") is { } labels && labels.TryGetProperty("swarmItem", out _);
                    agents.Add(new KimiAgentRef(entry.Name, parent, swarm));
                }
            }
            var info = new KimiStateInfo(
                NonBlank(SafeTitle(Jsonl.GetString(root, "title"))) ?? "",
                NonBlank(SafeTitle(Jsonl.GetString(root, "lastPrompt"))) ?? "",
                agents);
            KimiStates.Set(statePath, (stamp, info));
            return info;
        }
        catch
        {
            // 正在写：下次再读。
            return null;
        }
    }

    /// 主会话名：state.json 的 title → lastPrompt（截断）→ 目录名。
    internal static string KimiLabel(KimiStateInfo? info, string cwd, string sid) =>
        info is { Title.Length: > 0 } ? info.Title
        : info is { LastPrompt.Length: > 0 } ? ShortTitle(info.LastPrompt)
        : Fallback(cwd, sid);

    private sealed record KimiSpawn(string Name, string Type, long Time, bool Swarm);

    private sealed record KimiTask(string Name, string Status, long StartedAt, long? EndedAt, bool IsAgent);

    private sealed class KimiWire
    {
        public readonly Dictionary<string, KimiSpawn> Spawned = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Completed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Failed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, KimiTask> Tasks = new(StringComparer.Ordinal);
        public readonly List<(long Enter, long? Exit)> Swarms = new();

        public KimiWire Copy()
        {
            var copy = new KimiWire();
            foreach (var pair in Spawned) copy.Spawned[pair.Key] = pair.Value;
            foreach (var pair in Completed) copy.Completed[pair.Key] = pair.Value;
            foreach (var pair in Failed) copy.Failed[pair.Key] = pair.Value;
            foreach (var pair in Tasks) copy.Tasks[pair.Key] = pair.Value;
            copy.Swarms.AddRange(Swarms);
            return copy;
        }
    }

    /// wire.jsonl 里的 subagent.* / task.* / swarm_mode.* 事件，按关键字增量读。
    private static readonly LineIndex<KimiWire> KimiWires = new(
        ["\"type\":\"subagent.", "\"type\":\"task.", "\"type\":\"swarm_mode."],
        () => new KimiWire(),
        (state, line) =>
        {
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var root = doc.RootElement;
            var time = (long)(Jsonl.GetDouble(root, "time") ?? 0);
            switch (Jsonl.GetString(root, "type"))
            {
                case "subagent.spawned" when Jsonl.GetString(root, "subagentId") is { Length: > 0 } id:
                    state.Spawned[id] = new KimiSpawn(
                        (Jsonl.GetString(root, "description") ?? "").Trim(),
                        Jsonl.GetString(root, "subagentName") ?? "",
                        time,
                        (Jsonl.GetDouble(root, "swarmIndex") ?? 0) > 0);
                    break;
                case "subagent.completed" when Jsonl.GetString(root, "subagentId") is { Length: > 0 } id:
                    state.Completed[id] = time;
                    break;
                case "subagent.failed" when Jsonl.GetString(root, "subagentId") is { Length: > 0 } id:
                    state.Failed[id] = time;
                    break;
                case "task.started" or "task.terminated" when Jsonl.GetObject(root, "info") is { } info
                    && Jsonl.GetString(info, "taskId") is { Length: > 0 } taskId:
                    var ended = Jsonl.GetDouble(info, "endedAt");
                    state.Tasks[taskId] = new KimiTask(
                        (Jsonl.GetString(info, "description") ?? "").Trim(),
                        Jsonl.GetString(info, "status") ?? "",
                        (long)(Jsonl.GetDouble(info, "startedAt") ?? time),
                        ended is { } e and > 0 ? (long)e : null,
                        Jsonl.GetString(info, "kind") == "agent");
                    break;
                case "swarm_mode.enter":
                    state.Swarms.Add((time, null));
                    break;
                case "swarm_mode.exit" when state.Swarms.Count > 0 && state.Swarms[^1].Exit is null:
                    state.Swarms[^1] = (state.Swarms[^1].Enter, time);
                    break;
            }
        });

    private static string KimiWirePath(string sessionDir, string agentId) =>
        Path.Combine(sessionDir, "agents", agentId, "wire.jsonl");

    /// Kimi 会话的任务树：state.json 给出 agent 之间的父子关系，
    /// 主 wire（以及有子 agent 的那些 agent 的 wire）给出名字、开始和结束；
    /// 事件没写的就看 agent 自己 wire 的回合状态。
    internal static (List<TaskNode> Nodes, int Omitted, int OmittedRunning) KimiChildren(
        string sessionDir, KimiStateInfo? info, DateTimeOffset now)
    {
        var all = new List<TaskNode>();
        var notParsed = 0;
        try
        {
            var agents = info?.Agents ?? Array.Empty<KimiAgentRef>();
            var parents = agents.Select(a => a.ParentId).Concat(["main"]).ToHashSet(StringComparer.Ordinal);
            var view = new KimiWire();
            foreach (var owner in parents)
            {
                var wire = KimiWirePath(sessionDir, owner);
                if (!File.Exists(wire)) continue;
                var part = KimiWires.Read(wire, state => state.Copy(), new KimiWire());
                foreach (var pair in part.Spawned) view.Spawned[pair.Key] = pair.Value;
                foreach (var pair in part.Completed) view.Completed[pair.Key] = pair.Value;
                foreach (var pair in part.Failed) view.Failed[pair.Key] = pair.Value;
                if (owner == "main")
                {
                    foreach (var pair in part.Tasks) view.Tasks[pair.Key] = pair.Value;
                    view.Swarms.AddRange(part.Swarms);
                }
            }

            var known = agents.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            // 父 agent 不在 state.json 里的，挂回主对话。
            string ParentOf(KimiAgentRef agent) => known.Contains(agent.ParentId) ? agent.ParentId : "main";
            var byParent = agents.ToLookup(ParentOf, StringComparer.Ordinal);

            // 一层 agent 读成节点。主对话这一层不在这里截断：swarm 要先收拢，后台任务也要一起排。
            (List<TaskNode> Nodes, int Omitted, int OmittedRunning) Build(string parent, int depth, HashSet<string> seen)
            {
                var output = new List<TaskNode>();
                var candidates = byParent[parent].Where(a => !seen.Contains(a.Id)).ToList();
                var windowed = candidates.Where(a => KimiAgentMayShow(sessionDir, a.Id, view, now)).ToList();
                var (toParse, skipped) = NewestToParse(windowed, a => Mtime(KimiWirePath(sessionDir, a.Id)));
                foreach (var agent in toParse) seen.Add(agent.Id);
                foreach (var agent in toParse)
                {
                    var inner = depth < BuildDepth
                        ? Build(agent.Id, depth + 1, seen)
                        : (Nodes: new List<TaskNode>(), Omitted: 0, OmittedRunning: 0);
                    if (KimiAgentNode(sessionDir, agent, view, now) is not { } node) continue;
                    output.Add(node with { Children = inner.Nodes, Omitted = inner.Omitted, OmittedRunning = inner.OmittedRunning });
                }
                if (depth == 1) return (output, skipped, 0);
                var top = TaskTrees.Trim(output, skipped);
                return (top.Kept, top.Omitted, top.OmittedRunning);
            }

            var built = Build("main", 1, new HashSet<string>(StringComparer.Ordinal) { "main" });
            var tops = built.Nodes;
            notParsed += built.Omitted;

            // swarm：swarm_mode 期间派出的那一批 agent 收在一个 swarm 节点下面。
            var swarmAgents = tops.Where(node => agents.First(a => a.Id == node.Id).Swarm
                || view.Spawned.TryGetValue(node.Id, out var sp) && sp.Swarm).ToList();
            if (swarmAgents.Count > 0)
            {
                var periods = view.Swarms.OrderBy(p => p.Enter).ToList();
                var groups = swarmAgents.GroupBy(node =>
                {
                    var at = view.Spawned.TryGetValue(node.Id, out var sp) ? sp.Time : 0;
                    return periods.FindLastIndex(p => p.Enter <= at + 1000);
                });
                foreach (var group in groups)
                {
                    tops.RemoveAll(node => group.Contains(node));
                    var batch = Batch.Of(group.ToList(), 0);
                    var start = group.Key >= 0 ? FromMilliseconds(periods[group.Key].Enter) : group.Min(m => m.StartedAt);
                    all.Add(new TaskNode("swarm-" + (start?.ToUnixTimeMilliseconds() ?? 0), "", TaskNodeKind.Swarm,
                        batch.Waiting ? TaskNodeState.WaitingOnYou : batch.Live ? TaskNodeState.Running : TaskNodeState.Done, start)
                    {
                        UpdatedAt = group.Max(m => m.UpdatedAt),
                        Children = batch.Kept,
                        Omitted = batch.Omitted,
                        OmittedRunning = batch.OmittedRunning,
                    });
                }
            }

            // 后台进程任务（kind=agent 的和 subagent 是同一件事，已经由上面算过）。
            foreach (var (taskId, task) in view.Tasks)
            {
                if (task.IsAgent) continue;
                var started = FromMilliseconds(task.StartedAt);
                var ended = FromMilliseconds(task.EndedAt);
                var running = task.Status == "running";
                if (running && now - (started ?? now) > TimeSpan.FromHours(24)) continue;
                var state = running ? TaskNodeState.Running
                    : task.Status is "failed" or "timed_out" or "lost" ? TaskNodeState.Error
                    : TaskNodeState.Done;
                if (!TaskTrees.Keep(state, ended ?? started, now)) continue;
                all.Add(new TaskNode(taskId, task.Name, TaskNodeKind.Task, state, started) { UpdatedAt = ended ?? started });
            }
            all.AddRange(tops);
        }
        catch (Exception error) when (IsExpansionFault(error))
        {
            // 读不到子记录：留下已经读到的，父会话照常显示。
        }
        var trimmed = TaskTrees.Trim(all, notParsed);
        return (trimmed.Kept, trimmed.Omitted, trimmed.OmittedRunning);
    }

    /// 读它的记录之前先用修改时间和事件筛一遍：完成太久的、放弃了的不读。
    private static bool KimiAgentMayShow(string sessionDir, string id, KimiWire view, DateTimeOffset now)
    {
        if (view.Completed.TryGetValue(id, out var completed)) return TaskTrees.KeepDone(FromMilliseconds(completed), now);
        if (view.Failed.TryGetValue(id, out var failed)) return TaskTrees.Keep(TaskNodeState.Error, FromMilliseconds(failed), now);
        var wire = KimiWirePath(sessionDir, id);
        return File.Exists(wire) && now - Mtime(wire) <= ChildAbandonedAfter;
    }

    private static TaskNode? KimiAgentNode(string sessionDir, KimiAgentRef agent, KimiWire view, DateTimeOffset now)
    {
        var wire = KimiWirePath(sessionDir, agent.Id);
        view.Spawned.TryGetValue(agent.Id, out var spawn);
        var started = FromMilliseconds(spawn?.Time) is { } at ? at : File.Exists(wire) ? Created(wire) : (DateTimeOffset?)null;
        string name = spawn?.Name ?? "";
        string? type = string.IsNullOrEmpty(spawn?.Type) ? null : spawn!.Type;

        TaskNodeState state;
        DateTimeOffset? updated;
        PhaseReading? phase = null;
        if (view.Failed.TryGetValue(agent.Id, out var failed))
        {
            state = TaskNodeState.Error;
            updated = FromMilliseconds(failed);
        }
        else if (view.Completed.TryGetValue(agent.Id, out var completed))
        {
            state = TaskNodeState.Done;
            updated = FromMilliseconds(completed);
        }
        else
        {
            if (!File.Exists(wire)) return null;
            phase = AgentPhaseClassifier.Classify(TriggerTool.Kimi, wire, now);
            var (turn, modified) = ReadTurn(wire, SessionTurnState.Kimi, p => TailLines(p));
            updated = turn.ActivityDate is { } stamp && stamp > modified ? stamp : modified;
            state = NodeStateOf(turn, updated.Value, phase, now);
            // 开着的回合里还有没回答的批准或问题：在等用户。
            if (state == TaskNodeState.Running && KimiInteractions.Pending(TailLines(wire)) is not null)
                state = TaskNodeState.WaitingOnYou;
        }
        if (!TaskTrees.Keep(state, updated, now)) return null;
        return new TaskNode(agent.Id, name, TaskNodeKind.Subagent, state, started)
        {
            AgentType = type,
            Phase = state is TaskNodeState.Done ? null : phase,
            UpdatedAt = updated,
        };
    }

    // MARK: - Codex

    private sealed record CodexChild(string Sid, string ParentId, string Name, string? Role, string Path, DateTimeOffset Modified);

    /// 按 parent_thread_id 把派生出来的线程挂到父线程下。guardian 自动审查
    /// （thread_source = guardian_review）不进树。
    private static void AttachCodexChildren(
        List<ScannedSession> sessions, List<CodexChild> children, DateTimeOffset now)
    {
        if (children.Count == 0) return;
        var byParent = children.GroupBy(child => child.ParentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        (List<TaskNode> Nodes, int Omitted, int OmittedRunning) Build(string parent, int depth, HashSet<string> seen)
        {
            var all = new List<TaskNode>();
            if (!byParent.TryGetValue(parent, out var kids)) return (all, 0, 0);
            var live = kids.Where(kid => !seen.Contains(kid.Sid)).ToList();
            var (toParse, notParsed) = NewestToParse(live, kid => kid.Modified);
            foreach (var kid in toParse) seen.Add(kid.Sid);
            foreach (var kid in toParse)
            {
                var phase = AgentPhaseClassifier.Classify(TriggerTool.Codex, kid.Path, now);
                var (turn, modified) = ReadTurn(kid.Path, SessionTurnState.Codex, p => TailLines(p));
                var updated = turn.ActivityDate is { } at && at > modified ? at : modified;
                var state = NodeStateOf(turn, updated, phase, now);
                if (!TaskTrees.Keep(state, updated, now)) continue;
                var inner = depth < BuildDepth ? Build(kid.Sid, depth + 1, seen) : (Nodes: new List<TaskNode>(), Omitted: 0, OmittedRunning: 0);
                all.Add(new TaskNode(kid.Sid, kid.Name, TaskNodeKind.Subagent, state, Created(kid.Path))
                {
                    AgentType = kid.Role,
                    Phase = state == TaskNodeState.Done ? null : phase,
                    UpdatedAt = updated,
                    Children = inner.Nodes,
                    Omitted = inner.Omitted,
                    OmittedRunning = inner.OmittedRunning,
                });
            }
            var top = TaskTrees.Trim(all, notParsed);
            return (top.Kept, top.Omitted, top.OmittedRunning);
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.Status == ActivityState.Idle || !byParent.ContainsKey(session.SessionId)) continue;
            try
            {
                var (nodes, omitted, omittedRunning) = Build(session.SessionId, 1, new HashSet<string>(StringComparer.Ordinal) { session.SessionId });
                if (nodes.Count == 0 && omitted == 0) continue;
                sessions[i] = session with
                {
                    Children = TaskTrees.Cap(nodes),
                    ChildrenOmitted = omitted,
                    ChildrenOmittedRunning = omittedRunning,
                };
            }
            catch (Exception error) when (IsExpansionFault(error))
            {
                // 子线程读不到：父会话照常显示。
            }
        }
    }
}
