using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Cost;

/// Usage 页的额度区（设计稿 kit-panel-tasks-v1 §5A）：悬浮卡上只留一行紧凑额度，
/// 完整的额度条、重置倒计时、多久前同步、Kimi 本月拆分和加油包余额放在这里。
/// 只有官方程序自己报额度的几家（Claude、Codex、Kimi）有卡；数字和 Agents 页
/// 读的是同一份 AgentReports。
internal sealed class QuotaSection : IDisposable
{
    private static readonly DisplayProvider[] WithQuota = { DisplayProvider.Claude, DisplayProvider.Codex, DisplayProvider.Kimi };

    private readonly StackPanel _view = new() { Spacing = 12 };
    private readonly Subscriptions _subscriptions = new();
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _clock;
    private readonly Func<bool> _visible;
    private readonly Dictionary<DisplayProvider, Card> _cards = new();
    private readonly bool _pixel = Looks.Pixel;
    private string _lineup = "\0";
    private bool _pending;
    private bool _disposed;

    /// 一张卡上会变的部分。
    private sealed class Card
    {
        public required DisplayProvider Provider { get; init; }
        public required TextBlock Plan { get; init; }
        public required Border PlanBadge { get; init; }
        public required TextBlock Status { get; init; }
        public required StackPanel Meters { get; init; }
        public required StackPanel Notes { get; init; }
        public List<(IQuotaBar Bar, TextBlock Label, TextBlock Value, TextBlock Resets, FrameworkElement Row)> Rows { get; } = new();
    }

    public QuotaSection(DispatcherQueue queue, Func<bool> visible)
    {
        _queue = queue;
        _visible = visible;
        Render();
        Watch(UsageStore.Shared);
        Watch(KimiUsageStore.Shared);
        Watch(QuotaDisplayModeStore.Shared);
        // 抽屉里的顺序和显示开关一变，ProviderVisibilityStore 也会通知。
        Watch(ProviderVisibilityStore.Shared);
        // “3 分钟前同步”和重置倒计时在两次刷新之间也要走。
        _clock = queue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(5);
        _clock.Tick += (_, _) =>
        {
            if (Live()) Render();
        };
        _clock.Start();
    }

    public FrameworkElement View => _view;

    public void Dispose()
    {
        _disposed = true;
        _clock.Stop();
        _subscriptions.Dispose();
    }

    private void Watch(INotifyPropertyChanged source)
    {
        PropertyChangedEventHandler handler = (_, _) =>
        {
            if (_pending) return;
            _pending = true;
            _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _pending = false;
                if (Live()) Render();
            });
        };
        _subscriptions.Watch(source, handler);
    }

    /// 还在页面上，而且外观没变：换外观时窗口会整页重建，旧的这份不能再用另一套外观的资源。
    private bool Live() => !_disposed && Looks.Pixel == _pixel && _visible();

    /// 抽屉里的顺序，只取检测到、没被隐藏、而且有官方额度的几家。
    private static List<DisplayProvider> Shown() => DrawerStore.Shared.Order
        .Where(provider => WithQuota.Contains(provider)
            && ProviderVisibilityStore.Shared.IsShown(provider)
            && ProviderVisibilityStore.Shared.IsCharacterDetected(provider))
        .ToList();

    /// 哪几家有卡变了就重建，否则原地更新（额度条从旧值数到新值）。
    private void Render()
    {
        var shown = Shown();
        var lineup = string.Join(",", shown);
        if (lineup != _lineup)
        {
            _lineup = lineup;
            _cards.Clear();
            _view.Children.Clear();
            if (shown.Count == 0)
            {
                _view.Children.Add(new TextBlock
                {
                    Text = L10n.Tr("No quota to show yet. Claude, Codex and Kimi show theirs here once detected and synced."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
                });
                return;
            }
            var cards = shown.Select(provider =>
            {
                var card = Build(provider, out var element);
                _cards[provider] = card;
                return element;
            }).ToList();
            _view.Children.Add(CostPage.Columns(cards, 2, CostPage.TwoColumns));
        }
        foreach (var card in _cards.Values) Update(card, AgentReports.For(card.Provider));
    }

    private Card Build(DisplayProvider provider, out FrameworkElement element)
    {
        var plan = new TextBlock();
        var badge = Badge(plan, provider);
        var status = new TextBlock
        {
            Style = Res<Style>("CaptionTextBlockStyle"),
            Foreground = Res<Brush>("TextFillColorTertiaryBrush"),
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 180,
        };
        var head = CostPage.Head(provider, status);
        // 名字后面跟套餐徽章（MAX、PLUS…），和 Agents 页一样。
        if (head.Children.OfType<TextBlock>().FirstOrDefault() is { } name)
        {
            head.Children.Remove(name);
            var named = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { name, badge } };
            Grid.SetColumn(named, 1);
            head.Children.Add(named);
        }
        var meters = new StackPanel { Spacing = _pixel ? 8 : 10 };
        var notes = new StackPanel { Spacing = 4 };
        element = CostPage.Surface(new StackPanel { Spacing = 12, Children = { head, meters, notes } });
        return new Card { Provider = provider, Plan = plan, PlanBadge = badge, Status = status, Meters = meters, Notes = notes };
    }

    private void Update(Card card, AgentReport report)
    {
        card.Plan.Text = report.Plan ?? "";
        card.PlanBadge.Visibility = string.IsNullOrEmpty(report.Plan) ? Visibility.Collapsed : Visibility.Visible;
        card.Status.Text = report.Status;

        while (card.Rows.Count < report.Meters.Count) card.Rows.Add(Row(card));
        var accent = Accent(card.Provider);
        for (var i = 0; i < card.Rows.Count; i++)
        {
            var (bar, label, value, resets, row) = card.Rows[i];
            if (i >= report.Meters.Count)
            {
                row.Visibility = Visibility.Collapsed;
                continue;
            }
            var meter = report.Meters[i];
            row.Visibility = Visibility.Visible;
            label.Text = Settings.AgentSection.MeterTitle(meter.Label);
            resets.Text = meter.Resets ?? "";
            resets.Visibility = meter.Resets is null ? Visibility.Collapsed : Visibility.Visible;
            var text = meter.Value;
            var final = meter.Shown ?? 0;
            bar.Set(meter.Shown, accent, meter.Used >= 0.9, Brand.IsDark(), (share, warn) =>
            {
                value.Text = QuotaCount.Percent(share, final, text);
                if (_pixel) return;
                value.Foreground = Res<Brush>(warn ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
                value.FontWeight = warn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            });
        }
        card.Meters.Visibility = report.Meters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Kimi 本月拆分和加油包余额，以及要处理的警告（例如要重新登录）。
        card.Notes.Children.Clear();
        if (card.Provider == DisplayProvider.Kimi)
        {
            foreach (var phrase in StatusCards.KimiNote()) card.Notes.Children.Add(Note(phrase.Line, "TextFillColorSecondaryBrush"));
        }
        if (report.Warning is { } warning) card.Notes.Children.Add(Note(warning, "SystemFillColorCautionBrush"));
        card.Notes.Visibility = card.Notes.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// 一个额度窗口：名字、条和数字一行，重置倒计时在条下面。
    private (IQuotaBar, TextBlock, TextBlock, TextBlock, FrameworkElement) Row(Card card)
    {
        FrameworkElement bar = _pixel
            ? new PixelBar(30) { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left }
            : new QuotaBar(8) { VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        if (!_pixel) label.Style = Res<Style>("BodyTextBlockStyle");
        var value = new TextBlock
        {
            MinWidth = _pixel ? 64 : 48,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
        };
        var resets = new TextBlock
        {
            Style = Res<Style>("CaptionTextBlockStyle"),
            Foreground = Res<Brush>("TextFillColorTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var row = new Grid { ColumnSpacing = 12, RowSpacing = 2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_pixel ? 96 : 104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(value, 2);
        Grid.SetRow(resets, 1);
        Grid.SetColumn(resets, 1);
        Grid.SetColumnSpan(resets, 2);
        row.Children.Add(label);
        row.Children.Add(bar);
        row.Children.Add(value);
        row.Children.Add(resets);
        card.Meters.Children.Add(row);
        return ((IQuotaBar)bar, label, value, resets, row);
    }

    private static TextBlock Note(string text, string brush) => new()
    {
        Text = text,
        Style = Res<Style>("CaptionTextBlockStyle"),
        Foreground = Res<Brush>(brush),
        TextWrapping = TextWrapping.Wrap,
    };

    /// 条用角色色，和同页的图表一样。
    private static Windows.UI.Color Accent(DisplayProvider provider) => Looks.Pixel
        ? PixelTokens.Agent(provider, Brand.IsDark())
        : Brand.Rgb(Brand.Readable(DrawerLooks.ClassicSwatch(provider), Brand.IsDark()));
}
