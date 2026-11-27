using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

// The hover card: opening, refreshing and closing it, and what it says.
internal sealed partial class DrawerWindow
{
    /// Hovering a character opens a card pointing at it; null stacks every
    /// character's status in one card.
    private void OpenCard(Bot? only)
    {
        _cardClose.Stop();
        _cardClose.Interval = TimeSpan.FromMilliseconds(250);
        _cardBot = only;
        // What the queue held is on the card's list.
        foreach (var bot in _bots.Where(b => only is null || b == only)) bot.Speech.Clear();
        if (only is null) Hush();
        else only.Bubble?.Hide(animated: false);
        ShowCard();
    }

    // 上次因为缓存率和速度刷新卡的时刻（秒，_clock）。
    private double _metricsCardAt;

    private void RefreshCard()
    {
        if (_closed || !_card.IsOpen || _cardRefreshPending) return;
        _cardRefreshPending = true;
        if (!_queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _cardRefreshPending = false;
            if (!_closed && _card.IsOpen) ShowCard();
        })) _cardRefreshPending = false;
    }

    /// The card of the character the provider works under (Grok's for Cursor).
    private void RefreshCardFor(DisplayProvider provider)
    {
        if (_cardBot is null || _cardBot.Provider == provider.Character()) RefreshCard();
    }

    /// 卡上显示的：任务列表、紧凑额度、警告和 token。应用不在时头部右侧写它为什么不在。
    private StatusCard CardFor(Bot bot, CharacterTheme theme) =>
        StatusCards.For(bot.Provider, bot.Phase, bot.Detail, bot.Running) with
        {
            TaskRows = TasksFor(bot, theme),
            Quota = StatusTaskRows.Quota(bot.Provider),
            Loading = !ActivityMonitor.Shared.HasScanned,
            Warnings = AgentReports.Warnings(bot.Provider),
            Away = bot.Rest is { } rest ? RestDetail(rest, bot.Provider) : null,
        };

    /// 这个角色名下各会话的任务列表。角色自己的头号会话用角色正在演的 phase，好和动画说的一致。
    /// Grok 的角色演的可能是 Cursor 的 phase（SharedSubscription.Pick），那时不挂到 Grok 的会话上。
    private IReadOnlyList<StatusTaskRow> TasksFor(Bot bot, CharacterTheme theme)
    {
        var monitor = ActivityMonitor.Shared;
        var sessions = monitor.Sessions.Where(s => s.Tool.ToDisplayProvider().Character() == bot.Provider);
        var phases = new Dictionary<string, PhaseReading>(monitor.Phases, StringComparer.Ordinal);
        if (bot.Rest is null && bot.Phase != AgentPhase.Idle
            && _activity.ForProvider(bot.Tool.ToDisplayProvider()).Phase == bot.Phase
            && monitor.ThreadFor(bot.Tool) is { } thread)
        {
            phases[bot.Tool.RawValue() + ":" + thread.SessionId] = new PhaseReading(bot.Phase, bot.Detail, null);
        }
        return StatusTaskRows.Build(sessions, phases, DateTimeOffset.UtcNow, theme == CharacterTheme.Pixel, SessionMetricsStore.Shared.Snapshot());
    }

    /// 全员卡的面板：默认钉 Claude 的额度（D3，悬停某个角色时会换成它的单角色卡，那里有它自己的额度），
    /// 底部一行是所有角色的 token 合计（D2）。
    private HoverCardPanel PanelFor(IEnumerable<Bot> bots)
    {
        var shown = bots.ToList();
        var pinned = shown.Any(b => b.Provider == DisplayProvider.Claude) ? StatusTaskRows.Quota(DisplayProvider.Claude) : null;
        var total = StatusTaskRows.Total(shown.Select(b => StatusTaskRows.Totals(b.Provider)), TokenCountModeStore.Shared.Mode, DateTimeOffset.Now);
        return new HoverCardPanel(pinned, ProviderIdentity.DisplayName(DisplayProvider.Claude), total);
    }

    private void ShowCard()
    {
        if (!_shown) return;
        var dark = Brand.IsDark();
        var pixel = _theme == CharacterTheme.Pixel;
        var bots = Ordered(_bots.Where(b => b.Visible && (_cardBot is null || b == _cardBot))).ToList();
        var sections = bots
            .Select(b => (CardFor(b, _theme), Brand.Readable(SwatchOf(b), dark)))
            .ToList();
        var height = WindowHeightDip(_bots.Count(b => b.Visible));
        var anchor = _cardBot?.Bounds ?? new Rect(InnerInset, height / 2 - BotSize / 2, BotSize, BotSize);
        _card.Show(sections, pixel, DrawerStore.Shared.Side == DrawerSide.Right, anchor,
            CardArea(), _reducedMotion, _cardBot is null ? PanelFor(bots) : null);
    }

    /// Where the card may go: the screen's work area, or with
    /// AGENTISLAND_DEBUG_WORK_HEIGHT=<DIP> only that much of it, to check the
    /// all-agents card's columns as on a shorter screen.
    private Rect CardArea()
    {
        var area = WorkAreaDip();
        if (double.TryParse(Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_WORK_HEIGHT"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var height) && height > 0 && height < area.Height)
        {
            area.Height = height;
        }
        return area;
    }

    /// Closes the card once the pointer is off every character; the region
    /// clips the window, so leaving one circle for the next exits it first.
    private void CloseCardUnlessHovered()
    {
        _cardClose.Stop();
        // D5：全员卡在指针停在卡上时不自动关，离开后再过一秒关。
        if (_cardBot is null && CursorOnCard())
        {
            _cardClose.Interval = TimeSpan.FromSeconds(1);
            _cardClose.Start();
            return;
        }
        if (BotUnderCursor() is { } bot && _cardBot is not null)
        {
            if (bot != _cardBot) OpenCard(bot);
            return;
        }
        _card.Hide(animated: true);
        _cardBot = null;
    }

    /// 卡本身不接指针（穿透），所以按指针的屏幕位置换算成抽屉的 DIP 去比卡的范围。
    private bool CursorOnCard()
    {
        if (!Platform.Win32.GetCursorPos(out var cursor)) return false;
        var scene = _scene;
        return _card.Covers(new Point((cursor.X - scene.Left) / scene.Scale, (cursor.Y - scene.Top) / scene.Scale));
    }

    private void OpenCardIfStillHovered()
    {
        _cardIntent.Stop();
        if (_pressed is null && !_cardHeldBack && BotUnderCursor() is { } bot) OpenCard(bot);
    }
}
