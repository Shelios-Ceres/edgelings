using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI.Report;

/// Hosts a share card (weekly or monthly) on a light Grok panel: the dark
/// card is the exported artwork, the panel is chrome. Esc closes, drag
/// anywhere moves. A ← period →
/// pager plus an any-date calendar anchor ride above the card (macOS
/// report sheets); Copy image and Save PNG below, both served from a warm
/// 3x render of exactly the page on screen. Sharing is always the USER
/// posting an image; nothing leaves the machine on its own.
public sealed class ReportWindow : Window
{
    public enum Kind
    {
        Weekly,
        Monthly,
    }

    private static readonly Dictionary<Kind, ReportWindow> Open = new();

    private readonly Kind _kind;
    private readonly TextBlock _coach;
    private readonly Button _copy;
    private readonly StackPanel _actions;
    private readonly Grid _cardHost;
    private readonly TextBlock _periodLabel;
    private readonly PagerCircle _back;
    private readonly PagerCircle _forward;
    private readonly PagerCircle _calendarButton;
    private ReportCalendarPopup? _calendar;
    private object _display;
    private int _pageOffset;
    private DateTime? _anchorDate;
    private bool _loading;
    private BitmapSource? _rendered;
    private DispatcherTimer? _coachTimer;
    private readonly System.ComponentModel.PropertyChangedEventHandler _costChanged;

    public static void Show(Kind kind)
    {
        if (Open.TryGetValue(kind, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new ReportWindow(kind);
        Open[kind] = window;
        window.Closed += (_, _) => Open.Remove(kind);
        window.Show();
        window.Activate();
    }

    private ReportWindow(Kind kind)
    {
        _kind = kind;
        _display = CurrentData();
        Title = kind == Kind.Weekly
            ? Localization.L10n.Tr("Weekly report")
            : Localization.L10n.Tr("Share monthly report");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // ← period label → row above the card (macOS pager): the right edge
        // is the current period, the left edge the earliest scanned day, and
        // the calendar anchors the window to any start date.
        _back = new PagerCircle("\uE76B");
        _back.Clicked += () => Flip(_anchorDate is null ? _pageOffset + 1 : 1);
        _forward = new PagerCircle("");
        _forward.Clicked += () => Flip(_anchorDate is null ? _pageOffset - 1 : 0);
        _periodLabel = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            MinWidth = 150,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(_periodLabel, System.Windows.FontNumeralAlignment.Tabular);
        _calendarButton = new PagerCircle("");
        _calendarButton.Clicked += OpenCalendar;
        var pager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
        };
        pager.Children.Add(_back);
        _periodLabel.Margin = new Thickness(10, 0, 10, 0);
        pager.Children.Add(_periodLabel);
        pager.Children.Add(_forward);
        _calendarButton.Margin = new Thickness(10, 0, 0, 0);
        pager.Children.Add(_calendarButton);

        _copy = GrokButton.Create(Localization.L10n.Tr("Copy image"), primary: true, fontSize: 12);
        _copy.Click += (_, _) =>
        {
            if (!CopyImage()) return;
            _copy.Content = Localization.L10n.Tr("Copied");
            ShowCoach(Localization.L10n.Tr("Copied! Post it and bring a friend to the island 🏝️ Thanks for spreading the word"));
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                _copy.Content = Localization.L10n.Tr("Copy image");
            };
            reset.Start();
        };
        var save = GrokButton.Create(Localization.L10n.Tr("Save PNG"), primary: false, fontSize: 12);
        save.Click += (_, _) => SavePng();

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        _actions.Children.Add(_copy);
        save.Margin = new Thickness(10, 0, 0, 0);
        _actions.Children.Add(save);

        // Fixed one-line slot so the window never reflows.
        _coach = new TextBlock
        {
            Text = " ",
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.Brush(GrokTheme.Green),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Opacity = 0,
        };

        // The close control rides ON the card (top-right, dark disc, hover
        // red) — parked on the window's transparent margin it was invisible
        // against a light desktop.
        _cardHost = new Grid();
        RebuildCard();

        var stack = new StackPanel { Margin = new Thickness(22, 16, 22, 10) };
        stack.Children.Add(pager);
        stack.Children.Add(_cardHost);
        stack.Children.Add(_actions);
        stack.Children.Add(_coach);
        Content = new Border
        {
            Background = GrokTheme.Brush(GrokTheme.Elevated),
            BorderBrush = GrokTheme.InkBrush(GrokTheme.StrokeSecondary),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(18),
            Effect = GrokTheme.Float(0.16),
            Child = stack,
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };

        // A fresh scan self-heals a stale launch snapshot within seconds; the
        // store commit rebuilds the live page when it lands (macOS onAppear).
        _costChanged = (_, args) =>
        {
            if (args.PropertyName != nameof(Cost.CostStore.LastUpdated)) return;
            if (_pageOffset != 0 || _anchorDate is not null || _loading) return;
            _display = CurrentData();
            RebuildCard();
            AlignCurrentWeek();
        };
        Cost.CostStore.Shared.PropertyChanged += _costChanged;
        Closed += (_, _) => Cost.CostStore.Shared.PropertyChanged -= _costChanged;
        if (!Core.AppEnvironment.IsDemo) Cost.CostStore.Shared.Refresh();
        AlignCurrentWeek();

        // Warm the 3x export render off the click path — it costs a beat,
        // and doing it lazily made the first Copy feel broken.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = ExportRender());
    }

    private object CurrentData() => _kind == Kind.Weekly
        ? WeeklyReportData.Current()
        : MonthlyReportData.Current();

    private FrameworkElement CardFor(object data, bool rounded) => _kind == Kind.Weekly
        ? ReportCards.Weekly((WeeklyReportData)data, rounded)
        : ReportCards.Monthly((MonthlyReportData)data, rounded);

    private string PeriodText => _kind == Kind.Weekly
        ? ((WeeklyReportData)_display).RangeText
        : ((MonthlyReportData)_display).MonthText;

    /// Rebuild the on-screen card from the current display data and refresh
    /// every piece of pager chrome. Also resets the export cache — copy and
    /// save must ship exactly the page on screen.
    private void RebuildCard()
    {
        _rendered = null;
        var card = CardFor(_display, rounded: true);
        card.Effect = GrokTheme.Float(0.22);
        _cardHost.Children.Clear();
        _cardHost.Children.Add(card);
        _cardHost.Children.Add(CloseDisc());
        UpdatePagerChrome();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = ExportRender());
    }

    private void UpdatePagerChrome()
    {
        _periodLabel.Text = PeriodText;
        _periodLabel.Opacity = _loading ? 0.45 : 1;
        var interval = _kind == Kind.Weekly
            ? ReportPeriods.WeekInterval(_pageOffset)
            : ReportPeriods.MonthInterval(_pageOffset);
        _back.Enabled = !_loading && ReportPeriods.HasData(interval.Start, ReportPeriods.EarliestDataDay());
        _forward.Enabled = (_pageOffset > 0 || _anchorDate is not null) && !_loading;
        _calendarButton.Enabled = !_loading && !Core.AppEnvironment.IsDemo;
        _calendarButton.Tint = _anchorDate is null ? GrokTheme.InkA(GrokTheme.TextSecondary) : GrokTheme.Accent;
        // While a past page is still assembling, the card shows the previous
        // period — exporting would ship the wrong week.
        _actions.IsEnabled = !_loading;
        _actions.Opacity = _loading ? 0.5 : 1;
    }

    private void Flip(int target)
    {
        // Arrow paging leaves anchored mode and resumes calendar tiling.
        _anchorDate = null;
        if (target < 0) return;
        _pageOffset = target;
        if (target == 0)
        {
            _loading = false;
            _display = CurrentData();
            RebuildCard();
            AlignCurrentWeek();
            return;
        }
        LoadPage(target);
    }

    /// The live weekly card's model table and dollars come from the store's
    /// WALL-CLOCK last-7-days window, while its bars anchor to the freshest
    /// SCANNED day — on a machine whose logs stopped days ago the hero says
    /// 2693万 while the donut sits empty. Rebuild the current page from one
    /// anchored interval slice so every series shares one window by
    /// construction. (Same latent shear exists in the macOS current() —
    /// invisible there only while the machine is used daily.)
    private async void AlignCurrentWeek()
    {
        if (_kind != Kind.Weekly || Core.AppEnvironment.IsDemo) return;
        var (start, end) = ReportPeriods.WeekInterval(0);
        // Nothing to align when the anchored week IS the wall-clock week.
        if (end > DateTime.Today) return;
        var slices = await ReportPeriods.SlicesAsync(start, end);
        if (_pageOffset != 0 || _anchorDate is not null || _loading) return;
        _display = WeeklyReportData.ForInterval(start, end, slices);
        RebuildCard();
    }

    private async void LoadPage(int target)
    {
        _loading = true;
        UpdatePagerChrome();
        var (start, end) = _kind == Kind.Weekly
            ? ReportPeriods.WeekInterval(target)
            : ReportPeriods.MonthInterval(target);
        var slices = await ReportPeriods.SlicesAsync(start, end);
        // The user may have flipped again while the scan ran.
        if (_pageOffset != target || _anchorDate is not null) return;
        _display = _kind == Kind.Weekly
            ? WeeklyReportData.ForInterval(start, end, slices)
            : MonthlyReportData.ForInterval(start, slices);
        _loading = false;
        RebuildCard();
    }

    private void OpenCalendar()
    {
        _calendar = new ReportCalendarPopup(ReportPeriods.EarliestDataDay(), SetAnchor)
        {
            PlacementTarget = _calendarButton,
        };
        _calendar.IsOpen = true;
    }

    /// Any-date anchor: the window becomes [picked day, +7d) weekly /
    /// [picked day, +30d) monthly (macOS, owner ask 2026-08-08).
    private async void SetAnchor(DateTime day)
    {
        var start = day.Date;
        _anchorDate = start;
        _loading = true;
        UpdatePagerChrome();
        var end = start.AddDays(_kind == Kind.Weekly ? 7 : 30);
        var slices = await ReportPeriods.SlicesAsync(start, end);
        if (_anchorDate != start) return;
        _display = _kind == Kind.Weekly
            ? WeeklyReportData.ForInterval(start, end, slices)
            : MonthlyReportData.ForInterval(start, slices);
        _loading = false;
        RebuildCard();
    }

    /// The card's own close control: a quiet dark disc with an ✕, top-right
    /// corner, red on hover — always visible against the card's ink.
    private UIElement CloseDisc()
    {
        var glyph = new TextBlock
        {
            Text = "", // Segoe Fluent ChromeClose
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 8.5,
            Foreground = IslandColors.Brush(IslandColors.White(0.65)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var disc = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = IslandColors.Brush(IslandColors.White(0.10)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.12)),
            BorderThickness = new Thickness(0.5),
            Child = glyph,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
            Cursor = Cursors.Hand,
        };
        disc.MouseEnter += (_, _) =>
        {
            disc.Background = IslandColors.Brush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            glyph.Foreground = Brushes.White;
        };
        disc.MouseLeave += (_, _) =>
        {
            disc.Background = IslandColors.Brush(IslandColors.White(0.10));
            glyph.Foreground = IslandColors.Brush(IslandColors.White(0.65));
        };
        disc.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag
        disc.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        return disc;
    }

    private void ShowCoach(string text)
    {
        _coach.Text = text;
        _coach.Opacity = 1;
        _coachTimer?.Stop();
        _coachTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _coachTimer.Tick += (_, _) =>
        {
            _coachTimer?.Stop();
            _coach.Opacity = 0;
        };
        _coachTimer.Start();
    }

    // MARK: - Export

    /// The EXPORT version is the card itself, full-bleed with SQUARE outer
    /// corners on an opaque background, rendered at 3x for crispness — from
    /// exactly the page on screen.
    private BitmapSource ExportRender()
    {
        if (_rendered is not null) return _rendered;
        _rendered = Render(CardFor(_display, rounded: false));
        return _rendered;
    }

    private static BitmapSource Render(FrameworkElement card)
    {
        const double scale = 3;
        card.Measure(new Size(ReportCards.CardWidth, ReportCards.CardHeight));
        card.Arrange(new Rect(0, 0, ReportCards.CardWidth, ReportCards.CardHeight));
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)(ReportCards.CardWidth * scale), (int)(ReportCards.CardHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource RenderCard(Kind kind)
    {
        var card = kind == Kind.Weekly
            ? ReportCards.Weekly(WeeklyReportData.Current(), rounded: false)
            : ReportCards.Monthly(MonthlyReportData.Current(), rounded: false);
        return Render(card);
    }

    private bool CopyImage()
    {
        try
        {
            Clipboard.SetImage(ExportRender());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SavePng()
    {
        var tag = _kind == Kind.Weekly ? "weekly" : "monthly";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"agent-island-{tag}-{DateTime.Today:yyyy-MM-dd}.png",
            Filter = "PNG|*.png",
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var stream = File.Create(dialog.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ExportRender()));
            encoder.Save(stream);
            ShowCoach(Localization.L10n.Tr("Copied! Post it and bring a friend to the island 🏝️ Thanks for spreading the word"));
        }
        catch
        {
        }
    }

    /// Headless snapshot for tooling/screenshots:
    /// AGENTISLAND_REPORT_SNAPSHOT / AGENTISLAND_MONTHLY_SNAPSHOT = path.png.
    public static void WritePng(Kind kind, string path)
    {
        try
        {
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(RenderCard(kind)));
            encoder.Save(stream);
        }
        catch
        {
        }
    }

}

/// The pager's 26pt circular icon button (macOS ReportPagerArrow): ink
/// glyph on a faint ink disc, both dimmed when disabled.
internal sealed class PagerCircle : Border
{
    private readonly TextBlock _glyph;
    private bool _enabled = true;
    private Color? _tintOverride;

    public event Action? Clicked;

    public PagerCircle(string glyph)
    {
        Width = 26;
        Height = 26;
        CornerRadius = new CornerRadius(13);
        VerticalAlignment = VerticalAlignment.Center;
        _glyph = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _glyph;
        // Swallow the DOWN: the report window starts a DragMove on any
        // unclaimed press, which captures the mouse and eats the UP — the
        // pager read as dead (the close disc dodges this the same way).
        MouseLeftButtonDown += (_, args) => args.Handled = true;
        MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            if (_enabled) Clicked?.Invoke();
        };
        Render();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Render();
        }
    }

    /// Optional glyph tint override while enabled (the calendar button goes
    /// brighter when a date anchor is active).
    public Color Tint
    {
        set
        {
            _tintOverride = value;
            Render();
        }
    }

    private void Render()
    {
        Background = GrokTheme.InkBrush(_enabled ? GrokTheme.Tertiary : GrokTheme.Quinary);
        _glyph.Foreground = IslandColors.Brush(
            _enabled ? (_tintOverride ?? GrokTheme.InkA(GrokTheme.TextPrimary)) : GrokTheme.InkA(GrokTheme.TextQuaternary));
        Cursor = _enabled ? Cursors.Hand : Cursors.Arrow;
    }
}
