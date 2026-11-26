using System.Text;
using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 任务树的数据：父会话下的 subagent、workflow、swarm 和子对话。
/// 测试数据是和本机真实结构一样的假目录（Claude 的 subagents/ 与 workflows/wf_<runId>/、
/// 桌面应用的 spawnedFrom、Kimi 的 state.json 与 agents/agent-N/wire.jsonl、Codex 的 parent_thread_id），
/// 都放在临时根目录里，不碰任何真实文件。
public static class TaskTreeTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("claude: subagents hang under the working parent with name, type, state and phase, outside the flat list", ClaudeSubagents),
            ("claude: a workflow run holds its agents (session, run, agent) and takes its name from the parent's tool result", ClaudeWorkflow),
            ("claude: an idle parent is not expanded", IdleParentNotExpanded),
            ("claude: desktop sessions spawned from a parent group under it while staying in the flat list", DesktopSpawnedFrom),
            ("claude: only the newest nodes of a long fan-out are expanded, and the rest are counted", FanOutIsCapped),
            ("claude: what is kept past the cap is chosen by state, not by age", StateBeatsAge),
            ("claude: a workflow run reads running from every agent, not only the ones kept", RunStateFromWholeBatch),
            ("claude: a child that cannot be read leaves the parent as it was", UnreadableChild),
            ("claude: the main session's name falls through desktop title, custom-title, agent-name, last-prompt, folder", ClaudeTitleChain),
            ("claude: the session name is kept when the tail has no title record", ClaudeTitleKept),
            ("claude: the phase of an agent transcript is read though every line is a sidechain", ClaudeAgentPhase),
            ("kimi: agents nest by parentAgentId and past two levels flatten onto the second", KimiNesting),
            ("kimi: agents of a swarm are gathered under one swarm node", KimiSwarm),
            ("kimi: the title comes from state.json, then lastPrompt, then the folder", KimiTitle),
            ("kimi: a broken state.json or a missing sub wire leaves the session as it was", KimiUnreadable),
            ("kimi: finished agents stay for minutes, failed ones longer, and wild timestamps cost nothing", KimiRetentionAndWildTimes),
            ("kimi: a session the desktop app reports busy is expanded though its log is quiet", KimiLiveReportExpands),
            ("codex: a spawned thread hangs under its parent_thread_id, in either place the id is written", CodexSpawned),
            ("codex: guardian reviews never enter the tree", CodexGuardianSkipped),
            ("codex: meta carries the parent id and the thread source", CodexMetaParent),
            ("idle parents of kimi and codex are not expanded through the whole scan", IdleParentKimiCodexNotExpanded),
            ("at most MaxParsedPerList records of a list are read, and the rest are counted", ParseLimitPerList),
            ("only the newest workflow runs are read, the older ones are counted", WorkflowRunLimit),
            ("cap: the flattened second level is trimmed again and the cut is counted", CapTrimsTheFlattenedLevel),
            ("titles are redacted before they are cut: keys, tokens, long encoded strings and emails", TitlesAreRedacted),
            ("a line index drops the least recently used entry when full, not the whole table", LineIndexEvictsOldest),
            ("running count, best session and events are the same for claude, kimi and codex with a subtree", FlatListUnchangedForEveryAgent),
            ("depth: nodes past the second level flatten onto it and keep their own level", DepthFlatten),
            ("trim: the nodes that matter are kept and shown in start order", TrimKeepsWhatMatters),
            ("a session settled to idle loses its subtree", SettleClearsChildren),
            ("the flat list, the running count and the latest sessions are what they were", FlatListUnchanged),
            ("a line index reads only what was appended and holds back a half-written line", LineIndexIncremental),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS task tree: " + name);
        }
        Console.WriteLine("TaskTreeTests GREEN");
    }

    // MARK: - 假目录

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    /// 把整个家挪到一个临时根目录下跑，跑完还原，缓存清掉，免得上一个用例的解析留下来。
    private static void Isolated(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "limen-tasktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var before = IslandPaths.RootOverride;
        IslandPaths.RootOverride = root;
        SessionScanner.ClearTurnCache();
        try
        {
            body(root);
        }
        finally
        {
            IslandPaths.RootOverride = before;
            SessionScanner.ClearTurnCache();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string Write(string path, DateTimeOffset modified, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + (lines.Length > 0 ? "\n" : ""), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private static string ClaudeProject => Path.Combine(IslandPaths.ClaudeProjectRoots.First(), "C--work-proj");

    private static string UserLine(DateTimeOffset at, string text = "go", bool sidechain = false) =>
        $$$"""{"type":"user","uuid":"u{{{Ms(at)}}}","timestamp":"{{{Iso(at)}}}","cwd":"C:\\work\\proj","isSidechain":{{{(sidechain ? "true" : "false")}}},"message":{"role":"user","content":"{{{text}}}"}}""";

    private static string ToolUseLine(DateTimeOffset at, string tool = "Read", string id = "t1") =>
        $$$"""{"type":"assistant","uuid":"a{{{Ms(at)}}}","timestamp":"{{{Iso(at)}}}","isSidechain":true,"message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"{{{id}}}","name":"{{{tool}}}","input":{}}]}}""";

    private static string EndTurnLine(DateTimeOffset at, bool sidechain = false) =>
        $$$"""{"type":"assistant","uuid":"e{{{Ms(at)}}}","timestamp":"{{{Iso(at)}}}","isSidechain":{{{(sidechain ? "true" : "false")}}},"message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}}""";

    /// 一个正在跑的主会话：回合开着，刚写过。
    private static string WorkingParent(string sid, params string[] extra)
    {
        var lines = new List<string> { UserLine(Now.AddSeconds(-20)) };
        lines.AddRange(extra);
        return Write(Path.Combine(ClaudeProject, sid + ".jsonl"), Now.AddSeconds(-5), lines.ToArray());
    }

    private static void Agent(string transcript, string name, string description, string type, bool open, bool finished,
        DateTimeOffset modified, string? phase = null)
    {
        var dir = Path.GetDirectoryName(transcript)!;
        var lines = open
            ? new[] { UserLine(modified.AddSeconds(-4), "task", sidechain: true), ToolUseLine(modified) }
            : new[] { UserLine(modified.AddSeconds(-4), "task", sidechain: true), EndTurnLine(modified, sidechain: true) };
        Write(Path.Combine(dir, name + ".jsonl"), modified, lines);
        var meta = $$"""{"agentType":"{{type}}","description":"{{description}}","spawnDepth":1{{(finished ? ",\"finished\":true" : "")}}{{(phase is null ? "" : $",\"workflowPhase\":\"{phase}\"")}}}""";
        Write(Path.Combine(dir, name + ".meta.json"), modified, meta);
    }

    private static List<ScannedSession> Scan(TriggerTool tool) =>
        SessionScanner.MonitoringScan(Now, new Dictionary<string, DateTimeOffset>()).Where(s => s.Tool == tool).ToList();

    private static ScannedSession Only(List<ScannedSession> sessions, string sid)
    {
        var found = sessions.Where(s => s.SessionId == sid).ToList();
        Expect(found.Count == 1, $"expected one session {sid}, found {found.Count}");
        return found[0];
    }

    // MARK: - Claude

    private static void ClaudeSubagents()
    {
        Isolated(_ =>
        {
            var parent = WorkingParent("p1");
            var sub = Path.Combine(ClaudeProject, "p1", "subagents");
            Agent(Path.Combine(sub, "agent-a1.jsonl"), "agent-a1", "查 HoverCard 的宽度", "Explore", open: true, finished: false, Now.AddSeconds(-3));
            Agent(Path.Combine(sub, "agent-a2.jsonl"), "agent-a2", "写设计文档", "architect", open: false, finished: true, Now.AddSeconds(-40));
            var sessions = Scan(TriggerTool.Claude);

            Expect(sessions.Count, 1);
            var session = Only(sessions, "p1");
            Expect(session.Status, ActivityState.Working);
            Expect(session.Children.Count, 2);
            var running = session.Children.Single(n => n.Id == "agent-a1");
            Expect(running.Name, "查 HoverCard 的宽度");
            Expect(running.AgentType, "Explore");
            Expect(running.Kind, TaskNodeKind.Subagent);
            Expect(running.State, TaskNodeState.Running);
            Expect(running.Level, 1);
            Expect(running.Phase is { Phase: AgentPhase.Reading }, "the phase of the running agent is what its last tool call says");
            Expect(running.StartedAt is not null && running.UpdatedAt is not null, "start and last activity are filled");
            var done = session.Children.Single(n => n.Id == "agent-a2");
            Expect(done.State, TaskNodeState.Done);
            Expect(done.Phase is null, "a finished node has no phase");
            Expect(session.Children[0].Id, "agent-a1");
            Expect(!sessions.Any(s => s.SessionId.StartsWith("agent-", StringComparison.Ordinal)), "subagents stay out of the flat list");
        });
    }

    private static void ClaudeWorkflow()
    {
        Isolated(_ =>
        {
            var toolResult = $$$"""{"type":"user","uuid":"w1","timestamp":"{{{Iso(Now.AddMinutes(-3))}}}","message":{"role":"user","content":"ok"},"toolUseResult":{"status":"async_launched","taskType":"local_workflow","workflowName":"全项目审查","runId":"wf_run1"}}""";
            WorkingParent("p2", toolResult, toolResult);
            var run = Path.Combine(ClaudeProject, "p2", "subagents", "workflows", "wf_run1");
            Agent(Path.Combine(run, "agent-w1.jsonl"), "agent-w1", "Find 1", "workflow-subagent", open: true, finished: false, Now.AddSeconds(-2), phase: "Find");
            Agent(Path.Combine(run, "agent-w2.jsonl"), "agent-w2", "Verify 1", "workflow-subagent", open: false, finished: true, Now.AddSeconds(-30), phase: "Verify");
            // 同一个会话里还有一个单独的 subagent。
            Agent(Path.Combine(ClaudeProject, "p2", "subagents", "agent-s1.jsonl"), "agent-s1", "单个任务", "Explore", open: true, finished: false, Now.AddSeconds(-1));

            var session = Only(Scan(TriggerTool.Claude), "p2");
            Expect(session.Children.Count, 2);
            var workflow = session.Children.Single(n => n.Kind == TaskNodeKind.Workflow);
            Expect(workflow.Id, "wf_run1");
            Expect(workflow.Name, "全项目审查");
            Expect(workflow.State, TaskNodeState.Running);
            Expect(workflow.Level, 1);
            Expect(workflow.Children.Count, 2);
            Expect(workflow.Children.All(n => n.Level == 2), "the agents are the run's second level");
            Expect(workflow.Children.Single(n => n.Id == "agent-w1").State, TaskNodeState.Running);
            Expect(workflow.Children.Single(n => n.Id == "agent-w2").State, TaskNodeState.Done);
            Expect(workflow.Children.Single(n => n.Id == "agent-w1").AgentType, "workflow-subagent");
            Expect(session.Children.Single(n => n.Kind == TaskNodeKind.Subagent).Id, "agent-s1");
        });
    }

    private static void IdleParentNotExpanded()
    {
        Isolated(_ =>
        {
            // 回合早就结束，且已经超过关注窗口：Idle。
            var old = Now.AddHours(-3);
            Write(Path.Combine(ClaudeProject, "p3.jsonl"), old, UserLine(old.AddSeconds(-30)), EndTurnLine(old));
            Agent(Path.Combine(ClaudeProject, "p3", "subagents", "agent-x.jsonl"), "agent-x", "旧任务", "Explore", open: true, finished: false, Now.AddSeconds(-2));
            var session = Only(Scan(TriggerTool.Claude), "p3");
            Expect(session.Status, ActivityState.Idle);
            Expect(session.Children.Count, 0);
            Expect(session.ChildrenOmitted, 0);
        });
    }

    private static string DesktopFile(string name, string sessionId, string cliSessionId, string title, string? spawnedFrom)
    {
        var path = Path.Combine(IslandPaths.RoamingAppData, "Claude", "claude-code-sessions", "acct", "org", name + ".json");
        var spawned = spawnedFrom is null ? "" : $$""","spawnedFrom":{"sessionId":"{{spawnedFrom}}","taskId":"task_1","title":"x"}""";
        Write(path, Now.AddSeconds(-2),
            $$"""{"sessionId":"{{sessionId}}","cliSessionId":"{{cliSessionId}}","cwd":"C:\\work\\proj","title":"{{title}}","createdAt":{{Ms(Now.AddMinutes(-10))}},"lastActivityAt":{{Ms(Now.AddSeconds(-2))}},"latestUserFrameAt":{{Ms(Now.AddSeconds(-15))}}{{spawned}}}""");
        return path;
    }

    private static void DesktopSpawnedFrom()
    {
        Isolated(_ =>
        {
            WorkingParent("P");
            Write(Path.Combine(ClaudeProject, "C.jsonl"), Now.AddSeconds(-4), UserLine(Now.AddSeconds(-20)));
            Write(Path.Combine(ClaudeProject, "D.jsonl"), Now.AddSeconds(-4), UserLine(Now.AddSeconds(-20)));
            DesktopFile("local_p", "local_P", "P", "主对话", null);
            DesktopFile("local_c", "local_C", "C", "侧边会话", "local_P");
            // 孙辈：从侧边会话再派生出来的。
            DesktopFile("local_d", "local_D", "D", "再下一层", "local_C");

            var parsed = SessionScanner.ParseDesktopSessionFile(DesktopFileAt("local_c"));
            Expect(parsed is { SessionId: "local_C", SpawnedFromSessionId: "local_P" }, "the desktop session keeps its own id and where it was spawned from");
            Expect(SessionScanner.ParseDesktopSessionFile(DesktopFileAt("local_p"))!.SpawnedFromSessionId, "");

            var sessions = Scan(TriggerTool.Claude);
            Expect(sessions.Count, 3);
            Expect(sessions.Select(s => s.SessionId).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(new[] { "C", "D", "P" }), "the flat list keeps every desktop session");
            var parent = Only(sessions, "P");
            Expect(parent.Label, "主对话");
            Expect(parent.Children.Count, 1);
            var child = parent.Children[0];
            Expect(child.Kind, TaskNodeKind.Conversation);
            Expect(child.Name, "侧边会话");
            Expect(child.ConversationId, "claude:C");
            Expect(child.State, TaskNodeState.Running);
            Expect(child.Children.Count, 1);
            Expect(child.Children[0].ConversationId, "claude:D");
            Expect(child.Children[0].Level, 2);
            // 子会话本身也是平铺列表里的会话，有它自己的子任务视图。
            Expect(Only(sessions, "C").Children.Count, 1);
            Expect(Only(sessions, "D").Children.Count, 0);
        });
    }

    private static string DesktopFileAt(string name) =>
        Path.Combine(IslandPaths.RoamingAppData, "Claude", "claude-code-sessions", "acct", "org", name + ".json");

    private static void FanOutIsCapped()
    {
        Isolated(_ =>
        {
            WorkingParent("p4");
            var sub = Path.Combine(ClaudeProject, "p4", "subagents");
            for (var i = 0; i < 25; i++)
            {
                Agent(Path.Combine(sub, $"agent-f{i:00}.jsonl"), $"agent-f{i:00}", "任务 " + i, "Explore", open: true, finished: false, Now.AddSeconds(-1 - i));
            }
            var session = Only(Scan(TriggerTool.Claude), "p4");
            Expect(session.Children.Count, TaskTrees.MaxNodesPerList);
            Expect(session.ChildrenOmitted, 5);
            Expect(session.ChildrenOmittedRunning, 5);
            Expect(session.Children.Any(n => n.Id == "agent-f00"), "the newest are kept");
            Expect(!session.Children.Any(n => n.Id == "agent-f24"), "past the cap the oldest running ones are the ones left out, and they are counted as running");
        });
    }

    private static void StateBeatsAge()
    {
        Isolated(_ =>
        {
            WorkingParent("s1");
            var sub = Path.Combine(ClaudeProject, "s1", "subagents");
            // 最旧的那个还在跑（一条长命令，二十分钟没写东西），后面 21 个都已完成且更新。
            Agent(Path.Combine(sub, "agent-slow.jsonl"), "agent-slow", "长命令", "Explore", open: true, finished: false, Now.AddMinutes(-20));
            for (var i = 0; i < 21; i++)
            {
                Agent(Path.Combine(sub, $"agent-d{i:00}.jsonl"), $"agent-d{i:00}", "完成 " + i, "Explore", open: false, finished: true, Now.AddSeconds(-30 - i));
            }
            var session = Only(Scan(TriggerTool.Claude), "s1");
            Expect(session.Children.Count, TaskTrees.MaxNodesPerList);
            Expect(session.Children.Any(n => n.Id == "agent-slow" && n.State == TaskNodeState.Running), "the running one is kept though it is the oldest");
            Expect(session.ChildrenOmitted, 2);
            Expect(session.ChildrenOmittedRunning, 0);
        });
    }

    private static void RunStateFromWholeBatch()
    {
        Isolated(_ =>
        {
            WorkingParent("s2");
            var run = Path.Combine(ClaudeProject, "s2", "subagents", "workflows", "wf_big");
            Agent(Path.Combine(run, "agent-slow.jsonl"), "agent-slow", "还在跑", "workflow-subagent", open: true, finished: false, Now.AddMinutes(-20), phase: "Verify");
            for (var i = 0; i < 24; i++)
            {
                Agent(Path.Combine(run, $"agent-d{i:00}.jsonl"), $"agent-d{i:00}", "完成 " + i, "workflow-subagent", open: false, finished: true, Now.AddSeconds(-30 - i), phase: "Find");
            }
            var workflow = Only(Scan(TriggerTool.Claude), "s2").Children.Single();
            Expect(workflow.Kind, TaskNodeKind.Workflow);
            Expect(workflow.State, TaskNodeState.Running);
            Expect(workflow.Children.Count, TaskTrees.MaxNodesPerList);
            Expect(workflow.Children.Any(n => n.Id == "agent-slow"), "the running agent is among those shown");
            Expect(workflow.Omitted, 5);
            Expect(workflow.OmittedRunning, 0);
        });
    }

    private static void KimiRetentionAndWildTimes()
    {
        Isolated(_ =>
        {
            var dir = KimiSession("r1",
                KimiStateJson("留存", ("agent-1", "main", false), ("agent-2", "main", false), ("agent-3", "main", false), ("agent-4", "main", false), ("agent-5", "main", false)),
                Now.AddSeconds(-2),
                KimiOpen(Now.AddSeconds(-30)),
                // 5 分钟前完成：留。12 分钟前完成：不留。10 分钟前失败：留（出错留 15 分钟）。20 分钟前失败：不留。
                $$"""{"type":"subagent.completed","subagentId":"agent-1","time":{{Ms(Now.AddMinutes(-5))}}}""",
                $$"""{"type":"subagent.completed","subagentId":"agent-2","time":{{Ms(Now.AddMinutes(-12))}}}""",
                $$"""{"type":"subagent.failed","subagentId":"agent-3","time":{{Ms(Now.AddMinutes(-10))}}}""",
                $$"""{"type":"subagent.failed","subagentId":"agent-4","time":{{Ms(Now.AddMinutes(-20))}}}""",
                // 写坏的时间戳：越界、负数。
                """{"type":"subagent.spawned","subagentId":"agent-5","subagentName":"coder","description":"乱时间","time":1e30}""",
                """{"type":"subagent.completed","subagentId":"agent-5","time":-5}""");
            foreach (var agent in new[] { "agent-1", "agent-2", "agent-3", "agent-4", "agent-5" })
            {
                KimiAgentWire(dir, agent, Now.AddMinutes(-6), KimiOpen(Now.AddMinutes(-7)));
            }
            var session = Scan(TriggerTool.Kimi).Single();
            Expect(session.Status, ActivityState.Working);
            var shown = session.Children.Select(n => (n.Id, n.State)).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            Expect(shown.Any(n => n is ("agent-1", TaskNodeState.Done)), "a finish five minutes ago is kept");
            Expect(!shown.Any(n => n.Id == "agent-2"), "a finish twelve minutes ago is not");
            Expect(shown.Any(n => n is ("agent-3", TaskNodeState.Error)), "a failure ten minutes ago is kept");
            Expect(!shown.Any(n => n.Id == "agent-4"), "a failure twenty minutes ago is not");
        });
    }

    private static void KimiLiveReportExpands()
    {
        Isolated(_ =>
        {
            // 日志停了四十分钟（按日志是 Idle），桌面应用说它的回合还开着：会话读成在跑，子任务也要展开。
            var dir = KimiSession("l1", KimiStateJson("安静", ("agent-1", "main", false)), Now.AddMinutes(-1),
                $$"""{"type":"turn.ended","time":{{Ms(Now.AddMinutes(-40))}}}""");
            KimiAgentWire(dir, "agent-1", Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)));
            var idle = new ScannedSession(TriggerTool.Kimi, "l1", "C:\\work\\proj", "安静", Now, ActivityState.Idle, null, null, SessionLaunchTarget.Cli);
            var applied = KimiLiveSessions.Apply(idle, new KimiLiveSessions.Report(true, "none"));
            Expect(applied.Status, ActivityState.Working);
            var (nodes, _, _) = SessionScanner.KimiChildren(dir, SessionScanner.KimiState(Path.Combine(dir, "state.json")), Now);
            Expect(nodes.Count, 1);
        });
    }

    private static void TrimKeepsWhatMatters()
    {
        var nodes = new List<TaskNode>();
        for (var i = 0; i < 22; i++)
        {
            nodes.Add(new TaskNode($"d{i:00}", "", TaskNodeKind.Subagent, TaskNodeState.Done, Now.AddSeconds(-100 + i)) { UpdatedAt = Now.AddSeconds(-i) });
        }
        nodes.Add(new TaskNode("oldErr", "", TaskNodeKind.Subagent, TaskNodeState.Error, Now.AddSeconds(-500)) { UpdatedAt = Now.AddMinutes(-9) });
        nodes.Add(new TaskNode("oldWait", "", TaskNodeKind.Subagent, TaskNodeState.WaitingOnYou, Now.AddSeconds(-400)) { UpdatedAt = Now.AddMinutes(-8) });
        var (kept, omitted, omittedRunning) = TaskTrees.Trim(nodes);
        Expect(kept.Count, TaskTrees.MaxNodesPerList);
        Expect(kept.Any(n => n.Id == "oldErr") && kept.Any(n => n.Id == "oldWait"), "an error and a wait always make the cut");
        Expect(omitted, 4);
        Expect(omittedRunning, 0);
        Expect(kept.Select(n => n.StartedAt).SequenceEqual(kept.Select(n => n.StartedAt).OrderBy(t => t)), "the nodes kept are in start order");
        var (_, more, moreRunning) = TaskTrees.Trim(Enumerable.Range(0, 23).Select(i =>
            new TaskNode($"r{i}", "", TaskNodeKind.Subagent, TaskNodeState.Running, Now.AddSeconds(i))), alreadyOmitted: 7);
        Expect(more, 10);
        Expect(moreRunning, 3);
    }

    private static void SettleClearsChildren()
    {
        var child = Node("c");
        var session = new ScannedSession(TriggerTool.Claude, "s", "C:\\p", "l", Now, ActivityState.Working, null, null, SessionLaunchTarget.Cli)
        {
            Children = new[] { child },
            ChildrenOmitted = 3,
            ChildrenOmittedRunning = 1,
        };
        var settled = ActivityMonitor.Settle(new List<ScannedSession> { session }, new HashSet<TriggerTool> { TriggerTool.Claude }, offline: false);
        Expect(settled[0].Status, ActivityState.Idle);
        Expect(settled[0].Children.Count, 0);
        Expect(settled[0].ChildrenOmitted, 0);
        var kept = ActivityMonitor.Settle(new List<ScannedSession> { session }, new HashSet<TriggerTool>(), offline: false);
        Expect(kept[0].Children.Count, 1);
    }

    private static void IdleParentKimiCodexNotExpanded()
    {
        Isolated(_ =>
        {
            var old = Now.AddHours(-3);
            // Kimi：主 wire 在三小时前结束了回合（日志在一天窗口内，会被扫到），子 agent 的记录却是新的。
            var dir = KimiSession("idle1", KimiStateJson("早就停了", ("agent-1", "main", false)), old,
                $$"""{"type":"turn.ended","time":{{Ms(old)}}}""",
                KimiSpawned("agent-1", "coder", "还在写", Now.AddSeconds(-30)));
            KimiAgentWire(dir, "agent-1", Now.AddSeconds(-1), KimiOpen(Now.AddSeconds(-20)));
            // Codex：同样，父线程三小时前结束，子线程是新的。
            CodexRollout("par", ",\"source\":\"cli\"", old, open: false);
            CodexRollout("kid", ",\"parent_thread_id\":\"par\",\"source\":{\"subagent\":\"spawn\"}", Now.AddSeconds(-1));

            var kimi = Scan(TriggerTool.Kimi).Single();
            Expect(kimi.Status, ActivityState.Idle);
            Expect(kimi.Children.Count, 0);
            Expect(kimi.ChildrenOmitted, 0);
            var codex = Scan(TriggerTool.Codex).Single();
            Expect(codex.Status, ActivityState.Idle);
            Expect(codex.Children.Count, 0);
            // 同一个子任务，父会话在跑时就展开：证明上面不是因为子任务本身读不到。
            Expect(SessionScanner.KimiChildren(dir, SessionScanner.KimiState(Path.Combine(dir, "state.json")), Now).Nodes.Count, 1);
        });
    }

    private static void ParseLimitPerList()
    {
        Isolated(_ =>
        {
            WorkingParent("m1");
            var sub = Path.Combine(ClaudeProject, "m1", "subagents");
            for (var i = 0; i < 105; i++)
            {
                Agent(Path.Combine(sub, $"agent-m{i:000}.jsonl"), $"agent-m{i:000}", "任务", "Explore", open: true, finished: false, Now.AddSeconds(-1 - i));
            }
            Interlocked.Exchange(ref SessionScanner.ParsedChildren, 0);
            var session = Only(Scan(TriggerTool.Claude), "m1");
            Expect(Volatile.Read(ref SessionScanner.ParsedChildren), TaskTrees.MaxParsedPerList);
            Expect(session.Children.Count, TaskTrees.MaxNodesPerList);
            Expect(session.ChildrenOmitted, 105 - TaskTrees.MaxNodesPerList);
            // 没读的 5 个不知道状态，不算进在跑数；读了被挤掉的 80 个都是在跑的。
            Expect(session.ChildrenOmittedRunning, TaskTrees.MaxParsedPerList - TaskTrees.MaxNodesPerList);
        });
    }

    private static void WorkflowRunLimit()
    {
        Isolated(_ =>
        {
            WorkingParent("m2");
            var workflows = Path.Combine(ClaudeProject, "m2", "subagents", "workflows");
            for (var i = 0; i < 7; i++)
            {
                var run = Path.Combine(workflows, $"wf_r{i}");
                Agent(Path.Combine(run, "agent-a.jsonl"), "agent-a", "跑", "workflow-subagent", open: true, finished: false, Now.AddSeconds(-5), phase: "Find");
                Directory.SetLastWriteTimeUtc(run, Now.AddMinutes(-i).UtcDateTime);
            }
            var session = Only(Scan(TriggerTool.Claude), "m2");
            Expect(session.Children.Count, TaskTrees.MaxRunsPerSession);
            Expect(session.Children.Select(n => n.Id).OrderBy(i => i, StringComparer.Ordinal).SequenceEqual(new[] { "wf_r0", "wf_r1", "wf_r2", "wf_r3", "wf_r4" }), "the newest runs are the ones read");
            Expect(session.ChildrenOmitted, 2);
        });
    }

    private static void CapTrimsTheFlattenedLevel()
    {
        var kids = Enumerable.Range(0, 15).Select(i =>
            new TaskNode($"k{i:00}", "", TaskNodeKind.Subagent, TaskNodeState.Running, Now.AddSeconds(i))
            {
                Children = Enumerable.Range(0, 3).Select(j => new TaskNode($"k{i:00}g{j}", "", TaskNodeKind.Subagent, TaskNodeState.Running, Now.AddSeconds(i * 10 + j))).ToList(),
            }).ToList();
        var capped = TaskTrees.Cap(new[] { Node("top") with { Children = kids } });
        var top = capped.Single();
        Expect(top.Children.Count, TaskTrees.MaxNodesPerList);
        Expect(top.Omitted, 15 + 45 - TaskTrees.MaxNodesPerList);
        Expect(top.OmittedRunning, 15 + 45 - TaskTrees.MaxNodesPerList);
        Expect(top.Children.All(n => n.Children.Count == 0), "still nothing nested below level 2");
        // 不多的时候不动次序。
        var small = TaskTrees.Cap(new[] { Node("a", Node("b", Node("c"))) });
        Expect(small[0].Children.Select(n => n.Id).SequenceEqual(new[] { "b", "c" }), "a short list keeps its order");
        Expect(small[0].Omitted, 0);
    }

    private static void TitlesAreRedacted()
    {
        string[] secrets =
        [
            "sk-ant-api03-AbCdEf123456", "sk-AbCdEf1234567890", "ghp_abcdefghijklmnopqrstuvwxyz0123456789",
            "github_pat_11AAAAAAA0abcdef", "xoxb-1234-5678-abcdef", "AKIAIOSFODNN7EXAMPLE", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln",
            "me.name+tag@example.co.uk", "0123456789abcdef0123456789abcdef", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=",
        ];
        foreach (var secret in secrets)
        {
            var redacted = SessionScanner.RedactTitle($"用 {secret} 去调接口");
            Expect(!redacted.Contains(secret[..Math.Min(8, secret.Length)], StringComparison.Ordinal), "the secret is gone: " + secret[..4]);
            Expect(redacted.Contains('…') && redacted.StartsWith("用 ", StringComparison.Ordinal) && redacted.EndsWith(" 去调接口", StringComparison.Ordinal), "the rest of the line stays");
        }
        var bearer = SessionScanner.RedactTitle("header Bearer abc.def-ghi 之后");
        Expect(!bearer.Contains("abc.def", StringComparison.Ordinal), "a bearer token is gone");
        // 平常的话不动。
        foreach (var plain in new[] { "fix the task-list and the desk-top layout", "修一下设置页", "sk and ask-me", "v1.2.3 发布说明" })
        {
            Expect(SessionScanner.RedactTitle(plain), plain);
        }
        // 先遮蔽再截断：令牌不会被切成认不出的半截留在标题里。
        var cut = SessionScanner.SafeTitle("请检查 ghp_abcdefghijklmnopqrstuvwxyz0123456789 这个令牌有没有泄露到仓库里去，顺便看一下别的");
        Expect(!cut.Contains("ghp_", StringComparison.Ordinal), "no half token survives the cut");

        Isolated(_ =>
        {
            WorkingParent("x1", """{"type":"custom-title","customTitle":"联系 me@example.com 处理","sessionId":"x1"}""");
            WorkingParent("x2", """{"type":"agent-name","agentName":"AKIAIOSFODNN7EXAMPLE 的活","sessionId":"x2"}""");
            WorkingParent("x3", """{"type":"last-prompt","lastPrompt":"用 sk-ant-api03-AbCdEf123456 试试","sessionId":"x3"}""");
            KimiSession("kx", KimiStateJson("给 me@example.com 发信"), Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)));
            var claude = Scan(TriggerTool.Claude);
            Expect(Only(claude, "x1").Label, "联系 … 处理");
            Expect(!Only(claude, "x2").Label.Contains("AKIA", StringComparison.Ordinal), "agent-name is redacted");
            Expect(!Only(claude, "x3").Label.Contains("sk-ant", StringComparison.Ordinal), "last-prompt is redacted");
            Expect(Scan(TriggerTool.Kimi).Single().Label, "给 … 发信");
        });
    }

    private static void LineIndexEvictsOldest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "limen-lineindex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var adds = 0;
        var index = new LineIndex<List<string>>(["\"hit\""], () => new List<string>(), (state, line) => { adds++; state.Add(line); });
        try
        {
            string P(int i) => Path.Combine(dir, i + ".jsonl");
            for (var i = 0; i < 401; i++) File.WriteAllText(P(i), "{\"hit\":1}\n");
            for (var i = 0; i < 400; i++) index.Read(P(i), state => state.Count, -1);
            Expect(index.Count, 400);
            // 0 号刚被用过；再来一个新文件，被挤掉的是最久没用的 1 号，不是整张表。
            index.Read(P(0), state => state.Count, -1);
            index.Read(P(400), state => state.Count, -1);
            Expect(index.Count, 400);
            File.AppendAllText(P(0), "{\"hit\":2}\n");
            File.AppendAllText(P(1), "{\"hit\":2}\n");
            // 0 号还在：只补读新增的一行；1 号被淘汰：从头重读两行。
            var before = adds;
            Expect(index.Read(P(0), state => state.Count, -1), 2);
            Expect(adds - before, 1);
            before = adds;
            Expect(index.Read(P(1), state => state.Count, -1), 2);
            Expect(adds - before, 2);
            Expect(index.Count, 400);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void FlatListUnchangedForEveryAgent()
    {
        Isolated(_ =>
        {
            WorkingParent("e1");
            Agent(Path.Combine(ClaudeProject, "e1", "subagents", "agent-r1.jsonl"), "agent-r1", "跑着", "Explore", open: true, finished: false, Now.AddSeconds(-1));
            var kdir = KimiSession("e2", KimiStateJson("k", ("agent-1", "main", false)), Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)),
                KimiSpawned("agent-1", "coder", "写", Now.AddSeconds(-20)));
            KimiAgentWire(kdir, "agent-1", Now.AddSeconds(-1), KimiOpen(Now.AddSeconds(-10)));
            CodexRollout("e3", ",\"source\":\"cli\"", Now.AddSeconds(-3));
            CodexRollout("e3k", ",\"parent_thread_id\":\"e3\",\"source\":{\"subagent\":\"spawn\"}", Now.AddSeconds(-1));

            var withTrees = SessionScanner.MonitoringScan(Now, new Dictionary<string, DateTimeOffset>());
            Expect(withTrees.Count(s => s.Children.Count > 0), 3);
            SessionScanner.TaskTreesEnabled = false;
            List<ScannedSession> plain;
            try
            {
                plain = SessionScanner.MonitoringScan(Now, new Dictionary<string, DateTimeOffset>());
            }
            finally
            {
                SessionScanner.TaskTreesEnabled = true;
            }
            // 把每个会话再挂一个“在等你”的子任务：它不应该让任何东西变。
            var waiting = withTrees.Select(s => s with
            {
                Children = s.Children.Append(new TaskNode("w", "", TaskNodeKind.Subagent, TaskNodeState.WaitingOnYou, Now)).ToList(),
            }).ToList();

            Expect(withTrees.Select(s => s.Id).Order(StringComparer.Ordinal).SequenceEqual(plain.Select(s => s.Id).Order(StringComparer.Ordinal)), "the same sessions are listed");
            foreach (var tool in new[] { TriggerTool.Claude, TriggerTool.Kimi, TriggerTool.Codex })
            {
                Expect(ActivityMonitor.RunningCount(withTrees, tool), ActivityMonitor.RunningCount(plain, tool));
                Expect(ActivityMonitor.RunningCount(waiting, tool), ActivityMonitor.RunningCount(plain, tool));
                Expect(ActivityMonitor.LatestSessions(withTrees).Count(s => s.Tool == tool), ActivityMonitor.LatestSessions(plain).Count(s => s.Tool == tool));
                var best = ActivityMonitor.BestSession(plain, tool);
                foreach (var candidate in new[] { withTrees, waiting })
                {
                    var got = ActivityMonitor.BestSession(candidate, tool);
                    Expect(got.State, best.State);
                    Expect(got.Thread?.SessionId, best.Thread?.SessionId);
                }
            }
            // “轮到你”事件：子任务在等你，不产生事件，也不进等待列表。
            SessionObservation Observe(List<ScannedSession> sessions)
            {
                var detector = new SessionEventDetector((_, _, _) => null, () => Array.Empty<SkippedTask>());
                detector.Observe(sessions, Now.AddSeconds(-1));
                return detector.Observe(sessions, Now);
            }
            var expected = Observe(plain);
            foreach (var candidate in new[] { withTrees, waiting })
            {
                var seen = Observe(candidate);
                Expect(seen.Fresh.Count, expected.Fresh.Count);
                Expect(seen.Waiting.Select(e => e.Key).Order(StringComparer.Ordinal).SequenceEqual(expected.Waiting.Select(e => e.Key).Order(StringComparer.Ordinal)), "the waiting list is the same");
            }
        });
    }

    private static void UnreadableChild()
    {
        Isolated(_ =>
        {
            WorkingParent("p5");
            var sub = Path.Combine(ClaudeProject, "p5", "subagents");
            Directory.CreateDirectory(sub);
            // 空记录加坏掉的 meta，和一个名字像记录的目录。
            Write(Path.Combine(sub, "agent-bad.jsonl"), Now.AddSeconds(-1));
            Write(Path.Combine(sub, "agent-bad.meta.json"), Now.AddSeconds(-1), "{not json");
            Directory.CreateDirectory(Path.Combine(sub, "agent-dir.jsonl"));
            var session = Only(Scan(TriggerTool.Claude), "p5");
            Expect(session.Status, ActivityState.Working);
            Expect(session.Label.Length > 0, "the parent still has a name");
            Expect(session.Children.All(n => n.Id != "agent-dir"), "a directory is not an agent");
        });
    }

    private static void ClaudeTitleChain()
    {
        Isolated(_ =>
        {
            string Transcript(string sid, params string[] records) =>
                WorkingParent(sid, records);

            // last-prompt 只有它时：截断。
            Transcript("t1", """{"type":"last-prompt","lastPrompt":"请帮我把设置页的深色模式\n修一下，顺便检查一下所有相关的测试有没有一起跑过，然后把改动写进更新说明里再提交","sessionId":"t1"}""");
            // agent-name 压过 last-prompt。
            Transcript("t2", """{"type":"last-prompt","lastPrompt":"x","sessionId":"t2"}""", """{"type":"agent-name","agentName":"agent-名","sessionId":"t2"}""");
            // custom-title 压过 agent-name，且取最后一条。
            Transcript("t3", """{"type":"custom-title","customTitle":"旧名","sessionId":"t3"}""", """{"type":"agent-name","agentName":"agent-名","sessionId":"t3"}""", """{"type":"custom-title","customTitle":"新名","sessionId":"t3"}""");
            // 都没有：目录名（cwd 的最后一段）。
            Transcript("t4");
            // 桌面应用的 title 压过一切。
            Transcript("t5", """{"type":"custom-title","customTitle":"记录里的名","sessionId":"t5"}""");
            DesktopFile("local_t5", "local_t5", "t5", "桌面标题", null);

            var sessions = Scan(TriggerTool.Claude);
            Expect(Only(sessions, "t1").Label.StartsWith("请帮我把设置页的深色模式 修一下", StringComparison.Ordinal), "last-prompt is flattened to one line");
            Expect(Only(sessions, "t1").Label.EndsWith("…", StringComparison.Ordinal) && Only(sessions, "t1").Label.Length <= 41, "and cut");
            Expect(Only(sessions, "t2").Label, "agent-名");
            Expect(Only(sessions, "t3").Label, "新名");
            Expect(Only(sessions, "t4").Label, "proj");
            Expect(Only(sessions, "t5").Label, "桌面标题");
        });
    }

    private static void ClaudeTitleKept()
    {
        Isolated(_ =>
        {
            var path = WorkingParent("k1", """{"type":"custom-title","customTitle":"留着","sessionId":"k1"}""");
            Expect(SessionScanner.ClaudeTranscriptTitle(path), "留着");
            // 之后写了一大段没有标题记录的内容，把标题挤出尾部窗口。
            var filler = new string('x', 2000);
            File.AppendAllLines(path, Enumerable.Range(0, 200).Select(i => UserLine(Now, filler)));
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            Expect(SessionScanner.ClaudeTranscriptTitle(path), "留着");
        });
    }

    private static void ClaudeAgentPhase()
    {
        var at = Now.AddSeconds(-3);
        var lines = new[] { UserLine(at.AddSeconds(-4), "task", sidechain: true), ToolUseLine(at, "Bash") };
        var phase = AgentPhaseClassifier.ClaudeAgent(lines, Now);
        Expect(phase.Phase, AgentPhase.Running);
        // 同样的行按主会话读，旁支被跳过，什么也读不到。
        Expect(AgentPhaseClassifier.Claude(lines, Now).Phase, AgentPhase.Idle);
    }

    // MARK: - Kimi

    private static string KimiSession(string id, string stateJson, DateTimeOffset modified, params string[] mainWire)
    {
        var dir = Path.Combine(IslandPaths.KimiSessionsRoot, "wd_proj_1234", "session_" + id);
        Write(Path.Combine(dir, "state.json"), modified, stateJson);
        Write(Path.Combine(dir, "agents", "main", "wire.jsonl"), modified, mainWire);
        return dir;
    }

    private static void KimiAgentWire(string dir, string agent, DateTimeOffset modified, params string[] lines) =>
        Write(Path.Combine(dir, "agents", agent, "wire.jsonl"), modified, lines);

    private static string KimiStateJson(string title, params (string Id, string Parent, bool Swarm)[] agents)
    {
        var entries = new List<string> { "\"main\":{\"homedir\":\"x\",\"type\":\"main\"}" };
        foreach (var (id, parent, swarm) in agents)
        {
            var labels = swarm ? $"{{\"parentAgentId\":\"{parent}\",\"swarmItem\":\"i\"}}" : $"{{\"parentAgentId\":\"{parent}\"}}";
            entries.Add($"\"{id}\":{{\"homedir\":\"x\",\"type\":\"sub\",\"parentAgentId\":\"{parent}\",\"labels\":{labels}}}");
        }
        var head = title.Length > 0 ? $"\"title\":\"{title}\"," : "";
        return $$$"""{"id":"s","cwd":"C:\\work\\proj",{{{head}}}"lastPrompt":"最后一句话","agents":{ {{{string.Join(",", entries)}}} }}""";
    }

    private static string KimiOpen(DateTimeOffset at) => $$"""{"type":"agent.turn.started","time":{{Ms(at)}}}""";

    private static string KimiSpawned(string id, string name, string description, DateTimeOffset at, int swarmIndex = 0) =>
        $$"""{"type":"subagent.spawned","subagentId":"{{id}}","subagentName":"{{name}}","parentAgentId":"main","description":"{{description}}","swarmIndex":{{swarmIndex}},"time":{{Ms(at)}}}""";

    private static void KimiNesting()
    {
        Isolated(_ =>
        {
            var dir = KimiSession("n1",
                KimiStateJson("多层任务", ("agent-1", "main", false), ("agent-2", "agent-1", false), ("agent-3", "agent-2", false), ("agent-4", "main", false)),
                Now.AddSeconds(-2),
                KimiOpen(Now.AddSeconds(-30)),
                KimiSpawned("agent-1", "coder", "第一层", Now.AddSeconds(-25)),
                KimiSpawned("agent-4", "explore", "已经完成的", Now.AddSeconds(-24)),
                $$"""{"type":"subagent.completed","subagentId":"agent-4","time":{{Ms(Now.AddSeconds(-10))}}}""");
            KimiAgentWire(dir, "agent-1", Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-20)),
                KimiSpawned("agent-2", "coder", "第二层", Now.AddSeconds(-18)));
            KimiAgentWire(dir, "agent-2", Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-15)),
                KimiSpawned("agent-3", "coder", "第三层", Now.AddSeconds(-12)));
            KimiAgentWire(dir, "agent-3", Now.AddSeconds(-1), KimiOpen(Now.AddSeconds(-10)));
            KimiAgentWire(dir, "agent-4", Now.AddSeconds(-10), KimiOpen(Now.AddSeconds(-24)));

            var sessions = Scan(TriggerTool.Kimi);
            Expect(sessions.Count, 1);
            var session = sessions[0];
            Expect(session.Label, "多层任务");
            Expect(session.Status, ActivityState.Working);
            Expect(session.Children.Select(n => n.Id).OrderBy(i => i, StringComparer.Ordinal).SequenceEqual(new[] { "agent-1", "agent-4" }), "two agents hang on the main agent");
            var first = session.Children.Single(n => n.Id == "agent-1");
            Expect(first.Name, "第一层");
            Expect(first.AgentType, "coder");
            Expect(first.State, TaskNodeState.Running);
            Expect(first.Level, 1);
            // 第二层留在第一层下面；第三层压平到第二层的缩进上，紧跟在第二层后面，保留自己的层级。
            Expect(first.Children.Select(n => (n.Id, n.Level)).SequenceEqual(new[] { ("agent-2", 2), ("agent-3", 3) }), "the third level sits on the second, keeping its own level");
            Expect(first.Children.All(n => n.Children.Count == 0), "nothing is nested past the second level");
            Expect(session.Children.Single(n => n.Id == "agent-4").State, TaskNodeState.Done);
            Expect(TaskTrees.Count(session.Children), 4);
        });
    }

    private static void KimiSwarm()
    {
        Isolated(_ =>
        {
            var dir = KimiSession("w1",
                KimiStateJson("群", ("agent-1", "main", true), ("agent-2", "main", true), ("agent-3", "main", false)),
                Now.AddSeconds(-2),
                KimiOpen(Now.AddSeconds(-60)),
                $$"""{"type":"swarm_mode.enter","agentId":"main","trigger":"manual","time":{{Ms(Now.AddSeconds(-50))}}}""",
                KimiSpawned("agent-1", "coder", "甲", Now.AddSeconds(-49), swarmIndex: 1),
                KimiSpawned("agent-2", "coder", "乙", Now.AddSeconds(-48), swarmIndex: 2),
                $$"""{"type":"swarm_mode.exit","agentId":"main","time":{{Ms(Now.AddSeconds(-40))}}}""",
                KimiSpawned("agent-3", "explore", "丙", Now.AddSeconds(-20)));
            foreach (var agent in new[] { "agent-1", "agent-2", "agent-3" }) KimiAgentWire(dir, agent, Now.AddSeconds(-1), KimiOpen(Now.AddSeconds(-30)));

            var session = Scan(TriggerTool.Kimi).Single();
            var swarm = session.Children.Single(n => n.Kind == TaskNodeKind.Swarm);
            Expect(swarm.Children.Select(n => n.Id).OrderBy(i => i, StringComparer.Ordinal).SequenceEqual(new[] { "agent-1", "agent-2" }), "the swarm holds the agents it started");
            Expect(swarm.State, TaskNodeState.Running);
            Expect(swarm.Children.All(n => n.Level == 2), "the agents are the swarm's second level");
            Expect(session.Children.Single(n => n.Id == "agent-3").Kind, TaskNodeKind.Subagent);
        });
    }

    private static void KimiTitle()
    {
        Isolated(_ =>
        {
            KimiSession("a", KimiStateJson("有标题"), Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)));
            KimiSession("b", KimiStateJson(""), Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)));
            KimiSession("c", """{"id":"c","cwd":"C:\\work\\proj"}""", Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)));
            var sessions = Scan(TriggerTool.Kimi);
            Expect(Only(sessions, "a").Label, "有标题");
            Expect(Only(sessions, "b").Label, "最后一句话");
            Expect(Only(sessions, "c").Label, "proj");
        });
    }

    private static void KimiUnreadable()
    {
        Isolated(_ =>
        {
            KimiSession("bad", "{ not json", Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)));
            var dir = KimiSession("miss",
                KimiStateJson("缺记录", ("agent-1", "main", false)), Now.AddSeconds(-2), KimiOpen(Now.AddSeconds(-30)),
                KimiSpawned("agent-1", "coder", "没有 wire", Now.AddSeconds(-20)));
            var sessions = Scan(TriggerTool.Kimi);
            Expect(sessions.Count, 2);
            Expect(Only(sessions, "bad").Label, "bad");
            Expect(Only(sessions, "bad").Children.Count, 0);
            Expect(Only(sessions, "miss").Status, ActivityState.Working);
            Expect(Only(sessions, "miss").Children.Count, 0);
            Expect(Directory.Exists(dir), "the fixture is there");
        });
    }

    // MARK: - Codex

    private static string CodexRollout(string id, string metaPayloadExtra, DateTimeOffset modified, bool open = true)
    {
        var dir = Path.Combine(IslandPaths.CodexSessionsRoot, "2026", "11", "26");
        var meta = $$$"""{"type":"session_meta","timestamp":"{{{Iso(modified.AddMinutes(-1))}}}","payload":{"id":"{{{id}}}","cwd":"C:\\work\\proj","originator":"codex_cli_rs"{{{metaPayloadExtra}}}}}""";
        var start = $$$"""{"type":"event_msg","timestamp":"{{{Iso(modified.AddSeconds(-10))}}}","payload":{"type":"task_started"}}""";
        var end = $$$"""{"type":"event_msg","timestamp":"{{{Iso(modified.AddSeconds(-1))}}}","payload":{"type":"task_complete"}}""";
        return Write(Path.Combine(dir, $"rollout-2026-11-26T10-00-00-{id}.jsonl"), modified, open ? new[] { meta, start } : new[] { meta, start, "{\"type\":\"response_item\",\"timestamp\":\"" + Iso(modified.AddSeconds(-5)) + "\",\"payload\":{\"type\":\"reasoning\"}}", end });
    }

    private static void CodexSpawned()
    {
        Isolated(_ =>
        {
            CodexRollout("par", ",\"source\":\"cli\"", Now.AddSeconds(-3));
            // 真实样本里 parent_thread_id 在 payload 上；旧夹具里它在 source.subagent.thread_spawn 里。
            CodexRollout("kid1", ",\"parent_thread_id\":\"par\",\"thread_source\":\"subagent\",\"agent_role\":\"worker\",\"source\":{\"subagent\":\"spawn\"}", Now.AddSeconds(-2));
            CodexRollout("kid2", ",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"par\",\"depth\":1}}}", Now.AddSeconds(-60), open: false);
            // 孙线程。
            CodexRollout("grand", ",\"parent_thread_id\":\"kid1\",\"source\":{\"subagent\":\"spawn\"}", Now.AddSeconds(-1));

            var sessions = Scan(TriggerTool.Codex);
            Expect(sessions.Count, 1);
            var parent = sessions[0];
            Expect(parent.SessionId, "par");
            Expect(parent.Children.Select(n => n.Id).OrderBy(i => i, StringComparer.Ordinal).SequenceEqual(new[] { "kid1", "kid2" }), "both shapes of the parent id hang under the parent");
            var first = parent.Children.Single(n => n.Id == "kid1");
            Expect(first.State, TaskNodeState.Running);
            Expect(first.AgentType, "worker");
            Expect(first.Children.Select(n => n.Id).SequenceEqual(new[] { "grand" }), "a spawned thread's own children follow");
            Expect(first.Children[0].Level, 2);
            Expect(parent.Children.Single(n => n.Id == "kid2").State, TaskNodeState.Done);
        });
    }

    private static void CodexGuardianSkipped()
    {
        Isolated(_ =>
        {
            CodexRollout("par", ",\"source\":\"cli\"", Now.AddSeconds(-3));
            CodexRollout("g1", ",\"parent_thread_id\":\"par\",\"thread_source\":\"guardian_review\",\"source\":{\"subagent\":\"guardian\"}", Now.AddSeconds(-2));
            var sessions = Scan(TriggerTool.Codex);
            Expect(sessions.Count, 1);
            Expect(sessions[0].Children.Count, 0);
        });
    }

    private static void CodexMetaParent()
    {
        var direct = SessionScanner.ParseCodexMeta(
            """{"type":"session_meta","payload":{"id":"c","cwd":"C:\\p","originator":"Codex Desktop","parent_thread_id":"p1","thread_source":"guardian_review","source":{"subagent":"guardian"}}}""");
        Expect(direct is { ParentId: "p1", ThreadSource: "guardian_review", Kind: SessionScanner.CodexRolloutKind.Subagent }, "payload.parent_thread_id and thread_source");
        var nested = SessionScanner.ParseCodexMeta(
            """{"type":"session_meta","payload":{"id":"c","cwd":"C:\\p","originator":"codex_cli_rs","source":{"subagent":{"thread_spawn":{"parent_thread_id":"p2","depth":1}}}}}""");
        Expect(nested is { ParentId: "p2" }, "thread_spawn.parent_thread_id");
        var none = SessionScanner.ParseCodexMeta("""{"type":"session_meta","payload":{"id":"c","cwd":"C:\\p","source":"cli"}}""");
        Expect(none is { ParentId: "" }, "an ordinary thread has no parent");
    }

    // MARK: - 层级、不变量、增量读

    private static TaskNode Node(string id, params TaskNode[] children) =>
        new(id, id, TaskNodeKind.Subagent, TaskNodeState.Running, null) { Children = children };

    private static void DepthFlatten()
    {
        var tree = new[] { Node("a", Node("b", Node("c", Node("d")), Node("e")), Node("f")), Node("g") };
        var capped = TaskTrees.Cap(tree);
        Expect(capped.Select(n => n.Id).SequenceEqual(new[] { "a", "g" }), "level 1 is untouched");
        var a = capped[0];
        Expect(a.Children.Select(n => (n.Id, n.Level)).SequenceEqual(new[] { ("b", 2), ("c", 3), ("d", 4), ("e", 3), ("f", 2) }),
            "deeper nodes follow their level-2 ancestor with their own level");
        Expect(a.Children.All(n => n.Children.Count == 0), "no nesting below level 2");
        Expect(TaskTrees.Count(capped), 7);
    }

    private static void FlatListUnchanged()
    {
        Isolated(_ =>
        {
            WorkingParent("u1");
            Agent(Path.Combine(ClaudeProject, "u1", "subagents", "agent-r1.jsonl"), "agent-r1", "跑着", "Explore", open: true, finished: false, Now.AddSeconds(-1));
            Agent(Path.Combine(ClaudeProject, "u1", "subagents", "agent-r2.jsonl"), "agent-r2", "跑着", "Explore", open: true, finished: false, Now.AddSeconds(-1));
            var sessions = Scan(TriggerTool.Claude);
            Expect(sessions.Count, 1);
            Expect(TaskTrees.Count(sessions[0].Children), 2);
            // 子任务在跑不让运行数变多，也不让最新会话变多。
            Expect(ActivityMonitor.RunningCount(sessions, TriggerTool.Claude), 1);
            Expect(ActivityMonitor.LatestSessions(sessions).Count(), 1);
            // 关掉任务树，平铺列表完全一样。
            SessionScanner.TaskTreesEnabled = false;
            try
            {
                var plain = Scan(TriggerTool.Claude);
                Expect(plain.Count, 1);
                Expect(plain[0].Status, sessions[0].Status);
                Expect(plain[0].Modified, sessions[0].Modified);
                Expect(plain[0].TurnKey, sessions[0].TurnKey);
                Expect(plain[0].Children.Count, 0);
            }
            finally
            {
                SessionScanner.TaskTreesEnabled = true;
            }
        });
    }

    private static void LineIndexIncremental()
    {
        var path = Path.Combine(Path.GetTempPath(), "limen-lineindex-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var reads = 0;
        var index = new LineIndex<List<string>>(["\"hit\""], () => new List<string>(), (state, line) => { reads++; state.Add(line); });
        try
        {
            File.WriteAllText(path, "{\"a\":1}\n{\"hit\":1}\n{\"hit\":2");
            Expect(index.Read(path, state => state.Count, -1), 1);
            // 半行补完之后才算数；已经读过的不再解码。
            File.AppendAllText(path, "}\n{\"b\":2}\n{\"hit\":3}\n");
            Expect(index.Read(path, state => state.Count, -1), 3);
            Expect(reads, 3);
            Expect(index.Read(path, state => state.Count, -1), 3);
            Expect(reads, 3);
            // 文件被重写得更短：从头再来。
            File.WriteAllText(path, "{\"hit\":9}\n");
            Expect(index.Read(path, state => state.Count, -1), 1);
            Expect(index.Read(path + ".missing", state => state.Count, -1), -1);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
