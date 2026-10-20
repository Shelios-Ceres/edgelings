using AgentIsland.Model;
using AgentIsland.WinUI.Platform;
using AgentIsland.WinUI.Settings;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;

namespace AgentIsland.WinUI.Drawer;

// The pointer: hover, click, right-click and the drag of the drawer.
internal sealed partial class DrawerWindow
{
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
                    // A character asleep under reduced motion stays still.
                    if (!HoldsStill(_slots[index].EngineState, _theme, _reducedMotion, _bots[index].Look)) _engine?.Poke(index);
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
        _ = DrawerMenu.ShowAsync(bot.Provider,
            _bots.Select(b => (b.Provider, ProviderVisibilityStore.Shared.IsShown(b.Provider))).ToList(),
            keepOpen, at, scene.Scale);
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
}
