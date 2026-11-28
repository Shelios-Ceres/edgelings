using System.Text;
using System.Text.Json;

namespace AgentIsland.Core;

// Antigravity：~/.gemini 下的会话（三个已知的数据根目录）；运行状态和子任务来自
// conversation_summaries.db 与 brain\ 下的文件。全部只读本机文件，不向应用或它的语言服务器发任何请求。
public static partial class SessionScanner
{
    /// Google has renamed the data directory twice already (1.x
    /// `antigravity`, 2.x `antigravity-ide`, plus the separate
    /// `antigravity-cli` root), so every known variant is probed.
    private static readonly string[] AntigravityRootNames = { "antigravity", "antigravity-ide", "antigravity-cli" };

    /// db 可能比刚发出的提示慢一拍：记录里这么近才写过的开着的回合，不单凭 db 说“已停”就读成停了。
    private static readonly TimeSpan AntigravityDbLag = TimeSpan.FromSeconds(2);

    /// `conversation_summaries` 的一行。旧版 db 没有的列取默认值。
    internal sealed record AntigravityRow(
        string Id, string? Title, string? Workspace, string Status, bool NotFullyIdle, bool Killed,
        string ParentId, string AgentName);

    /// `brain\<父>\.system_generated\subagents\` 下的一个文件。
    internal sealed record AntigravitySub(string Id, string TypeName, string Role, bool Killed);

    private sealed record AntigravityConversation(
        string Id, string TranscriptPath, AntigravityRow? Row, ScannedSession Session, bool Asking);

    /// Antigravity keeps a readable transcript per conversation at
    /// `<root>/brain/<conversation-id>/.system_generated/logs/transcript_full.jsonl`
    /// — the desktop IDE and the `agy` CLI write the same shape into their
    /// own roots. Always `transcript_full`, never `transcript` (truncated).
    /// 步骤做完才写，长命令或慢的模型调用在记录里就是静默：开着的回合算在跑，
    /// 再由 db 的行说这次运行是在跑还是已经停了（AntigravityStatus）。
    /// 父会话起的会话是它的子任务：挂在它下面，不算独立会话，不计入运行数。
    public static List<ScannedSession> ScanAntigravity(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = new List<ScannedSession>();
        var home = Path.Combine(IslandPaths.Home, ".gemini");
        foreach (var rootName in AntigravityRootNames)
        {
            var root = Path.Combine(home, rootName);
            var brain = Path.Combine(root, "brain");
            if (!Directory.Exists(brain)) continue;
            var rows = AntigravityRows(root);
            var directories = SafeEnumerateDirectories(brain);
            var subs = AntigravitySubagents(directories);
            var parentOf = AntigravityParents(rows, subs);
            var conversations = new Dictionary<string, AntigravityConversation>(StringComparer.Ordinal);
            foreach (var conversationDir in directories)
            {
                var conversation = Path.GetFileName(conversationDir);
                var path = Path.Combine(
                    conversationDir, ".system_generated", "logs", "transcript_full.jsonl");
                if (!File.Exists(path)) continue;
                rows.TryGetValue(conversation, out var row);
                var state = SessionState(path, now, lastWorking, null, SessionTurnState.Antigravity);
                var lastCall = AntigravityLastCall(path);
                var (status, asking) = AntigravityStatus(state.Status, row, lastCall, now - state.Modified);
                var cwd = row?.Workspace ?? AntigravityWorkspace(root, conversation) ?? "";
                var label = row?.Title
                    ?? AntigravityTitle(path)
                    ?? (conversation.Length > 8 ? conversation[..8] : conversation);
                var session = new ScannedSession(
                    TriggerTool.Antigravity,
                    conversation,
                    cwd,
                    label,
                    state.Modified,
                    status,
                    path,
                    state.TurnKey,
                    SessionLaunchTarget.Cli)
                {
                    TurnEnded = status == ActivityState.NeedsYou || (status == ActivityState.Idle && state.TurnEnded),
                    // 在问用户：和 Kimi 一样，会话仍算在跑，由 PendingPrompt 说它在等你；
                    // NeedsYou 在这里只表示“做完了”。
                    PendingPrompt = asking ? new PhaseReading(AgentPhase.Asking, lastCall, state.Modified) : null,
                };
                conversations[conversation] = new AntigravityConversation(conversation, path, row, session, asking);
            }
            var childrenOf = parentOf.GroupBy(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            foreach (var conversation in conversations.Values)
            {
                if (parentOf.ContainsKey(conversation.Id)) continue;
                var session = conversation.Session;
                // 父会话停在 swarm_wait 之类的调用上很久没写记录，会被读成 Idle；
                // 它的行还是在跑、而子任务确实在动，就是在跑，否则整棵树连同运行数都会消失。
                if (session.Status == ActivityState.Idle && conversation.Row is { } parentRow && AntigravityRunning(parentRow) == true
                    && AntigravityLiveSince(session.SessionId, childrenOf, conversations, subs, new HashSet<string>(StringComparer.Ordinal) { session.SessionId }) is { } since)
                {
                    session = session with { Status = ActivityState.Working, Modified = since > session.Modified ? since : session.Modified, TurnEnded = false };
                }
                if (TaskTreesEnabled && session.Status != ActivityState.Idle && childrenOf.ContainsKey(session.SessionId))
                {
                    try
                    {
                        var (nodes, omitted, omittedRunning) = AntigravityChildren(
                            session.SessionId, 1, new HashSet<string>(StringComparer.Ordinal) { session.SessionId },
                            childrenOf, conversations, subs, now);
                        if (nodes.Count > 0 || omitted > 0)
                        {
                            session = session with
                            {
                                Children = TaskTrees.Cap(nodes),
                                ChildrenOmitted = omitted,
                                ChildrenOmittedRunning = omittedRunning,
                            };
                        }
                    }
                    catch (Exception error) when (IsExpansionFault(error))
                    {
                        // 子任务读不到：父会话照常显示。
                    }
                }
                output.Add(session);
            }
        }
        return output;
    }

    /// 谁是谁的子会话：取 db 行里的父会话，或列出它的 `subagents\` 所在的会话（两处取并集）。
    /// db 不一定读得到，而子任务无论如何不能当成独立会话。
    internal static Dictionary<string, string> AntigravityParents(
        IReadOnlyDictionary<string, AntigravityRow> rows, IReadOnlyDictionary<string, List<AntigravitySub>> subs)
    {
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (parent, list) in subs)
        {
            foreach (var sub in list)
            {
                if (sub.Id != parent) parentOf[sub.Id] = parent;
            }
        }
        foreach (var row in rows.Values)
        {
            if (row.ParentId.Length > 0 && row.ParentId != row.Id) parentOf[row.Id] = row.ParentId;
        }
        return parentOf;
    }

    /// 子孙里在跑的，最近一次动静的时间；没有就是 null。（killed 的不算在跑。）
    private static DateTimeOffset? AntigravityLiveSince(
        string parent, Dictionary<string, List<string>> childrenOf,
        Dictionary<string, AntigravityConversation> conversations,
        IReadOnlyDictionary<string, List<AntigravitySub>> subs, HashSet<string> seen)
    {
        DateTimeOffset? newest = null;
        if (!childrenOf.TryGetValue(parent, out var ids)) return null;
        foreach (var id in ids)
        {
            if (!seen.Add(id)) continue;
            var sub = subs.TryGetValue(parent, out var siblings) ? siblings.FirstOrDefault(s => s.Id == id) : null;
            if (conversations.TryGetValue(id, out var kid) && kid.Session.Status == ActivityState.Working
                && kid.Row?.Killed != true && sub?.Killed != true)
            {
                newest = newest is { } known && known > kid.Session.Modified ? known : kid.Session.Modified;
            }
            if (AntigravityLiveSince(id, childrenOf, conversations, subs, seen) is { } deeper && (newest is not { } now || deeper > now)) newest = deeper;
        }
        return newest;
    }

    private static (List<TaskNode> Nodes, int Omitted, int OmittedRunning) AntigravityChildren(
        string parent, int depth, HashSet<string> seen,
        Dictionary<string, List<string>> childrenOf,
        Dictionary<string, AntigravityConversation> conversations,
        IReadOnlyDictionary<string, List<AntigravitySub>> subs,
        DateTimeOffset now)
    {
        var all = new List<TaskNode>();
        if (!childrenOf.TryGetValue(parent, out var ids)) return (all, 0, 0);
        var kids = ids.Where(id => !seen.Contains(id) && conversations.ContainsKey(id)).Select(id => conversations[id]).ToList();
        var (toParse, notParsed) = NewestToParse(kids, kid => kid.Session.Modified);
        foreach (var kid in toParse) seen.Add(kid.Id);
        foreach (var kid in toParse)
        {
            var sub = subs.TryGetValue(parent, out var siblings) ? siblings.FirstOrDefault(s => s.Id == kid.Id) : null;
            var killed = kid.Row?.Killed == true || sub?.Killed == true;
            var state = killed ? TaskNodeState.Done : kid.Session.Status switch
            {
                ActivityState.Working when kid.Asking => TaskNodeState.WaitingOnYou,
                ActivityState.Working => TaskNodeState.Running,
                _ => TaskNodeState.Done,
            };
            if (!TaskTrees.Keep(state, kid.Session.Modified, now)) continue;
            var inner = depth < BuildDepth
                ? AntigravityChildren(kid.Id, depth + 1, seen, childrenOf, conversations, subs, now)
                : (Nodes: new List<TaskNode>(), Omitted: 0, OmittedRunning: 0);
            var name = NonBlank(SafeTitle(kid.Row?.AgentName)) ?? NonBlank(SafeTitle(sub?.TypeName))
                ?? NonBlank(SafeTitle(kid.Row?.Title)) ?? (kid.Id.Length > 8 ? kid.Id[..8] : kid.Id);
            all.Add(new TaskNode(kid.Id, name, TaskNodeKind.Subagent, state, Created(kid.TranscriptPath))
            {
                AgentType = NonBlank(SafeTitle(sub?.Role)),
                Phase = state == TaskNodeState.Done ? null : AgentPhaseClassifier.Classify(TriggerTool.Antigravity, kid.TranscriptPath, now),
                UpdatedAt = kid.Session.Modified,
                Children = inner.Nodes,
                Omitted = inner.Omitted,
                OmittedRunning = inner.OmittedRunning,
            });
        }
        var top = TaskTrees.Trim(all, notParsed);
        return (top.Kept, top.Omitted, top.OmittedRunning);
    }

    /// 一个会话此刻在做什么：由记录自己的读法加 db 的行得出。
    /// - 在跑（RUNNING，或 not_fully_idle）：不管记录多安静都算在跑；但记录老到已被放弃（超过注意窗口）时
    ///   仍读成 Idle，因为应用崩溃会把行留在原样。
    /// - IDLE 就是这一轮结束了：记录读成 NeedsYou（有回答）是做完了；最后一步调的是 ask_question 或
    ///   exit_plan_mode 是在问用户，这时仍是 Working、Asking 为 true（和其他 agent 的提示一样，
    ///   NeedsYou 只表示一轮做完）；其余情况（取消、出错停下）不欠用户什么，安静成 Idle。
    /// - 等权限审批认不出来（库里仍是 RUNNING，记录里也没有标记），当作在跑。
    /// - 没有行，或状态没见过：沿用记录的读法。
    internal static (ActivityState Status, bool Asking) AntigravityStatus(
        ActivityState fromTranscript, AntigravityRow? row, string? lastCall, TimeSpan quiet)
    {
        if (row is null || AntigravityRunning(row) is not { } running) return (fromTranscript, false);
        if (running) return (fromTranscript == ActivityState.Idle ? ActivityState.Idle : ActivityState.Working, false);
        if (fromTranscript != ActivityState.Working) return (fromTranscript, false);
        if (lastCall is "ask_question" or "exit_plan_mode") return (ActivityState.Working, true);
        return (quiet < AntigravityDbLag ? ActivityState.Working : ActivityState.Idle, false);
    }

    /// null：这一行没说它在不在跑。
    private static bool? AntigravityRunning(AntigravityRow row)
    {
        if (row.NotFullyIdle) return true;
        if (row.Status.EndsWith("_RUNNING", StringComparison.Ordinal) || row.Status.EndsWith("_BUSY", StringComparison.Ordinal)
            || row.Status.EndsWith("_CANCELING", StringComparison.Ordinal)) return true;
        return row.Status.EndsWith("_IDLE", StringComparison.Ordinal) ? false : null;
    }

    private static readonly ScanCache<(string Stamp, string? Call)> AntigravityCalls = new();

    /// 记录里最新一步是模型发起的工具调用时，返回最后调的那个工具名；否则 null
    /// （后面跟了结果、提示或回答，说明调用已被接走）。
    internal static string? AntigravityLastCall(string path)
    {
        var stamp = FileStamp(path);
        if (stamp.Length == 0) return null;
        if (AntigravityCalls.TryGet(path, out var known) && known.Stamp == stamp) return known.Call;
        string? call = null;
        try
        {
            var lines = TailLines(path);
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                using var doc = Jsonl.TryParseLine(lines[i]);
                if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var root = doc.RootElement;
                var source = Jsonl.GetString(root, "source");
                if (source is not ("MODEL" or "USER_EXPLICIT")) continue;
                if (source == "MODEL" && Jsonl.GetString(root, "type") == "PLANNER_RESPONSE"
                    && root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in calls.EnumerateArray())
                    {
                        if (entry.ValueKind == JsonValueKind.Object && Jsonl.GetString(entry, "name") is { Length: > 0 } name) call = name;
                    }
                }
                break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        AntigravityCalls.Set(path, (stamp, call));
        return call;
    }

    // MARK: - brain\<conversation>\.system_generated\subagents\*.json

    private static readonly ScanCache<(string Stamp, AntigravitySub? Sub)> AntigravitySubFiles = new();

    /// 各父会话的 subagent，按父会话 id 分组。没有 `subagents\` 目录的会话只花一次存在性检查。
    internal static Dictionary<string, List<AntigravitySub>> AntigravitySubagents(IEnumerable<string> conversationDirs)
    {
        var output = new Dictionary<string, List<AntigravitySub>>(StringComparer.Ordinal);
        foreach (var directory in conversationDirs)
        {
            var folder = Path.Combine(directory, ".system_generated", "subagents");
            string[] files;
            try
            {
                if (!Directory.Exists(folder)) continue;
                files = Directory.GetFiles(folder, "*.json");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var list = new List<AntigravitySub>();
            foreach (var file in files)
            {
                if (AntigravitySubOf(file) is { } sub) list.Add(sub);
            }
            if (list.Count > 0) output[Path.GetFileName(directory)] = list;
        }
        return output;
    }

    /// {conversationId, state: SUBAGENT_STATE_ALIVE | SUBAGENT_STATE_KILLED, subagentDescriptor: {typeName, role}, …}
    private static AntigravitySub? AntigravitySubOf(string path)
    {
        var stamp = FileStamp(path);
        if (stamp.Length == 0) return null;
        if (AntigravitySubFiles.TryGet(path, out var known) && known.Stamp == stamp) return known.Sub;
        try
        {
            using var stream = SharedFile.Open(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Jsonl.GetString(root, "conversationId") is not { Length: > 0 } id) return null;
            var descriptor = Jsonl.GetObject(root, "subagentDescriptor");
            var sub = new AntigravitySub(
                id,
                descriptor is { } named ? Jsonl.GetString(named, "typeName") ?? "" : "",
                descriptor is { } roled ? Jsonl.GetString(roled, "role") ?? "" : "",
                (Jsonl.GetString(root, "state") ?? "").EndsWith("_KILLED", StringComparison.Ordinal));
            AntigravitySubFiles.Set(path, (stamp, sub));
            return sub;
        }
        catch
        {
            // 正在写：下次扫描再读。
            return null;
        }
    }

    // MARK: - conversation_summaries.db

    /// `conversation_summaries.db` 每个会话一行，是 Antigravity 放在 `brain/` 旁边的 SQLite 索引。
    /// 只读、共享方式打开，不加锁、不写，也不留连接池（Pooling=False，用完即放开文件句柄）。
    /// db 是 WAL 模式：应用持有 db 时普通只读打开能读到 WAL 里的新行，但会碰 -shm（改它的修改时间）；
    /// 应用关了时普通只读打开还可能新建 -shm 和 -wal。所以按“这个数据目录对应的程序在不在跑”选打开方式：
    /// 在跑 → 只读共享打开；没在跑 → immutable URI（完全不看 WAL，不碰任何文件）。
    /// 不能看 -shm 是否存在：程序异常退出会留下旧 -shm，用它判断会每次扫描都去碰它。
    /// immutable 读不到 -wal 里还没合并的行，可以接受：程序下次运行会自己合并，
    /// 此时标题和状态顶多旧一点（会话记录自己的时间会让它读成 Idle）。
    /// 对应程序还判断不了（第一次进程扫描前、读不到进程表）时，退回旧办法：有 -shm 就共享打开。
    /// 完全读不了的 db 得到空结果，只按各会话记录判断。
    internal static Dictionary<string, AntigravityRow> AntigravityRows(string root)
    {
        var dbPath = Path.Combine(root, "conversation_summaries.db");
        if (!File.Exists(dbPath)) return new(StringComparer.Ordinal);
        var immutable = $"Data Source=file:{Uri.EscapeDataString(dbPath).Replace("%5C", "/").Replace("%3A", ":")}?immutable=1;Mode=ReadOnly;Pooling=False";
        var shared = $"Data Source={dbPath};Mode=ReadOnly;Pooling=False";
        var hasShm = File.Exists(dbPath + "-shm");
        var running = (AntigravityProgramRunning(Path.GetFileName(root)) ?? true) && hasShm;
        if (!running) return AntigravityRowsAt(immutable) ?? new(StringComparer.Ordinal);
        return AntigravityRowsAt(shared) ?? AntigravityRowsAt(immutable) ?? new(StringComparer.Ordinal);
    }

    /// 数据目录名 → 对应程序在不在跑；null 表示判断不了。测试缝：测试里换成固定答案。
    /// antigravity 和 antigravity-ide 是桌面应用（改过名的两代目录），antigravity-cli 是 agy CLI。
    /// 用的是进程扫描已经防抖好的结果（AgentSurfaces.Shared），这里不枚举进程。
    internal static Func<string, bool?> AntigravityProgramRunning { get; set; } = rootName =>
    {
        var tracker = AgentSurfaces.Shared;
        return ProgramRunningFor(rootName, tracker.Seen, tracker.Unknown, tracker.Current);
    };

    internal static bool? ProgramRunningFor(
        string rootName, bool seen, IReadOnlySet<AgentSurface> unknown, IReadOnlySet<AgentSurface> current)
    {
        var surface = rootName == "antigravity-cli" ? AgentSurface.AgyCli : AgentSurface.AntigravityApp;
        if (!seen || unknown.Contains(surface)) return null;
        return current.Contains(surface);
    }

    private const string AntigravityWideQuery =
        "SELECT conversation_id, title, preview, workspace_uris, status, not_fully_idle, killed, parent_conversation_id, agent_name "
        + "FROM conversation_summaries";

    private const string AntigravityNarrowQuery =
        "SELECT conversation_id, title, preview, workspace_uris FROM conversation_summaries";

    /// null 表示这种打开方式失败了，调用方换另一种；空字典表示表里确实没有行。
    /// 没有较新列的旧版 db 仍然给出标题。
    private static Dictionary<string, AntigravityRow>? AntigravityRowsAt(string connectionString)
    {
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            try
            {
                return ReadAntigravityRows(connection, AntigravityWideQuery, wide: true);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return ReadAntigravityRows(connection, AntigravityNarrowQuery, wide: false);
            }
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, AntigravityRow> ReadAntigravityRows(
        Microsoft.Data.Sqlite.SqliteConnection connection, string query, bool wide)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 2;
        using var reader = command.ExecuteReader();
        var output = new Dictionary<string, AntigravityRow>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = Text(reader, 0);
            if (string.IsNullOrEmpty(id)) continue;
            // title is usually blank; preview carries the generated name.
            var title = NonEmpty(Text(reader, 1)) ?? NonEmpty(Text(reader, 2));
            if (title is { Length: > 48 }) title = title[..48];
            var workspace = NonEmpty(Text(reader, 3)) is { } uris ? AntigravityWorkspaceUri(uris) : null;
            output[id] = wide
                ? new AntigravityRow(id, title, workspace, Text(reader, 4) ?? "", Truthy(reader, 5), Truthy(reader, 6),
                    Text(reader, 7) ?? "", Text(reader, 8) ?? "")
                : new AntigravityRow(id, title, workspace, "", false, false, "", "");
        }
        return output;

        static string? Text(Microsoft.Data.Sqlite.SqliteDataReader reader, int column) =>
            reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture);

        // 标志列存的是数字；文本的 "true" 也认。
        static bool Truthy(Microsoft.Data.Sqlite.SqliteDataReader reader, int column) =>
            !reader.IsDBNull(column) && reader.GetValue(column) switch
            {
                long number => number != 0,
                double number => number != 0,
                string text => text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1",
                _ => false,
            };

        static string? NonEmpty(string? raw)
        {
            var trimmed = raw?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
    }

    /// `workspace_uris` is a JSON array of file:// URIs; the first one is
    /// the session's directory. Empty for sessions started outside a
    /// project.
    private static string? AntigravityWorkspaceUri(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var text = entry.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile)
                {
                    return uri.LocalPath;
                }
                return text;
            }
        }
        catch
        {
            // Malformed JSON only costs the cwd nicety.
        }
        return null;
    }

    private static readonly ScanCache<string> AntigravityTitles = new();

    /// The first user message, unwrapped from the `<USER_REQUEST>` envelope
    /// the CLI writes — the fallback when the summaries table has no row
    /// yet (it is written asynchronously). The transcript only grows, so a
    /// title once found holds.
    internal static string? AntigravityTitle(string path)
    {
        if (AntigravityTitles.TryGet(path, out var known)) return known;
        string? title = null;
        try
        {
            using var stream = SharedFile.Open(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            for (var i = 0; i < 40 && reader.ReadLine() is { } line; i++)
            {
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                if (Jsonl.GetString(doc.RootElement, "type") != "USER_INPUT") continue;
                if (Jsonl.GetString(doc.RootElement, "content") is not { Length: > 0 } content) continue;
                title = AntigravityRequestText(content);
                break;
            }
        }
        catch
        {
            // Unreadable transcript — the id-prefix fallback covers it.
        }
        if (title is null) return null;
        AntigravityTitles.Set(path, title);
        return title;
    }

    internal static string? AntigravityRequestText(string content)
    {
        var body = content;
        var start = content.IndexOf("<USER_REQUEST>", StringComparison.Ordinal);
        var end = content.IndexOf("</USER_REQUEST>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            body = content[(start + "<USER_REQUEST>".Length)..end];
        }
        var first = body.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (first.Length == 0) return null;
        return first.Length > 48 ? first[..48] : first;
    }

    private static readonly ScanCache<(string Stamp, Dictionary<string, string> Workspaces)> AntigravityHistories = new();

    /// `history.jsonl` maps conversationId to its workspace. It is read
    /// whole once per change, not once per conversation on every scan, and
    /// through a handle that lets agy keep writing it.
    internal static string? AntigravityWorkspace(string root, string conversation)
    {
        var historyPath = Path.Combine(root, "history.jsonl");
        var stamp = FileStamp(historyPath);
        if (stamp.Length == 0) return null;
        Dictionary<string, string> workspaces;
        if (AntigravityHistories.TryGet(historyPath, out var known) && known.Stamp == stamp)
        {
            workspaces = known.Workspaces;
        }
        else
        {
            // An unreadable history only costs the cwd nicety, and is read again next scan.
            if (ReadAntigravityHistory(historyPath) is not { } read) return null;
            workspaces = read;
            AntigravityHistories.Set(historyPath, (stamp, workspaces));
        }
        return workspaces.TryGetValue(conversation, out var workspace) ? workspace : null;
    }

    private static Dictionary<string, string>? ReadAntigravityHistory(string historyPath)
    {
        var workspaces = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = SharedFile.Open(historyPath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                if (Jsonl.GetString(doc.RootElement, "conversationId") is not { Length: > 0 } conversation) continue;
                if (Jsonl.GetString(doc.RootElement, "workspace") is { Length: > 0 } workspace)
                {
                    workspaces.TryAdd(conversation, workspace);
                }
            }
        }
        catch
        {
            return null;
        }
        return workspaces;
    }
}
