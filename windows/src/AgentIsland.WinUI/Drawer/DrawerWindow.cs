using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using AgentIsland.WinUI.Platform;
using AgentIsland.WinUI.Shell;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Drawer;

/// The drawer: a transparent column of characters on a side edge, one per
/// provider, each playing its agent's live phase. Win2D draws them on its
/// own game-loop thread, at the display's refresh rate while any of them is
/// busy; only the characters' circles take clicks. The character set is a
/// theme: the bundled pixel bots, or the classic recolourable shapes.
internal sealed class DrawerWindow : Window
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
    private const double SpeechGapSeconds = 20;
    // Idle characters fidget every 25-75 s, the first one within a minute.
    private const double FidgetGap = 50;
    private const double FirstFidgetAfter = 12;
    // Sessions found at launch are not news: no lines for the first scans.
    private const double SpeechQuietAtStartSeconds = 8;

    private sealed class Bot
    {
        public required DisplayProvider Provider { get; init; }
        public required TriggerTool Tool { get; init; }
        public required int Index { get; init; }
        public required string Shape { get; set; }
        public required string Color { get; set; }
        public Rect Bounds { get; set; }
        public bool Visible { get; set; }
        public AgentPhase Phase { get; set; }
        public string? Detail { get; set; }
        public int Running { get; set; }
        public double PhaseSince { get; set; } = double.NegativeInfinity;
        public SpeechBubble? Bubble { get; set; }
        public double LastSpoke { get; set; } = double.NegativeInfinity;
    }

    /// A character's animation state, owned by the game loop.
    private sealed class Slot
    {
        public string EngineState = "idle";
        public string Hold = "idle";
        public string? Svg;
        public string? RenderedSvg;
        public CanvasRenderTarget? Rendered;
        public bool Gazing;
        public double LastStep;
        public double NextStep;
        public uint Swatch;
        public string[] Fidgets = Array.Empty<string>();
        public string? Fidget;
        public double FidgetUntil;
        public double NextFidget;
    }

    /// What the game loop draws: the shown slots at their DIP offsets, and
    /// the window's physical origin and scale for the gaze.
    private sealed record Scene((int Index, float X, float Y)[] Shown, int Left, int Top, double Scale);

    private readonly IntPtr _hwnd;
    // Built on the UI thread, then owned by the game loop: a theme switch
    // hands the new engine over through _commands.
    private ICharacterEngine? _engine;
    private CharacterTheme _theme;
    private volatile CharacterTheme _loopTheme;
    // The classic engine's shapes and colours for the look picker; null in
    // the pixel theme, which hides the picker.
    private IReadOnlyList<string>? _shapes;
    private IReadOnlyList<string>? _colors;
    private readonly SvgCanvas _svg = new();
    private readonly List<Bot> _bots = new();
    private readonly Slot[] _slots;
    private readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly CanvasAnimatedControl _canvas;
    private readonly HoverCard _card;
    private readonly Random _random = new();
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _poll;
    private readonly DispatcherQueueTimer _guard;
    private readonly OverlappedPresenter _presenter;
    private readonly DispatcherQueueTimer _cardClose;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly List<int> _due = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Action> _teardown = new();
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly DrawerStats? _stats;
    private volatile Scene _scene = new(Array.Empty<(int, float, float)>(), 0, 0, 1);
    private volatile bool _reducedMotion;
    private volatile int _activeCap;
    private int _pace = -1;
    private Win32.Screen? _screen;
    private bool _shown;
    private bool _pollInFlight;
    private bool _coveredByFullscreen;
    private bool _deliberatelyHidden;
    private int _topmostCountdown = TopmostEverySeconds;
    private Bot? _cardBot;
    private bool _cardRefreshPending;
    // Grok and Cursor share one X Subscription: Grok's character stands for
    // both apps and Cursor's steps aside (SharedSubscription).
    private bool _merged;
    private Bot? _pressed;
    private bool _dragging;
    private int _pressY;
    private int _pressTop;
    private bool _closed;

    public DrawerWindow()
    {
        Title = "Agent Island";
        _hwnd = WindowNative.GetWindowHandle(this);
        _queue = DispatcherQueue;
        _reducedMotion = !_uiSettings.AnimationsEnabled;
        UpdateCap();

        _theme = _loopTheme = DrawerStore.Shared.Theme;
        _engine = CreateEngine(_theme);
        foreach (var provider in DisplayProviders.All)
        {
            var (shape, color) = ValidLook(DrawerLooks.For(provider));
            var bot = new Bot
            {
                Provider = provider,
                Tool = provider.ToTriggerTool(),
                Index = _bots.Count,
                Shape = shape,
                Color = color,
            };
            AddBot(_engine, bot);
            _bots.Add(bot);
        }
        _slots = _bots.Select(b => new Slot
        {
            Swatch = SwatchOf(b),
            Fidgets = _engine?.Fidgets(b.Provider) ?? Array.Empty<string>(),
            NextFidget = FirstFidgetAfter + Random.Shared.NextDouble() * FidgetGap,
        }).ToArray();

        SystemBackdrop = new TransparentBackdrop();
        _canvas = new CanvasAnimatedControl
        {
            ClearColor = Microsoft.UI.Colors.Transparent,
            IsFixedTimeStep = true,
            TargetElapsedTime = TimeSpan.FromSeconds(1 / 30.0),
        };
        _canvas.Update += OnUpdate;
        _canvas.Draw += OnDraw;
        _root.Children.Add(_canvas);
        _card = new HoverCard(_root);
        Content = _root;
        WirePointer();

        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(_presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Agent Island";
        var style = (long)Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle);
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlExStyle, (IntPtr)(style | Win32.WsExToolWindow | Win32.WsExNoActivate));
        var noRounding = 1;
        Win32.DwmSetWindowAttribute(_hwnd, 33, ref noRounding, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE);
        Win32.DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));

        _poll = Timer(TimeSpan.FromMilliseconds(500), Poll);
        _guard = Timer(TimeSpan.FromSeconds(1), Guard);
        _cardClose = Timer(TimeSpan.FromMilliseconds(250), CloseCardUnlessHovered);
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DRAWER_STATS") is { Length: > 0 } statsPath)
        {
            _stats = new DrawerStats(statsPath);
        }

        Watch(ProviderVisibilityStore.Shared, Layout);
        Watch(DrawerStore.Shared, () =>
        {
            ApplyTheme();
            UpdateCap();
            Layout();
        });
        Watch(IslandTargetDisplayStore.Shared, Layout);
        Watch(UsageStore.Shared, RefreshCard);
        Watch(KimiUsageStore.Shared, RefreshCard);
        Watch(GrokUsageStore.Shared, () => OnSubscriptionUsage(DisplayProvider.Grok));
        Watch(CursorUsageStore.Shared, () => OnSubscriptionUsage(DisplayProvider.Cursor));
        Watch(AntigravityUsageStore.Shared, () => RefreshCardFor(DisplayProvider.Antigravity));
        Watch(QuotaDisplayModeStore.Shared, RefreshCard);
        Watch(TokenCountModeStore.Shared, RefreshCard);
        Watch(CostStore.Shared, RefreshCard);
        EventHandler onDisplay = (_, _) => _queue.TryEnqueue(Layout);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += onDisplay;
        _teardown.Add(() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= onDisplay);

        Layout();
        _poll.Start();
        _guard.Start();
        Poll();
        ApplyVisibility();
    }

    /// Tray click: hide the drawer, or bring it back.
    public void ToggleDeliberately()
    {
        _deliberatelyHidden = !_deliberatelyHidden;
        ApplyVisibility();
    }

    /// Bring the drawer back and briefly show every character's status at
    /// once — the one place the cards appear together.
    public void Reveal()
    {
        _deliberatelyHidden = false;
        ApplyVisibility();
        OpenCard(null);
        _cardClose.Interval = TimeSpan.FromSeconds(4);
        _cardClose.Start();
    }

    public void Relocalize() => RefreshCard();

    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _poll.Stop();
        _guard.Stop();
        _cardClose.Stop();
        _card.Dispose();
        Hush();
        foreach (var teardown in _teardown) teardown();
        _canvas.Paused = true;
        _canvas.RemoveFromVisualTree();
        Close();
    }

    /// The classic engine's shapes and colours; null without it or in the
    /// pixel theme, where each bot has its one look.
    public IReadOnlyList<string>? Shapes => _shapes;

    public IReadOnlyList<string>? Colors => _colors;

    /// The provider's colour in the current theme, for cards and charts.
    public uint SwatchOf(DisplayProvider provider) => SwatchOf(_bots.First(b => b.Provider == provider));

    private uint SwatchOf(Bot bot) => DrawerLooks.Swatch(bot.Provider, bot.Color, _theme);

    public (string Shape, string Color) LookOf(DisplayProvider provider)
    {
        var bot = _bots.First(b => b.Provider == provider);
        return (bot.Shape, bot.Color);
    }

    public void SetLook(DisplayProvider provider, string shape, string color) =>
        ChangeLook(_bots.First(b => b.Provider == provider), shape, color);

    /// What the provider's character is playing now, with its detail.
    public (AgentPhase Phase, string? Detail) PhaseOf(DisplayProvider provider)
    {
        var bot = _bots.First(b => b.Provider == provider);
        return (bot.Phase, bot.Detail);
    }

    public async void DebugCard(int? index, string? snapshot)
    {
        var visible = _bots.Where(b => b.Visible).ToList();
        OpenCard(index is { } n && n < visible.Count ? visible[n] : null);
        if (snapshot is null) return;
        await Task.Delay(1500);
        Directory.CreateDirectory(snapshot);
        await _card.SnapshotAsync(Path.Combine(snapshot, $"card-{index?.ToString() ?? "all"}.png"));
    }

    /// AGENTISLAND_DEBUG_SAY=<phase>: the first character says that phase's
    /// line, past the launch quiet period.
    public void DebugSay(AgentPhase phase)
    {
        if (_bots.FirstOrDefault(b => b.Visible) is not { } bot) return;
        bot.LastSpoke = double.NegativeInfinity;
        Speak(bot, phase, Math.Max(_clock.Elapsed.TotalSeconds, SpeechQuietAtStartSeconds));
    }

    public void DebugMenu()
    {
        if (_bots.FirstOrDefault(b => b.Visible) is { } bot) OpenMenu(bot, keepOpen: true);
    }

    private DispatcherQueueTimer Timer(TimeSpan interval, Action tick)
    {
        var timer = _queue.CreateTimer();
        timer.Interval = interval;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private void Watch(INotifyPropertyChanged source, Action handler)
    {
        PropertyChangedEventHandler h = (_, _) => _queue.TryEnqueue(() =>
        {
            if (!_closed) handler();
        });
        source.PropertyChanged += h;
        _teardown.Add(() => source.PropertyChanged -= h);
    }

    // MARK: - Layout

    private (string Shape, string Color) ValidLook((string Shape, string Color) look)
    {
        if (_shapes is null || _colors is null) return look;
        return (_shapes.Contains(look.Shape) ? look.Shape : "blob",
            _colors.Contains(look.Color) ? look.Color : "blue");
    }

    // MARK: - Theme

    /// The theme's engine, or null when it cannot run: the classic look needs
    /// the local Grok Bot engine copy, and either can fail to load V8. Without
    /// one the drawer draws its fallback discs.
    private ICharacterEngine? CreateEngine(CharacterTheme theme)
    {
        ICharacterEngine? engine = null;
        try
        {
            if (theme == CharacterTheme.Pixel) engine = new PixelEngine(PixelEngine.Directory);
            else if (GrokEngineLocator.Find() is { } directory) engine = new GrokEngine(directory);
        }
        catch (Exception error)
        {
            CrashLog.Write(error, theme == CharacterTheme.Pixel ? "Pixel engine" : "Grok engine");
        }
        _shapes = (engine as GrokEngine)?.Shapes;
        _colors = (engine as GrokEngine)?.Colors;
        return engine;
    }

    private static void AddBot(ICharacterEngine? engine, Bot bot)
    {
        if (engine is null) return;
        var index = engine.AddBot(bot.Provider, bot.Shape, bot.Color,
            DrawerLooks.PersonalityOf(bot.Provider).Tempo, Random.Shared.Next(1, 1_000_000));
        if (index != bot.Index) throw new InvalidOperationException($"engine index {index} for drawer bot {bot.Index}");
    }

    /// Swaps the character set live: a new engine gets every bot, resting in
    /// its agent's current phase, and the game loop takes it over.
    private void ApplyTheme()
    {
        var theme = DrawerStore.Shared.Theme;
        if (theme == _theme) return;
        _theme = theme;
        var engine = CreateEngine(theme);
        var rest = new List<(int Index, string Hold, uint Swatch, string[] Fidgets)>();
        foreach (var bot in _bots)
        {
            (bot.Shape, bot.Color) = ValidLook((bot.Shape, bot.Color));
            AddBot(engine, bot);
            var hold = bot.Phase == AgentPhase.Idle ? "idle" : DrawerLooks.Hold(bot.Phase, theme);
            rest.Add((bot.Index, hold, SwatchOf(bot), engine?.Fidgets(bot.Provider) ?? Array.Empty<string>()));
        }
        _commands.Enqueue(() =>
        {
            var old = _engine;
            _engine = engine;
            _loopTheme = theme;
            foreach (var (index, hold, swatch, fidgets) in rest)
            {
                var slot = _slots[index];
                slot.Hold = slot.EngineState = hold;
                slot.Swatch = swatch;
                slot.Fidgets = fidgets;
                slot.Fidget = null;
                slot.Gazing = false;
                slot.Svg = null;
                slot.NextStep = 0;
                engine?.SetState(index, hold, -1);
            }
            old?.Dispose();
        });
        Hush();
        RefreshCard();
    }

    private static Win32.Screen ResolveScreen()
    {
        var screens = Win32.Screens();
        var choice = IslandTargetDisplayStore.Shared.Choice;
        return screens.FirstOrDefault(s => s.Device == choice)
            ?? screens.FirstOrDefault(s => s.Primary)
            ?? screens[0];
    }

    private double WindowHeightDip(int count) => VerticalPadding * 2 + count * BotSize + Math.Max(0, count - 1) * Gap;

    private void Layout()
    {
        var visibility = ProviderVisibilityStore.Shared;
        var store = DrawerStore.Shared;
        foreach (var bot in _bots) bot.Visible = store.IsShown(bot.Provider, visibility.IsDetected(bot.Provider));
        _merged = SharedSubscription.GrokWithCursor();
        if (_merged)
        {
            var grok = BotOf(DisplayProvider.Grok);
            var cursor = BotOf(DisplayProvider.Cursor);
            grok.Visible |= cursor.Visible;
            cursor.Visible = false;
        }
        // An empty drawer would leave no character to right-click back.
        if (!_bots.Any(b => b.Visible)) _bots[0].Visible = true;

        var visible = _bots.Where(b => b.Visible).ToList();
        var left = store.Side == DrawerSide.Right ? InnerInset : EdgeInset;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Bounds = new Rect(left, VerticalPadding + i * (BotSize + Gap), BotSize, BotSize);
        }

        var screen = _screen = ResolveScreen();
        var scale = screen.Scale;
        var width = (int)Math.Ceiling((InnerInset + BotSize + EdgeInset) * scale);
        var height = (int)Math.Ceiling(WindowHeightDip(visible.Count) * scale);
        var work = screen.Work;
        var x = store.Side == DrawerSide.Right ? work.Right - width : work.Left;
        var y = work.Top + (int)Math.Round(store.Offset * Math.Max(0, work.Bottom - work.Top - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // Only the characters take clicks: the window region is their circles.
        var region = Win32.CreateRectRgn(0, 0, 0, 0);
        foreach (var bot in visible)
        {
            var b = bot.Bounds;
            var circle = Win32.CreateEllipticRgn(
                (int)Math.Floor(b.Left * scale), (int)Math.Floor(b.Top * scale),
                (int)Math.Ceiling(b.Right * scale) + 1, (int)Math.Ceiling(b.Bottom * scale) + 1);
            Win32.CombineRgn(region, region, circle, 2);
            Win32.DeleteObject(circle);
        }
        Win32.SetWindowRgn(_hwnd, region, true);

        var before = _scene;
        Publish(x, y, scale);
        // A bubble beside a character that moved would point at nothing.
        if (before.Left != x || before.Top != y || !before.Shown.SequenceEqual(_scene.Shown)) Hush();
        RefreshCard();
    }

    private void Publish(int x, int y, double scale)
    {
        _scene = new Scene(
            _bots.Where(b => b.Visible).Select(b => (b.Index, (float)b.Bounds.X, (float)b.Bounds.Y)).ToArray(),
            x, y, scale);
    }

    /// The monitor's work area in the window's DIPs.
    private Rect WorkAreaDip()
    {
        var scene = _scene;
        var work = (_screen ??= ResolveScreen()).Work;
        return new Rect(
            (work.Left - scene.Left) / scene.Scale,
            (work.Top - scene.Top) / scene.Scale,
            (work.Right - work.Left) / scene.Scale,
            (work.Bottom - work.Top) / scene.Scale);
    }

    // MARK: - Visibility

    /// Fullscreen apps hide the drawer (and pause it); otherwise it re-asserts
    /// topmost now and then, since fullscreen and topmost apps park above it.
    private void Guard()
    {
        _reducedMotion = !_uiSettings.AnimationsEnabled;
        UpdateCap();
        var monitor = Win32.MonitorFromWindow(_hwnd, Win32.MonitorDefaultToNearest);
        var foreground = Win32.GetForegroundWindow();
        var covered = foreground != _hwnd && Win32.IsFullscreen(foreground, monitor);
        if (covered != _coveredByFullscreen)
        {
            _coveredByFullscreen = covered;
            ApplyVisibility();
        }
        if (!_shown) return;
        KeepOnTop();
        if (--_topmostCountdown > 0) return;
        _topmostCountdown = TopmostEverySeconds;
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
    }

    /// Topmost has to go through the presenter: AppWindow holds the window to
    /// its presenter's state and quietly undoes a bare SetWindowPos. Asked for
    /// before the window was first shown, it never took on a normal launch,
    /// so the drawer sat under other apps; flipping it makes WinUI apply it.
    private void KeepOnTop()
    {
        if (((long)Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle) & Win32.WsExTopmost) != 0) return;
        _presenter.IsAlwaysOnTop = false;
        _presenter.IsAlwaysOnTop = true;
    }

    /// The busy characters' ceiling: the user's choice (0 follows the
    /// display), at most 60 fps on battery unless that saving is off.
    private void UpdateCap()
    {
        var store = DrawerStore.Shared;
        var cap = store.FrameRate;
        if (store.SaveBattery && Win32.OnBattery()) cap = cap == 0 ? 60 : Math.Min(cap, 60);
        _activeCap = cap;
    }

    private void ApplyVisibility()
    {
        var show = !_deliberatelyHidden && !_coveredByFullscreen;
        if (show == _shown) return;
        _shown = show;
        if (show)
        {
            AppWindow.Show(false);
            KeepOnTop();
            _canvas.Paused = false;
        }
        else
        {
            _card.Hide();
            Hush();
            _canvas.Paused = true;
            AppWindow.Hide();
        }
    }

    // MARK: - Phases

    private async void Poll()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var monitor = ActivityMonitor.Shared;
            var merged = _merged;
            // The merged character follows both apps' sessions.
            var jobs = _bots
                .Where(b => b.Visible)
                .SelectMany(b => (merged && b.Provider == DisplayProvider.Grok ? new[] { b.Tool, TriggerTool.Cursor } : new[] { b.Tool })
                    .Select(tool =>
                    {
                        var thread = monitor.ThreadFor(tool);
                        return (Bot: b, Tool: tool, State: monitor.StateFor(tool), Path: thread?.TranscriptPath,
                            Running: monitor.RunningFor(tool), Prompt: thread?.PendingPrompt);
                    }))
                .ToList();
            // Tail reads stay off the UI thread. Working sessions are read for
            // how they work, network trouble for a prompt that may be open, and
            // finished turns for whether they ended on an error. The pixel
            // panels read in English, details included.
            var english = _theme == CharacterTheme.Pixel;
            var readings = await Task.Run(() =>
            {
                using (english ? L10n.English() : null)
                {
                    return jobs
                        .Select(job => job.Prompt ?? (job.State is ActivityState.Working or ActivityState.NetworkTrouble or ActivityState.NeedsYou
                            && job.Path is { } path
                            ? AgentPhaseClassifier.Classify(job.Tool, path, now)
                            : null))
                        .ToList();
                }
            });
            if (_closed) return;
            var seconds = _clock.Elapsed.TotalSeconds;
            var recounted = false;
            var results = new List<(Bot Bot, AgentPhase Phase, string? Detail, int Running)>();
            for (var i = 0; i < jobs.Count; i++)
            {
                var (bot, tool, state, _, running, _) = jobs[i];
                var reading = readings[i];
                var phase = AgentPhases.Combine(state, reading, QuotaExhausted(tool.ToDisplayProvider()));
                var detail = reading is { } r && r.Phase == phase ? r.Detail : null;
                if (phase == AgentPhase.NetworkTrouble && detail is null && monitor.IsOffline)
                {
                    using (english ? L10n.English() : null) detail = L10n.Tr("No internet connection");
                }
                results.Add((bot, phase, detail, running));
            }
            foreach (var parts in results.GroupBy(result => result.Bot))
            {
                var bot = parts.Key;
                var running = parts.Sum(part => part.Running);
                recounted |= bot.Running != running && (_cardBot is null || _cardBot == bot);
                bot.Running = running;
                var (phase, detail) = parts.Count() == 2
                    ? SharedSubscription.Pick((parts.First().Phase, parts.First().Detail), (parts.Last().Phase, parts.Last().Detail))
                    : (parts.First().Phase, parts.First().Detail);
                Offer(bot, phase, detail, seconds);
            }
            // A task can start or end without the shown phase changing.
            if (recounted) RefreshCard();
        }
        catch (Exception error)
        {
            CrashLog.Write(error, "Drawer poll");
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    /// The activity monitor only overlays usage for some providers, so the
    /// drawer checks every character's quota itself.
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
                RefreshCardFor(bot.Provider);
            }
            return;
        }
        var dwellOver = now - bot.PhaseSince >= MinDwellSeconds;
        var outranks = AgentPhases.Priority(phase) < AgentPhases.Priority(bot.Phase);
        if (!dwellOver && !outranks) return;
        bot.Phase = phase;
        bot.Detail = detail;
        bot.PhaseSince = now;
        Speak(bot, phase, now);
        var index = bot.Index;
        var theme = _theme;
        var entry = DrawerLooks.Entry(phase, theme);
        var hold = phase == AgentPhase.Idle ? "idle" : DrawerLooks.Hold(phase, theme);
        _commands.Enqueue(() =>
        {
            var slot = _slots[index];
            slot.Hold = hold;
            slot.Fidget = null;
            slot.NextFidget = _clock.Elapsed.TotalSeconds + FidgetGap * (0.5 + Random.Shared.NextDouble());
            if (_engine is not null) _engine.SetState(index, entry, DrawerLooks.IsOneShot(entry, theme) ? 0 : -1);
            else slot.EngineState = hold;
            slot.NextStep = 0;
        });
        RefreshCardFor(bot.Provider);
    }

    /// A line in a bubble when the agent needs you; a line that no longer
    /// holds (the agent went back to work) is taken back.
    private void Speak(Bot bot, AgentPhase phase, double now)
    {
        if (!BotLines.Speaks(phase))
        {
            bot.Bubble?.Hide(animated: true);
            return;
        }
        if (!DrawerStore.Shared.SpeechBubbles || !_shown || !bot.Visible) return;
        if (now < SpeechQuietAtStartSeconds || now - bot.LastSpoke < SpeechGapSeconds) return;
        if (_card.IsOpen && (_cardBot is null || _cardBot == bot)) return;
        var pixel = _theme == CharacterTheme.Pixel;
        string? line;
        using (pixel ? L10n.English() : null) line = BotLines.For(phase, _random);
        if (line is null) return;
        bot.LastSpoke = now;
        bot.Bubble ??= new SpeechBubble(_root);
        var swatch = Brand.Readable(SwatchOf(bot), Brand.IsDark());
        bot.Bubble.Say(line, swatch, pixel, DrawerStore.Shared.Side == DrawerSide.Right,
            bot.Bounds, WorkAreaDip(), _reducedMotion);
    }

    private void Hush()
    {
        foreach (var bot in _bots) bot.Bubble?.Hide(animated: false);
    }

    // MARK: - Game loop

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        var started = Stopwatch.GetTimestamp();
        while (_commands.TryDequeue(out var command)) command();
        var scene = _scene;
        var reduced = _reducedMotion;
        // Each character steps on its own schedule, with a few ms of vsync
        // tolerance so callbacks landing early are not dropped.
        var now = _clock.Elapsed.TotalSeconds;
        _due.Clear();
        foreach (var (index, _, _) in scene.Shown)
        {
            if (now >= _slots[index].NextStep - 0.004) _due.Add(index);
        }
        var near = !reduced && _engine is not null && UpdateGaze(scene);
        if (_due.Count > 0) Step(now, near, reduced);
        Pace(scene, near, reduced);
        _stats?.Frame(_due.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// Busy characters step on every vsync (or at the user's ceiling); calm
    /// ones whose eyes follow a nearby pointer run at 60 fps and the rest
    /// breathe at 30. Reduced motion keeps 12 / 6. Pixel bots move in whole
    /// cells, at most 10-15 frames a second, so 30 is plenty for them.
    private void Step(double now, bool near, bool reduced)
    {
        var theme = _loopTheme;
        var pixel = theme == CharacterTheme.Pixel;
        var cap = _activeCap;
        var activeFps = reduced ? 12 : pixel ? (cap == 0 ? 30 : Math.Min(cap, 30)) : cap == 0 ? double.PositiveInfinity : cap;
        var gazeFps = reduced ? 12 : pixel ? 30 : 60;
        var calmFps = reduced ? 6 : 30;
        var dts = new double[_slots.Length];
        Array.Fill(dts, double.NaN);
        foreach (var index in _due)
        {
            var slot = _slots[index];
            dts[index] = Math.Min(now - slot.LastStep, 0.1);
            slot.LastStep = now;
            var fps = !DrawerLooks.IsCalm(slot.EngineState, theme) ? activeFps : near ? gazeFps : calmFps;
            var interval = 1.0 / fps;
            slot.NextStep = now - slot.NextStep > interval ? now + interval : slot.NextStep + interval;
        }
        if (_engine is null) return;
        var frames = _engine.Tick(dts, BotSize);
        foreach (var index in _due)
        {
            if (frames[index] is not var (engineState, svg)) continue;
            var slot = _slots[index];
            // Self-completing entries (finished, spawning…) fall back to
            // idle; keep playing the agent's phase instead.
            if (engineState == "idle" && slot.Hold != "idle") _engine.SetState(index, slot.Hold, -1);
            slot.EngineState = engineState;
            slot.Svg = svg;
            Fidget(index, slot, now, reduced);
        }
    }

    /// Now and then an idle character does one of its own little things, on
    /// its own clock, and settles back.
    private void Fidget(int index, Slot slot, double now, bool reduced)
    {
        if (slot.Fidget is not null)
        {
            if (now < slot.FidgetUntil) return;
            slot.Fidget = null;
            slot.NextFidget = now + FidgetGap * (0.5 + Random.Shared.NextDouble());
            if (slot.Hold == "idle") _engine!.SetState(index, "idle", -1);
            return;
        }
        if (reduced || slot.Fidgets.Length == 0 || slot.Hold != "idle" || slot.EngineState != "idle" || now < slot.NextFidget) return;
        slot.Fidget = slot.Fidgets[Random.Shared.Next(slot.Fidgets.Length)];
        slot.FidgetUntil = now + 2.5 + Random.Shared.NextDouble() * 2;
        _engine!.SetState(index, slot.Fidget, -1);
    }

    /// Feeds the pointer to the due characters; true when it is near any.
    private bool UpdateGaze(Scene scene)
    {
        if (!Win32.GetCursorPos(out var cursor)) return false;
        var x = (cursor.X - scene.Left) / scene.Scale;
        var y = (cursor.Y - scene.Top) / scene.Scale;
        var anyNear = false;
        foreach (var (index, left, top) in scene.Shown)
        {
            var dx = x - (left + BotSize / 2);
            var dy = y - (top + BotSize / 2);
            var near = Math.Sqrt(dx * dx + dy * dy) < GazeRange;
            anyNear |= near;
            if (!_due.Contains(index)) continue;
            var slot = _slots[index];
            if (near)
            {
                _engine!.SetGaze(index, left, top, BotSize, BotSize, x, y);
                slot.Gazing = true;
            }
            else if (slot.Gazing)
            {
                _engine!.ClearGaze(index);
                slot.Gazing = false;
            }
        }
        return anyNear;
    }

    /// The loop runs on vsync only while a character is busy; otherwise at
    /// the calm rate the characters need.
    private void Pace(Scene scene, bool near, bool reduced)
    {
        var theme = _loopTheme;
        var busy = false;
        foreach (var (index, _, _) in scene.Shown)
        {
            busy |= !DrawerLooks.IsCalm(_slots[index].EngineState, theme);
        }
        var pixel = theme == CharacterTheme.Pixel;
        var active = pixel ? (_activeCap == 0 ? 30 : Math.Min(_activeCap, 30)) : _activeCap;
        var fps = busy ? (reduced ? 12 : active) : near ? (pixel ? 30 : 60) : (reduced ? 6 : 30);
        if (fps == _pace) return;
        _pace = fps;
        _queue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (fps == 0)
            {
                _canvas.IsFixedTimeStep = false;
                return;
            }
            _canvas.TargetElapsedTime = TimeSpan.FromSeconds(1.0 / fps);
            _canvas.IsFixedTimeStep = true;
        });
    }

    /// A frame's SVG is rasterised once into a small bitmap at the display's
    /// DPI; every vsync then only blits. Calm characters change a few times
    /// a second, so most frames re-tessellate nothing.
    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var started = Stopwatch.GetTimestamp();
        var ds = args.DrawingSession;
        var time = _clock.Elapsed.TotalSeconds;
        var theme = _loopTheme;
        // Pixel frames stay hard-edged: drawn without antialiasing and blitted
        // whole-pixel, unfiltered, even at scalings like 125 %.
        var pixel = theme == CharacterTheme.Pixel;
        var scale = sender.Dpi / 96f;
        foreach (var (index, x, y) in _scene.Shown)
        {
            var slot = _slots[index];
            if (slot.Svg is not { } svg)
            {
                ds.Transform = Matrix3x2.CreateTranslation(x, y);
                DrawFallback(ds, slot, time, theme);
                ds.Transform = Matrix3x2.Identity;
                continue;
            }
            if (slot.Rendered is null || slot.Rendered.Device != ds.Device || slot.Rendered.Dpi != sender.Dpi)
            {
                slot.Rendered?.Dispose();
                slot.Rendered = new CanvasRenderTarget(ds, (float)BotSize, (float)BotSize, sender.Dpi);
                slot.RenderedSvg = null;
            }
            if (svg != slot.RenderedSvg)
            {
                using (var session = slot.Rendered.CreateDrawingSession())
                {
                    session.Clear(Microsoft.UI.Colors.Transparent);
                    if (pixel) session.Antialiasing = CanvasAntialiasing.Aliased;
                    _svg.Render(svg, session);
                }
                slot.RenderedSvg = svg;
            }
            if (pixel)
            {
                var at = new Vector2(MathF.Round(x * scale) / scale, MathF.Round(y * scale) / scale);
                ds.DrawImage(slot.Rendered, at, slot.Rendered.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
            }
            else ds.DrawImage(slot.Rendered, x, y);
        }
        _stats?.Drew(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// Without the engine: a disc in the character's colour with two eyes,
    /// breathing faster while its agent is busy.
    private static void DrawFallback(CanvasDrawingSession ds, Slot slot, double time, CharacterTheme theme)
    {
        var busy = !DrawerLooks.IsCalm(slot.EngineState, theme);
        var pulse = (float)Math.Sin(time * (busy ? 6 : 1.5)) * (busy ? 1.6f : 0.6f);
        var center = new Vector2((float)BotSize / 2, (float)BotSize / 2);
        var radius = (float)BotSize / 2 - 4 + pulse;
        ds.FillCircle(center, radius, Brand.Rgb(slot.Swatch));
        ds.DrawCircle(center, radius, Brand.Rgb(0xFFFFFF, 60), 1);
        var eye = Brand.Rgb(0xFFFFFF);
        ds.FillEllipse(center + new Vector2(-6, -2), 3.2f, 4.2f, eye);
        ds.FillEllipse(center + new Vector2(6, -2), 3.2f, 4.2f, eye);
    }

    // MARK: - Hover card

    /// Hovering a character opens a card pointing at it; null stacks every
    /// character's status in one card.
    private void OpenCard(Bot? only)
    {
        _cardClose.Stop();
        _cardClose.Interval = TimeSpan.FromMilliseconds(250);
        var retarget = _card.IsOpen && _cardBot != only;
        _cardBot = only;
        if (only is null) Hush();
        else only.Bubble?.Hide(animated: false);
        ShowCard(retarget);
    }

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

    private void RefreshCardFor(DisplayProvider provider)
    {
        // The merged character's card shows Cursor's data too.
        if (_merged && provider == DisplayProvider.Cursor) provider = DisplayProvider.Grok;
        if (_cardBot is null || _cardBot.Provider == provider) RefreshCard();
    }

    private Bot BotOf(DisplayProvider provider) => _bots.First(b => b.Provider == provider);

    /// Grok's character stands for Cursor too while they share a subscription.
    private StatusCard CardFor(Bot bot) => _merged && bot.Provider == DisplayProvider.Grok
        ? StatusCards.ForGrokWithCursor(bot.Phase, bot.Detail, bot.Running)
        : StatusCards.For(bot.Provider, bot.Phase, bot.Detail, bot.Running);

    /// New Grok or Cursor usage can start or end a shared subscription, which
    /// merges or splits their characters; otherwise it only refreshes a card.
    private void OnSubscriptionUsage(DisplayProvider provider)
    {
        if (SharedSubscription.GrokWithCursor() != _merged) Layout();
        else RefreshCardFor(provider);
    }

    private void ShowCard(bool retarget = false)
    {
        if (!_shown) return;
        var dark = Brand.IsDark();
        var pixel = _theme == CharacterTheme.Pixel;
        List<(StatusCard, uint)> sections;
        using (pixel ? L10n.English() : null)
        {
            sections = _bots
                .Where(b => b.Visible && (_cardBot is null || b == _cardBot))
                .Select(b => (CardFor(b), Brand.Readable(SwatchOf(b), dark)))
                .ToList();
        }
        var height = WindowHeightDip(_bots.Count(b => b.Visible));
        var anchor = _cardBot?.Bounds ?? new Rect(InnerInset, height / 2 - BotSize / 2, BotSize, BotSize);
        _card.Show(sections, pixel, DrawerStore.Shared.Side == DrawerSide.Right, anchor,
            WorkAreaDip(), _reducedMotion, retarget);
    }

    /// Closes the card once the pointer is off every character; the region
    /// clips the window, so leaving one circle for the next exits it first.
    private void CloseCardUnlessHovered()
    {
        _cardClose.Stop();
        if (BotUnderCursor() is { } bot && _cardBot is not null)
        {
            if (bot != _cardBot) OpenCard(bot);
            return;
        }
        _card.Hide(animated: true);
        _cardBot = null;
    }

    // MARK: - Interaction

    private Bot? BotAt(Point point) => _bots.FirstOrDefault(b =>
    {
        if (!b.Visible) return false;
        var dx = point.X - (b.Bounds.X + BotSize / 2);
        var dy = point.Y - (b.Bounds.Y + BotSize / 2);
        return dx * dx + dy * dy <= BotSize * BotSize / 4;
    });

    private Bot? BotUnderCursor()
    {
        if (!Win32.GetCursorPos(out var cursor)) return null;
        var scene = _scene;
        return BotAt(new Point((cursor.X - scene.Left) / scene.Scale, (cursor.Y - scene.Top) / scene.Scale));
    }

    private void WirePointer()
    {
        _root.PointerMoved += (_, e) =>
        {
            if (_pressed is not null)
            {
                Drag();
                return;
            }
            if (BotAt(e.GetCurrentPoint(_root).Position) is { } bot && (bot != _cardBot || !_card.IsOpen)) OpenCard(bot);
        };
        _root.PointerExited += (_, _) =>
        {
            if (_pressed is null) _cardClose.Start();
        };
        _root.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(_root);
            if (!point.Properties.IsLeftButtonPressed || BotAt(point.Position) is not { } bot) return;
            _pressed = bot;
            _dragging = false;
            Win32.GetCursorPos(out var cursor);
            _pressY = cursor.Y;
            _pressTop = AppWindow.Position.Y;
            _root.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        _root.PointerReleased += (_, e) =>
        {
            if (_pressed is not { } bot) return;
            _pressed = null;
            _root.ReleasePointerCapture(e.Pointer);
            if (_dragging)
            {
                var work = _screen!.Work;
                var travel = work.Bottom - work.Top - AppWindow.Size.Height;
                DrawerStore.Shared.Offset = travel > 0 ? (double)(AppWindow.Position.Y - work.Top) / travel : 0.5;
            }
            else
            {
                var index = bot.Index;
                _commands.Enqueue(() =>
                {
                    _engine?.Poke(index);
                    _slots[index].NextStep = 0;
                });
            }
            e.Handled = true;
        };
        _root.PointerCaptureLost += (_, _) => _pressed = null;
        _root.RightTapped += (_, e) =>
        {
            if (BotAt(e.GetPosition(_root)) is not { } bot) return;
            OpenMenu(bot, keepOpen: false);
            e.Handled = true;
        };
    }

    private void OpenMenu(Bot bot, bool keepOpen)
    {
        _cardClose.Stop();
        _card.Hide();
        _cardBot = null;
        // The verification hook has no pointer on the character: open at it.
        var scene = _scene;
        PointInt32? at = keepOpen
            ? new PointInt32(scene.Left + (int)(bot.Bounds.X * scene.Scale), scene.Top + (int)(bot.Bounds.Bottom * scene.Scale))
            : null;
        DrawerMenu.Show(bot.Provider, bot.Shape, bot.Color, _shapes, _colors,
            _bots.Select(b => (b.Provider, b.Visible)).ToList(),
            (shape, color) => ChangeLook(bot, shape, color), keepOpen, at);
    }

    private void Drag()
    {
        Win32.GetCursorPos(out var cursor);
        var dy = cursor.Y - _pressY;
        if (!_dragging && Math.Abs(dy) < DragThreshold * _scene.Scale) return;
        if (!_dragging)
        {
            _dragging = true;
            _card.Hide();
            Hush();
        }
        var work = _screen!.Work;
        var height = AppWindow.Size.Height;
        var top = Math.Clamp(_pressTop + dy, work.Top, Math.Max(work.Top, work.Bottom - height));
        var left = AppWindow.Position.X;
        AppWindow.Move(new PointInt32(left, top));
        Publish(left, top, _scene.Scale);
    }

    private void ChangeLook(Bot bot, string shape, string color)
    {
        bot.Shape = shape;
        bot.Color = color;
        DrawerStore.Shared.SetLook(bot.Provider, shape, color);
        var index = bot.Index;
        var swatch = SwatchOf(bot);
        _commands.Enqueue(() =>
        {
            _engine?.SetLook(index, shape, color);
            _slots[index].Swatch = swatch;
            _slots[index].NextStep = 0;
        });
    }

    /// AGENTISLAND_DEBUG_DRAWER_STATS: game-loop rate and process CPU, once a
    /// second, for verifying the pacing.
    private sealed class DrawerStats(string path)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Process _process = Process.GetCurrentProcess();
        private TimeSpan _cpu = Process.GetCurrentProcess().TotalProcessorTime;
        private double _windowStart;
        private int _frames;
        private int _steps;
        private double _updateMs;
        private double _drawMs;

        public void Drew(double ms) => _drawMs += ms;

        public void Frame(int steps, double updateMs)
        {
            _frames++;
            _steps += steps;
            _updateMs += updateMs;
            var now = _clock.Elapsed.TotalSeconds;
            if (now - _windowStart < 1) return;
            _process.Refresh();
            var cpu = _process.TotalProcessorTime;
            var span = now - _windowStart;
            try
            {
                File.AppendAllText(path,
                    $"{now:F1}s loop={_frames / span:F0}/s steps={_steps / span:F0}/s update={_updateMs / Math.Max(1, _frames):F2}ms draw={_drawMs / Math.Max(1, _frames):F2}ms cpu={(cpu - _cpu).TotalSeconds / span * 100:F1}%core{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            _frames = _steps = 0;
            _updateMs = _drawMs = 0;
            _windowStart = now;
            _cpu = cpu;
        }
    }
}
