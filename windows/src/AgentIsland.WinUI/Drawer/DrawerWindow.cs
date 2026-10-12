using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using AgentIsland.Core;
using AgentIsland.Cost;
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
/// busy; only the characters' circles take clicks.
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
    }

    /// What the game loop draws: the shown slots at their DIP offsets, and
    /// the window's physical origin and scale for the gaze.
    private sealed record Scene((int Index, float X, float Y)[] Shown, int Left, int Top, double Scale);

    private readonly IntPtr _hwnd;
    private readonly GrokEngine? _engine;
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

        try
        {
            if (GrokEngineLocator.Find() is { } directory) _engine = new GrokEngine(directory);
        }
        catch (Exception error)
        {
            // Native V8 failed to load or the engine copy is unusable: the
            // fallback characters keep the drawer working.
            CrashLog.Write(error, "Grok engine");
            _engine = null;
        }

        foreach (var provider in DisplayProviders.All)
        {
            var (shape, color) = ValidLook(DrawerLooks.For(provider));
            _bots.Add(new Bot
            {
                Provider = provider,
                Tool = provider.ToTriggerTool(),
                Index = _engine?.AddBot(shape, color, "idle") ?? _bots.Count,
                Shape = shape,
                Color = color,
            });
        }
        _slots = _bots.Select(b => new Slot { Swatch = DrawerLooks.Swatch(b.Color) }).ToArray();

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

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
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
            UpdateCap();
            Layout();
        });
        Watch(IslandTargetDisplayStore.Shared, Layout);
        Watch(UsageStore.Shared, RefreshCard);
        Watch(KimiUsageStore.Shared, RefreshCard);
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
        _card.Hide();
        Hush();
        foreach (var teardown in _teardown) teardown();
        _canvas.Paused = true;
        _canvas.RemoveFromVisualTree();
        Close();
    }

    /// The engine's shapes and colours; null without the engine.
    public IReadOnlyList<string>? Shapes => _engine?.Shapes;

    public IReadOnlyList<string>? Colors => _engine?.Colors;

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
        await Snapshot.SaveAsync(_card.Visual, Path.Combine(snapshot, $"card-{index?.ToString() ?? "all"}.png"));
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
        if (_engine is null) return look;
        return (_engine.Shapes.Contains(look.Shape) ? look.Shape : "blob",
            _engine.Colors.Contains(look.Color) ? look.Color : "blue");
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
        if (!_shown || --_topmostCountdown > 0) return;
        _topmostCountdown = TopmostEverySeconds;
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
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
            var jobs = _bots
                .Where(b => b.Visible)
                .Select(b => (Bot: b, State: monitor.StateFor(b.Tool), Path: monitor.ThreadFor(b.Tool)?.TranscriptPath, Running: monitor.RunningFor(b.Tool)))
                .ToList();
            // Tail reads stay off the UI thread; only working (or seemingly
            // stalled, maybe waiting on a prompt) sessions are read.
            var readings = await Task.Run(() => jobs
                .Select(job => job.State is ActivityState.Working or ActivityState.Stalled && job.Path is { } path
                    ? AgentPhaseClassifier.Classify(job.Bot.Tool, path, now)
                    : null)
                .ToList());
            if (_closed) return;
            var seconds = _clock.Elapsed.TotalSeconds;
            var recounted = false;
            for (var i = 0; i < jobs.Count; i++)
            {
                var (bot, state, _, running) = jobs[i];
                recounted |= bot.Running != running;
                bot.Running = running;
                var reading = readings[i];
                var phase = AgentPhases.Combine(state, reading, QuotaExhausted(bot.Provider));
                Offer(bot, phase, reading is { } r && r.Phase == phase ? r.Detail : null, seconds);
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
        return (!usage.FiveHour.HasError && usage.FiveHour.UsedPercent >= 1)
            || (!usage.Weekly.HasError && usage.Weekly.UsedPercent >= 1);
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
                if (_card.IsOpen) RefreshCard();
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
        var entry = DrawerLooks.Entry(phase);
        var hold = phase == AgentPhase.Idle ? "idle" : DrawerLooks.Hold(phase);
        _commands.Enqueue(() =>
        {
            var slot = _slots[index];
            slot.Hold = hold;
            if (_engine is not null) _engine.SetState(index, entry);
            else slot.EngineState = hold;
            slot.NextStep = 0;
        });
        if (_card.IsOpen) RefreshCard();
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
        if (BotLines.For(phase, _random) is not { } line) return;
        bot.LastSpoke = now;
        bot.Bubble ??= new SpeechBubble(_root);
        bot.Bubble.Say(line, DrawerStore.Shared.Side == DrawerSide.Right, bot.Bounds, WorkAreaDip(), _reducedMotion);
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
    /// breathe at 30. Reduced motion keeps 12 / 6.
    private void Step(double now, bool near, bool reduced)
    {
        var cap = _activeCap;
        var activeFps = reduced ? 12 : cap == 0 ? double.PositiveInfinity : cap;
        var gazeFps = reduced ? 12 : 60;
        var calmFps = reduced ? 6 : 30;
        var dts = new double[_slots.Length];
        Array.Fill(dts, double.NaN);
        foreach (var index in _due)
        {
            var slot = _slots[index];
            dts[index] = Math.Min(now - slot.LastStep, 0.1);
            slot.LastStep = now;
            var fps = !DrawerLooks.CalmStates.Contains(slot.EngineState) ? activeFps : near ? gazeFps : calmFps;
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
            if (engineState == "idle" && slot.Hold != "idle") _engine.SetState(index, slot.Hold);
            slot.EngineState = engineState;
            slot.Svg = svg;
        }
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
        var busy = false;
        foreach (var (index, _, _) in scene.Shown)
        {
            busy |= !DrawerLooks.CalmStates.Contains(_slots[index].EngineState);
        }
        var fps = busy ? (reduced ? 12 : _activeCap) : near ? 60 : (reduced ? 6 : 30);
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
        foreach (var (index, x, y) in _scene.Shown)
        {
            var slot = _slots[index];
            if (slot.Svg is not { } svg)
            {
                ds.Transform = Matrix3x2.CreateTranslation(x, y);
                DrawFallback(ds, slot, time);
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
                    _svg.Render(svg, session);
                }
                slot.RenderedSvg = svg;
            }
            ds.DrawImage(slot.Rendered, x, y);
        }
        _stats?.Drew(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// Without the engine: a disc in the character's colour with two eyes,
    /// breathing faster while its agent is busy.
    private static void DrawFallback(CanvasDrawingSession ds, Slot slot, double time)
    {
        var busy = !DrawerLooks.CalmStates.Contains(slot.EngineState);
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
        if (_card.IsOpen) ShowCard();
    }

    private void ShowCard(bool retarget = false)
    {
        if (!_shown) return;
        var dark = Brand.IsDark();
        var sections = _bots
            .Where(b => b.Visible && (_cardBot is null || b == _cardBot))
            .Select(b => (StatusCards.For(b.Provider, b.Phase, b.Detail, b.Running), Brand.Readable(DrawerLooks.Swatch(b.Color), dark)))
            .ToList();
        var height = WindowHeightDip(_bots.Count(b => b.Visible));
        var anchor = _cardBot?.Bounds ?? new Rect(InnerInset, height / 2 - BotSize / 2, BotSize, BotSize);
        _card.Show(sections, DrawerStore.Shared.Side == DrawerSide.Right, anchor, WorkAreaDip(), _reducedMotion, retarget);
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

    private Bot? BotAt(Point point) =>
        _bots.FirstOrDefault(b => b.Visible && b.Bounds.Contains(point));

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
            else if (_engine is not null)
            {
                var index = bot.Index;
                _commands.Enqueue(() =>
                {
                    _engine.Poke(index, "nod");
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
        DrawerMenu.Show(bot.Provider, bot.Shape, bot.Color, _engine?.Shapes, _engine?.Colors,
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
        var swatch = DrawerLooks.Swatch(color);
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
