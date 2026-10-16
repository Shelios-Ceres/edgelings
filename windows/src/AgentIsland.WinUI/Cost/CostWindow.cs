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
internal sealed partial class CostWindow : Window
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

    // Built for the look the window wears (Build).
    private SelectorBar _periods = null!;
    private StackPanel _body = null!;
    private TextBlock _scanned = null!;
    private TextBlock _periodRange = null!;
    private TextBlock _calendarNote = null!;
    private bool _pixel;
    private readonly List<Action> _teardown = new();
    private readonly DispatcherQueue _queue;
    private Period _period;
    private bool _pending;

    public static void Open()
    {
        // A closed window was only hidden (Chrome.HideOnClose): it comes back
        // in the current look and language.
        if (_shared is { AppWindow.IsVisible: false } hidden) hidden.Build();
        _shared ??= new CostWindow();
        Chrome.Show(_shared);
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
        Build();
        Watch(CostStore.Shared);
        Watch(ProviderVisibilityStore.Shared);
        Watch(TokenCountModeStore.Shared);
        // The other character style brings the other look.
        PropertyChangedEventHandler look = (_, e) =>
        {
            if (e.PropertyName == nameof(DrawerStore.Theme) && Looks.Pixel != _pixel) _queue.TryEnqueue(Rebuild);
        };
        DrawerStore.Shared.PropertyChanged += look;
        _teardown.Add(() => DrawerStore.Shared.PropertyChanged -= look);
        // Closing hides the window; its numbers go until it opens again.
        Chrome.HideOnClose(this, () => _body.Children.Clear());
        Closed += (_, _) =>
        {
            foreach (var teardown in _teardown) teardown();
            _shared = null;
        };
    }

    /// The frame, the header and the numbers in the current look: when the
    /// window opens, and again on every switch.
    private void Build()
    {
        _count?.Stop();
        _glide?.Stop();
        using var english = Looks.English();
        var title = "Agent Island — " + L10n.Tr("Cost");
        var root = Content is null
            ? Chrome.Apply(this, title, 1040, 760, 640, 480)
            : Chrome.Restyle(this, title);
        _pixel = Looks.Pixel;
        _periods = new SelectorBar();
        _body = new StackPanel { Spacing = 12, MaxWidth = 1100, Padding = new Thickness(40, 4, 40, 40) };
        _scanned = new TextBlock();
        _periodRange = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _calendarNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
        // The title and the period picker stay put; the numbers scroll.
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(Header());
        var scroll = PageScroller();
        SetPage(scroll, _body);
        // Under Pixel the numbers sit on a layer panel below the title strip.
        FrameworkElement body = scroll;
        if (_pixel)
        {
            _body.Padding = new Thickness(40, 20, 40, 40);
            body = new Border { Style = Res<Style>("PixelLayerStyle"), Child = scroll };
        }
        Grid.SetRow(body, 1);
        page.Children.Add(body);
        Chrome.SetBody(root, page);
        Render();
    }

    /// In place: closing the window for a new one would leak GDI objects
    /// (Chrome.Restyle). A hidden window is rebuilt when it opens.
    private static void Rebuild()
    {
        if (_shared is { AppWindow.IsVisible: true } window) window.Build();
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
                if (AppWindow.IsVisible) Recount();
            });
        };
        source.PropertyChanged += handler;
        _teardown.Add(() => source.PropertyChanged -= handler);
    }

    /// One agent's numbers for the chosen period.
    private sealed record Slice(DisplayProvider Provider, bool Dollars, double Spend, long Tokens, long Billable,
        IReadOnlyList<(string Label, double Value)> Bars, IReadOnlyList<ModelSpend> Models, DateTimeOffset? AsOf);

    private FrameworkElement Header()
    {
        foreach (var (label, glyph) in new[] { ("Today", "\uE823"), ("Last 7 days", "\uE787"), ("This month", "\uE8BF") })
        {
            var item = new SelectorBarItem { Text = L10n.Tr(label), Icon = Icon(glyph), Style = Looks.Kit<Style>("SelectorBarItemStyle") };
            _periods.Items.Add(item);
        }
        _period = Enum.TryParse<Period>(Preferences.Get<string?>(PeriodKey), out var saved) ? saved : Period.Today;
        _periods.SelectedItem = _periods.Items[(int)_period];
        var periods = _periods;
        periods.SelectionChanged += (_, _) =>
        {
            var before = _period;
            _period = (Period)Math.Max(0, periods.Items.IndexOf(periods.SelectedItem));
            Preferences.Set(PeriodKey, _period.ToString());
            SwitchPeriod((int)before, (int)_period);
        };

        var header = new Grid { ColumnSpacing = 16, MaxWidth = 1100, Padding = new Thickness(40, _pixel ? 20 : 24, 40, 8) };
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(PageTitle(L10n.Tr("Cost")));
        titles.Children.Add(PageIntro(L10n.Tr("Estimated at API list prices from this computer's session logs — not a bill.")));
        _tabs = new Grid();
        _tabMarker = TabMarker();
        _tabs.Children.Add(_tabMarker);
        _tabs.Children.Add(_periods);
        if (_pixel)
        {
            titles.Children.Add(_tabs);
        }
        else
        {
            titles.Children.Add(new Border { Style = Looks.Kit<Style>("TrackStyle"), Margin = new Thickness(0, 10, 0, 0), Child = _tabs });
        }
        _periodRange.Style = Res<Style>("CaptionTextBlockStyle");
        _periodRange.Foreground = Res<Brush>("TextFillColorSecondaryBrush");
        _periodRange.Margin = new Thickness(0, _pixel ? 8 : 12, 0, 0);
        _calendarNote.Style = Res<Style>("CaptionTextBlockStyle");
        _calendarNote.Foreground = Res<Brush>("TextFillColorTertiaryBrush");
        var captions = new StackPanel { Children = { _periodRange, _calendarNote } };
        Grid.SetRow(captions, 1);
        Grid.SetColumnSpan(captions, 2);
        header.Children.Add(captions);
        header.Children.Add(titles);
        var side = new StackPanel { Spacing = 8, MaxWidth = 160, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var rescan = Button(L10n.Tr("Scan now"), () => CostStore.Shared.Refresh());
        rescan.HorizontalAlignment = HorizontalAlignment.Right;
        side.Children.Add(rescan);
        _scanned.Foreground = Res<Brush>("TextFillColorTertiaryBrush");
        _scanned.Style = Res<Style>("CaptionTextBlockStyle");
        _scanned.HorizontalAlignment = HorizontalAlignment.Right;
        _scanned.TextWrapping = TextWrapping.Wrap;
        _scanned.TextAlignment = TextAlignment.Right;
        _scanned.Margin = new Thickness(0, 0, 0, 10);
        side.Children.Add(_scanned);
        Grid.SetColumn(side, 1);
        header.Children.Add(side);
        return header;
    }

    private void Render()
    {
        Commit(Compose());
        Remember();
    }

    private void Commit(List<UIElement> page)
    {
        _body.Children.Clear();
        foreach (var part in page) _body.Children.Add(part);
    }

    /// The page for the chosen period, built apart from the window; its
    /// numbers and chart columns are kept for counting (M17).
    private List<UIElement> Compose()
    {
        using var english = Looks.English();
        _numbers.Clear();
        _columns.Clear();
        _scanned.Text = CostStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("Last scanned {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("Not scanned yet");

        // In the drawer's order.
        var shown = DrawerStore.Shared.Order.Where(ProviderVisibilityStore.Shared.IsShown).ToList();
        var slices = shown.Where(p => p != DisplayProvider.Antigravity).Select(SliceFor).ToList();
        UpdatePeriodCaption(slices);
        var page = new List<UIElement> { Totals(slices) };

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cards = shown.Select(provider => provider == DisplayProvider.Antigravity
            ? NoLogCard(provider)
            : AgentCard(slices.First(slice => slice.Provider == provider))).ToList();
        for (var i = 0; i < cards.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cards[i], i / 2);
            Grid.SetColumn(cards[i], i % 2);
            grid.Children.Add(cards[i]);
        }
        page.Add(grid);
        return page;
    }

    private void UpdatePeriodCaption(IReadOnlyList<Slice> slices)
    {
        var period = _period switch
        {
            Period.Week => UsageDisplayPeriod.Week,
            Period.Month => UsageDisplayPeriod.Month,
            _ => UsageDisplayPeriod.Today,
        };
        var stamps = slices.Where(s => s.AsOf is not null).Select(s => s.AsOf!.Value).ToList();
        var ranges = stamps.Select(stamp => TokenDisplayText.PeriodRange(period, stamp)).Distinct().ToList();
        _periodRange.Text = L10n.TrFormat("{0} · Local time zone: {1}",
            ranges.Count == 0 ? L10n.Tr("Not scanned yet") : string.Join(" / ", ranges),
            TokenDisplayText.TimeZoneLabel(stamps.Count == 0 ? DateTimeOffset.Now : stamps.Max()));
        _calendarNote.Text = L10n.Tr(ranges.Count > 1
            ? "Snapshot dates differ; scan to refresh them together."
            : "Today starts at this computer's midnight. Previous days stay in history.");
    }

    private Slice SliceFor(DisplayProvider provider)
    {
        var summary = CostStore.Shared.Summary(provider);
        var dollars = ProvidesDollars(provider);
        var billableMode = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
        var today = (summary.AsOf ?? DateTimeOffset.Now).ToLocalTime().Date;
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
                return new Slice(provider, dollars, summary.TodayDollars, summary.TodayTokens, summary.TodayBillableTokens, hours, summary.RecentModels, summary.AsOf);
            case Period.Week:
                var week = summary.LastSevenDays;
                return new Slice(provider, dollars, week.Dollars, week.DailyTokens.Sum(d => d.Tokens), week.DailyTokens.Sum(d => d.BillableTokens),
                    week.DailyTokens.Select(d => (d.DayStart.ToString(L10n.IsChinese ? "M月d日" : "MMM d"), DayValue(d))).ToList(), week.ByModel, summary.AsOf);
            default:
                return new Slice(provider, dollars, summary.MonthDollars, summary.MonthTokens, summary.MonthBillableTokens,
                    Days(new DateTime(today.Year, today.Month, 1)), summary.MonthModels, summary.AsOf);
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
        var scanned = slices.Any(s => s.AsOf is not null);
        var tokensFormat = L10n.Tr("{0} tokens");
        var row = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Stat(int column, string label, string value, string caption, DisplayProvider? face = null,
            (string Key, double Value, Func<double, string> Format)? count = null, (string Key, double Value, Func<double, string> Format)? captionCount = null)
        {
            var number = new TextBlock { Text = value, Style = Res<Style>(_pixel ? "SubtitleTextBlockStyle" : "ClassicHeroTextStyle") };
            if (count is { } counted) Counted(number, counted.Key, counted.Value, counted.Format);
            FrameworkElement shown = number;
            if (!_pixel && face is { } provider)
            {
                number.VerticalAlignment = VerticalAlignment.Center;
                shown = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new FaceChip(22, ClassicTokens.Identity(provider, Brand.IsDark()), lookRight: true) { VerticalAlignment = VerticalAlignment.Center }, number },
                };
            }
            var labelText = new TextBlock { Text = label, Foreground = Res<Brush>("TextFillColorSecondaryBrush") };
            if (!_pixel) labelText.Style = Res<Style>("CaptionTextBlockStyle");
            var captionText = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, Style = Res<Style>("CaptionTextBlockStyle"), Foreground = Res<Brush>("TextFillColorTertiaryBrush") };
            if (captionCount is { } countedCaption) Counted(captionText, countedCaption.Key, countedCaption.Value, countedCaption.Format);
            var card = Surface(new StackPanel
            {
                Spacing = 2,
                Children = { labelText, shown, captionText },
            }, totals: true);
            Grid.SetColumn(card, column);
            row.Children.Add(card);
        }
        var unpriced = slices.Where(s => s.Dollars).SelectMany(s => s.Models).Count(Unpriced);
        Stat(0, L10n.Tr("Estimated cost"), scanned ? Formatting.Money(spend) : "—", unpriced > 0
            ? L10n.TrFormat(unpriced == 1 ? "{0} model has no price and counts as $0" : "{0} models have no price and count as $0", unpriced)
            : L10n.Tr("At API list prices"),
            count: scanned ? ("cost", spend, Formatting.Money) : null);
        Stat(1, L10n.Tr("Tokens"), scanned ? Formatting.CompactTokens(tokens) : "—",
            TokenDisplayText.ModeLabel(TokenCountModeStore.Shared.Mode),
            count: scanned ? ("tokens", tokens, CountedTokens) : null);
        var active = busiest is { } top && (billable ? top.Billable : top.Tokens) > 0;
        Stat(2, L10n.Tr("Busiest agent"),
            active ? ProviderIdentity.DisplayName(busiest!.Provider) : "—",
            active ? L10n.TrFormat("{0} tokens", Tokens(busiest!)) : L10n.Tr(scanned ? "No activity yet" : "Not scanned yet"),
            active ? busiest!.Provider : null,
            captionCount: active ? ($"busiest-{busiest!.Provider}", billable ? busiest!.Billable : busiest!.Tokens, v => string.Format(tokensFormat, CountedTokens(v))) : null);
        return row;
    }

    private FrameworkElement AgentCard(Slice slice)
    {
        var panel = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Mark(slice.Provider));
        var name = new TextBlock { Text = ProviderIdentity.DisplayName(slice.Provider), Style = Res<Style>("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var heroText = new TextBlock
        {
            Text = slice.AsOf is null ? "—" : slice.Dollars ? Formatting.Money(slice.Spend) : Tokens(slice),
            Style = Res<Style>("SubtitleTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var sliceTokens = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable ? slice.Billable : slice.Tokens;
        if (slice.AsOf is not null)
        {
            if (slice.Dollars) Counted(heroText, $"{slice.Provider}-spend", slice.Spend, Formatting.Money);
            else Counted(heroText, $"{slice.Provider}-tokens", sliceTokens, CountedTokens);
        }
        hero.Children.Add(heroText);
        var captionText = new TextBlock
        {
            Text = slice.AsOf is null ? L10n.Tr("Not scanned yet")
                : slice.Dollars ? L10n.TrFormat("{0} tokens", Tokens(slice)) : L10n.Tr("tokens · billed by Cursor's own plan"),
            Style = Res<Style>("CaptionTextBlockStyle"),
            Foreground = Res<Brush>("TextFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (slice.AsOf is not null && slice.Dollars)
        {
            var tokensFormat = L10n.Tr("{0} tokens");
            Counted(captionText, $"{slice.Provider}-tokens", sliceTokens, v => string.Format(tokensFormat, CountedTokens(v)));
        }
        hero.Children.Add(captionText);
        Grid.SetColumn(hero, 2);
        head.Children.Add(hero);
        panel.Children.Add(head);

        // Bars wear the agent's character colour, as in the drawer.
        var swatch = App.Current.Drawer?.SwatchOf(slice.Provider)
            ?? DrawerLooks.Swatch(slice.Provider, DrawerLooks.For(slice.Provider).Color, DrawerStore.Shared.Theme);
        var accent = Looks.Pixel ? PixelTokens.Agent(slice.Provider, Brand.IsDark()) : Brand.Rgb(Brand.Readable(swatch, Brand.IsDark()));
        if (slice.AsOf is not null && slice.Bars.Count > 0) panel.Children.Add(Looks.Pixel ? PixelChart(slice, accent) : Chart(slice, accent));

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
                var modelName = new TextBlock { Text = model.Model, TextTrimming = TextTrimming.CharacterEllipsis };
                // The pixel face is the kit's only type; Fluent shows names in a mono face.
                if (!Looks.Pixel)
                {
                    modelName.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    modelName.FontSize = 12;
                }
                line.Children.Add(modelName);
                var price = !slice.Dollars ? "" : Unpriced(model) ? "  ·  " + L10n.Tr("no price") : "  ·  " + Formatting.Money(model.Dollars);
                var amount = new TextBlock
                {
                    Text = Formatting.CompactTokens(billable ? model.BillableTokens : model.Tokens) + price,
                    Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
                };
                if (!Looks.Pixel) amount.FontSize = 12;
                Grid.SetColumn(amount, 1);
                line.Children.Add(amount);
                panel.Children.Add(line);
            }
        }
        else if (slice.AsOf is not null && slice.Tokens == 0)
        {
            panel.Children.Add(new TextBlock { Text = L10n.Tr("No sessions in this period"), Foreground = Res<Brush>("TextFillColorTertiaryBrush") });
        }
        return Surface(panel);
    }

    /// Bars scaled to the period's largest, rounded more at the top, at
    /// most 22 wide and at least 6 tall; an idle hour or day is a faint dot.
    /// Each carries its value as a tooltip.
    private FrameworkElement Chart(Slice slice, Windows.UI.Color accent)
    {
        var max = slice.Bars.Max(b => b.Value);
        var fill = new SolidColorBrush(accent);
        var idle = Res<Brush>("TextFillColorTertiaryBrush");
        var grid = new Grid { Height = ChartHeight, ColumnSpacing = slice.Bars.Count > 20 ? 3 : 6 };
        for (var i = 0; i < slice.Bars.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (label, value) = slice.Bars[i];
            FrameworkElement column = value > 0 && max > 0
                ? new Border
                {
                    Height = Math.Max(6, value / max * ChartHeight),
                    MaxWidth = 22,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(6, 6, 3, 3),
                    Background = fill,
                }
                : new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 4,
                    Height = 4,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Fill = idle,
                    Opacity = 0.35,
                };
            ToolTipService.SetToolTip(column, Tip($"{label}  ·  {(slice.Dollars ? Formatting.Money(value) : Formatting.CompactTokens((long)value))}"));
            Grid.SetColumn(column, i);
            grid.Children.Add(column);
            var quiet = !(value > 0 && max > 0);
            _columns.Add(new Column(slice.Provider, i, slice.Bars.Count, column, quiet ? 0 : column.Height, quiet));
        }
        return grid;
    }

    /// Under Pixel: stacks of 4-DIP blocks, at most 12 (72 DIP) in the
    /// agent's colour; an idle hour or day keeps one faint block.
    private FrameworkElement PixelChart(Slice slice, Windows.UI.Color accent)
    {
        var max = slice.Bars.Max(b => b.Value);
        var dark = Brand.IsDark();
        var fill = new SolidColorBrush(accent);
        var idle = new SolidColorBrush(PixelTokens.OverPaper(PixelTokens.Get("Text3", dark), 0.35, dark));
        var grid = new Grid { Height = ChartHeight, ColumnSpacing = slice.Bars.Count > 20 ? 2 : 4 };
        for (var i = 0; i < slice.Bars.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (label, value) = slice.Bars[i];
            var blocks = value > 0 && max > 0 ? Math.Clamp((int)Math.Round(value / max * 12), 1, 12) : 0;
            var column = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            for (var b = 0; b < Math.Max(1, blocks); b++) column.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 4, Fill = blocks > 0 ? fill : idle });
            ToolTipService.SetToolTip(column, Tip($"{label}  ·  {(slice.Dollars ? Formatting.Money(value) : Formatting.CompactTokens((long)value))}"));
            Grid.SetColumn(column, i);
            grid.Children.Add(column);
            _columns.Add(new Column(slice.Provider, i, slice.Bars.Count, column, Math.Max(1, blocks), blocks == 0));
        }
        return grid;
    }

    /// A counted token figure reads as the page does, in whole tokens.
    private static string CountedTokens(double tokens) => Formatting.CompactTokens((long)Math.Round(tokens));

    private static FrameworkElement NoLogCard(DisplayProvider provider)
    {
        var mark = Mark(provider);
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
                        new TextBlock { Text = ProviderIdentity.DisplayName(provider), Style = Res<Style>("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center },
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

    /// A card: paper with the card edge, or under Pixel a stepped plate.
    /// The totals are rounded 14 with 16/14 padding; agent cards 16 with 18/16.
    private static FrameworkElement Surface(UIElement child, bool totals = false)
    {
        if (Looks.Pixel)
        {
            return new Grid
            {
                Children =
                {
                    new PixelSurface { Fill = Res<Brush>("PixelPaper"), Outline = Res<Brush>("PixelLine") },
                    new Border { Child = child, Padding = new Thickness(16, 14, 16, 14) },
                },
            };
        }
        return new Border
        {
            Child = child,
            Padding = totals ? new Thickness(16, 14, 16, 14) : new Thickness(18, 16, 18, 16),
            CornerRadius = new CornerRadius(totals ? 14 : 16),
            Background = Res<Brush>("ClassicPaper"),
            BorderBrush = Res<Brush>("ClassicEdge"),
            BorderThickness = new Thickness(1),
        };
    }

    /// The agent's puck (its mark on its tint), or under Pixel its pixel bot face.
    private static FrameworkElement Mark(DisplayProvider provider)
    {
        if (Looks.Pixel)
        {
            return new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Source = PixelIcons.Face(provider) };
        }
        var puck = Puck.Of(provider, 32, 18);
        puck.VerticalAlignment = VerticalAlignment.Center;
        return puck;
    }
}
