using System.Globalization;
using System.Text.Json.Nodes;
using AgentIsland.Core;
using AgentIsland.Model;
using Microsoft.Data.Sqlite;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Antigravity 的会话和子任务只从本机文件读：
/// 临时 home 里放一个按真实结构建的小 conversation_summaries.db，
/// 加上假的 transcript 和 subagent 文件。
public static class AntigravityWatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Parent = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string ChildA = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string ChildB = "cccccccc-0000-0000-0000-000000000003";

    private const string Running = "CASCADE_RUN_STATUS_RUNNING";
    private const string Idle = "CASCADE_RUN_STATUS_IDLE";

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a run in progress is work, unless the transcript is too old to be believed", StatusRunning),
            ("not_fully_idle counts as running even when the status reads idle", StatusNotFullyIdle),
            ("an idle run: answered is done, asking is yours, anything else is quiet", StatusIdle),
            ("no row or an unknown status leaves the transcript's reading", StatusUnknown),
            ("a prompt just sent is not undone by a db that has not caught up", StatusDbLag),
            ("rows, subagent files and the parent map are read from the files", ReadRowsAndSubagents),
            ("subagents hang under their parent and do not count as sessions or runs", ChildrenHangUnderParent),
            ("a question that ended the turn is a waiting prompt, an answer is a done turn", AskEndsTurn),
            ("a parent quiet behind its working subagents is still working", ParentBehindChildren),
            ("a cancelled run goes quiet and a stale RUNNING row after a crash does not keep it busy", CancelAndStale),
            ("a killed subagent is done, not an error", KilledChildIsDone),
            ("a killed subagent is done even when its row still says running", KilledOverRunningRow),
            ("a subagent file marked killed makes it done when the db cannot be read", KilledByFileOnly),
            ("an unreadable db leaves the transcripts and still hides subagents", UnreadableDb),
            ("a db without the newer columns still gives titles and reads by transcript", OlderDb),
            ("the db is opened for reading only and left unchanged", ReadOnlyOpen),
            ("a closed WAL db is read without creating -shm, -wal or -journal", ClosedWalDbLeavesNoFiles),
            ("a db another connection holds open in WAL mode is read, with its newest rows", HeldWalDb),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS antigravity watch: {name}");
        }
        Console.WriteLine("AntigravityWatchTests GREEN");
    }

    // MARK: - 状态映射

    private static SessionScanner.AntigravityRow Row(string status, bool notFullyIdle = false) =>
        new("x", null, null, status, notFullyIdle, false, "", "");

    private static ActivityState Map(ActivityState transcript, SessionScanner.AntigravityRow? row, string? call = null, double quiet = 60) =>
        SessionScanner.AntigravityStatus(transcript, row, call, TimeSpan.FromSeconds(quiet)).Status;

    private static void StatusRunning()
    {
        Expect(Map(ActivityState.Working, Row(Running)), ActivityState.Working);
        Expect(Map(ActivityState.NeedsYou, Row(Running)), ActivityState.Working);
        // 超过注意窗口，记录读成 Idle，没人更新的 RUNNING 行不能把它拉回来。
        Expect(Map(ActivityState.Idle, Row(Running), quiet: 3600), ActivityState.Idle);
    }

    private static void StatusNotFullyIdle() =>
        Expect(Map(ActivityState.Working, Row(Idle, notFullyIdle: true)), ActivityState.Working);

    private static void StatusIdle()
    {
        Expect(SessionScanner.AntigravityStatus(ActivityState.NeedsYou, Row(Idle), null, TimeSpan.FromSeconds(30)),
            (ActivityState.NeedsYou, false));
        // 在问你：仍是 Working，由 Asking 说它在等用户（NeedsYou 只表示做完了）。
        Expect(SessionScanner.AntigravityStatus(ActivityState.Working, Row(Idle), "ask_question", TimeSpan.FromSeconds(30)),
            (ActivityState.Working, true));
        Expect(SessionScanner.AntigravityStatus(ActivityState.Working, Row(Idle), "exit_plan_mode", TimeSpan.FromSeconds(30)),
            (ActivityState.Working, true));
        // 取消或出错停下：最后一步是普通的工具调用。
        Expect(Map(ActivityState.Working, Row(Idle), "run_command"), ActivityState.Idle);
        Expect(Map(ActivityState.Working, Row(Idle), null), ActivityState.Idle);
        Expect(Map(ActivityState.Idle, Row(Idle)), ActivityState.Idle);
        // 问题放到会话被放弃（超过注意窗口）还没人答：transcript 读成 Idle，不再问。
        Expect(SessionScanner.AntigravityStatus(ActivityState.Idle, Row(Idle), "ask_question", TimeSpan.FromMinutes(40)),
            (ActivityState.Idle, false));
    }

    private static void StatusUnknown()
    {
        Expect(Map(ActivityState.Working, null), ActivityState.Working);
        Expect(Map(ActivityState.NeedsYou, null), ActivityState.NeedsYou);
        Expect(Map(ActivityState.Working, Row("")), ActivityState.Working);
        Expect(Map(ActivityState.Working, Row("CASCADE_RUN_STATUS_SOMETHING_NEW")), ActivityState.Working);
    }

    private static void StatusDbLag()
    {
        Expect(Map(ActivityState.Working, Row(Idle), "run_command", quiet: 0.5), ActivityState.Working);
        Expect(Map(ActivityState.Working, Row(Idle), "run_command", quiet: 3), ActivityState.Idle);
    }

    // MARK: - 文件

    private sealed class World : IDisposable
    {
        private readonly string? _previous = IslandPaths.RootOverride;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "limen-agy-" + Guid.NewGuid().ToString("N"));
        public string Agy => Path.Combine(Root, "home", ".gemini", "antigravity");
        public string Db => Path.Combine(Agy, "conversation_summaries.db");

        public World()
        {
            IslandPaths.RootOverride = Root;
            Directory.CreateDirectory(Agy);
            SessionScanner.ClearTurnCache();
        }

        public string Transcript(string id, params string[] steps)
        {
            var path = Path.Combine(Agy, "brain", id, ".system_generated", "logs", "transcript_full.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, steps);
            return path;
        }

        public void Subagent(string parent, string child, string state, string typeName = "builder")
        {
            var folder = Path.Combine(Agy, "brain", parent, ".system_generated", "subagents");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, child + ".json"), new JsonObject
            {
                ["conversationId"] = child,
                ["subagentDescriptor"] = new JsonObject { ["typeName"] = typeName, ["role"] = "worker" },
                ["state"] = state,
                ["spawnStepIndex"] = 3,
                ["workspaceUris"] = new JsonArray(),
            }.ToJsonString());
        }

        /// 按真实结构建的 db：有扫描要读的列，也有它不读的列。
        public void CreateDb(params (string Id, string Status, int NotFullyIdle, int Killed, string Parent, string AgentName)[] rows)
        {
            using var connection = Open(Db);
            Sql(connection, "CREATE TABLE conversation_summaries (conversation_id text, title text NOT NULL DEFAULT '', preview text NOT NULL DEFAULT '', "
                + "step_count integer NOT NULL DEFAULT 0, last_modified_time datetime NOT NULL, workspace_uris text NOT NULL, status text NOT NULL DEFAULT '', "
                + "agent_name text NOT NULL DEFAULT '', parent_conversation_id text NOT NULL DEFAULT '', nesting_depth integer NOT NULL DEFAULT 0, "
                + "not_fully_idle numeric NOT NULL DEFAULT false, killed numeric NOT NULL DEFAULT false, raw_summary blob, PRIMARY KEY (conversation_id))");
            foreach (var row in rows)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO conversation_summaries (conversation_id, title, preview, last_modified_time, workspace_uris, status, "
                    + "agent_name, parent_conversation_id, not_fully_idle, killed) VALUES ($id, $title, '', '0001-01-01 00:00:00+00:00', '[]', $status, "
                    + "$agent, $parent, $idle, $killed)";
                command.Parameters.AddWithValue("$id", row.Id);
                command.Parameters.AddWithValue("$title", "task " + row.Id[..8]);
                command.Parameters.AddWithValue("$status", row.Status);
                command.Parameters.AddWithValue("$agent", row.AgentName);
                command.Parameters.AddWithValue("$parent", row.Parent);
                command.Parameters.AddWithValue("$idle", row.NotFullyIdle);
                command.Parameters.AddWithValue("$killed", row.Killed);
                command.ExecuteNonQuery();
            }
        }

        public void CreateOlderDb(string id, string title)
        {
            using var connection = Open(Db);
            Sql(connection, "CREATE TABLE conversation_summaries (conversation_id text, title text NOT NULL DEFAULT '', preview text NOT NULL DEFAULT '', "
                + "workspace_uris text NOT NULL, PRIMARY KEY (conversation_id))");
            Sql(connection, $"INSERT INTO conversation_summaries VALUES ('{id}', '{title}', '', '[]')");
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        private static void Sql(SqliteConnection connection, string text)
        {
            using var command = connection.CreateCommand();
            command.CommandText = text;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            IslandPaths.RootOverride = _previous;
            SqliteConnection.ClearAllPools();
            SessionScanner.ClearTurnCache();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static string Step(string source, string type, int ago, string[]? calls = null, string? content = null)
    {
        var step = new JsonObject
        {
            ["step_index"] = 1,
            ["source"] = source,
            ["type"] = type,
            ["status"] = "DONE",
            ["created_at"] = Now.AddSeconds(-ago).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        };
        if (content is not null) step["content"] = content;
        if (calls is not null)
        {
            step["tool_calls"] = new JsonArray(calls.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["args"] = new JsonObject() }).ToArray());
        }
        return step.ToJsonString();
    }

    /// 一条提示加一步模型调用 `tool`，最后一步在 30 秒前。
    private static string[] Calling(string tool) =>
        [Step("USER_EXPLICIT", "USER_INPUT", 120, content: "do it"), Step("MODEL", "PLANNER_RESPONSE", 30, calls: [tool])];

    private static List<ScannedSession> Scan() =>
        SessionScanner.ScanAntigravity(Now, new Dictionary<string, DateTimeOffset>());

    private static void ReadRowsAndSubagents()
    {
        using var world = new World();
        world.CreateDb((Parent, Running, 1, 0, "", ""), (ChildA, Idle, 0, 1, Parent, "builder-one"), ("dddddddd-0000-0000-0000-000000000004", Idle, 0, 0, "", ""));
        world.Subagent(Parent, ChildA, "SUBAGENT_STATE_KILLED");
        var rows = SessionScanner.AntigravityRows(world.Agy);
        Expect(rows.Count, 3);
        Expect(rows[Parent].NotFullyIdle, true);
        Expect(rows[Parent].Status, Running);
        Expect(rows[ChildA].Killed, true);
        Expect(rows[ChildA].ParentId, Parent);
        Expect(rows[ChildA].AgentName, "builder-one");
        Expect(rows[Parent].Title, "task aaaaaaaa");
        var subs = SessionScanner.AntigravitySubagents(Directory.GetDirectories(Path.Combine(world.Agy, "brain")));
        Expect(subs[Parent].Count, 1);
        Expect(subs[Parent][0].Id, ChildA);
        Expect(subs[Parent][0].Killed, true);
        Expect(subs[Parent][0].TypeName, "builder");
        var parents = SessionScanner.AntigravityParents(rows, subs);
        Expect(parents.Count, 1);
        Expect(parents[ChildA], Parent);
        // 只用其中一个来源，得到同一张父子表。
        Expect(SessionScanner.AntigravityParents(new Dictionary<string, SessionScanner.AntigravityRow>(), subs)[ChildA], Parent);
        Expect(SessionScanner.AntigravityParents(rows, new Dictionary<string, List<SessionScanner.AntigravitySub>>())[ChildA], Parent);
    }

    private static void ChildrenHangUnderParent()
    {
        using var world = new World();
        world.CreateDb((Parent, Running, 1, 0, "", ""), (ChildA, Running, 1, 0, Parent, "builder-one"), (ChildB, Idle, 0, 0, Parent, "reviewer"));
        world.Transcript(Parent, Calling("swarm_wait"));
        world.Transcript(ChildA, Calling("run_command"));
        world.Transcript(ChildB, [Step("USER_EXPLICIT", "USER_INPUT", 200, content: "go"), Step("MODEL", "PLANNER_RESPONSE", 60, content: "Done.")]);
        world.Subagent(Parent, ChildA, "SUBAGENT_STATE_ALIVE");
        world.Subagent(Parent, ChildB, "SUBAGENT_STATE_ALIVE", typeName: "reviewer");
        var sessions = Scan();
        Expect(sessions.Count, 1);
        Expect(sessions[0].SessionId, Parent);
        Expect(sessions[0].Status, ActivityState.Working);
        Expect(sessions.Count(s => s.Status == ActivityState.Working), 1);
        var children = sessions[0].Children;
        Expect(children.Count, 2);
        var running = children.Single(node => node.Id == ChildA);
        Expect(running.State, TaskNodeState.Running);
        Expect(running.Name, "builder-one");
        Expect(running.Kind, TaskNodeKind.Subagent);
        Expect(running.AgentType, "worker");
        Expect(running.Phase is not null, "a running subagent says what it is doing");
        Expect(children.Single(node => node.Id == ChildB).State, TaskNodeState.Done);
        Expect(children.Single(node => node.Id == ChildB).Name, "reviewer");
    }

    private static void AskEndsTurn()
    {
        using var world = new World();
        const string Answered = "dddddddd-0000-0000-0000-000000000004";
        world.CreateDb((Parent, Idle, 0, 0, "", ""), (ChildA, Running, 1, 0, "", ""), (Answered, Idle, 0, 0, "", ""));
        var ask = world.Transcript(Parent, Calling("ask_question"));
        world.Transcript(ChildA, Calling("run_command"));
        world.Transcript(Answered, [Step("USER_EXPLICIT", "USER_INPUT", 200, content: "go"), Step("MODEL", "PLANNER_RESPONSE", 60, content: "Done.")]);
        var sessions = Scan().ToDictionary(s => s.SessionId);
        // 在问：Working 加 PendingPrompt(Asking)；做完：NeedsYou，没有 PendingPrompt。
        Expect(sessions[Parent].Status, ActivityState.Working);
        Expect(sessions[Parent].PendingPrompt?.Phase, (AgentPhase?)AgentPhase.Asking);
        Expect(sessions[Parent].TurnEnded, false);
        Expect(sessions[ChildA].Status, ActivityState.Working);
        Expect(sessions[ChildA].PendingPrompt is null, "a run in progress is not asking");
        Expect(sessions[Answered].Status, ActivityState.NeedsYou);
        Expect(sessions[Answered].PendingPrompt is null, "an answer is not a question");
        Expect(sessions[Answered].TurnEnded, true);
        // 到显示的那一层：提问读成提问事件，做完读成完成事件。
        var detector = new SessionEventDetector((_, _, _) => null, () => []);
        var waiting = detector.Observe(sessions.Values, Now).Waiting.ToDictionary(item => item.SessionId, item => item.Kind);
        Expect(waiting[Parent], SessionEventKind.Question);
        Expect(waiting[Answered], SessionEventKind.Done);
        Expect(!waiting.ContainsKey(ChildA), "a working run is not waiting");
        var rows = StatusTaskRows.Build(sessions.Values, new Dictionary<string, PhaseReading>(), Now, pixel: false)
            .Where(row => row.IsMain).ToDictionary(row => row.Id);
        Expect(rows[sessions[Parent].Id].State, StatusTaskState.NeedsYou);
        Expect(rows[sessions[Answered].Id].State, StatusTaskState.NeedsYou);
        Expect(rows[sessions[Parent].Id].Word != rows[sessions[Answered].Id].Word, "asking and done read differently");
        Expect(rows[sessions[ChildA].Id].State, StatusTaskState.Running);
        // 问题 25 分钟没人答仍在问（还在注意窗口内）；过了窗口，会话被放弃，不再问。
        var later = SessionScanner.ScanAntigravity(Now.AddMinutes(25), new Dictionary<string, DateTimeOffset>()).Single(s => s.SessionId == Parent);
        Expect(later.PendingPrompt?.Phase, (AgentPhase?)AgentPhase.Asking);
        var gone = SessionScanner.ScanAntigravity(Now.AddMinutes(40), new Dictionary<string, DateTimeOffset>()).Single(s => s.SessionId == Parent);
        Expect(gone.Status, ActivityState.Idle);
        Expect(gone.PendingPrompt is null, "an abandoned question is not asked again");
        // 用户答了：结果步骤跟在调用后面，调用已被接走。
        world.Transcript(Parent, [.. Calling("ask_question"), Step("MODEL", "GENERIC", 10)]);
        Expect(SessionScanner.AntigravityLastCall(ask), (string?)null);
    }

    private static void ParentBehindChildren()
    {
        using var world = new World();
        world.CreateDb((Parent, Running, 1, 0, "", ""), (ChildA, Running, 1, 0, Parent, "builder-one"));
        // 父会话停在 swarm_wait 一个多小时没写记录；子任务 30 秒前还有步骤。
        world.Transcript(Parent, [Step("USER_EXPLICIT", "USER_INPUT", 4000, content: "go"), Step("MODEL", "PLANNER_RESPONSE", 3700, calls: ["swarm_wait"])]);
        world.Transcript(ChildA, Calling("run_command"));
        var sessions = Scan();
        Expect(sessions.Count, 1);
        Expect(sessions[0].Status, ActivityState.Working);
        Expect(sessions[0].Modified >= Now.AddSeconds(-30), "its last activity is its subagent's");
        Expect(sessions[0].Children.Single().State, TaskNodeState.Running);
        // 父子都是很久以前的陈旧 RUNNING（应用崩过）：还是 Idle。
        world.Transcript(ChildA, [Step("USER_EXPLICIT", "USER_INPUT", 4000, content: "go"), Step("MODEL", "PLANNER_RESPONSE", 3700, calls: ["run_command"])]);
        var stale = Scan();
        Expect(stale.Count, 1);
        Expect(stale[0].Status, ActivityState.Idle);
        Expect(stale[0].Children.Count, 0);
    }

    private static void CancelAndStale()
    {
        using var world = new World();
        world.CreateDb((Parent, Idle, 0, 0, "", ""), (ChildA, Running, 1, 0, "", ""));
        // 取消：db 说 IDLE，记录最后一步是普通的工具调用。
        world.Transcript(Parent, Calling("run_command"));
        // 崩溃：行里还是 RUNNING，但一个小时没写过任何东西。
        world.Transcript(ChildA, [Step("USER_EXPLICIT", "USER_INPUT", 4000), Step("MODEL", "PLANNER_RESPONSE", 3600, calls: ["run_command"])]);
        var sessions = Scan().ToDictionary(s => s.SessionId);
        Expect(sessions[Parent].Status, ActivityState.Idle);
        Expect(sessions[ChildA].Status, ActivityState.Idle);
    }

    private static void KilledChildIsDone()
    {
        using var world = new World();
        world.CreateDb((Parent, Running, 1, 0, "", ""), (ChildA, Idle, 0, 1, Parent, "reviewer"));
        world.Transcript(Parent, Calling("swarm_wait"));
        // 做到一半被收掉：记录停在一次调用上，但父会话已经收走它。
        world.Transcript(ChildA, Calling("run_command"));
        world.Subagent(Parent, ChildA, "SUBAGENT_STATE_KILLED");
        var sessions = Scan();
        Expect(sessions.Count, 1);
        Expect(sessions[0].Children.Single().State, TaskNodeState.Done);
    }

    private static void KilledOverRunningRow()
    {
        using var world = new World();
        world.CreateDb((Parent, Running, 1, 0, "", ""), (ChildA, Running, 0, 1, Parent, "a"), (ChildB, Idle, 1, 1, Parent, "b"));
        world.Transcript(Parent, Calling("swarm_wait"));
        world.Transcript(ChildA, Calling("run_command"));
        world.Transcript(ChildB, Calling("run_command"));
        world.Subagent(Parent, ChildA, "SUBAGENT_STATE_ALIVE");
        world.Subagent(Parent, ChildB, "SUBAGENT_STATE_ALIVE");
        var children = Scan().Single().Children;
        Expect(children.Single(node => node.Id == ChildA).State, TaskNodeState.Done);
        Expect(children.Single(node => node.Id == ChildB).State, TaskNodeState.Done);
    }

    private static void KilledByFileOnly()
    {
        using var world = new World();
        File.WriteAllText(world.Db, "this is not a database");
        world.Transcript(Parent, Calling("swarm_wait"));
        world.Transcript(ChildA, Calling("run_command"));
        world.Subagent(Parent, ChildA, "SUBAGENT_STATE_KILLED");
        var sessions = Scan();
        Expect(sessions.Count, 1);
        Expect(sessions[0].Children.Single().State, TaskNodeState.Done);
    }

    private static void UnreadableDb()
    {
        using var world = new World();
        File.WriteAllText(world.Db, "this is not a database");
        world.Transcript(Parent, Calling("run_command"));
        world.Transcript(ChildA, Calling("run_command"));
        world.Subagent(Parent, ChildA, "SUBAGENT_STATE_ALIVE");
        Expect(SessionScanner.AntigravityRows(world.Agy).Count, 0);
        var sessions = Scan();
        Expect(sessions.Count, 1);
        Expect(sessions[0].SessionId, Parent);
        Expect(sessions[0].Status, ActivityState.Working);
        Expect(sessions[0].Children.Single().Id, ChildA);
    }

    private static void OlderDb()
    {
        using var world = new World();
        world.CreateOlderDb(Parent, "old title");
        world.Transcript(Parent, Calling("run_command"));
        var sessions = Scan();
        Expect(sessions.Count, 1);
        Expect(sessions[0].Label, "old title");
        Expect(sessions[0].Status, ActivityState.Working);
    }

    private static void ReadOnlyOpen()
    {
        using var world = new World();
        world.CreateDb((Parent, Idle, 0, 0, "", ""));
        world.Transcript(Parent, Calling("run_command"));
        var before = File.ReadAllBytes(world.Db);
        var stamp = File.GetLastWriteTimeUtc(world.Db);
        Scan();
        Scan();
        Expect(File.ReadAllBytes(world.Db).AsSpan().SequenceEqual(before), "the db is unchanged");
        Expect(File.GetLastWriteTimeUtc(world.Db), stamp);
        // 别的进程以写方式占着文件，不妨碍读。
        using (new FileStream(world.Db, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Expect(SessionScanner.AntigravityRows(world.Agy).ContainsKey(Parent), "read while the app holds it");
        }
        // 用完也不留句柄：文件马上能被替换。
        File.Delete(world.Db);
        Expect(!File.Exists(world.Db), "no handle is left on the db");
    }

    private static void ClosedWalDbLeavesNoFiles()
    {
        using var world = new World();
        world.CreateDb((Parent, Running, 1, 0, "", ""));
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = world.Db, Pooling = false }.ToString()))
        {
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        string[] Listing() => Directory.GetFiles(world.Agy).Select(Path.GetFileName).Order().ToArray()!;
        var before = Listing();
        var stamp = File.GetLastWriteTimeUtc(world.Db);
        Expect(!before.Any(name => name!.EndsWith("-shm") || name.EndsWith("-wal")), "the app is closed: only the db is left");
        var rows = SessionScanner.AntigravityRows(world.Agy);
        Expect(rows.ContainsKey(Parent), "the closed db is still read");
        Expect(string.Join(",", Listing()), string.Join(",", before));
        Expect(File.GetLastWriteTimeUtc(world.Db), stamp);
    }

    private static void HeldWalDb()
    {
        using var world = new World();
        world.CreateDb((Parent, Idle, 0, 0, "", ""));
        world.Transcript(Parent, Calling("run_command"));
        // 应用的做法：一个写连接一直开着，WAL 模式，改动留在 -wal 里不合并回主文件。
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = world.Db, Pooling = false }.ToString());
        writer.Open();
        void Sql(string text)
        {
            using var command = writer.CreateCommand();
            command.CommandText = text;
            command.ExecuteNonQuery();
        }
        Sql("PRAGMA journal_mode=WAL");
        Sql("PRAGMA wal_autocheckpoint=0");
        Sql($"UPDATE conversation_summaries SET status = '{Running}', not_fully_idle = 1 WHERE conversation_id = '{Parent}'");
        Expect(new FileInfo(world.Db + "-wal").Length > 0, "the new row state is only in the wal");
        Expect(SessionScanner.AntigravityRows(world.Agy)[Parent].Status, Running);
        Expect(Scan().Single().Status, ActivityState.Working);
        Sql($"UPDATE conversation_summaries SET status = '{Idle}', not_fully_idle = 0 WHERE conversation_id = '{Parent}'");
        Expect(Scan().Single().Status, ActivityState.Idle);
    }
}
