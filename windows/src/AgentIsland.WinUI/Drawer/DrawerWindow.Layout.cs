using AgentIsland.Model;
using AgentIsland.WinUI.Platform;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;

namespace AgentIsland.WinUI.Drawer;

// Where the drawer sits: its plan, its window and region, and the roster and edge changes.
internal sealed partial class DrawerWindow
{
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
}
