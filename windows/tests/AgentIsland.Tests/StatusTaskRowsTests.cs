using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 悬浮卡的任务列表（第 7 批）：主对话、子任务的层级和折叠、状态、额度行和 token 合计。
/// 全是内存里的假会话，不读任何文件。
public static class StatusTaskRowsTests
{
    public static void RunAll()
    {
        var previous = L10n.Current;
        L10n.Current = L10n.Language.English;
        try
        {
            var tests = new (string Name, Action Test)[]
            {
                ("a working conversation is one running main row saying what it does", WorkingMain),
                ("D4: a finished turn waits on you, an error is an error, both for fifteen minutes", FinishedAndFailed),
                ("an open prompt and unread mail wait on you; an idle session has no row", PromptAndMail),
                ("subagents hang one level in, their own children two levels in", Nesting),
                ("past three children two show, errors and waits first, and the fold counts what runs", FoldPicksUrgent),
                ("of equals the earliest started show, in start order", FoldKeepsEarliest),
                ("every child that errs or waits shows, however many", UrgentAlwaysShows),
                ("a finished subtask shows for a minute", DoneForAMinute),
                ("what the scan left unexpanded is counted in the fold", OmittedCounted),
                ("a side conversation under its parent is not listed again", SideConversationOnce),
                ("D4: a side conversation whose turn ended waits on you under its parent, past the minute a done subtask gets", SideConversationWaits),
                ("a fold never stands for one row alone", FoldNeverForOne),
                ("past three conversations the rest are one row", MoreConversations),
                ("a section keeps to its line budget by folding the lowest subtrees", LineBudget),
                ("names are cleaned to one line, and missing names say what they are", Names),
                ("a Chinese title stays as written in English", ChineseTitleKept),
                ("the panel summary counts what needs you and what runs", Summary),
                ("the summary counts errors, and never subtasks as running", SummaryErrorsAndSubtasks),
                ("a conversation waiting on you is shown however old, and first", WaitingConversationFirst),
                ("a conversation whose subtask needs you is not folded away, and the hidden ones still count", UrgentSubtreeShown),
                ("a grandchild's error lifts its folded parent into view", GrandchildErrorLiftsParent),
                ("a tree deeper than three levels is drawn no deeper than the second indent", DepthCappedInRows),
                ("what the scan left out under a second-level node is counted in its fold", SecondLevelOmittedCounted),
                ("the card's words are its own (design §5.1), not the shared phase labels", CardWords),
                ("the compact quota drops unread windows and says syncing", CompactQuota),
                ("the compact quota knows its windows by label, not by place", CompactQuotaByLabel),
                ("the total adds today's and this month's, skipping a stale agent", TotalTokens),
                ("phases are read only for sessions that are not idle", PhasesOnlyForBusy),
            };
            foreach (var (name, test) in tests)
            {
                test();
                Console.WriteLine("PASS status task rows: " + name);
            }
        }
        finally
        {
            L10n.Current = previous;
        }
        Console.WriteLine("StatusTaskRowsTests GREEN");
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static ScannedSession Session(string id, ActivityState status, string label = "Refactor the card", int minutesAgo = 0) =>
        new(TriggerTool.Claude, id, "/work", label, Now.AddMinutes(-minutesAgo), status, $"/work/{id}.jsonl", null, SessionLaunchTarget.Cli);

    private static TaskNode Node(string id, TaskNodeState state, int startedSecondsAgo, string? name = null) =>
        new(id, name ?? "task " + id, TaskNodeKind.Subagent, state, Now.AddSeconds(-startedSecondsAgo)) { UpdatedAt = Now.AddSeconds(-1) };

    private static IReadOnlyList<StatusTaskRow> Rows(params ScannedSession[] sessions) =>
        StatusTaskRows.Build(sessions, new Dictionary<string, PhaseReading>(), Now, pixel: false);

    private static IReadOnlyList<StatusTaskRow> Rows(ScannedSession session, PhaseReading phase) =>
        StatusTaskRows.Build(new[] { session }, new Dictionary<string, PhaseReading> { [session.Id] = phase }, Now, pixel: false);

    private static void WorkingMain()
    {
        var rows = Rows(Session("a", ActivityState.Working), new PhaseReading(AgentPhase.Writing, "Edit", Now));
        Expect(rows.Count, 1);
        var row = rows[0];
        Expect(row.IsMain, "main");
        Expect(row.State, StatusTaskState.Running);
        Expect(row.Name, "Refactor the card");
        Expect(row.Activity, "Editing code");
        Expect(row.Word, "Editing");
        Expect(row.Object, "Edit");
        // 记录跟不上扫描时就是笼统的“在跑”。
        Expect(Rows(Session("b", ActivityState.Working))[0].Activity, "Working");
    }

    private static void FinishedAndFailed()
    {
        var done = Rows(Session("a", ActivityState.NeedsYou, minutesAgo: 3));
        Expect(done[0].State, StatusTaskState.NeedsYou);
        Expect(done[0].Activity, "Waiting for your reply");
        var failed = Rows(Session("b", ActivityState.NeedsYou, minutesAgo: 3) with { TurnFailed = true });
        Expect(failed[0].State, StatusTaskState.Error);
        Expect(Rows(Session("c", ActivityState.NeedsYou, minutesAgo: 16)).Count, 0);
        Expect(Rows(Session("d", ActivityState.NeedsYou, minutesAgo: 16) with { TurnFailed = true }).Count, 0);
        var offline = Rows(Session("e", ActivityState.NetworkTrouble), new PhaseReading(AgentPhase.NetworkTrouble, "Reconnecting · attempt 3 of 10", Now));
        Expect(offline[0].State, StatusTaskState.Error);
        Expect(offline[0].Object, "Reconnecting · attempt 3 of 10");
        // 断网中的回合还在重试，不过期。
        Expect(Rows(Session("f", ActivityState.NetworkTrouble, minutesAgo: 30))[0].State, StatusTaskState.Error);
    }

    private static void PromptAndMail()
    {
        var prompt = Rows(Session("a", ActivityState.Working) with { PendingPrompt = new PhaseReading(AgentPhase.Approving, null, Now) });
        Expect(prompt[0].State, StatusTaskState.NeedsYou);
        Expect(prompt[0].Activity, "Waiting for your approval");
        Expect(Rows(Session("b", ActivityState.Idle)).Count, 0);
        var mail = Rows(Session("c", ActivityState.Idle) with { UnreadMail = new[] { new MailFile("1.json", Now), new MailFile("2.json", Now) } });
        Expect(mail[0].State, StatusTaskState.NeedsYou);
        Expect(mail[0].Activity, "2 new messages");
    }

    private static void Nesting()
    {
        var session = Session("a", ActivityState.Working) with
        {
            Children = new[]
            {
                Node("s1", TaskNodeState.Running, 30) with { Children = new[] { Node("w1", TaskNodeState.Running, 20) with { Level = 2 } } },
                Node("s2", TaskNodeState.Running, 10),
            },
        };
        var rows = Rows(session);
        Expect(string.Join(",", rows.Select(r => $"{r.Id}:{r.Depth}")), "claude:a:0,s1:1,w1:2,s2:1");
        Expect(rows[1].Word, "Working");
    }

    private static TaskNode[] Five(params TaskNodeState[] states) =>
        states.Select((state, i) => Node("n" + i, state, 100 - i * 10)).ToArray();

    private static void FoldPicksUrgent()
    {
        var session = Session("a", ActivityState.Working) with
        {
            Children = Five(TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Error, TaskNodeState.Running, TaskNodeState.WaitingOnYou),
        };
        var rows = Rows(session);
        Expect(string.Join(",", rows.Skip(1).Select(r => r.Id)), "n2,n4,claude:a/more");
        Expect(rows[3].Name, "+3 subtasks · 3 running");
        Expect(rows[3].Kind, StatusRowKind.MoreSubtasks);
        Expect(rows[3].Depth, 1);
    }

    private static void FoldKeepsEarliest()
    {
        var session = Session("a", ActivityState.Working) with { Children = Five(TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Running) };
        var rows = Rows(session);
        Expect(string.Join(",", rows.Skip(1).Select(r => r.Id)), "n0,n1,claude:a/more");
        Expect(rows[3].Name, "+2 subtasks · 2 running");
        var three = Rows(Session("b", ActivityState.Working) with { Children = Five(TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Done) });
        Expect(three.Count, 4);
    }

    private static void UrgentAlwaysShows()
    {
        var session = Session("a", ActivityState.Working) with
        {
            Children = Five(TaskNodeState.Error, TaskNodeState.Error, TaskNodeState.WaitingOnYou, TaskNodeState.Running, TaskNodeState.Running),
        };
        var rows = Rows(session);
        Expect(string.Join(",", rows.Skip(1).Select(r => r.Id)), "n0,n1,n2,claude:a/more");
        Expect(rows[4].Name, "+2 subtasks · 2 running");
    }

    private static void FoldNeverForOne()
    {
        // 三条要紧的加一条在跑：藏一条换一行折叠行不划算，四条都露。
        var four = Rows(Session("a", ActivityState.Working) with
        {
            Children = Five(TaskNodeState.Error, TaskNodeState.Error, TaskNodeState.WaitingOnYou, TaskNodeState.Running),
        });
        Expect(four.Count(r => r.Kind == StatusRowKind.MoreSubtasks), 0);
        Expect(four.Count, 5);
        // 扫描时就省掉了一些，折叠行本来就在：藏下的那一条并进去。
        var omitted = Rows(Session("b", ActivityState.Working) with
        {
            Children = Five(TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Running),
            ChildrenOmitted = 2,
        });
        Expect(omitted.Last().Name, "+2 subtasks");
        Expect(omitted.Count(r => r.Kind == StatusRowKind.Task && !r.IsMain), 3);
    }

    private static void DoneForAMinute()
    {
        var session = Session("a", ActivityState.Working) with
        {
            Children = new[]
            {
                Node("fresh", TaskNodeState.Done, 100) with { UpdatedAt = Now.AddSeconds(-30) },
                Node("old", TaskNodeState.Done, 200) with { UpdatedAt = Now.AddSeconds(-90) },
            },
        };
        var rows = Rows(session);
        Expect(string.Join(",", rows.Skip(1).Select(r => r.Id)), "fresh");
        Expect(rows[1].State, StatusTaskState.Done);
        Expect(rows[1].Word, "Done");
    }

    private static void OmittedCounted()
    {
        var session = Session("a", ActivityState.Working) with
        {
            Children = new[] { Node("n0", TaskNodeState.Running, 10) },
            ChildrenOmitted = 4,
            ChildrenOmittedRunning = 2,
        };
        var rows = Rows(session);
        Expect(rows.Last().Name, "+4 subtasks · 2 running");
    }

    private static void SideConversationOnce()
    {
        var kid = Session("kid", ActivityState.Working, "Side chat");
        var parent = Session("parent", ActivityState.Working) with
        {
            Children = new[] { new TaskNode("kid", "Side chat", TaskNodeKind.Conversation, TaskNodeState.Running, Now) { ConversationId = kid.Id } },
        };
        var rows = Rows(parent, kid);
        Expect(rows.Count(r => r.IsMain), 1);
        Expect(rows.Count(r => r.Name == "Side chat"), 1);
    }

    private static void SideConversationWaits()
    {
        var kid = Session("kid", ActivityState.NeedsYou, "Side chat", minutesAgo: 2);
        var parent = Session("parent", ActivityState.Working) with
        {
            Children = new[]
            {
                new TaskNode("kid", "Side chat", TaskNodeKind.Conversation, TaskNodeState.Done, Now.AddMinutes(-5))
                {
                    ConversationId = kid.Id,
                    UpdatedAt = kid.Modified,
                },
            },
        };
        var rows = Rows(parent, kid);
        var side = rows.Single(r => r.Name == "Side chat");
        Expect(side.Depth, 1);
        Expect(side.State, StatusTaskState.NeedsYou);
        Expect(side.Word, "Your turn");
        Expect(rows.Count(r => r.IsMain), 1);
    }

    private static void MoreConversations()
    {
        var rows = Rows(
            Session("a", ActivityState.Working, minutesAgo: 1),
            Session("b", ActivityState.Working, minutesAgo: 2),
            Session("c", ActivityState.NeedsYou, minutesAgo: 3),
            Session("d", ActivityState.Working, minutesAgo: 4),
            Session("e", ActivityState.Working, minutesAgo: 5));
        Expect(rows.Count(r => r.IsMain), 3);
        // 等你的排在前面，而且一定在露出的三个里。
        Expect(rows[0].Id, "claude:c");
        Expect(rows.Last().Kind, StatusRowKind.MoreConversations);
        Expect(rows.Last().Name, "+2 more conversations");
    }

    private static void LineBudget()
    {
        ScannedSession Busy(string id) => Session(id, ActivityState.Working) with
        {
            Children = new[]
            {
                Node(id + "1", TaskNodeState.Running, 30) with { Children = new[] { Node(id + "1a", TaskNodeState.Running, 20) with { Level = 2 } } },
                Node(id + "2", TaskNodeState.Error, 20),
                Node(id + "3", TaskNodeState.Running, 10),
            },
        };
        var classic = StatusTaskRows.Build(new[] { Busy("a"), Busy("b") }, new Dictionary<string, PhaseReading>(), Now, pixel: false);
        var pixel = StatusTaskRows.Build(new[] { Busy("a"), Busy("b") }, new Dictionary<string, PhaseReading>(), Now, pixel: true);
        int Lines(IEnumerable<StatusTaskRow> rows) => rows.Sum(r => r.IsMain ? 2 : 1);
        Expect(Lines(classic) <= StatusTaskRows.ClassicMaxLines, "classic budget: " + Lines(classic));
        Expect(Lines(pixel) <= StatusTaskRows.PixelMaxLines, "pixel budget: " + Lines(pixel));
        // 出错的子任务压不掉。
        Expect(pixel.Count(r => r.State == StatusTaskState.Error), 2);
    }

    private static void Names()
    {
        Expect(StatusTaskRows.Clean("  /review\n\n the **card**  `now` "), "review the card now");
        Expect(StatusTaskRows.Clean("# > Title"), "Title");
        Expect(Rows(Session("a", ActivityState.Working, label: "  "))[0].Name, "Untitled conversation");
        var session = Session("b", ActivityState.Working) with
        {
            Children = new[] { new TaskNode("x", "", TaskNodeKind.Subagent, TaskNodeState.Running, Now) { AgentType = "Explore" } },
        };
        Expect(Rows(session)[1].Name, "Subtask · Explore");
    }

    private static void ChineseTitleKept()
    {
        var rows = Rows(Session("a", ActivityState.Working, label: "重构 kit panel 的布局"));
        Expect(rows[0].Name, "重构 kit panel 的布局");
    }

    private static void SummaryErrorsAndSubtasks()
    {
        var failed = Rows(Session("a", ActivityState.NeedsYou, minutesAgo: 2) with { TurnFailed = true });
        Expect(StatusTaskRows.Summary(failed), "1 failed");
        var busy = Rows(Session("b", ActivityState.Working) with
        {
            Children = new[] { Node("s1", TaskNodeState.Running, 20), Node("s2", TaskNodeState.Running, 10) },
        });
        Expect(StatusTaskRows.Summary(busy), "1 running");
        var mixed = Rows(
            Session("c", ActivityState.Working) with { Children = new[] { Node("e", TaskNodeState.Error, 10) } },
            Session("d", ActivityState.NeedsYou, minutesAgo: 1));
        Expect(StatusTaskRows.Summary(mixed), "1 need you · 1 failed · 1 running");
        Expect(StatusTaskRows.Summary(mixed, urgentOnly: true), "1 need you · 1 failed");
    }

    private static void WaitingConversationFirst()
    {
        var rows = Rows(
            Session("a", ActivityState.Working, minutesAgo: 1),
            Session("b", ActivityState.Working, minutesAgo: 2),
            Session("c", ActivityState.Working, minutesAgo: 3),
            Session("d", ActivityState.NeedsYou, minutesAgo: 4));
        Expect(rows[0].Id, "claude:d");
        Expect(rows.Count(r => r.IsMain), 3);
        Expect(rows.Any(r => r.Id == "claude:c"), false);
    }

    private static void UrgentSubtreeShown()
    {
        var rows = Rows(
            Session("a", ActivityState.Working, minutesAgo: 1),
            Session("b", ActivityState.Working, minutesAgo: 2),
            Session("c", ActivityState.Working, minutesAgo: 3),
            Session("d", ActivityState.Working, minutesAgo: 4) with { Children = new[] { Node("ask", TaskNodeState.WaitingOnYou, 10) } },
            Session("e", ActivityState.Working, minutesAgo: 5) with { Children = new[] { Node("bad", TaskNodeState.Error, 10) } });
        Expect(rows.Any(r => r.Id == "ask"), true);
        Expect(rows.Any(r => r.Id == "bad"), true);
        var more = rows.Last();
        Expect(more.Kind, StatusRowKind.MoreConversations);
        Expect(more.Hidden, new StatusTally(0, 0, 2));
        Expect(StatusTaskRows.Summary(rows), "1 need you · 1 failed · 5 running");
    }

    private static void GrandchildErrorLiftsParent()
    {
        var kids = Five(TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Running, TaskNodeState.Running);
        kids[4] = kids[4] with { Children = new[] { Node("deep", TaskNodeState.Error, 5) with { Level = 2 } } };
        var rows = Rows(Session("a", ActivityState.Working) with { Children = kids });
        Expect(string.Join(",", rows.Skip(1).Select(r => r.Id)), "n0,n4,deep,claude:a/more");
        Expect(rows.Single(r => r.Id == "deep").Depth, 2);
        Expect(rows.Last().Name, "+3 subtasks · 3 running");
    }

    private static void DepthCappedInRows()
    {
        // 不经 Core 的压平，直接给四层：第 3、4 层都画在第 2 层的缩进上。
        var tree = Node("l1", TaskNodeState.Running, 40) with
        {
            Children = new[]
            {
                Node("l2", TaskNodeState.Running, 30) with
                {
                    Children = new[] { Node("l3", TaskNodeState.Running, 20) with { Children = new[] { Node("l4", TaskNodeState.Running, 10) } } },
                },
            },
        };
        var rows = Rows(Session("a", ActivityState.Working) with { Children = new[] { tree } });
        Expect(string.Join(",", rows.Select(r => $"{r.Id}:{r.Depth}")), "claude:a:0,l1:1,l2:2,l3:2,l4:2");
    }

    private static void SecondLevelOmittedCounted()
    {
        var tree = Node("l1", TaskNodeState.Running, 40) with
        {
            Children = new[] { Node("l2", TaskNodeState.Running, 30) with { Level = 2, Omitted = 5, OmittedRunning = 1 } },
        };
        var rows = Rows(Session("a", ActivityState.Working) with { Children = new[] { tree } });
        var fold = rows.Last();
        Expect(fold.Kind, StatusRowKind.MoreSubtasks);
        Expect(fold.Depth, 2);
        Expect(fold.Name, "+5 subtasks · 1 running");
    }

    private static void CardWords()
    {
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            Expect(StatusTaskRows.Full(AgentPhase.Approving), "等你批准");
            Expect(StatusTaskRows.Full(AgentPhase.NetworkTrouble), "网络中断");
            Expect(StatusTaskRows.Full(AgentPhase.Finished), "等你回复");
            Expect(StatusTaskRows.Full(AgentPhase.Streaming), "写回复");
            Expect(StatusTaskRows.Full(AgentPhase.Failed), "出错");
            Expect(StatusTaskRows.Full(AgentPhase.AuthRequired), "需要重新登录");
            Expect(StatusTaskRows.Full(AgentPhase.Working), "在跑");
            Expect(StatusTaskRows.Short(AgentPhase.Approving), "等你批准");
            Expect(StatusTaskRows.Short(AgentPhase.NetworkTrouble), "断网");
            Expect(StatusTaskRows.Short(AgentPhase.Compacting), "压缩中");
            // 共用的说法不变：Settings 等处照旧。
            Expect(DrawerLooks.PhaseLabel(AgentPhase.NetworkTrouble), "网络异常");
            Expect(L10n.Tr("Needs OK"), "待批准");
        }
        finally { L10n.Current = L10n.Language.English; }
        Expect(StatusTaskRows.Full(AgentPhase.Approving), "Waiting for your approval");
        Expect(StatusTaskRows.Short(AgentPhase.Approving), "Needs OK");
        Expect(StatusTaskRows.Short(AgentPhase.Finished), "Your turn");
        Expect(StatusTaskRows.Short(AgentPhase.WebSearching), "Web");
        // 每个 phase 的两种说法都有中文。
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            foreach (var phase in Enum.GetValues<AgentPhase>())
            {
                Expect(StatusTaskRows.Full(phase).Any(c => c >= '\u4E00'), "zh full " + phase);
                Expect(StatusTaskRows.Short(phase).Any(c => c >= '\u4E00'), "zh short " + phase);
            }
        }
        finally { L10n.Current = L10n.Language.English; }
    }

    private static void Summary()
    {
        var rows = Rows(
            Session("a", ActivityState.Working),
            Session("b", ActivityState.Working),
            Session("c", ActivityState.NeedsYou));
        Expect(StatusTaskRows.Summary(rows), "1 need you · 2 running");
        Expect(StatusTaskRows.Summary(rows, urgentOnly: true), "1 need you");
        Expect(StatusTaskRows.Summary(Rows(Session("a", ActivityState.Working))), "1 running");
        Expect(StatusTaskRows.Summary(Array.Empty<StatusTaskRow>()), "All idle");
        L10n.Current = L10n.Language.SimplifiedChinese;
        try { Expect(StatusTaskRows.Summary(rows), "1 个在等你 · 2 个在跑"); }
        finally { L10n.Current = L10n.Language.English; }
    }

    private static void CompactQuota()
    {
        var quota = StatusTaskRows.Quota(new[]
        {
            ("5h", new StatusMeter("5h", "93%", 0.93, 0.93)),
            ("Wk", new StatusMeter("Week", "61%", 0.61, 0.61)),
        }, stale: false);
        Expect(quota?.Text, "5h 93% · Wk 61%");
        Expect(quota?.Busiest?.Text, "5h 93%");
        Expect(quota!.Windows[0].Warn, "93 % warns");
        Expect(!quota.Windows[1].Warn, "61 % does not");
        var partial = StatusTaskRows.Quota(new[]
        {
            ("5h", new StatusMeter("5h", "—", null, null)),
            ("Wk", new StatusMeter("Week", "—", null, null, "Awaiting sync")),
        }, stale: true);
        Expect(partial?.Text, "Wk syncing");
        Expect(partial!.Stale, "stale");
        Expect(StatusTaskRows.Quota(new[] { ("5h", new StatusMeter("5h", "—", null, null)) }, stale: false) is null, "nothing readable, no quota");
    }

    private static void CompactQuotaByLabel()
    {
        // Kimi 没有 5 小时窗口时，StatusCards.Meters 给的是“周、月”。
        var kimi = StatusTaskRows.QuotaOf(new[]
        {
            new StatusMeter(L10n.Tr("Week"), "61%", 0.61, 0.61),
            new StatusMeter(L10n.Tr("Month"), "12%", 0.12, 0.12),
        }, stale: false);
        Expect(kimi?.Text, "Wk 61%");
        var claude = StatusTaskRows.QuotaOf(new[]
        {
            new StatusMeter(L10n.Tr("5h"), "38%", 0.38, 0.38),
            new StatusMeter(L10n.Tr("Week"), "61%", 0.61, 0.61),
            new StatusMeter(L10n.Tr("Month"), "12%", 0.12, 0.12),
        }, stale: false);
        Expect(claude?.Text, "5h 38% · Wk 61%");
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            var zh = StatusTaskRows.QuotaOf(new[]
            {
                new StatusMeter(L10n.Tr("5h"), "38%", 0.38, 0.38),
                new StatusMeter(L10n.Tr("Week"), "剩 39%", 0.39, 0.61),
            }, stale: false);
            Expect(zh?.Text, "5h 38% · 周 剩 39%");
        }
        finally { L10n.Current = L10n.Language.English; }
    }

    private static void TotalTokens()
    {
        var today = new TokenTotals(Now, 1_000_000, 500_000, 30_000_000, 9_000_000);
        var yesterday = new TokenTotals(Now.AddDays(-1), 400_000, 1, 8_000_000, 1);
        var total = StatusTaskRows.Total(new TokenTotals?[] { today, yesterday, null }, TokenCountMode.All, Now);
        Expect(total.Label, "Total");
        Expect(total.TodayNumber, Formatting.CompactTokens(1_000_000));
        Expect(total.MonthNumber, Formatting.CompactTokens(38_000_000));
        var stale = StatusTaskRows.Total(new TokenTotals?[] { yesterday }, TokenCountMode.All, Now);
        Expect(stale.TodayNumber, "—");
        Expect(StatusTaskRows.Total(Array.Empty<TokenTotals?>(), TokenCountMode.All, Now).TodayNumber, "");
    }

    private static void PhasesOnlyForBusy()
    {
        var read = new List<string>();
        var phases = ActivityMonitor.PhasesOf(new[]
        {
            Session("a", ActivityState.Working),
            Session("b", ActivityState.Idle),
            Session("c", ActivityState.NeedsYou),
        }, Now, (_, path, _) =>
        {
            read.Add(Path.GetFileNameWithoutExtension(path));
            return new PhaseReading(AgentPhase.Reading, null, Now);
        });
        Expect(string.Join(",", read), "a,c");
        Expect(phases.Count, 2);
    }
}
