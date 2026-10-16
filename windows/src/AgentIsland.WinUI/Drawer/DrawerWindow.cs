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
using AgentIsland.WinUI.Settings;
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
/// theme: the bundled pixel bots, or the Classic recolourable shapes. The
/// drawer's own moments (reveal, roster changes, drag, edge switch, a new
/// look) play in the same loop.
internal sealed partial class DrawerWindow : Window
{
    private const double BotSize = 44;
    private const double Gap = 10;
    private const double EdgeInset = 6;
    private const double InnerInset = 6;
    private const double VerticalPadding = 12;
    private const double GazeRange = 480;
    private const double MinDwellSeconds = 2;
    private const double DragThreshold = 4;
    // A pointer passing over the drawer opens no card; one that rests does.
    private static readonly TimeSpan HoverIntent = TimeSpan.FromMilliseconds(200);
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
    // The Classic engine's shapes and colours for the look picker; null in
    // the pixel theme, which hides the picker.
    private IReadOnlyList<string>? _shapes;
    private IReadOnlyList<string>? _colors;
    private Func<string, string?>? _shapeFor;
    private readonly SvgCanvas _svg = new();
    private readonly List<Bot> _bots = new();
    private DrawerActivityFrame _activity = new(Array.Empty<(DisplayProvider, AgentPhase, string?, int)>());
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
    private readonly DispatcherQueueTimer _cardIntent;
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
    private Bot? _pressed;
    private bool _dragging;
    // A locked drawer turned this press's drag down: no move, and no click.
    private bool _refused;
    // After a click or drag the card stays away until the pointer leaves.
    private bool _cardHeldBack;
    private int _pressY;
    private int _pressTop;
    private bool _closed;
    // The drawer moment playing (M1, M2, M6, M18, M20): the loop draws its
    // bots in their poses instead of the scene. Set on the UI thread.
    private volatile DrawerMoment? _moment;
    private Action? _afterMoment;
    // Ends a moment the loop never saw finish (the loop normally does).
    private readonly DispatcherQueueTimer _momentGuard;
    // Classic's locked-drag tug, in DIP, while the pointer pulls.
    private volatile float _tug;
    // What the window was last laid out as, to tell what a layout changes.
    private (int[] Shown, RectInt32 Rect, DrawerSide Side, double Scale)? _laidOut;
    // A roster or edge moment has yet to put the window where it ends up.
    private bool _layoutPending;
    private CanvasDevice? _loopDevice;

    public DrawerWindow()
    {
        Title = "Agent Island";
        _hwnd = WindowNative.GetWindowHandle(this);
        _queue = DispatcherQueue;
        _reducedMotion = ReducedMotion();
        UpdateCap();

        _theme = _loopTheme = DrawerStore.Shared.Theme;
        _engine = CreateEngine(_theme);
        foreach (var provider in DisplayProviders.All)
        {
            var (shape, color) = ValidLook(provider, DrawerLooks.For(provider));
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
        _cardIntent = Timer(HoverIntent, OpenCardIfStillHovered);
        _momentGuard = Timer(TimeSpan.FromSeconds(1), () => EndMoment(_moment));
        _momentGuard.IsRepeating = false;
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DRAWER_STATS") is { Length: > 0 } statsPath)
        {
            _stats = new DrawerStats(statsPath);
        }

        Watch(ProviderVisibilityStore.Shared, Layout);
        Watch(DrawerStore.Shared, () =>
        {
            ApplyTheme();
            ApplyLock();
            UpdateCap();
            Layout();
        });
        Watch(IslandTargetDisplayStore.Shared, Layout);
        Watch(UsageStore.Shared, RefreshCard);
        Watch(KimiUsageStore.Shared, RefreshCard);
        Watch(GrokUsageStore.Shared, () => RefreshCardFor(DisplayProvider.Grok));
        Watch(CursorUsageStore.Shared, () => RefreshCardFor(DisplayProvider.Cursor));
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

    /// Windows' animation effects are off, or AGENTISLAND_DEBUG_REDUCED_MOTION=1
    /// says to act as if they were.
    private bool ReducedMotion() =>
        !_uiSettings.AnimationsEnabled || Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_REDUCED_MOTION") == "1";

    /// Hidden from the tray menu, as opposed to stepping aside for a full-screen app.
    public bool DeliberatelyHidden => _deliberatelyHidden;

    /// Tray menu: hide the drawer, or bring it back (M1).
    public void ToggleDeliberately()
    {
        _deliberatelyHidden = !_deliberatelyHidden;
        ApplyVisibility(animate: true);
    }

    /// Bring the drawer back and briefly show every character's status at
    /// once — the one place the cards appear together. A hidden drawer
    /// slides in first and the card opens as its last bot lands.
    public void Reveal()
    {
        _deliberatelyHidden = false;
        var hidden = !_shown;
        ApplyVisibility(animate: true);
        var delay = hidden && _moment is { } moment
            ? DrawerMoves.CardAfterReveal(moment.Pixel, moment.Bots.Length)
            : 0;
        After(delay, () =>
        {
            OpenCard(null);
            _cardClose.Interval = TimeSpan.FromSeconds(4);
            _cardClose.Start();
        });
    }

    /// Runs an action on the UI thread after ms, or now.
    private void After(double ms, Action action)
    {
        if (ms <= 0)
        {
            action();
            return;
        }
        var timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(ms);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_closed) action();
        };
        timer.Start();
    }

    public void Relocalize() => RefreshCard();

    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _poll.Stop();
        _guard.Stop();
        _cardClose.Stop();
        _cardIntent.Stop();
        _momentGuard.Stop();
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

    /// What the provider's character is doing: for Grok, Cursor's work too.
    public (AgentPhase Phase, string? Detail) PhaseOf(DisplayProvider provider)
    {
        var activity = _activity.ForCharacter(provider);
        return (activity.Phase, activity.Detail);
    }

    public async void DebugCard(int? index, string? snapshot)
    {
        var visible = Ordered(_bots.Where(b => b.Visible)).ToList();
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

    /// AGENTISLAND_DEBUG_SAY=Locked: the line a locked drawer answers a drag with.
    public void DebugSayLocked()
    {
        if (_bots.FirstOrDefault(b => b.Visible) is { } bot) SayLocked(bot);
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

    /// A saved look the Classic engine can draw: an old shape name maps to
    /// the nearest, and anything else falls back to the agent's default.
    private (string Shape, string Color) ValidLook(DisplayProvider provider, (string Shape, string Color) look)
    {
        if (_shapes is null || _colors is null) return look;
        var fallback = DrawerLooks.Default(provider);
        return (_shapeFor?.Invoke(look.Shape) ?? fallback.Shape,
            _colors.Contains(look.Color) ? look.Color : fallback.Color);
    }

    // MARK: - Theme

    /// The theme's engine, or null when V8 cannot run it; the drawer then
    /// draws its fallback discs.
    private ICharacterEngine? CreateEngine(CharacterTheme theme)
    {
        ICharacterEngine? engine = null;
        try
        {
            engine = theme == CharacterTheme.Pixel
                ? new PixelEngine(PixelEngine.Directory)
                : new ClassicEngine(PixelEngine.Directory);
        }
        catch (Exception error)
        {
            CrashLog.Write(error, theme == CharacterTheme.Pixel ? "Pixel engine" : "Classic engine");
        }
        var classic = engine as ClassicEngine;
        _shapes = classic?.Shapes;
        _colors = classic?.Colors;
        _shapeFor = classic is null ? null : classic.ShapeFor;
        return engine;
    }

    private static void AddBot(ICharacterEngine? engine, Bot bot)
    {
        if (engine is null) return;
        var index = engine.AddBot(bot.Provider, bot.Shape, bot.Color,
            DrawerLooks.TempoOf(bot.Provider), Random.Shared.Next(1, 1_000_000));
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
            (bot.Shape, bot.Color) = ValidLook(bot.Provider, (bot.Shape, bot.Color));
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

    /// Where the drawer goes: which bots it shows, top first, and its window.
    private sealed record Plan(List<Bot> Visible, RectInt32 Rect, DrawerSide Side, double Scale)
    {
        public int[] Shown { get; } = Visible.Select(b => b.Index).ToArray();
    }

    private Plan PlanLayout()
    {
        var visibility = ProviderVisibilityStore.Shared;
        var store = DrawerStore.Shared;
        var shown = DrawerVisibility.Characters(_bots.ToDictionary(b => b.Provider, b => visibility.IsShown(b.Provider)));
        var visible = Ordered(_bots.Where(b => shown[b.Provider])).ToList();
        var screen = _screen = ResolveScreen();
        var scale = screen.Scale;
        var width = (int)Math.Ceiling((InnerInset + BotSize + EdgeInset) * scale);
        var height = (int)Math.Ceiling(WindowHeightDip(visible.Count) * scale);
        var work = screen.Work;
        var x = store.Side == DrawerSide.Right ? work.Right - width : work.Left;
        var y = DrawerPlacement.Top(work.Top, work.Bottom, height, scale, store.Offset, store.Locked ? store.PinnedTop : null);
        return new Plan(visible, new RectInt32(x, y, width, height), store.Side, scale);
    }

    /// Lays the drawer out. A bot shown or hidden (M2) and a new edge (M18)
    /// play as moments while the drawer is on screen; anything else moves at once.
    private void Layout()
    {
        var plan = PlanLayout();
        var actual = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        if (_laidOut is { } last && last.Shown.SequenceEqual(plan.Shown) && last.Rect.Equals(plan.Rect)
            && last.Side == plan.Side && last.Scale == plan.Scale && (_moment is not null || actual.Equals(plan.Rect)))
        {
            RefreshCard();
            return;
        }
        var animate = _laidOut is { } before && _shown && !_reducedMotion && _moment is not { Holds: true }
            && before.Scale == plan.Scale;
        if (animate && _laidOut!.Value.Side != plan.Side)
        {
            SwitchEdge(plan);
            return;
        }
        if (animate && !_laidOut!.Value.Shown.Order().SequenceEqual(plan.Shown.Order()))
        {
            ChangeRoster(plan);
            return;
        }
        // A move alone keeps a moment playing: its bots are placed in the
        // window (a drop lands while the new position is saved).
        var moveOnly = _laidOut is { } previous && previous.Shown.SequenceEqual(plan.Shown) && previous.Side == plan.Side
            && previous.Scale == plan.Scale;
        if (_moment is not null && (!moveOnly || _layoutPending)) ClearMoment();
        Apply(plan);
    }

    /// Puts the plan on screen at once.
    private void Apply(Plan plan)
    {
        _layoutPending = false;
        foreach (var bot in _bots) bot.Visible = plan.Visible.Contains(bot);
        Place(plan, plan.Rect);
        AppWindow.MoveAndResize(plan.Rect);
        _laidOut = (plan.Shown, plan.Rect, plan.Side, plan.Scale);
        var before = _scene;
        Publish(plan.Rect.X, plan.Rect.Y, plan.Scale);
        if (_moment is null) ApplyRegion();
        // A bubble beside a character that moved would point at nothing.
        if (before.Left != plan.Rect.X || before.Top != plan.Rect.Y || !before.Shown.SequenceEqual(_scene.Shown)) Hush();
        RefreshCard();
    }

    /// The plan's bots in their slots, in the DIPs of a window at `window`.
    private void Place(Plan plan, RectInt32 window)
    {
        var left = plan.Side == DrawerSide.Right ? InnerInset : EdgeInset;
        var top = (plan.Rect.Y - window.Y) / plan.Scale;
        for (var i = 0; i < plan.Visible.Count; i++)
        {
            plan.Visible[i].Bounds = new Rect(left, top + VerticalPadding + i * (BotSize + Gap), BotSize, BotSize);
        }
    }

    /// Only the characters take clicks: the window region is their circles.
    private void ApplyRegion()
    {
        var scale = _scene.Scale;
        var region = Win32.CreateRectRgn(0, 0, 0, 0);
        foreach (var bot in _bots.Where(b => b.Visible))
        {
            var b = bot.Bounds;
            var circle = Win32.CreateEllipticRgn(
                (int)Math.Floor(b.Left * scale), (int)Math.Floor(b.Top * scale),
                (int)Math.Ceiling(b.Right * scale) + 1, (int)Math.Ceiling(b.Bottom * scale) + 1);
            Win32.CombineRgn(region, region, circle, 2);
            Win32.DeleteObject(circle);
        }
        Win32.SetWindowRgn(_hwnd, region, true);
    }

    /// M2: bots leave, the rest close the gap or make room, and newcomers
    /// slide in. The window covers the old and the new column meanwhile, so
    /// every bot starts where it was on screen; it settles on the new one after.
    private void ChangeRoster(Plan plan)
    {
        var old = _scene;
        var scale = plan.Scale;
        var from = OnScreenNow();
        // The window as it stands, which may already be mid-change.
        var window = Union(new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height), plan.Rect);
        // Where each bot is drawn now, in screen DIPs.
        var drawnTop = old.Shown.ToDictionary(s => s.Index, s => old.Top / scale + s.Y + (from?.Invoke(s.Index)?.Y ?? 0));
        var drawnLeft = old.Shown.ToDictionary(s => s.Index, s => s.X);
        Hush();
        foreach (var bot in _bots) bot.Visible = plan.Visible.Contains(bot);
        Place(plan, window);
        AppWindow.MoveAndResize(window);
        // Where the moment ends up, so a layout meanwhile sees no change.
        _laidOut = (plan.Shown, plan.Rect, plan.Side, scale);
        Publish(window.X, window.Y, scale);
        _layoutPending = true;
        var leaving = old.Shown.Select(s => s.Index).Where(i => !plan.Shown.Contains(i)).ToList();
        var entering = plan.Visible.Where(b => !drawnTop.ContainsKey(b.Index)).Select(b => b.Index).ToList();
        var dy = plan.Visible.Where(b => drawnTop.ContainsKey(b.Index))
            .ToDictionary(b => b.Index, b => (float)(drawnTop[b.Index] - (window.Y / scale + b.Bounds.Y)));
        var bots = _scene.Shown
            .Concat(leaving.Select(i => (i, drawnLeft[i], (float)(drawnTop[i] - window.Y / scale))))
            .ToArray();
        Play(DrawerMoves.Roster(NewMoment(bots, plan.Side), leaving, dy, entering, from), () => Apply(PlanLayout()));
        RefreshCard();
    }

    private static RectInt32 Union(RectInt32 a, RectInt32 b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        return new RectInt32(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
    }

    /// M18: the bots leave by the old edge, the window crosses the screen,
    /// and they slide in from the new one.
    private void SwitchEdge(Plan plan)
    {
        _card.Hide();
        _cardBot = null;
        Hush();
        var shown = _scene.Shown;
        var side = _laidOut!.Value.Side;
        _laidOut = (plan.Shown, plan.Rect, plan.Side, plan.Scale);
        _layoutPending = true;
        var hide = NewMoment(shown, side, holds: true);
        Play(DrawerMoves.Hide(hide, TopDown(shown), staggered: false, OnScreenNow()), () =>
        {
            Apply(PlanLayout());
            var now = _scene.Shown;
            var reveal = NewMoment(now, DrawerStore.Shared.Side);
            Play(DrawerMoves.Reveal(reveal, TopDown(now), staggered: !reveal.Pixel));
        });
    }

    /// A roster or edge moment cut short by another trigger still leaves the
    /// drawer laid out where it was going, before the new moment starts.
    private void Settle()
    {
        if (!_layoutPending) return;
        ClearMoment();
        Apply(PlanLayout());
    }

    /// Bots in the order the user set, top first.
    private static IEnumerable<Bot> Ordered(IEnumerable<Bot> bots)
    {
        var order = DrawerStore.Shared.Order.ToList();
        return bots.OrderBy(bot => order.IndexOf(bot.Provider));
    }

    /// Locking pins the drawer's top where it stands; unlocking turns that
    /// back into an offset, so neither moves it.
    private void ApplyLock()
    {
        var store = DrawerStore.Shared;
        if (_screen is not { } screen) return;
        var top = AppWindow.Position.Y;
        if (store.Locked && store.PinnedTop is null)
        {
            store.PinnedTop = DrawerPlacement.PinFor(top, screen.Work.Top, screen.Scale);
        }
        else if (!store.Locked && store.PinnedTop is not null)
        {
            store.Offset = DrawerPlacement.OffsetFor(top, screen.Work.Top, screen.Work.Bottom, AppWindow.Size.Height);
            store.PinnedTop = null;
        }
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
        _reducedMotion = ReducedMotion();
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

    /// Shows or hides the drawer: a deliberate change from the tray slides
    /// the bots in or out (M1); stepping aside for a full-screen app does not.
    private void ApplyVisibility(bool animate = false)
    {
        var show = !_deliberatelyHidden && !_coveredByFullscreen;
        if (show == _shown) return;
        Settle();
        _shown = show;
        var moving = animate && !_reducedMotion;
        if (show)
        {
            var shown = _scene.Shown;
            if (moving) Play(DrawerMoves.Reveal(NewMoment(shown, DrawerStore.Shared.Side), TopDown(shown), from: OnScreenNow()));
            else if (_moment is not null) ClearMoment();
            AppWindow.Show(false);
            KeepOnTop();
            _canvas.Paused = false;
        }
        else
        {
            _card.Hide();
            Hush();
            var shown = _scene.Shown;
            if (moving) Play(DrawerMoves.Hide(NewMoment(shown, DrawerStore.Shared.Side, holds: true), TopDown(shown), from: OnScreenNow()), HideWindow);
            else HideWindow();
        }
    }

    private void HideWindow()
    {
        _canvas.Paused = true;
        AppWindow.Hide();
    }

    // MARK: - Moments

    private DrawerMoment NewMoment((int Index, float X, float Y)[] bots, DrawerSide side, bool holds = false, bool tug = false) => new()
    {
        Bots = bots,
        Start = _clock.Elapsed.TotalSeconds,
        Pixel = _theme == CharacterTheme.Pixel,
        Dark = Brand.IsDark(),
        Edge = side == DrawerSide.Right ? 1 : -1,
        Holds = holds,
        Tug = tug,
    };

    private static List<int> TopDown((int Index, float X, float Y)[] bots) =>
        bots.OrderBy(b => b.Y).Select(b => b.Index).ToList();

    /// The pose each bot is drawn in now, for a moment that takes over.
    private DrawerMoves.OnScreen? OnScreenNow() =>
        _moment is { } moment ? moment.OnScreenAt((_clock.Elapsed.TotalSeconds - moment.Start) * 1000) : null;

    /// Plays a moment, replacing any in flight (it starts from the frame on
    /// screen; nothing queues). Bots move past their circles meanwhile, so
    /// the window region is the whole window until the moment ends; then the
    /// circles return and `after` runs, on the UI thread.
    private void Play(DrawerMoment moment, Action? after = null)
    {
        if (_moment is { } old) _commands.Enqueue(old.RunRest);
        _afterMoment = after;
        _moment = moment;
        Win32.SetWindowRgn(_hwnd, IntPtr.Zero, true);
        _momentGuard.Stop();
        _momentGuard.Interval = TimeSpan.FromMilliseconds(moment.DurationMs + 500);
        _momentGuard.Start();
        DebugTrace.Write($"drawer moment {moment.DurationMs:F0} ms, {moment.Bots.Length} bots{(moment.Holds ? ", held" : "")}");
    }

    /// The loop drew the moment's last frame (or the guard gave up waiting).
    private void EndMoment(DrawerMoment? moment)
    {
        if (moment is null || _moment != moment) return;
        _momentGuard.Stop();
        DebugTrace.Write($"drawer moment ended {(_clock.Elapsed.TotalSeconds - moment.Start) * 1000:F0} ms in, planned {moment.DurationMs:F0}");
        var after = _afterMoment;
        _afterMoment = null;
        after?.Invoke();
        if (_moment != moment || moment.Holds) return;
        _moment = null;
        ApplyRegion();
    }

    /// Jumps a moment to its end, as reduced motion does.
    private void ClearMoment()
    {
        _momentGuard.Stop();
        if (_moment is { } old) _commands.Enqueue(old.RunRest);
        _moment = null;
        _afterMoment = null;
        ApplyRegion();
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
            // Settings can show any provider, including one whose character is hidden.
            var jobs = _bots
                .Select(b =>
                {
                    var thread = monitor.ThreadFor(b.Tool);
                    return (Bot: b, Tool: b.Tool, State: monitor.StateFor(b.Tool), Path: thread?.TranscriptPath,
                        Running: monitor.RunningFor(b.Tool), Prompt: thread?.PendingPrompt);
                })
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
            _activity = new DrawerActivityFrame(results.Select(r => (r.Bot.Provider, r.Phase, r.Detail, r.Running)));
            foreach (var bot in _bots)
            {
                var activity = _activity.ForCharacter(bot.Provider);
                recounted |= bot.Running != activity.Running && (_cardBot is null || _cardBot == bot);
                bot.Running = activity.Running;
                Offer(bot, activity.Phase, activity.Detail, seconds);
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
        Say(bot, line, SpeechBubble.KindOf(phase));
    }

    private void Say(Bot bot, string line, LineKind kind)
    {
        bot.Bubble ??= new SpeechBubble(_root);
        var swatch = Brand.Readable(SwatchOf(bot), Brand.IsDark());
        bot.Bubble.Say(line, swatch, _theme == CharacterTheme.Pixel, DrawerStore.Shared.Side == DrawerSide.Right,
            bot.Bounds, WorkAreaDip(), _reducedMotion, kind);
    }

    /// A locked drawer turns a drag down out loud, with the way out; it is an
    /// answer to the drag, so it speaks even with character lines off.
    private void SayLocked(Bot bot)
    {
        _card.Hide();
        _cardBot = null;
        string line;
        using (_theme == CharacterTheme.Pixel ? L10n.English() : null) line = L10n.Tr("Locked in place.\nRight-click to unlock.");
        Say(bot, line, LineKind.Plain);
    }

    private void Hush()
    {
        foreach (var bot in _bots) bot.Bubble?.Hide(animated: false);
    }

    // MARK: - Game loop

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        var started = Stopwatch.GetTimestamp();
        _loopDevice = sender.Device;
        while (_commands.TryDequeue(out var command)) command();
        var scene = _scene;
        var reduced = _reducedMotion;
        // Each character steps on its own schedule, with a few ms of vsync
        // tolerance so callbacks landing early are not dropped.
        var now = _clock.Elapsed.TotalSeconds;
        var moment = _moment;
        var momentMs = moment is null ? 0 : (now - moment.Start) * 1000;
        moment?.RunDue(momentMs);
        _due.Clear();
        foreach (var (index, _, _) in scene.Shown)
        {
            if (now >= _slots[index].NextStep - 0.004) _due.Add(index);
        }
        var near = !reduced && _engine is not null && UpdateGaze(scene);
        if (_due.Count > 0) Step(now, near, reduced);
        Pace(scene, near, reduced, moment is not null && moment.Moving(momentMs));
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

    /// The loop runs on vsync only while a character is busy or a drawer
    /// moment plays (Pixel's 30-tick clock for pixel moments); otherwise at
    /// the calm rate the characters need.
    private void Pace(Scene scene, bool near, bool reduced, bool moving)
    {
        var theme = _loopTheme;
        var busy = false;
        foreach (var (index, _, _) in scene.Shown)
        {
            busy |= !DrawerLooks.IsCalm(_slots[index].EngineState, theme);
        }
        var pixel = theme == CharacterTheme.Pixel;
        var active = pixel ? (_activeCap == 0 ? 30 : Math.Min(_activeCap, 30)) : _activeCap;
        var fps = moving ? active : busy ? (reduced ? 12 : active) : near ? (pixel ? 30 : 60) : (reduced ? 6 : 30);
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
        // A drawer moment draws its own bots, each in its pose.
        var moment = _moment;
        var ms = moment is null ? 0 : (time - moment.Start) * 1000;
        var tug = moment is { Tug: true } ? _tug : 0;
        foreach (var (index, x, y) in moment?.Bots ?? _scene.Shown)
        {
            var slot = _slots[index];
            var pose = moment?.Pose(index, ms) ?? BotPose.Rest;
            if (tug != 0) pose = pose with { Y = pose.Y + tug };
            var edge = moment?.Edge ?? 1;
            if (slot.Svg is not { } svg)
            {
                ds.Transform = BotPainter.Transform(x, y, pose, pixel, edge, scale);
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
            BotPainter.Draw(ds, slot.Rendered, svg, _svg, x, y, pose, pixel, moment?.Dark ?? false, edge, scale, circle: moment is not null);
        }
        // The last frame is on screen: the UI thread can move on (hide the
        // window, cross the screen, give the circles back).
        if (moment is { Ended: false } && ms >= moment.DurationMs)
        {
            moment.Ended = true;
            _queue.TryEnqueue(() => EndMoment(moment));
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

    /// The card of the character the provider works under (Grok's for Cursor).
    private void RefreshCardFor(DisplayProvider provider)
    {
        if (_cardBot is null || _cardBot.Provider == provider.Character()) RefreshCard();
    }

    private Bot BotOf(DisplayProvider provider) => _bots.First(b => b.Provider == provider);

    private static StatusCard CardFor(Bot bot) => StatusCards.For(bot.Provider, bot.Phase, bot.Detail, bot.Running);

    private void ShowCard(bool retarget = false)
    {
        if (!_shown) return;
        var dark = Brand.IsDark();
        var pixel = _theme == CharacterTheme.Pixel;
        List<(StatusCard, uint)> sections;
        using (pixel ? L10n.English() : null)
        {
            sections = Ordered(_bots.Where(b => b.Visible && (_cardBot is null || b == _cardBot)))
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

    private void OpenCardIfStillHovered()
    {
        _cardIntent.Stop();
        if (_pressed is null && !_cardHeldBack && BotUnderCursor() is { } bot) OpenCard(bot);
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
            if (_cardHeldBack) return;
            if (BotAt(e.GetCurrentPoint(_root).Position) is not { } bot)
            {
                _cardIntent.Stop();
                return;
            }
            // An open card follows the pointer from character to character
            // at once; a closed one waits for the pointer to rest.
            if (_card.IsOpen)
            {
                if (bot != _cardBot) OpenCard(bot);
            }
            else if (!_cardIntent.IsRunning) _cardIntent.Start();
        };
        // The region clips the window to the circles, so these come per circle.
        _root.PointerEntered += (_, _) => _cardHeldBack = false;
        _root.PointerExited += (_, _) =>
        {
            _cardIntent.Stop();
            _cardHeldBack = false;
            if (_pressed is null) _cardClose.Start();
        };
        _root.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(_root);
            if (!point.Properties.IsLeftButtonPressed || BotAt(point.Position) is not { } bot) return;
            _cardIntent.Stop();
            _pressed = bot;
            _dragging = _refused = false;
            _cardHeldBack = true;
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
            if (_dragging || _refused) EndPress();
            else
            {
                var index = bot.Index;
                _commands.Enqueue(() =>
                {
                    _engine?.Poke(index);
                    _slots[index].NextStep = 0;
                });
                // A click opens the character's agent in Settings; the shared
                // character opens Grok's. Pixel's card goes in one frame: the
                // click is deliberate, and Settings is the answer (M19).
                _cardClose.Stop();
                _card.Hide(animated: _theme != CharacterTheme.Pixel);
                _cardBot = null;
                var provider = bot.Provider;
                _queue.TryEnqueue(() => SettingsWindow.Open(provider));
            }
            e.Handled = true;
        };
        _root.PointerCaptureLost += (_, _) =>
        {
            if (_pressed is null) return;
            _pressed = null;
            EndPress();
        };
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
        _cardIntent.Stop();
        _card.Hide();
        _cardBot = null;
        // The verification hook has no pointer on the character: open at it.
        var scene = _scene;
        PointInt32? at = keepOpen
            ? new PointInt32(scene.Left + (int)(bot.Bounds.X * scene.Scale), scene.Top + (int)(bot.Bounds.Bottom * scene.Scale))
            : null;
        _ = DrawerMenu.ShowAsync(bot.Provider, bot.Shape, bot.Color, _shapes, _colors,
            _bots.Select(b => (b.Provider, ProviderVisibilityStore.Shared.IsShown(b.Provider))).ToList(),
            (shape, color) => ChangeLook(bot, shape, color), keepOpen, at, scene.Scale);
    }

    private void Drag()
    {
        Win32.GetCursorPos(out var cursor);
        var dy = cursor.Y - _pressY;
        if (_refused)
        {
            // Classic's rubber band follows the pointer while it pulls.
            if (_moment is { Tug: true }) _tug = DrawerMoves.Rubber(dy / _scene.Scale, DragThreshold);
            return;
        }
        if (!_dragging && Math.Abs(dy) < DragThreshold * _scene.Scale) return;
        if (DrawerStore.Shared.Locked)
        {
            _refused = true;
            if (_pressed is { } bot) SayLocked(bot);
            Refuse(dy / _scene.Scale);
            return;
        }
        if (!_dragging)
        {
            _dragging = true;
            _card.Hide();
            Hush();
            // M6: the column lifts off the edge while it is dragged.
            Settle();
            if (_shown && !_reducedMotion)
            {
                var shown = _scene.Shown;
                Play(DrawerMoves.Lift(NewMoment(shown, DrawerStore.Shared.Side, holds: true), TopDown(shown), OnScreenNow()));
            }
        }
        var work = _screen!.Work;
        var height = AppWindow.Size.Height;
        var top = Math.Clamp(_pressTop + dy, work.Top, Math.Max(work.Top, work.Bottom - height));
        var left = AppWindow.Position.X;
        AppWindow.Move(new PointInt32(left, top));
        Publish(left, top, _scene.Scale);
    }

    /// M6, refused: Pixel shakes the column; Classic tugs it a little after
    /// the pointer and springs it home on release.
    private void Refuse(double pullDip)
    {
        if (!_shown || _reducedMotion) return;
        Settle();
        var shown = _scene.Shown;
        var side = DrawerStore.Shared.Side;
        if (_theme == CharacterTheme.Pixel)
        {
            Play(DrawerMoves.Shake(NewMoment(shown, side), TopDown(shown)));
            return;
        }
        _tug = DrawerMoves.Rubber(pullDip, DragThreshold);
        Play(NewMoment(shown, side, holds: true, tug: true));
    }

    /// The press is over: a dragged drawer is dropped where it is (and lands,
    /// M6); a tugged one springs home.
    private void EndPress()
    {
        var shown = _scene.Shown;
        var side = DrawerStore.Shared.Side;
        if (_dragging)
        {
            var work = _screen!.Work;
            var travel = work.Bottom - work.Top - AppWindow.Size.Height;
            DrawerStore.Shared.Offset = travel > 0 ? (double)(AppWindow.Position.Y - work.Top) / travel : 0.5;
        }
        if (_moment is null || (!_dragging && !_refused)) return;
        if (_reducedMotion) ClearMoment();
        else if (_dragging) Play(DrawerMoves.Drop(NewMoment(shown, side), TopDown(shown), OnScreenNow()));
        else if (_moment is { Tug: true })
        {
            Play(DrawerMoves.Spring(NewMoment(shown, side), TopDown(shown), _tug));
            _tug = 0;
        }
    }

    private void ChangeLook(Bot bot, string shape, string color)
    {
        bot.Shape = shape;
        bot.Color = color;
        DrawerStore.Shared.SetLook(bot.Provider, shape, color);
        var index = bot.Index;
        var swatch = SwatchOf(bot);
        void Swap()
        {
            _engine?.SetLook(index, shape, color);
            _slots[index].Swatch = swatch;
            _slots[index].NextStep = 0;
        }
        // M20: the bot turns like a coin and its new look swaps in edge-on.
        if (_shown && bot.Visible && !_reducedMotion && _theme == CharacterTheme.Classic && _moment is null)
        {
            Play(DrawerMoves.Flip(NewMoment(_scene.Shown, DrawerStore.Shared.Side), index, Swap));
        }
        else _commands.Enqueue(Swap);
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
