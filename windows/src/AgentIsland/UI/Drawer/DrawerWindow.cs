using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.UI.Drawer;

/// The side-drawer placement: instead of a bar over the top edge — where
/// Windows keeps title bars and browser tabs — a transparent column of
/// characters on a side edge, one per provider. Only drawn pixels take
/// clicks. Each character plays its provider's live phase.
public sealed class DrawerWindow : Window
{
    private const double BotSize = 44;
    private const double Gap = 10;
    private const double EdgeInset = 6;
    private const double InnerInset = 6;
    private const double VerticalPadding = 12;
    private const double GazeRange = 480;
    private const double MinDwellSeconds = 2;
    private const double DragThreshold = 4;
    private const int TopmostEverySeconds = 10;

    private sealed class Bot
    {
        public required DisplayProvider Provider { get; init; }
        public required TriggerTool Tool { get; init; }
        public required int Index { get; init; }
        public required BotSurface Surface { get; init; }
        public string Color { get; set; } = "blue";
        public Rect Bounds { get; set; }
        public bool Visible { get; set; }
        public AgentPhase Phase { get; set; }
        public string? Detail { get; set; }
        public double PhaseSince { get; set; } = double.NegativeInfinity;
        public string EngineState { get; set; } = "";
        public string LastSvg { get; set; } = "";
        public bool Gazing { get; set; }
        public double LastStep { get; set; }
        public double NextStep { get; set; }
    }

    private readonly GrokEngine? _engine;
    private readonly SvgRenderer _renderer = new();
    private readonly List<Bot> _bots = new();
    private readonly List<Bot> _due = new();
    private readonly Canvas _root = new();
    private readonly Popup _card;
    private readonly PointerCard _bubble = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _guard = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _cardClose = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Action> _teardown = new();
    private readonly string? _phaseLog = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_PHASE_LOG");
    private bool _rendering;
    private bool _pollInFlight;
    private bool _coveredByFullscreen;
    private bool _deliberatelyHidden;
    private int _topmostCountdown = TopmostEverySeconds;
    private Bot? _cardBot;
    private Bot? _pressed;
    private bool _dragging;
    private double _pressY;
    private double _pressTop;

    public DrawerWindow()
    {
        Title = "Agent Island";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = InnerInset + BotSize + EdgeInset;
        Content = _root;

        try
        {
            if (GrokEngineLocator.Find() is { } directory) _engine = new GrokEngine(directory);
        }
        catch
        {
            // Native V8 failed to load or the engine copy is unusable: the
            // fallback characters keep the drawer working.
            _engine = null;
        }

        foreach (var provider in DisplayProviders.All)
        {
            var (shape, color) = ValidLook(DrawerLooks.For(provider));
            var surface = new BotSurface(BotSize);
            _root.Children.Add(surface);
            var bot = new Bot
            {
                Provider = provider,
                Tool = provider.ToTriggerTool(),
                Index = _engine?.AddBot(shape, color, "idle") ?? _bots.Count,
                Surface = surface,
                Color = color,
            };
            _bots.Add(bot);
            Wire(bot);
        }

        // The hover card is its own popup: the drawer window stays exactly
        // as large as the character column, since a layered window
        // re-composes its whole surface every frame.
        _card = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.Absolute,
            Child = _bubble,
        };
        _cardClose.Tick += (_, _) =>
        {
            _cardClose.Stop();
            _card.IsOpen = false;
        };
        // Primary-screen layout now so the first show is the right size;
        // SourceInitialized redoes it in the window's own DPI space.
        Layout();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = (long)DrawerNative.GetWindowLongPtr(hwnd, DrawerNative.GwlExStyle);
            style |= DrawerNative.WsExNoActivate | DrawerNative.WsExToolWindow;
            DrawerNative.SetWindowLongPtr(hwnd, DrawerNative.GwlExStyle, (IntPtr)style);
            Layout();
        };
        Loaded += (_, _) => OnLoaded();
        Closed += (_, _) =>
        {
            StopRendering();
            _poll.Stop();
            _guard.Stop();
            _cardClose.Stop();
            _card.IsOpen = false;
            foreach (var teardown in _teardown) teardown();
            _engine?.Dispose();
        };
    }

    /// Tray "Show / Hide island" in drawer mode.
    public void ToggleDeliberately()
    {
        _deliberatelyHidden = !_deliberatelyHidden;
        ApplyVisibility();
    }

    /// Tray "show island": bring the drawer back and briefly show every
    /// character's status at once — the one place the rows appear together.
    public void Reveal()
    {
        _deliberatelyHidden = false;
        ApplyVisibility();
        OpenCard(null);
        _cardClose.Interval = TimeSpan.FromSeconds(4);
        _cardClose.Start();
    }

    private void OnLoaded()
    {
        void Watch(System.ComponentModel.INotifyPropertyChanged source, Action handler)
        {
            System.ComponentModel.PropertyChangedEventHandler h = (_, _) => Dispatcher.BeginInvoke(handler);
            source.PropertyChanged += h;
            _teardown.Add(() => source.PropertyChanged -= h);
        }
        Watch(ProviderVisibilityStore.Shared, Layout);
        Watch(DrawerStore.Shared, Layout);
        Watch(IslandTargetDisplayStore.Shared, Layout);
        Watch(UsageStore.Shared, RefreshCard);
        Watch(QuotaDisplayModeStore.Shared, RefreshCard);
        EventHandler onDisplay = (_, _) => Dispatcher.BeginInvoke(Layout);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += onDisplay;
        _teardown.Add(() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= onDisplay);

        _poll.Tick += (_, _) => Poll();
        _guard.Tick += (_, _) => Guard();
        _poll.Start();
        _guard.Start();
        Poll();
        StartRendering();

        // AGENTISLAND_DEBUG_BUBBLE=all|<n>: open the hover card at startup for verification.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_BUBBLE") is { Length: > 0 } bubble)
        {
            var visible = _bots.Where(b => b.Visible).ToList();
            OpenCard(int.TryParse(bubble, out var n) && n < visible.Count ? visible[n] : null);
        }
        // AGENTISLAND_DEBUG_MENU=1: open the first character's menu with its Color submenu expanded.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_MENU") == "1"
            && _bots.FirstOrDefault(b => b.Visible) is { Surface.ContextMenu: { } menu } first)
        {
            FillMenu(menu, first);
            menu.PlacementTarget = first.Surface;
            menu.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Header, L10n.Tr("Color"))) is { } colors)
                {
                    colors.IsSubmenuOpen = true;
                }
            });
        }
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DRAWER_PNG") is { Length: > 0 } png)
        {
            var capture = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            capture.Tick += (_, _) =>
            {
                capture.Stop();
                CaptureScreen(png);
            };
            capture.Start();
        }
    }

    // MARK: - Layout

    private (string Shape, string Color) ValidLook((string Shape, string Color) look)
    {
        if (_engine is null) return look;
        return (_engine.Shapes.Contains(look.Shape) ? look.Shape : "blob",
            _engine.Colors.Contains(look.Color) ? look.Color : "blue");
    }

    private void Layout()
    {
        var visibility = ProviderVisibilityStore.Shared;
        var store = DrawerStore.Shared;
        foreach (var bot in _bots) bot.Visible = store.IsShown(bot.Provider, visibility.IsDetected(bot.Provider));
        // An empty drawer would leave no character to right-click back.
        if (!_bots.Any(b => b.Visible)) _bots[0].Visible = true;

        var visible = _bots.Where(b => b.Visible).ToList();
        Height = VerticalPadding * 2 + visible.Count * BotSize + (visible.Count - 1) * Gap;
        var left = store.Side == DrawerSide.Right ? InnerInset : EdgeInset;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Bounds = new Rect(left, VerticalPadding + i * (BotSize + Gap), BotSize, BotSize);
            Canvas.SetLeft(visible[i].Surface, visible[i].Bounds.Left);
            Canvas.SetTop(visible[i].Surface, visible[i].Bounds.Top);
        }
        foreach (var bot in _bots) bot.Surface.Visibility = bot.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (_engine is null)
        {
            foreach (var bot in visible) bot.Surface.PresentFallback(DrawerSwatch.Of(bot.Color), bot.Phase);
        }
        Position();
        RefreshCard();
    }

    private void Position()
    {
        var area = WorkAreaDip(IslandTargetDisplayStore.Shared.Resolve());
        Left = DrawerStore.Shared.Side == DrawerSide.Right ? area.Right - Width : area.Left;
        Top = area.Top + DrawerStore.Shared.Offset * Math.Max(0, area.Height - Height);
    }

    private Rect WorkAreaDip(System.Windows.Forms.Screen screen)
    {
        var area = screen.WorkingArea;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var device = target.TransformFromDevice;
            return new Rect(
                device.Transform(new Point(area.Left, area.Top)),
                device.Transform(new Point(area.Right, area.Bottom)));
        }
        return SystemParameters.WorkArea;
    }

    // MARK: - Visibility

    /// Fullscreen apps hide the drawer (and pause it); otherwise it re-asserts
    /// topmost now and then, since fullscreen and topmost apps park above it.
    private void Guard()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var monitor = DrawerNative.MonitorFromWindow(hwnd, DrawerNative.MonitorDefaultToNearest);
        var foreground = DrawerNative.GetForegroundWindow();
        var covered = foreground != hwnd && DrawerNative.IsFullscreen(foreground, monitor);
        if (covered != _coveredByFullscreen)
        {
            _coveredByFullscreen = covered;
            ApplyVisibility();
        }
        if (!IsVisible || --_topmostCountdown > 0) return;
        _topmostCountdown = TopmostEverySeconds;
        DrawerNative.SetWindowPos(hwnd, DrawerNative.HwndTopmost, 0, 0, 0, 0,
            DrawerNative.SwpNoMove | DrawerNative.SwpNoSize | DrawerNative.SwpNoActivate);
    }

    private void ApplyVisibility()
    {
        var show = !_deliberatelyHidden && !_coveredByFullscreen;
        if (show && !IsVisible)
        {
            Show();
            StartRendering();
        }
        else if (!show && IsVisible)
        {
            _card.IsOpen = false;
            Hide();
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (_rendering || _engine is null) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    // MARK: - Phases

    private void Poll()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        var now = DateTimeOffset.UtcNow;
        var monitor = ActivityMonitor.Shared;
        var jobs = _bots
            .Where(b => b.Visible)
            .Select(b =>
            {
                var thread = monitor.ThreadFor(b.Tool);
                return (Bot: b, State: monitor.StateFor(b.Tool), Path: thread?.TranscriptPath, Prompt: thread?.PendingPrompt);
            })
            .ToList();
        // Tail reads stay off the UI thread; only working sessions are read.
        Task.Run(() => jobs
                .Select(job => job.Prompt ?? (job.State == ActivityState.Working && job.Path is { } path
                    ? AgentPhaseClassifier.Classify(job.Bot.Tool, path, now)
                    : null))
                .ToList())
            .ContinueWith(task =>
            {
                _pollInFlight = false;
                if (!task.IsCompletedSuccessfully || !IsLoaded) return;
                var seconds = _clock.Elapsed.TotalSeconds;
                for (var i = 0; i < jobs.Count; i++)
                {
                    var (bot, state, _, _) = jobs[i];
                    var reading = task.Result[i];
                    var phase = AgentPhases.Combine(state, reading, QuotaExhausted(bot.Provider));
                    Offer(bot, phase, reading is { } r && r.Phase == phase ? r.Detail : null, seconds);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// Guests carry no ActivityMonitor usage overlay, and the monitor only
    /// overlays the two island-slot providers, so the drawer checks quota
    /// for every character itself.
    private static bool QuotaExhausted(DisplayProvider provider)
    {
        var usage = ProviderUsage.For(provider);
        var now = DateTimeOffset.Now;
        return (!usage.FiveHour.HasError && usage.FiveHour.At(now).UsedPercent >= 1)
            || (!usage.Weekly.HasError && usage.Weekly.At(now).UsedPercent >= 1);
    }

    /// A shown phase stays at least MinDwellSeconds so quick tool bursts do
    /// not flicker, unless the new phase outranks it (asking, errors).
    private void Offer(Bot bot, AgentPhase phase, string? detail, double now)
    {
        if (phase == bot.Phase)
        {
            if (detail != bot.Detail)
            {
                bot.Detail = detail;
                RefreshCard();
            }
            return;
        }
        var dwellOver = now - bot.PhaseSince >= MinDwellSeconds;
        var outranks = AgentPhases.Priority(phase) < AgentPhases.Priority(bot.Phase);
        if (!dwellOver && !outranks) return;
        bot.Phase = phase;
        bot.Detail = detail;
        bot.PhaseSince = now;
        if (_engine is not null) _engine.SetState(bot.Index, DrawerLooks.Entry(phase));
        else bot.Surface.PresentFallback(DrawerSwatch.Of(bot.Color), phase);
        RefreshCard();
        if (_phaseLog is not null)
        {
            try
            {
                File.AppendAllText(_phaseLog,
                    $"{DateTimeOffset.Now:HH:mm:ss.fff}\t{bot.Provider}\t{phase}\t{detail}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }

    // MARK: - Animation

    private static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    private void OnRendering(object? sender, EventArgs e)
    {
        // Each character steps on its own schedule with a few ms of vsync
        // tolerance; comparing raw elapsed time drops callbacks landing early.
        var now = _clock.Elapsed.TotalSeconds;
        _due.Clear();
        foreach (var bot in _bots)
        {
            if (bot.Visible && now >= bot.NextStep - 0.004) _due.Add(bot);
        }
        if (_due.Count > 0) Step(now);
    }

    /// Working characters step on every frame WPF composes — the display's
    /// refresh rate, up to WPF's own ceiling (~120 Hz on a 240 Hz panel).
    /// Calm ones whose eyes are following a nearby pointer run at 60 fps and
    /// the rest breathe at 30: WPF composes every window of the process on one
    /// render thread, and this layered window re-uploads on each step, so idle
    /// characters at full rate would cost the Settings page frames. Reduced
    /// motion keeps 12 / 6.
    private void Step(double now)
    {
        var engine = _engine!;
        var pointerNear = !ReducedMotion && UpdateGaze();
        var activeFps = ReducedMotion ? 12 : double.PositiveInfinity;
        var gazeFps = ReducedMotion ? 12 : 60;
        var calmFps = ReducedMotion ? 6 : 30;
        var dts = new double[_bots.Count];
        Array.Fill(dts, double.NaN);
        foreach (var bot in _due)
        {
            dts[bot.Index] = Math.Min(now - bot.LastStep, 0.1);
            bot.LastStep = now;
            var fps = !DrawerLooks.CalmStates.Contains(bot.EngineState) ? activeFps : pointerNear ? gazeFps : calmFps;
            var interval = 1.0 / fps;
            bot.NextStep = now - bot.NextStep > interval ? now + interval : bot.NextStep + interval;
        }
        var frames = engine.Tick(dts, BotSize);
        foreach (var bot in _due)
        {
            if (frames[bot.Index] is not var (engineState, svg)) continue;
            // Self-completing entries (finished, spawning…) fall back to
            // idle; keep playing the agent's phase instead.
            if (engineState == "idle" && bot.Phase != AgentPhase.Idle)
            {
                engine.SetState(bot.Index, DrawerLooks.Hold(bot.Phase));
            }
            bot.EngineState = engineState;
            if (svg == bot.LastSvg) continue;
            bot.LastSvg = svg;
            bot.Surface.Present(svg, _renderer);
        }
    }

    /// Feeds the pointer to the due characters; true when it is near.
    private bool UpdateGaze()
    {
        if (!DrawerNative.GetCursorPos(out var cursor)) return false;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return false;
        var dip = target.TransformFromDevice.Transform(new Point(cursor.X, cursor.Y));
        var local = new Point(dip.X - Left, dip.Y - Top);
        var anyNear = false;
        foreach (var bot in _bots)
        {
            if (!bot.Visible) continue;
            var center = new Point(bot.Bounds.X + bot.Bounds.Width / 2, bot.Bounds.Y + bot.Bounds.Height / 2);
            var near = (local - center).Length < GazeRange;
            anyNear |= near;
            if (!_due.Contains(bot)) continue;
            if (near)
            {
                _engine!.SetGaze(bot.Index, bot.Bounds, local);
                bot.Gazing = true;
            }
            else if (bot.Gazing)
            {
                _engine!.ClearGaze(bot.Index);
                bot.Gazing = false;
            }
        }
        return anyNear;
    }

    // MARK: - Hover card

    /// Hovering a character opens a card pointing at it; null (the tray's
    /// show action) stacks every character's status in one card.
    private void OpenCard(Bot? only)
    {
        _cardClose.Stop();
        _cardClose.Interval = TimeSpan.FromMilliseconds(250);
        var popping = !_card.IsOpen || _cardBot != only;
        _cardBot = only;
        RefreshCard();
        _card.IsOpen = true;
        if (popping) _bubble.PlayIn(ReducedMotion);
    }

    /// The pointer's tip sits just off the character, on the screen side; the
    /// card centres on it and slides along the edge to stay inside the work
    /// area, the pointer following the character.
    private void RefreshCard()
    {
        var right = DrawerStore.Shared.Side == DrawerSide.Right;
        var sections = _bots
            .Where(b => b.Visible && (_cardBot is null || b == _cardBot))
            .Select(bot => new CardSection(
                ProviderIdentity.DisplayName(bot.Provider),
                DrawerLooks.PhaseLabel(bot.Phase),
                bot.Detail,
                DrawerSwatch.Of(bot.Color),
                Meters(bot.Provider),
                TokenCounts(bot.Provider),
                QuotaNote(bot.Provider)))
            .ToList();
        _bubble.SetContent(sections, pointRight: right);
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var card = _bubble.CardSize;
        var anchor = _cardBot?.Bounds ?? new Rect(InnerInset, Height / 2 - BotSize / 2, BotSize, BotSize);
        var area = WorkAreaDip(IslandTargetDisplayStore.Shared.Resolve());
        // Absolute screen DIPs: relative placement against the drawer's canvas
        // landed a whole card-width too far from the character.
        var aim = Top + anchor.Top + anchor.Height / 2;
        var top = Math.Clamp(aim - card.Height / 2, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - 6 - card.Height));
        _bubble.AimAt(aim - top);
        var tip = _bubble.TipOffset;
        var tipX = right ? Left + anchor.Left - 4 : Left + anchor.Right + 4;
        _card.HorizontalOffset = tipX - tip.X;
        _card.VerticalOffset = top - PointerCard.Room;
    }

    /// Quota windows in the user's Used/Remaining mode: the bar fills with
    /// the shown share, the warning tint follows actual use.
    private static IReadOnlyList<CardMeter> Meters(DisplayProvider provider)
    {
        var usage = ProviderUsage.For(provider);
        var mode = QuotaDisplayModeStore.Shared;
        CardMeter Meter(WindowUsage window, string label)
        {
            var meter = StatusCards.MeterFor(window, L10n.Tr(label), mode.ShowsRemaining, DateTimeOffset.Now);
            return new CardMeter(meter.Label, meter.Value, meter.Shown, meter.Used);
        }
        return provider switch
        {
            DisplayProvider.Claude => new[] { Meter(usage.FiveHour, "5h"), Meter(usage.Weekly, "week") },
            DisplayProvider.Codex when usage.SecondaryMissing => new[] { Meter(usage.FiveHour, "week") },
            DisplayProvider.Codex => new[] { Meter(usage.FiveHour, "5h"), Meter(usage.Weekly, "week") },
            DisplayProvider.Cursor => new[] { Meter(usage.FiveHour, "30d") },
            // No quota source: the card shows phases and tokens only.
            DisplayProvider.Kimi or DisplayProvider.DeepSeek => Array.Empty<CardMeter>(),
            _ => new[] { Meter(usage.FiveHour, "week") },
        };
    }

    /// Today's and this month's tokens from the local logs, counted the way
    /// the Token counting setting says. Antigravity keeps no local log.
    private static CardTokens? TokenCounts(DisplayProvider provider)
    {
        if (provider == DisplayProvider.Antigravity) return null;
        var summary = Cost.CostStore.Shared.Summary(provider);
        var billable = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
        var today = Formatting.CompactTokens(billable ? summary.TodayBillableTokens : summary.TodayTokens);
        var month = Formatting.CompactTokens(billable ? summary.MonthBillableTokens : summary.MonthTokens);
        return new CardTokens(
            L10n.Tr("Tokens"),
            $"{L10n.Tr("today")} {today}", today,
            L10n.TrFormat("{0} this month", month), month);
    }

    /// Codex's banked reset cards and the account the auto-switcher last
    /// rotated to — both used to live in the island panel.
    private static string? QuotaNote(DisplayProvider provider)
    {
        if (provider != DisplayProvider.Codex) return null;
        var parts = new List<string>();
        if (ProviderUsage.For(provider).ResetCards is > 0 and var cards) parts.Add(L10n.TrFormat("{0} reset cards", cards));
        if (UsageStore.Shared.CodexAutoSwitched is { } account) parts.Add(L10n.TrFormat("Auto-switched to {0}", account));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // MARK: - Interaction

    private void Wire(Bot bot)
    {
        var surface = bot.Surface;
        surface.MouseEnter += (_, _) => OpenCard(bot);
        surface.MouseLeave += (_, _) => _cardClose.Start();
        surface.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = bot;
            _dragging = false;
            _pressY = CursorDip().Y;
            _pressTop = Top;
            surface.CaptureMouse();
            e.Handled = true;
        };
        surface.MouseMove += (_, _) =>
        {
            if (_pressed != bot) return;
            var dy = CursorDip().Y - _pressY;
            if (!_dragging && Math.Abs(dy) < DragThreshold) return;
            _dragging = true;
            var area = WorkAreaDip(IslandTargetDisplayStore.Shared.Resolve());
            Top = Math.Clamp(_pressTop + dy, area.Top, Math.Max(area.Top, area.Bottom - Height));
        };
        surface.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed != bot) return;
            _pressed = null;
            surface.ReleaseMouseCapture();
            if (_dragging)
            {
                var area = WorkAreaDip(IslandTargetDisplayStore.Shared.Resolve());
                var travel = area.Height - Height;
                DrawerStore.Shared.Offset = travel > 0 ? (Top - area.Top) / travel : 0.5;
            }
            else
            {
                _engine?.Poke(bot.Index, "nod");
            }
            e.Handled = true;
        };
        // One menu instance, refilled on each opening so the checkmarks and
        // the shown-provider list are current.
        var menu = Theme.GrokMenu.Create();
        surface.ContextMenu = menu;
        FillMenu(menu, bot);
        surface.ContextMenuOpening += (_, _) => FillMenu(menu, bot);
    }

    private void FillMenu(ContextMenu menu, Bot bot)
    {
        menu.Items.Clear();
        menu.Items.Add(Theme.GrokMenu.Title(ProviderIdentity.DisplayName(bot.Provider), DrawerSwatch.Of(bot.Color)));
        menu.Items.Add(new Separator());
        if (_engine is not null)
        {
            var (shape, color) = ValidLook(DrawerLooks.For(bot.Provider));
            var shapes = new MenuItem { Header = L10n.Tr("Shape") };
            foreach (var option in _engine.Shapes)
            {
                var item = new MenuItem { Header = DrawerLooks.ShapeLabel(option), IsCheckable = true, IsChecked = option == shape };
                item.Click += (_, _) => ChangeLook(bot, option, color);
                shapes.Items.Add(item);
            }
            var colors = new MenuItem { Header = L10n.Tr("Color") };
            foreach (var option in _engine.Colors)
            {
                var swatch = new StackPanel { Orientation = Orientation.Horizontal };
                swatch.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Fill = new SolidColorBrush(DrawerSwatch.Of(option)),
                    Stroke = Theme.GrokTheme.InkBrush(Theme.GrokTheme.StrokeSecondary),
                    StrokeThickness = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                });
                swatch.Children.Add(new TextBlock { Text = DrawerLooks.ColorLabel(option), VerticalAlignment = VerticalAlignment.Center });
                var item = new MenuItem { Header = swatch, IsCheckable = true, IsChecked = option == color };
                item.Click += (_, _) => ChangeLook(bot, shape, option);
                colors.Items.Add(item);
            }
            menu.Items.Add(shapes);
            menu.Items.Add(colors);
            menu.Items.Add(new Separator());
        }
        var shown = new MenuItem { Header = L10n.Tr("Show in drawer") };
        foreach (var other in _bots)
        {
            var item = new MenuItem
            {
                Header = ProviderIdentity.DisplayName(other.Provider),
                IsCheckable = true,
                IsChecked = other.Visible,
            };
            var provider = other.Provider;
            item.Click += (_, _) => DrawerStore.Shared.SetShown(provider, item.IsChecked);
            shown.Items.Add(item);
        }
        menu.Items.Add(shown);
        var right = DrawerStore.Shared.Side == DrawerSide.Right;
        var flip = new MenuItem { Header = L10n.Tr(right ? "Dock to the left edge" : "Dock to the right edge") };
        flip.Click += (_, _) => DrawerStore.Shared.Side = right ? DrawerSide.Left : DrawerSide.Right;
        menu.Items.Add(flip);
        menu.Items.Add(new Separator());
        // The drawer has a UI thread of its own; app windows live on the app's.
        var app = Application.Current.Dispatcher;
        var cost = new MenuItem { Header = L10n.Tr("Cost…") };
        cost.Click += (_, _) => app.BeginInvoke(CostWindow.Open);
        menu.Items.Add(cost);
        var settings = new MenuItem { Header = L10n.Tr("Settings…") };
        settings.Click += (_, _) => app.BeginInvoke(SettingsWindow.Open);
        menu.Items.Add(settings);
    }

    private void ChangeLook(Bot bot, string shape, string color)
    {
        DrawerStore.Shared.SetLook(bot.Provider, shape, color);
        bot.Color = color;
        if (_engine is not null)
        {
            _engine.SetLook(bot.Index, shape, color);
            bot.LastSvg = "";
        }
        else
        {
            bot.Surface.PresentFallback(DrawerSwatch.Of(color), bot.Phase);
        }
    }

    private Point CursorDip()
    {
        DrawerNative.GetCursorPos(out var cursor);
        return PresentationSource.FromVisual(this)?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(new Point(cursor.X, cursor.Y))
            : new Point(cursor.X, cursor.Y);
    }

    /// AGENTISLAND_DEBUG_DRAWER_PNG: the real composed screen pixels of the
    /// drawer area, so verification sees exactly what is over the desktop.
    private void CaptureScreen(string path)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        var topLeft = target.TransformToDevice.Transform(new Point(Left, Top));
        var bottomRight = target.TransformToDevice.Transform(new Point(Left + ActualWidth, Top + ActualHeight));
        int x = (int)topLeft.X, y = (int)topLeft.Y;
        int w = (int)(bottomRight.X - topLeft.X), h = (int)(bottomRight.Y - topLeft.Y);
        var screen = DrawerNative.GetDC(IntPtr.Zero);
        var memory = DrawerNative.CreateCompatibleDC(screen);
        var bitmap = DrawerNative.CreateCompatibleBitmap(screen, w, h);
        var previous = DrawerNative.SelectObject(memory, bitmap);
        try
        {
            DrawerNative.BitBlt(memory, 0, 0, w, h, screen, x, y, DrawerNative.SrcCopy | DrawerNative.CaptureBlt);
            DrawerNative.SelectObject(memory, previous);
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (IOException)
        {
        }
        finally
        {
            DrawerNative.DeleteObject(bitmap);
            DrawerNative.DeleteDC(memory);
            DrawerNative.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
