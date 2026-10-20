using AgentIsland.Model;
using AgentIsland.WinUI.Platform;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Windowing;

namespace AgentIsland.WinUI.Drawer;

// Showing and hiding the drawer, the full-screen guard, and the drawer moments (reveal, roster, drag, edge).
internal sealed partial class DrawerWindow
{
    /// Fullscreen apps hide the drawer (and pause it); otherwise it re-asserts
    /// topmost now and then, since fullscreen and topmost apps park above it.
    private void Guard()
    {
        _reducedMotion = Motion.ReducedMotion;
        UpdateCap();
        var monitor = Win32.MonitorFromWindow(_hwnd, Win32.MonitorDefaultToNearest);
        var foreground = Win32.GetForegroundWindow();
        var fullscreen = foreground != _hwnd && Win32.IsFullscreen(foreground, monitor);
        // A full-screen overlay (the Win+Shift+S snipping layer) is not an
        // app to step aside for: the drawer stays put beneath it, without
        // jumping back on top, until it closes.
        var overlay = fullscreen && Win32.IsOverlay(foreground);
        var covered = fullscreen && !overlay;
        if (covered != _coveredByFullscreen)
        {
            _coveredByFullscreen = covered;
            ApplyVisibility();
        }
        if (!_shown || overlay) return;
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
}
