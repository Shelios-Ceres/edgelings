using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Model;
using AgentIsland.Usage;
using AgentIsland.WinUI.Platform;
using AgentIsland.WinUI.Shell;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        // The look its engine bot wears now (a LookCatalog id); null without an engine.
        public string? Look { get; set; }
        public Rect Bounds { get; set; }
        public bool Visible { get; set; }
        public AgentPhase Phase { get; set; }
        public string? Detail { get; set; }
        public int Running { get; set; }
        public double PhaseSince { get; set; } = double.NegativeInfinity;
        // Why an idle character rests: its agent's app is closed or out of
        // sight. Null while any of its apps is on screen, or it has work.
        public AgentPresence? Rest { get; set; }
        public SpeechBubble? Bubble { get; set; }
        public double LastSpoke { get; set; } = double.NegativeInfinity;
        // What its agent's sessions have to say, one bubble at a time.
        public SpeechQueue Speech { get; } = new();
        // The bubble on screen is a whole-agent line (quota, sign-in, network), not a session's.
        public bool PhaseLine { get; set; }
        // Hops the character as a session's bubble lands; held in a field so it is not collected.
        public DispatcherQueueTimer? Hop { get; set; }
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
        // Resting, though a set without its own resting state shows idle: no fidgets.
        public bool Resting;
        // When the character took its current state.
        public double Since;
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
    private readonly SvgCanvas _svg = new();
    private readonly List<Bot> _bots = new();
    private DrawerActivityFrame _activity = new(Array.Empty<(DisplayProvider, AgentPhase, string?, int)>());
    // Which agents' apps are on screen, looked up about once a second.
    private IReadOnlyDictionary<TriggerTool, AgentPresence> _presence = new Dictionary<TriggerTool, AgentPresence>();
    private double _presenceAt = double.NegativeInfinity;
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

    private readonly Subscriptions _subscriptions = new();


    private readonly DrawerStats? _stats;
    private volatile Scene _scene = new(Array.Empty<(int, float, float)>(), 0, 0, 1);
    private volatile bool _reducedMotion;
    private volatile int _activeCap;
    private int _pace = -1;
    private Win32.Screen? _screen;
    private bool _shown;
    private bool _away;
    private readonly DisplayPower _displays = new();
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
    // The drawer moment playing (M1, M2, M6, M18): the loop draws its
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
        _reducedMotion = Motion.ReducedMotion;
        UpdateCap();

        _theme = _loopTheme = DrawerStore.Shared.Theme;
        _engine = CreateEngine(_theme);
        foreach (var provider in DisplayProviders.All)
        {
            var bot = new Bot
            {
                Provider = provider,
                Tool = provider.ToTriggerTool(),
                Index = _bots.Count,
            };
            AddBot(_engine, bot);
            _bots.Add(bot);
        }
        _slots = _bots.Select(b => new Slot
        {
            Swatch = SwatchOf(b),
            Fidgets = _engine?.Fidgets(b.Provider, b.Look) ?? Array.Empty<string>(),
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
            ApplyLooks();
            ApplyLock();
            UpdateCap();
            Layout();
        });
        Watch(IslandTargetDisplayStore.Shared, Layout);
        ActivityMonitor.Shared.SessionEventsRaised += OnSessionEvents;
        _subscriptions.Add(() => ActivityMonitor.Shared.SessionEventsRaised -= OnSessionEvents);
        // An open card lists what waits; its bot's phase and count can stay put while that list changes.
        ActivityMonitor.Shared.WaitingChanged += RefreshCard;
        _subscriptions.Add(() => ActivityMonitor.Shared.WaitingChanged -= RefreshCard);
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
        _subscriptions.Add(() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= onDisplay);
        _subscriptions.Add(_displays.Dispose);

        Layout();
        _poll.Start();
        _guard.Start();
        Poll();
        ApplyVisibility();
    }

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
        _subscriptions.Dispose();
        _canvas.Paused = true;
        _canvas.RemoveFromVisualTree();
        Close();
    }

    private uint SwatchOf(Bot bot) => DrawerLooks.Swatch(bot.Provider, _theme);

    /// What the provider's character is doing: for Grok, Cursor's work too.
    public (AgentPhase Phase, string? Detail) PhaseOf(DisplayProvider provider)
    {
        var activity = _activity.ForCharacter(provider);
        return (activity.Phase, activity.Detail);
    }

    /// Why the provider's character rests, or null while it doesn't.
    public AgentPresence? RestOf(DisplayProvider provider) => _bots.FirstOrDefault(b => b.Provider == provider.Character())?.Rest;

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

    private void Watch(INotifyPropertyChanged source, Action handler) =>
        _subscriptions.Watch(source, (_, _) => _queue.TryEnqueue(() =>
        {
            if (!_closed) handler();
        }));
}
