using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AgentIsland.WinUI.Shell;

/// AGENTISLAND_DEBUG_TASK_SHOTS=<dir>：任务视图的几种情况（设计稿 §1.1–1.4），各存一张 PNG 后退出。
/// 会话、额度和 token 全是假的，经 StatusTaskRows.Build 走一遍真实的折叠和状态规则，
/// 不扫描会话，也不读任何账号。外观用 AGENTISLAND_DEBUG_CARD_LOOK，语言用 AGENTISLAND_LANG，
/// 125% 的 Pixel 字号用 AGENTISLAND_DEBUG_PIXEL_SCALE=1.25 模拟。
internal sealed partial class HoverCardPreview
{
    private static readonly DateTimeOffset ShotNow = DateTimeOffset.UtcNow;

    private async Task TaskShotsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var look = _pixel ? "pixel" : "classic";
        var language = L10n.IsChinese ? "zh" : "en";
        var scale = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_PIXEL_SCALE") is { Length: > 0 } simulated && _pixel
            ? "-sim" + simulated.Replace(".", "", StringComparison.Ordinal)
            : "";
        string File(string take) => Path.Combine(directory, $"{take}-{look}-{language}{scale}.png");
        // 和抽屉一样有整个工作区的高度，全员卡只在真放不下时才分栏。
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X, work.Y, AppWindow.Size.Width, work.Height));
        await Task.Delay(500);
        try
        {
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_TASK_MOTION") == "1")
            {
                await MotionShots(File);
                Close();
                return;
            }
            await Shot(File("1.1-single-one-task"), new[] { (OneTask(), Swatches[0]) }, 0, null);
            await Shot(File("1.2-single-subtasks"), new[] { (WithSubtasks(), Swatches[0]) }, 0, null);
            await Shot(File("1.2b-single-folded"), new[] { (Folded(), Swatches[0]) }, 0, null);
            // 第 8 批：只有缓存率（日志里没有速度，或太久没有新输出）。
            await Shot(File("1.5-single-cache-only"), new[] { (WithSubtasks(cacheOnly: true), Swatches[0]) }, 0, null);
            var all = new[] { (AllClaude(), Swatches[0]), (AllCodex(), Swatches[1]), (IdleKimi(), Swatches[2]) };
            var panel = new HoverCardPanel(ClaudeQuota(), "Claude",
                StatusTaskRows.Total(new TokenTotals?[] { Totals(1_200_000, 38_400_000), Totals(2_200_000, 57_700_000), null }, TokenCountMode.All, DateTimeOffset.Now));
            await Shot(File("1.3-all-agents"), all, 1, panel);
            await Shot(File("1.3b-all-agents-many-conversations"),
                new[] { (ManyConversations(), Swatches[0]), (AllCodex(), Swatches[1]), (IdleKimi(), Swatches[2]) }, 1, panel);
            await Shot(File("1.4-error"), new[] { (Failing(), Swatches[0]) }, 0, null);
            DebugTrace.Write("task shots saved in " + directory);
        }
        catch (Exception error)
        {
            DebugTrace.Write("task shots failed: " + error);
        }
        Close();
    }

    /// AGENTISLAND_DEBUG_TASK_MOTION=1：不减少动效，验证标记动画真的在走。
    /// 同一张卡隔 300 ms 连拍两张（Pixel 在跑的方块该换了角），再把一个子任务改成等你、
    /// 原地刷新（等你的入场：Classic 放大，Pixel 闪两次），刷新后马上拍一张、稍后再拍一张。
    private async Task MotionShots(Func<string, string> file)
    {
        var card = WithSubtasks();
        _card.Hide();
        _card.Show(new[] { (card, Swatches[0]) }, _pixel, characterRight: true, Anchor(0), Area, reducedMotion: false);
        await Task.Delay(800);
        await _card.SnapshotAsync(file("motion-a"));
        await Task.Delay(300);
        await _card.SnapshotAsync(file("motion-b"));
        var rows = card.TaskRows.Select(row => row.Id == "s1" ? row with { State = StatusTaskState.NeedsYou, Word = StatusTaskRows.Short(AgentPhase.Approving) } : row).ToList();
        _card.Show(new[] { (card with { TaskRows = rows }, Swatches[0]) }, _pixel, characterRight: true, Anchor(0), Area, reducedMotion: false);
        await Task.Delay(40);
        await _card.SnapshotAsync(file("motion-entering"));
        await Task.Delay(800);
        await _card.SnapshotAsync(file("motion-settled"));
        // 关掉再打开同一张卡：在跑的标记要接着动（内容没变也重建，重启标记动画）。
        _card.Hide();
        _card.Show(new[] { (card, Swatches[0]) }, _pixel, characterRight: true, Anchor(0), Area, reducedMotion: false);
        await Task.Delay(800);
        await _card.SnapshotAsync(file("motion-reopen-a"));
        await Task.Delay(300);
        await _card.SnapshotAsync(file("motion-reopen-b"));
        DebugTrace.Write("task motion shots done");
    }

    private async Task Shot(string path, (StatusCard, uint)[] sections, int anchor, HoverCardPanel? panel)
    {
        _card.Hide();
        _card.Show(sections, _pixel, characterRight: true, Anchor(anchor), Area, reducedMotion: true, panel);
        await Task.Delay(600);
        await _card.SnapshotAsync(path);
    }

    private static TokenTotals Totals(long today, long month) => new(DateTimeOffset.Now, today, today / 3, month, month / 3);

    private static StatusQuota? ClaudeQuota(double fiveHours = 0.38) => StatusTaskRows.QuotaOf(new[]
    {
        new StatusMeter(L10n.Tr("5h"), $"{fiveHours * 100:0}%", fiveHours, fiveHours),
        new StatusMeter(L10n.Tr("Week"), "61%", 0.61, 0.61),
    }, stale: false);

    private static ScannedSession Conversation(string id, string title, ActivityState status, TriggerTool tool = TriggerTool.Claude) =>
        new(tool, id, @"C:\work\limen", title, ShotNow, status, null, null, SessionLaunchTarget.Cli);

    private static TaskNode Sub(string id, string name, TaskNodeState state, int startedSecondsAgo, AgentPhase? phase = null) =>
        new(id, name, TaskNodeKind.Subagent, state, ShotNow.AddSeconds(-startedSecondsAgo))
        {
            Phase = phase is { } p ? new PhaseReading(p, null, ShotNow) : null,
            UpdatedAt = ShotNow.AddSeconds(-5),
        };

    /// 样本里的缓存率和速度（第 8 批）：在跑的主对话带上，其余按状态自己不显示。
    private static readonly SessionMetrics SampleMetrics = new(92, 41.4, 5, ShotNow);

    private static IReadOnlyList<StatusTaskRow> Rows(bool pixel, IEnumerable<(ScannedSession Session, AgentPhase Phase, string? Detail)> sessions,
        SessionMetrics? metrics = null)
    {
        var list = sessions.ToList();
        var phases = list.ToDictionary(s => s.Session.Id, s => new PhaseReading(s.Phase, s.Detail, ShotNow));
        var each = list.ToDictionary(s => s.Session.Id, _ => metrics ?? SampleMetrics);
        return StatusTaskRows.Build(list.Select(s => s.Session), phases, ShotNow, pixel, each);
    }

    private StatusCard Card(DisplayProvider provider, string name, string? plan, IReadOnlyList<StatusTaskRow> rows, StatusQuota? quota, long today, long month) =>
        new(provider, name, "", null, null, Array.Empty<StatusMeter>(),
            StatusCards.TokensFrom(Totals(today, month), TokenCountMode.All, DateTimeOffset.Now), Array.Empty<StatusPhrase>())
        {
            Plan = plan,
            TaskRows = rows,
            Quota = quota,
        };

    private StatusCard OneTask() => Card(DisplayProvider.Claude, "Claude", "MAX",
        Rows(_pixel, new[] { (Conversation("a", "重构 kit panel 的布局", ActivityState.Working), AgentPhase.Writing, (string?)"Edit") }),
        ClaudeQuota(), 1_200_000, 38_400_000);

    private StatusCard WithSubtasks(bool cacheOnly = false)
    {
        var session = Conversation("a", "重构 kit panel 的布局", ActivityState.Working) with
        {
            Children = new[]
            {
                Sub("s1", "查 HoverCard 的宽度和内边距", TaskNodeState.Running, 90, AgentPhase.Reading),
                Sub("s2", "写设计文档", TaskNodeState.Running, 60, AgentPhase.Writing) with
                {
                    Children = new[] { Sub("w1", "搜 token monitor 的出处", TaskNodeState.Done, 40) with { Level = 2 } },
                },
            },
        };
        return Card(DisplayProvider.Claude, "Claude", "MAX",
            Rows(_pixel, new[] { (session, AgentPhase.WaitingOnAgents, (string?)null) }, cacheOnly ? new SessionMetrics(88, null, 5, ShotNow) : null),
            ClaudeQuota(), 1_200_000, 38_400_000);
    }

    private StatusCard Folded()
    {
        var session = Conversation("a", "Swarm review of the Windows port", ActivityState.Working) with
        {
            Children = new[]
            {
                Sub("s1", "Review HoverCard.Content", TaskNodeState.Running, 90, AgentPhase.Reading),
                Sub("s2", "Review the scanner", TaskNodeState.Running, 80, AgentPhase.Searching),
                Sub("s3", "Check the L10n tables", TaskNodeState.WaitingOnYou, 70, AgentPhase.Approving),
                Sub("s4", "Run the test suite", TaskNodeState.Running, 60, AgentPhase.Running),
                Sub("s5", "Verify the release script", TaskNodeState.Running, 50, AgentPhase.Running),
            },
        };
        return Card(DisplayProvider.Claude, "Claude", "MAX",
            Rows(_pixel, new[] { (session, AgentPhase.WaitingOnAgents, (string?)null) }), ClaudeQuota(), 1_200_000, 38_400_000);
    }

    private StatusCard AllClaude()
    {
        var session = Conversation("a", "重构 kit panel 的布局", ActivityState.Working) with
        {
            Children = new[] { Sub("s1", "查 HoverCard 的宽度和内边距", TaskNodeState.Running, 90, AgentPhase.Reading) },
        };
        return Card(DisplayProvider.Claude, "Claude", "MAX",
            Rows(_pixel, new[] { (session, AgentPhase.Writing, (string?)null) }), ClaudeQuota(), 1_200_000, 38_400_000);
    }

    private StatusCard AllCodex() => Card(DisplayProvider.Codex, "Codex", "PLUS",
        Rows(_pixel, new[]
        {
            (Conversation("c1", "修 Sparkle 发版脚本", ActivityState.Working, TriggerTool.Codex)
                with { PendingPrompt = new PhaseReading(AgentPhase.Approving, "Bash", ShotNow) }, AgentPhase.Approving, (string?)"Bash"),
            (Conversation("c2", "补 UsageFetcher 的测试", ActivityState.Working, TriggerTool.Codex), AgentPhase.Running, null),
        }), null, 2_200_000, 57_700_000);

    private StatusCard KimiBusy() => Card(DisplayProvider.Kimi, "Kimi", null,
        Rows(_pixel, new[] { (Conversation("k1", "整理第 8 批的笔记", ActivityState.Working, TriggerTool.Kimi), AgentPhase.Searching, (string?)null) }),
        null, 96_000, 1_800_000);

    /// 超过 3 个对话的全员卡：Claude 五个对话（一个在等你、一个的子任务出错，都要露出来），其余收进“+N 个对话”。
    private StatusCard ManyConversations() => Card(DisplayProvider.Claude, "Claude", "MAX",
        Rows(_pixel, new[]
        {
            (Conversation("m1", "重构 kit panel 的布局", ActivityState.Working), AgentPhase.Writing, (string?)null),
            (Conversation("m2", "补任务树的测试", ActivityState.Working) with { Modified = ShotNow.AddMinutes(-1) }, AgentPhase.Running, null),
            (Conversation("m3", "核对 Sparkle 公钥", ActivityState.Working) with { Modified = ShotNow.AddMinutes(-2) }, AgentPhase.Reading, null),
            (Conversation("m4", "迁移 OAuth 刷新地址", ActivityState.Working) with
            {
                Modified = ShotNow.AddMinutes(-3),
                Children = new[] { Sub("x1", "核对 platform.claude.com 的响应", TaskNodeState.Error, 60) },
            }, AgentPhase.WaitingOnAgents, null),
            (Conversation("m5", "写第 7 批的回报", ActivityState.NeedsYou) with { Modified = ShotNow.AddMinutes(-6) }, AgentPhase.Finished, null),
        }), ClaudeQuota(), 1_200_000, 38_400_000);

    private StatusCard IdleKimi() => Card(DisplayProvider.Kimi, "Kimi", null, Array.Empty<StatusTaskRow>(), null, 0, 0);

    private StatusCard Failing()
    {
        var session = Conversation("a", "迁移 OAuth 刷新地址", ActivityState.NetworkTrouble) with
        {
            Children = new[]
            {
                Sub("s1", "核对 platform.claude.com 的响应", TaskNodeState.Error, 60),
                Sub("s2", "更新 CLAUDE.md 的说明", TaskNodeState.Done, 30),
            },
        };
        return Card(DisplayProvider.Claude, "Claude", "MAX",
            Rows(_pixel, new[] { (session, AgentPhase.NetworkTrouble, (string?)L10n.TrFormat("Reconnecting · attempt {0} of {1}", 3, 10)) }),
            ClaudeQuota(0.92), 1_200_000, 38_400_000);
    }
}
