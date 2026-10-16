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

/// What the local logs say each agent used, on Settings' Usage & cost page:
/// a period picker, totals, and one card per agent with its spend or tokens,
/// a bar chart and its top models. Dollars are API list-price estimates;
/// agents without published prices (Cursor, Kimi Code, DeepSeek) show tokens
/// only, and Antigravity keeps no local log. The page builds one for each
/// showing and disposes of it when it goes.
internal sealed partial class CostPage : IDisposable
{
    private enum Period
    {
        Today,
        Week,
        Month,
    }

    private const string PeriodKey = "Cost.period";
    private const double ChartHeight = 72;
    // Narrower than these, the totals and the agents' cards stand in one column.
    private const double ThreeTotals = 600;
    private const double TwoColumns = 560;

    private readonly SelectorBar _periods = new();
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly TextBlock _scanned = new();
    // The period's dates and the time zone, each moving whole to the next line.
    private readonly Border _periodRange = new();
    private readonly TextBlock _calendarNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly bool _pixel = Looks.Pixel;
    private readonly List<Action> _teardown = new();
    private readonly DispatcherQueue _queue;
    private readonly Func<bool> _visible;
    private readonly Action _showAgents;
    private Period _period;
    private bool _pending;

    /// visible says whether the page is on screen: only then do its numbers
    /// move (M17) and follow a new scan. showAgents turns to the Agents page.
    public CostPage(DispatcherQueue queue, Func<bool> visible, Action showAgents)
    {
        _queue = queue;
        _visible = visible;
        _showAgents = showAgents;
        View = new StackPanel { Spacing = 12, Children = { Header(), _body } };
        Render();
        Watch(CostStore.Shared);
        Watch(ProviderVisibilityStore.Shared);
        Watch(TokenCountModeStore.Shared);
    }

    /// The period tabs, the period's dates beside the scan, the totals and
    /// the agents' cards.
    public FrameworkElement View { get; }

    public void Dispose()
    {
        _count?.Stop();
        _glide?.Stop();
        foreach (var teardown in _teardown) teardown();
        _teardown.Clear();
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
                if (_visible()) Recount();
            });
        };
        source.PropertyChanged += handler;
        _teardown.Add(() => source.PropertyChanged -= handler);
    }

    /// One agent's numbers for the chosen period.
    private sealed record Slice(DisplayProvider Provider, bool Dollars, double Spend, long Tokens, long Billable,
        IReadOnlyList<(string Label, double Value)> Bars, IReadOnlyList<ModelSpend> Models, DateTimeOffset? AsOf)
    {
        /// Cursor's part of Grok's tokens: it works under Grok's character
        /// and bills by its own plan, so its tokens count but carry no price.
        public (long Tokens, long Billable)? Cursor { get; init; }
    }

    /// The period tabs, and under them the period's dates beside the scan
    /// and when it last ran.
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

        var header = new Grid { ColumnSpacing = 16 };
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tabs = new Grid();
        _tabMarker = TabMarker();
        _tabs.Children.Add(_tabMarker);
        _tabs.Children.Add(_periods);
        FrameworkElement tabs = _pixel ? _tabs : new Border { Style = Looks.Kit<Style>("TrackStyle"), Child = _tabs };
        tabs.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumnSpan(tabs, 2);
        header.Children.Add(tabs);
        _periodRange.Margin = new Thickness(0, _pixel ? 8 : 12, 0, 0);
        _calendarNote.Style = Res<Style>("CaptionTextBlockStyle");
        _calendarNote.Foreground = Res<Brush>("TextFillColorTertiaryBrush");
        var captions = new StackPanel { Children = { _periodRange, _calendarNote } };
        Grid.SetRow(captions, 1);
        header.Children.Add(captions);
        var side = new StackPanel { Spacing = 8, MaxWidth = 160, Margin = _periodRange.Margin, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var rescan = Button(L10n.Tr("Scan now"), () => CostStore.Shared.Refresh());
        rescan.HorizontalAlignment = HorizontalAlignment.Right;
        side.Children.Add(rescan);
        _scanned.Foreground = Res<Brush>("TextFillColorTertiaryBrush");
        _scanned.Style = Res<Style>("CaptionTextBlockStyle");
        _scanned.HorizontalAlignment = HorizontalAlignment.Right;
        _scanned.TextWrapping = TextWrapping.Wrap;
        _scanned.TextAlignment = TextAlignment.Right;
        side.Children.Add(_scanned);
        Grid.SetRow(side, 1);
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

    /// The numbers for the chosen period, built apart from the page; its
    /// numbers and chart columns are kept for counting (M17).
    private List<UIElement> Compose()
    {
        using var english = Looks.English();
        _numbers.Clear();
        _columns.Clear();
        _scanned.Text = CostStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("Last scanned {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("Not scanned yet");

        // In the drawer's order, one card per character.
        var shown = DrawerStore.Shared.Order.Where(p => p.HasCharacter() && ProviderVisibilityStore.Shared.IsShown(p)).ToList();
        var slices = shown.Where(p => p != DisplayProvider.Antigravity).Select(CharacterSlice).ToList();
        UpdatePeriodCaption(slices);
        var page = new List<UIElement> { Totals(slices) };
        var cards = shown.Select(provider => provider == DisplayProvider.Antigravity
            ? NoLogCard(provider, _showAgents)
            : AgentCard(slices.First(slice => slice.Provider == provider))).ToList();
        page.Add(Columns(cards, 2, TwoColumns, new Thickness(0, 8, 0, 0)));
        return page;
    }

    /// Cards in count equal columns, or in one where the page is narrower
    /// than narrow: the totals three to a row, the agents' cards two.
    private static Grid Columns(IReadOnlyList<FrameworkElement> cards, int count, double narrow, Thickness margin = default)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12, Margin = margin };
        var columns = 0;
        void Lay(int count)
        {
            if (count == columns) return;
            columns = count;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (var c = 0; c < count; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < cards.Count; i++)
            {
                if (i % count == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(cards[i], i / count);
                Grid.SetColumn(cards[i], i % count);
            }
        }
        Lay(count);
        foreach (var card in cards) grid.Children.Add(card);
        grid.SizeChanged += (_, e) => Lay(e.NewSize.Width < narrow ? 1 : count);
        return grid;
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
        var dates = L10n.TrFormat("{0} · Local time zone: {1}",
            ranges.Count == 0 ? L10n.Tr("Not scanned yet") : string.Join(" / ", ranges),
            TokenDisplayText.TimeZoneLabel(stamps.Count == 0 ? DateTimeOffset.Now : stamps.Max()));
        _periodRange.Child = Drawer.PhraseFlow.Of(
            dates.Split(" · ").Select(part => (FrameworkElement)Drawer.PhraseFlow.Words(part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Dated))).ToList(),
            () => Dated("·", new Thickness(4, 0, 4, 0)));
        _calendarNote.Text = L10n.Tr(ranges.Count > 1
            ? "Snapshot dates differ; scan to refresh them together."
            : "Today starts at this computer's midnight. Previous days stay in history.");
    }

    /// A word of the period's caption.
    private static TextBlock Dated(string word) => Dated(word, default);

    private static TextBlock Dated(string word, Thickness margin) => new()
    {
        Text = word,
        Margin = margin,
        Style = Res<Style>("CaptionTextBlockStyle"),
        Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    /// A character's numbers: Grok's carry Cursor's tokens too.
    private Slice CharacterSlice(DisplayProvider provider)
    {
        var slice = SliceFor(provider);
        if (provider != DisplayProvider.Grok || !ProviderVisibilityStore.Shared.IsDetected(DisplayProvider.Cursor)) return slice;
        var cursor = SliceFor(DisplayProvider.Cursor);
        return slice with
        {
            Tokens = slice.Tokens + cursor.Tokens,
            Billable = slice.Billable + cursor.Billable,
            AsOf = slice.AsOf ?? cursor.AsOf,
            Cursor = (cursor.Tokens, cursor.Billable),
        };
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
        var stats = new List<FrameworkElement>();
        void Stat(string label, string value, string caption, DisplayProvider? face = null,
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
            stats.Add(card);
        }
        var unpriced = slices.Where(s => s.Dollars).SelectMany(s => s.Models).Count(Unpriced);
        Stat(L10n.Tr("Estimated cost"), scanned ? Formatting.Money(spend) : "—", unpriced > 0
            ? L10n.TrFormat(unpriced == 1 ? "{0} model has no price and counts as $0" : "{0} models have no price and count as $0", unpriced)
            : L10n.Tr("At API list prices"),
            count: scanned ? ("cost", spend, Formatting.Money) : null);
        Stat(L10n.Tr("Tokens"), scanned ? Formatting.CompactTokens(tokens) : "—",
            TokenDisplayText.ModeLabel(TokenCountModeStore.Shared.Mode),
            count: scanned ? ("tokens", tokens, CountedTokens) : null);
        var active = busiest is { } top && (billable ? top.Billable : top.Tokens) > 0;
        Stat(L10n.Tr("Busiest agent"),
            active ? ProviderIdentity.DisplayName(busiest!.Provider) : "—",
            active ? L10n.TrFormat("{0} tokens", Tokens(busiest!)) : L10n.Tr(scanned ? "No activity yet" : "Not scanned yet"),
            active ? busiest!.Provider : null,
            captionCount: active ? ($"busiest-{busiest!.Provider}", billable ? busiest!.Billable : busiest!.Tokens, v => string.Format(tokensFormat, CountedTokens(v))) : null);
        return Columns(stats, 3, ThreeTotals);
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
        if (slice.Cursor is { } cursor && (TokenCountModeStore.Shared.Mode == TokenCountMode.Billable ? cursor.Billable : cursor.Tokens) is > 0 and var cursorTokens)
        {
            panel.Children.Add(CursorLine(cursorTokens));
        }
        return Surface(panel);
    }

    /// Cursor's tokens on Grok's card, with how Cursor bills them.
    private static FrameworkElement CursorLine(long tokens)
    {
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock { Text = ProviderIdentity.DisplayName(DisplayProvider.Cursor), TextTrimming = TextTrimming.CharacterEllipsis });
        var amount = new TextBlock
        {
            Text = $"{Formatting.CompactTokens(tokens)} {L10n.Tr("tokens · billed by Cursor's own plan")}",
            Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
        };
        if (!Looks.Pixel) amount.FontSize = 12;
        Grid.SetColumn(amount, 1);
        line.Children.Add(amount);
        return line;
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

    /// Antigravity's card: no log to count, and the way to its quota.
    private static FrameworkElement NoLogCard(DisplayProvider provider, Action showAgents)
    {
        var mark = Mark(provider);
        var agents = new HyperlinkButton { Content = L10n.Tr("See its quota on the Agents page"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        agents.Click += (_, _) => showAgents();
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
                    Text = L10n.Tr("Antigravity keeps no local usage log."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
                },
                agents,
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
