using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Cost;

/// What the local logs say each agent used: a period picker, totals, and one
/// card per agent with its spend or tokens, a bar chart and its top models.
/// Dollars are API list-price estimates; agents without published prices
/// (Cursor, Kimi Code, DeepSeek) show tokens only, and Antigravity keeps no
/// local log.
internal sealed class CostWindow : Window
{
    private enum Period
    {
        Today,
        Week,
        Month,
    }

    private const string PeriodKey = "Cost.period";
    private const double ChartHeight = 72;

    private static CostWindow? _shared;

    private readonly SelectorBar _periods = new();
    private readonly StackPanel _body = new() { Spacing = 12, MaxWidth = 1100, Padding = new Thickness(40, 4, 40, 40) };
    private readonly TextBlock _scanned = new();
    private readonly List<Action> _teardown = new();
    private readonly DispatcherQueue _queue;
    private Period _period;
    private bool _pending;

    public static void Open()
    {
        _shared ??= new CostWindow();
        _shared.Activate();
    }

    /// AGENTISLAND_DEBUG_SNAPSHOT: each period, rendered by the app itself.
    public static async Task SnapshotAsync(string directory)
    {
        Open();
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        foreach (var period in Enum.GetValues<Period>())
        {
            window._periods.SelectedItem = window._periods.Items[(int)period];
            await Task.Delay(1200);
            if (window.Content is { } content) await Snapshot.SaveAsync(content, Path.Combine(directory, $"cost-{period}.png"));
        }
    }

    private CostWindow()
    {
        _queue = DispatcherQueue;
        var root = Chrome.Apply(this, "Agent Island — " + L10n.Tr("Cost"), 1040, 760, 640, 480);
        // The title and the period picker stay put; the numbers scroll.
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(Header());
        var scroll = new ScrollViewer { Content = _body, HorizontalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(scroll, 1);
        page.Children.Add(scroll);
        Chrome.SetBody(root, page);

        Watch(CostStore.Shared);
        Watch(ProviderVisibilityStore.Shared);
        Watch(TokenCountModeStore.Shared);
        Closed += (_, _) =>
        {
            foreach (var teardown in _teardown) teardown();
            _shared = null;
        };
        Render();
    }

    private void Watch(INotifyPropertyChanged source)
    {
        // The scan raises once per provider; one render covers the burst.
        PropertyChangedEventHandler handler = (_, _) =>
        {
            if (_pending) return;
            _pending = true;
            _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _pending = false;
                Render();
            });
        };
        source.PropertyChanged += handler;
        _teardown.Add(() => source.PropertyChanged -= handler);
    }

    /// One agent's numbers for the chosen period.
    private sealed record Slice(DisplayProvider Provider, bool Dollars, double Spend, long Tokens, long Billable,
        IReadOnlyList<(string Label, double Value)> Bars, IReadOnlyList<ModelSpend> Models);

    private FrameworkElement Header()
    {
        foreach (var (label, glyph) in new[] { ("Today", "\uE823"), ("Last 7 days", "\uE787"), ("This month", "\uE8BF") })
        {
            _periods.Items.Add(new SelectorBarItem { Text = L10n.Tr(label), Icon = Icon(glyph) });
        }
        _period = Enum.TryParse<Period>(Preferences.Get<string?>(PeriodKey), out var saved) ? saved : Period.Today;
        _periods.SelectedItem = _periods.Items[(int)_period];
        _periods.SelectionChanged += (_, _) =>
        {
            _period = (Period)Math.Max(0, _periods.Items.IndexOf(_periods.SelectedItem));
            Preferences.Set(PeriodKey, _period.ToString());
            Render();
        };

        var header = new Grid { ColumnSpacing = 16, MaxWidth = 1100, Padding = new Thickness(40, 20, 40, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(PageTitle(L10n.Tr("Cost")));
        titles.Children.Add(PageIntro(L10n.Tr("Estimated at API list prices from this computer's session logs — not a bill.")));
        titles.Children.Add(_periods);
        header.Children.Add(titles);
        var side = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var rescan = Button(L10n.Tr("Scan now"), () => CostStore.Shared.Refresh());
        rescan.HorizontalAlignment = HorizontalAlignment.Right;
        side.Children.Add(rescan);
        _scanned.Foreground = Res<Brush>("TextFillColorTertiaryBrush");
        _scanned.Style = Res<Style>("CaptionTextBlockStyle");
        _scanned.HorizontalAlignment = HorizontalAlignment.Right;
        _scanned.Margin = new Thickness(0, 0, 0, 10);
        side.Children.Add(_scanned);
        Grid.SetColumn(side, 1);
        header.Children.Add(side);
        return header;
    }

    private void Render()
    {
        _body.Children.Clear();
        _scanned.Text = CostStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("Last scanned {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("Not scanned yet");

        var shown = DisplayProviders.All.Where(ProviderVisibilityStore.Shared.IsShown).ToList();
        var slices = shown.Where(p => p != DisplayProvider.Antigravity).Select(SliceFor).ToList();
        _body.Children.Add(Totals(slices));

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cards = slices.Select(AgentCard).ToList();
        if (shown.Contains(DisplayProvider.Antigravity)) cards.Add(NoLogCard(DisplayProvider.Antigravity));
        for (var i = 0; i < cards.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cards[i], i / 2);
            Grid.SetColumn(cards[i], i % 2);
            grid.Children.Add(cards[i]);
        }
        _body.Children.Add(grid);
    }

    private Slice SliceFor(DisplayProvider provider)
    {
        var summary = CostStore.Shared.Summary(provider);
        var dollars = ProvidesDollars(provider);
        var billableMode = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
        var today = DateTimeOffset.Now.Date;
        double DayValue(DailyTokenBucket day) => dollars ? day.Dollars : billableMode ? day.BillableTokens : day.Tokens;
        var byDay = summary.DailyHistory.GroupBy(d => d.DayStart.Date).ToDictionary(g => g.Key, g => g.Sum(DayValue));
        // Every day of the period gets a bar, idle days included.
        IReadOnlyList<(string, double)> Days(DateTime from)
        {
            var days = new List<(string, double)>();
            for (var day = from; day <= today; day = day.AddDays(1))
            {
                days.Add((day.ToString(L10n.IsChinese ? "M月d日" : "MMM d"), byDay.GetValueOrDefault(day)));
            }
            return days;
        }

        switch (_period)
        {
            case Period.Today:
                // Hourly increments of the cumulative spend; token-only
                // agents have no hourly series.
                var hours = new List<(string, double)>();
                if (dollars)
                {
                    var cumulative = summary.TodayCumulativeDollars;
                    for (var h = 0; h < cumulative.Count; h++) hours.Add(($"{h}:00", cumulative[h] - (h > 0 ? cumulative[h - 1] : 0)));
                }
                return new Slice(provider, dollars, summary.TodayDollars, summary.TodayTokens, summary.TodayBillableTokens, hours, summary.RecentModels);
            case Period.Week:
                var week = summary.WeeklyModels;
                return new Slice(provider, dollars, week.Sum(m => m.Dollars), week.Sum(m => m.Tokens), week.Sum(m => m.BillableTokens),
                    Days(today.AddDays(-6)), week);
            default:
                return new Slice(provider, dollars, summary.MonthDollars, summary.MonthTokens, summary.MonthBillableTokens,
                    Days(new DateTime(today.Year, today.Month, 1)), summary.MonthModels);
        }
    }

    /// Cursor bills by its own plan and requests, not tokens; Antigravity
    /// keeps no log. Everyone else has API list prices.
    private static bool ProvidesDollars(DisplayProvider provider) =>
        provider is not (DisplayProvider.Cursor or DisplayProvider.Antigravity);

    /// A model the price table does not know and the log did not price.
    private static bool Unpriced(ModelSpend model) =>
        model.Dollars == 0 && model.Tokens > 0 && !Pricing.IsKnown(model.Model);

    private static string Tokens(Slice slice) => Formatting.CompactTokens(
        TokenCountModeStore.Shared.Mode == TokenCountMode.Billable ? slice.Billable : slice.Tokens);

    private UIElement Totals(IReadOnlyList<Slice> slices)
    {
        var billable = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
        var spend = slices.Where(s => s.Dollars).Sum(s => s.Spend);
        var tokens = slices.Sum(s => billable ? s.Billable : s.Tokens);
        var busiest = slices.OrderByDescending(s => billable ? s.Billable : s.Tokens).FirstOrDefault();
        var row = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Stat(int column, string label, string value, string caption)
        {
            var card = Surface(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label, Foreground = Res<Brush>("TextFillColorSecondaryBrush") },
                    new TextBlock { Text = value, Style = Res<Style>("SubtitleTextBlockStyle") },
                    new TextBlock { Text = caption, Style = Res<Style>("CaptionTextBlockStyle"), Foreground = Res<Brush>("TextFillColorTertiaryBrush") },
                },
            });
            Grid.SetColumn(card, column);
            row.Children.Add(card);
        }
        var unpriced = slices.Where(s => s.Dollars).SelectMany(s => s.Models).Count(Unpriced);
        Stat(0, L10n.Tr("Estimated cost"), Formatting.Money(spend), unpriced > 0
            ? L10n.TrFormat("{0} models have no price and count as $0", unpriced)
            : L10n.Tr("At API list prices"));
        Stat(1, L10n.Tr("Tokens"), Formatting.CompactTokens(tokens),
            L10n.Tr(billable ? "Input + output" : "All tokens (including cache)"));
        Stat(2, L10n.Tr("Busiest agent"),
            busiest is { } top && (billable ? top.Billable : top.Tokens) > 0 ? ProviderIdentity.DisplayName(top.Provider) : "—",
            busiest is { } b && (billable ? b.Billable : b.Tokens) > 0 ? L10n.TrFormat("{0} tokens", Tokens(b)) : L10n.Tr("No activity yet"));
        return row;
    }

    private FrameworkElement AgentCard(Slice slice)
    {
        var panel = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        _ = SetMark(mark, slice.Provider);
        head.Children.Add(mark);
        var name = new TextBlock { Text = ProviderIdentity.DisplayName(slice.Provider), Style = Res<Style>("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        hero.Children.Add(new TextBlock
        {
            Text = slice.Dollars ? Formatting.Money(slice.Spend) : Tokens(slice),
            Style = Res<Style>("SubtitleTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        hero.Children.Add(new TextBlock
        {
            Text = slice.Dollars ? L10n.TrFormat("{0} tokens", Tokens(slice)) : L10n.Tr("tokens · billed by Cursor's own plan"),
            Style = Res<Style>("CaptionTextBlockStyle"),
            Foreground = Res<Brush>("TextFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        Grid.SetColumn(hero, 2);
        head.Children.Add(hero);
        panel.Children.Add(head);

        // Bars wear the agent's character colour, as in the drawer.
        var look = App.Current.Drawer?.LookOf(slice.Provider) ?? DrawerLooks.For(slice.Provider);
        var accent = Brand.Rgb(Brand.Readable(DrawerLooks.Swatch(look.Color), Brand.IsDark()));
        if (slice.Bars.Count > 0) panel.Children.Add(Chart(slice, accent));

        var models = slice.Models.Take(3).ToList();
        if (models.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = L10n.Tr(_period == Period.Today ? "Models, last 5 hours" : "Models"),
                Style = Res<Style>("CaptionTextBlockStyle"),
                Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
            });
            var billable = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
            foreach (var model in models)
            {
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = model.Model, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 });
                var price = !slice.Dollars ? "" : Unpriced(model) ? "  ·  " + L10n.Tr("no price") : "  ·  " + Formatting.Money(model.Dollars);
                var amount = new TextBlock
                {
                    Text = Formatting.CompactTokens(billable ? model.BillableTokens : model.Tokens) + price,
                    FontSize = 12,
                    Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
                };
                Grid.SetColumn(amount, 1);
                line.Children.Add(amount);
                panel.Children.Add(line);
            }
        }
        else if (slice.Tokens == 0)
        {
            panel.Children.Add(new TextBlock { Text = L10n.Tr("No sessions in this period"), Foreground = Res<Brush>("TextFillColorTertiaryBrush") });
        }
        return Surface(panel);
    }

    /// Bars scaled to the period's largest; each carries its value as a
    /// tooltip.
    private static FrameworkElement Chart(Slice slice, Windows.UI.Color accent)
    {
        var max = slice.Bars.Max(b => b.Value);
        var grid = new Grid { Height = ChartHeight, ColumnSpacing = slice.Bars.Count > 20 ? 2 : 4 };
        for (var i = 0; i < slice.Bars.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (label, value) = slice.Bars[i];
            var height = max > 0 ? Math.Max(value > 0 ? 3 : 1.5, value / max * ChartHeight) : 1.5;
            var column = new Border
            {
                Height = height,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = new SolidColorBrush(value > 0 ? accent : Brand.Rgb(0x808080, 0x40)),
            };
            ToolTipService.SetToolTip(column, $"{label}  ·  {(slice.Dollars ? Formatting.Money(value) : Formatting.CompactTokens((long)value))}");
            Grid.SetColumn(column, i);
            grid.Children.Add(column);
        }
        return grid;
    }

    private static FrameworkElement NoLogCard(DisplayProvider provider)
    {
        var mark = new Image { Width = 20, Height = 20 };
        _ = SetMark(mark, provider);
        return Surface(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        mark,
                        new TextBlock { Text = ProviderIdentity.DisplayName(provider), Style = Res<Style>("BodyStrongTextBlockStyle") },
                    },
                },
                new TextBlock
                {
                    Text = L10n.Tr("Antigravity keeps no local usage log; its quota is on the Agents page in Settings."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
                },
            },
        });
    }

    private static Border Surface(UIElement child) => new()
    {
        Child = child,
        Padding = new Thickness(16, 14, 16, 14),
        CornerRadius = new CornerRadius(8),
        Background = Res<Brush>("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Res<Brush>("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
    };

    private static async Task SetMark(Image image, DisplayProvider provider)
    {
        if (await Brand.MarkAsync(provider, Brand.IsDark()) is { } mark) image.Source = mark;
    }
}
