using System.Numerics;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Settings;

/// Settings' own motion: the page switch (M8), the Agents page's cards
/// gliding as an agent is hidden or shown (M10) or moved (M11), and an agent
/// opened from the drawer (M19). Expanders open and close by themselves (M9,
/// Shell/ExpanderMotion.cs). Pixel steps whole cells on its 30-tick clock;
/// Classic eases on its curves; reduced motion goes at once.
internal sealed partial class SettingsWindow
{
    // The page list sits over one plate (Pixel) or pill (Classic) of its
    // own, which glides between items as the page changes; meanwhile the
    // new item's own plate waits, hidden.
    private Grid _nav = null!;
    private FrameworkElement _slider = null!;
    private ListViewItem? _waiting;
    private StackPanel? _body;
    private UiMoment? _switch;
    private Action? _switched;
    private UiMoment? _regroup;
    private UiMoment? _arrive;

    // The Agents page's cards, each with its grip, which M10, M11 and M19 move.
    private readonly Dictionary<DisplayProvider, FrameworkElement> _cards = new();
    // The agent an arrow or a drag just moved, for the glide that follows,
    // and whether a drop moved it (the glide then settles it as a drop).
    private DisplayProvider? _moving;
    private bool _dropped;

    // MARK: - M8 page switch

    /// The open page's plate and accent bar (Pixel), or its pill and lift
    /// (Classic), as an element of its own behind the list.
    private FrameworkElement NavSlider()
    {
        var slider = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        if (_pixel)
        {
            slider.Children.Add(new PixelSurface { Shape = PixelShape.Control, Fill = Res<Brush>("PixelPaper"), Outline = Res<Brush>("PixelLine") });
            slider.Children.Add(new Rectangle
            {
                Width = 4,
                Height = 16,
                Margin = new Thickness(6, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = Res<Brush>("PixelAccent"),
            });
            return slider;
        }
        var pill = new Rectangle { RadiusX = 20, RadiusY = 20, Fill = Res<Brush>("ClassicPaper") };
        slider.Children.Add(new SoftShadow { Kind = "Lift", Target = pill });
        slider.Children.Add(pill);
        return slider;
    }

    /// The parts of an item's template that draw it as the open page.
    private IEnumerable<FrameworkElement> ItemPlate(ListViewItem item)
    {
        foreach (var name in _pixel ? new[] { "Chosen", "Bar" } : new[] { "Pill", "Lift" })
        {
            if (ControlMotion.Find<FrameworkElement>(item, name) is { } part) yield return part;
        }
    }

    private void HoldItemPlate(ListViewItem item)
    {
        _waiting = item;
        foreach (var part in ItemPlate(item)) part.Opacity = 0;
    }

    private void ReleaseItemPlate()
    {
        if (_waiting is { } item)
        {
            foreach (var part in ItemPlate(item)) part.Opacity = 1;
        }
        _waiting = null;
        _slider.Visibility = Visibility.Collapsed;
    }

    /// M8: the plate or pill glides from the old page's item to the new one
    /// while the new page comes in by groups: its title and intro, its first
    /// section, then the rest. Pixel: the plate on in2; each group shows 4
    /// DIP low, one tick after the last, and settles on the next. Classic:
    /// the pill over 333 ms on inOut, stretching 18 % at halfway; the groups
    /// 33 ms apart, each rising 8 DIP over 250 ms as it fades in over 167.
    /// `_switched` runs once the switch has played.
    private void PlaySwitch(int from, int to)
    {
        // An interrupted glide starts from where the slider is.
        float? gliding = _slider.Visibility == Visibility.Visible ? _slider.Translation.Y : null;
        _switch?.Stop();
        _switch = null;
        ReleaseItemPlate();
        var next = _switched;
        _switched = null;
        if (Motion.ReducedMotion || _body is not { } body
            || _pages.ContainerFromIndex(from) is not ListViewItem old || _pages.ContainerFromIndex(to) is not ListViewItem item
            || item.ActualHeight <= 0)
        {
            next?.Invoke();
            return;
        }
        var start = old.TransformToVisual(_nav).TransformPoint(default);
        var end = item.TransformToVisual(_nav).TransformPoint(default);
        double x = end.X, y0 = gliding ?? start.Y, y1 = end.Y;
        _slider.Width = item.ActualWidth;
        _slider.Height = item.ActualHeight;
        _slider.CenterPoint = new Vector3((float)(item.ActualWidth / 2), (float)(item.ActualHeight / 2), 0);
        _slider.Scale = Vector3.One;
        _slider.Translation = new Vector3((float)x, (float)y0, 0);
        _slider.Visibility = Visibility.Visible;
        HoldItemPlate(item);
        var groups = PageGroups(body);
        void Glide(double y) => _slider.Translation = new Vector3((float)x, (float)y, 0);
        void Done()
        {
            _switch = null;
            next?.Invoke();
        }
        if (_pixel)
        {
            var glide = Motion.Remaining(y0, y1, Motion.In2);
            _switch = UiMoment.Play(3 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k >= 2) ReleaseItemPlate();
                else Glide(k == 0 ? y0 : glide[0]);
                for (var g = 0; g < groups.Count; g++)
                {
                    foreach (var element in groups[g])
                    {
                        element.Opacity = k >= g ? 1 : 0;
                        element.Translation = new Vector3(0, k == g ? 4 : 0, 0);
                    }
                }
            }, Done);
            return;
        }
        _switch = UiMoment.Play(333, t =>
        {
            var p = Motion.InOut(t / 333);
            Glide(y0 + (y1 - y0) * p);
            _slider.Scale = new Vector3(1, (float)(1 + 0.18 * (1 - Math.Abs(2 * p - 1))), 1);
            if (t >= 333) ReleaseItemPlate();
            for (var g = 0; g < groups.Count; g++)
            {
                var since = t - 33 * g;
                foreach (var element in groups[g])
                {
                    element.Opacity = Motion.Out(since / 167);
                    element.Translation = new Vector3(0, (float)(8 * (1 - Motion.Out(since / 250))), 0);
                }
            }
        }, Done);
    }

    /// A page's children in M8's three groups: before the first section,
    /// the first section, and the rest.
    private static List<List<UIElement>> PageGroups(Panel body)
    {
        var groups = new List<List<UIElement>> { new(), new(), new() };
        var sections = 0;
        foreach (var child in body.Children)
        {
            if (IsSectionStart(child)) sections++;
            groups[Math.Min(sections, 2)].Add(child);
        }
        return groups;
    }

    // MARK: - M10 hide and show, M11 move

    /// Hiding, showing or moving an agent rearranges the Agents page's list:
    /// the cards change places, kept as they are (open or not), and each
    /// glides from where it stood (Glide). A change in which agents are here
    /// rebuilds the page.
    private void Rearrange()
    {
        if (_page != Page.Agents || !AppWindow.IsVisible || _drag is not null) return;
        var lineup = AgentLineup();
        var key = LineupKey(lineup);
        if (key == _lineupKey) return;
        var moving = _moving;
        var dropped = _dropped;
        _moving = null;
        _dropped = false;
        if (_agentList is not { } list || lineup.Count != _cards.Count || lineup.Any(entry => !_cards.ContainsKey(entry.Provider)))
        {
            Rebuild();
            return;
        }
        _regroup?.Stop();
        var before = _cards.ToDictionary(pair => pair.Key, pair => Top(pair.Value));
        // The card the user acted on: switched on or off, or moved.
        var acted = lineup.Where(entry => _lineup.Any(old => old.Provider == entry.Provider && old.Shown != entry.Shown))
            .Select(entry => (DisplayProvider?)entry.Provider)
            .FirstOrDefault() ?? moving;
        var shownCount = lineup.Count(entry => entry.Shown);
        Reorder(list, lineup.Select(entry => (UIElement)_cards[entry.Provider]).ToList(), acted is { } kept ? _cards[kept] : null);
        for (var i = 0; i < lineup.Count; i++)
        {
            var (provider, shown) = lineup[i];
            _agents[provider].Place(i, shownCount, shown);
        }
        _lineup = lineup;
        _lineupKey = key;
        list.UpdateLayout();
        Glide(before, acted, dropped && acted == moving);
    }

    /// The list's children into order, the card acted on never moved itself:
    /// the others step round it, so nothing disturbs its switch's flip or the
    /// keyboard focus inside it.
    private static void Reorder(Panel list, IReadOnlyList<UIElement> order, UIElement? kept)
    {
        var children = list.Children;
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == kept)
            {
                // Whatever still stands before it goes past it.
                while (children.IndexOf(kept) is var at && at > i) children.Move((uint)i, (uint)at);
                continue;
            }
            var from = children.IndexOf(order[i]);
            if (from != i) children.Move((uint)from, (uint)i);
        }
    }

    /// The Agents page anew, at once; the scroll and the open cards stay as
    /// they were.
    private void Rebuild()
    {
        var offset = _scroll.VerticalOffset;
        var open = _agents.Where(pair => pair.Value.Expander.IsExpanded).Select(pair => pair.Key).ToList();
        Show(Page.Agents, keepOffset: true);
        foreach (var provider in open)
        {
            if (_agents.TryGetValue(provider, out var section)) section.Expander.IsExpanded = true;
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, offset, null, disableAnimation: true));
    }

    /// Where a card stands as seen: its place in the list and any glide or
    /// drag still on it.
    private static double Top(UIElement card) => card.ActualOffset.Y + card.Translation.Y;

    /// M10/M11: every card that changed places glides from where it stood,
    /// as an arrow move does: Pixel in3 (half, then 15 % of the way left),
    /// the card acted on riding 2 DIP up in hand over the rest; Classic over
    /// 333 ms on inOut. A drop settles as M11's drop: Pixel in2, the card
    /// stepping down from its lift (4, 2, 0), its shadow gone on the last
    /// frame; Classic over 250 ms on out, its outline fading over 167.
    /// Reduced: at once.
    private void Glide(Dictionary<DisplayProvider, double> before, DisplayProvider? acted, bool drop = false)
    {
        var lead = acted is { } chosen && _cards.ContainsKey(chosen) ? chosen : (DisplayProvider?)null;
        var hand = lead is { } inHand ? _agents[inHand] : null;
        UIElement? held = null;
        var lift = 0.0;
        var moves = new List<(UIElement Card, double From)>();
        foreach (var (provider, card) in _cards)
        {
            var from = before[provider] - card.ActualOffset.Y;
            if (provider == lead)
            {
                held = card;
                // A dropped card's lift, as far right as up, steps down apart.
                lift = drop ? hand!.Shifted : 0;
                from += lift;
            }
            else _agents[provider].Hold(0);
            card.Translation = Vector3.Zero;
            Canvas.SetZIndex(card, 0);
            if (Math.Abs(from) >= 0.5 || provider == lead && drop) moves.Add((card, from));
        }
        void Land()
        {
            foreach (var (moved, _) in moves) moved.Translation = Vector3.Zero;
            if (held is not null) Canvas.SetZIndex(held, 0);
            hand?.Hold(0);
            _regroup = null;
        }
        if (moves.Count == 0 || Motion.ReducedMotion)
        {
            Land();
            return;
        }
        if (held is not null) Canvas.SetZIndex(held, 1);
        // Only a card that moves is carried in hand.
        var carried = moves.Exists(move => move.Card == held);
        if (_pixel)
        {
            var curve = drop ? Motion.In2 : Motion.In3;
            var ticks = curve.Length;
            var steps = moves.ToDictionary(move => move.Card, move => Motion.Remaining(move.From, 0, curve));
            _regroup = UiMoment.Play(ticks * Motion.TickMs, t =>
            {
                var k = Math.Min(Motion.Tick(t), ticks);
                foreach (var (moved, from) in moves)
                {
                    var y = k == 0 ? from : steps[moved][k - 1];
                    var up = moved != held ? 0
                        : drop ? (k == 0 ? lift : k < ticks ? Math.Min(lift, 2) : 0)
                        : k > 0 && k < ticks ? 2 : 0;
                    moved.Translation = new Vector3(0, (float)(y - up), 0);
                    if (drop && moved == held) hand?.Shift(up);
                }
                if (carried) hand?.Hold(k < ticks && (drop || k > 0) ? 1 : 0);
            }, Land);
            return;
        }
        var duration = drop ? 250 : 333;
        var ease = drop ? Motion.Out : Motion.InOut;
        _regroup = UiMoment.Play(duration, t =>
        {
            var left = 1 - ease(t / duration);
            foreach (var (moved, from) in moves) moved.Translation = new Vector3(0, (float)(from * left), 0);
            if (drop) hand?.Hold(1 - Math.Min(t / 167, 1));
        }, Land);
    }

    // MARK: - M11 drag

    /// A card dragged by its grip: where the pointer took hold (in the list),
    /// the shown cards' order and places when the drag began, the place it
    /// would take if let go now, how far the pointer has moved it, and its
    /// lift (Pixel: as far right as up).
    private sealed class Drag(DisplayProvider provider, UIElement card, double start,
        IReadOnlyList<DisplayProvider> order, Dictionary<DisplayProvider, (double Top, double Height)> slots)
    {
        public DisplayProvider Provider { get; } = provider;
        public UIElement Card { get; } = card;
        public double Start { get; } = start;
        public IReadOnlyList<DisplayProvider> Order { get; } = order;
        public Dictionary<DisplayProvider, (double Top, double Height)> Slots { get; } = slots;
        public int Target { get; set; } = order.ToList().IndexOf(provider);
        public double Dy { get; set; }
        public double Lift { get; set; }
    }

    private Drag? _drag;
    // The card in hand lifting, and the others making room around it.
    private UiMoment? _lift;
    private UiMoment? _room;

    /// A shown card follows the pointer by its grip, in hand over the others,
    /// while they make room around where it would go; let go, the list takes
    /// the new order and the card settles into its place (M11).
    private void HookDrag(AgentSection section, DisplayProvider provider)
    {
        var grip = section.Grip;
        grip.PointerPressed += (_, e) =>
        {
            if (_drag is not null || _agentList is not { } list || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            if (!_lineup.Any(entry => entry.Provider == provider && entry.Shown) || !grip.CapturePointer(e.Pointer)) return;
            Take(provider, e.GetCurrentPoint(list).Position.Y);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_drag is not { } drag || drag.Provider != provider || _agentList is not { } list) return;
            DragTo(drag, e.GetCurrentPoint(list).Position.Y - drag.Start);
            KeepInView(e.GetCurrentPoint(_scroll).Position.Y);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (_drag?.Provider != provider) return;
            grip.ReleasePointerCapture(e.Pointer);
            Drop();
        };
        grip.PointerCaptureLost += (_, _) =>
        {
            if (_drag?.Provider == provider) Drop();
        };
    }

    /// The card taken in hand: Pixel lifts it 2, then 4 DIP right and up on
    /// its accent outline and hard shadow; Classic fades its accent outline
    /// in over 83 ms. Reduced: the look at once, no lift.
    private void Take(DisplayProvider provider, double start)
    {
        if (_agentList is not { } list) return;
        // A glide in flight lands first, so the places are the cards' own.
        _regroup?.Stop();
        _regroup = null;
        foreach (var (each, card) in _cards)
        {
            card.Translation = Vector3.Zero;
            Canvas.SetZIndex(card, 0);
            _agents[each].Hold(0);
        }
        list.UpdateLayout();
        var order = _lineup.Where(entry => entry.Shown).Select(entry => entry.Provider).ToList();
        var drag = _drag = new Drag(provider, _cards[provider], start, order, Slots(order));
        Canvas.SetZIndex(drag.Card, 1);
        var hand = _agents[provider];
        if (Motion.ReducedMotion)
        {
            hand.Hold(1);
            return;
        }
        if (_pixel)
        {
            _lift = UiMoment.Play(2 * Motion.TickMs, t =>
            {
                var k = Math.Min(Motion.Tick(t), 2);
                drag.Lift = 2 * k;
                hand.Hold(k > 0 ? 1 : 0);
                Follow(drag);
            }, () => _lift = null);
            return;
        }
        _lift = UiMoment.Play(83, t => hand.Hold(t / 83), () => _lift = null);
    }

    /// The shown cards' places in the list, for a drag.
    private Dictionary<DisplayProvider, (double Top, double Height)> Slots(IEnumerable<DisplayProvider> order) =>
        order.ToDictionary(p => p, p => ((double)_cards[p].ActualOffset.Y, (double)_cards[p].ActualSize.Y));

    /// The card in hand where the pointer has it, lifted: Pixel on whole
    /// cells, Classic 1 : 1.
    private void Follow(Drag drag)
    {
        var dy = _pixel ? Motion.Cell(drag.Dy) : drag.Dy;
        drag.Card.Translation = new Vector3(0, (float)(dy - drag.Lift), 0);
        _agents[drag.Provider].Shift(drag.Lift);
    }

    /// The card in hand at dy from where it was taken; past another card's
    /// middle, the others make room.
    private void DragTo(Drag drag, double dy)
    {
        drag.Dy = dy;
        Follow(drag);
        var (top, height) = drag.Slots[drag.Provider];
        var middle = top + height / 2 + dy;
        var target = drag.Order.Count(p => p != drag.Provider && drag.Slots[p].Top + drag.Slots[p].Height / 2 < middle);
        if (target == drag.Target) return;
        drag.Target = target;
        MakeRoom(drag);
    }

    /// The other cards step to the places they would take with the card in
    /// hand let go here: Pixel on in2, Classic over 250 ms on out. Reduced:
    /// at once.
    private void MakeRoom(Drag drag)
    {
        var arranged = drag.Order.Where(p => p != drag.Provider).ToList();
        arranged.Insert(drag.Target, drag.Provider);
        var moves = new List<(UIElement Card, double From, double To)>();
        var y = drag.Slots[drag.Order[0]].Top;
        foreach (var p in arranged)
        {
            if (p != drag.Provider) moves.Add((_cards[p], _cards[p].Translation.Y, y - drag.Slots[p].Top));
            y += drag.Slots[p].Height + CardGap;
        }
        _room?.Stop();
        _room = null;
        if (Motion.ReducedMotion)
        {
            foreach (var (card, _, to) in moves) card.Translation = new Vector3(0, (float)to, 0);
            return;
        }
        if (_pixel)
        {
            var steps = moves.Select(move => Motion.Remaining(move.From, move.To, Motion.In2)).ToList();
            _room = UiMoment.Play(2 * Motion.TickMs, t =>
            {
                var k = Math.Min(Motion.Tick(t), 2);
                for (var i = 0; i < moves.Count; i++) moves[i].Card.Translation = new Vector3(0, (float)(k == 0 ? moves[i].From : steps[i][k - 1]), 0);
            }, () => _room = null);
            return;
        }
        _room = UiMoment.Play(250, t =>
        {
            var share = Motion.Out(t / 250);
            foreach (var (card, from, to) in moves) card.Translation = new Vector3(0, (float)(from + (to - from) * share), 0);
        }, () => _room = null);
    }

    /// Near the page's top or bottom edge, the page scrolls on as the
    /// pointer moves.
    private void KeepInView(double y)
    {
        const double edge = 48, step = 12;
        if (y < edge) _scroll.ChangeView(null, Math.Max(0, _scroll.VerticalOffset - step), null, disableAnimation: true);
        else if (y > _scroll.ViewportHeight - edge) _scroll.ChangeView(null, Math.Min(_scroll.ScrollableHeight, _scroll.VerticalOffset + step), null, disableAnimation: true);
    }

    /// Let go: a new order is saved and the list rearranges from where the
    /// cards stand, the card settling as a drop; the same order puts it
    /// back.
    private void Drop()
    {
        if (_drag is not { } drag) return;
        _drag = null;
        _lift?.Stop();
        _lift = null;
        _room?.Stop();
        _room = null;
        var arranged = drag.Order.Where(p => p != drag.Provider).ToList();
        arranged.Insert(drag.Target, drag.Provider);
        if (!arranged.SequenceEqual(drag.Order))
        {
            _moving = drag.Provider;
            _dropped = true;
            DrawerStore.Shared.SetOrder(arranged);
            return;
        }
        Glide(_cards.ToDictionary(pair => pair.Key, pair => Top(pair.Value)), drag.Provider, drop: true);
    }

    /// AGENTISLAND_DEBUG_SETTINGS_FILM: a drag by hand, with no pointer: the
    /// first shown card taken, held dy below where it was, and let go.
    internal void DebugTake()
    {
        var order = _lineup.Where(entry => entry.Shown).Select(entry => entry.Provider).ToList();
        if (order.Count > 1) Take(order[0], 0);
    }

    internal void DebugDrag(double dy)
    {
        if (_drag is { } drag) DragTo(drag, dy);
    }

    internal void DebugDrop() => Drop();

    // MARK: - M19 arriving from the drawer

    /// Opens an agent's card. A window that is itself appearing opens with
    /// the card open and in view: nothing animates inside it. An open window
    /// plays M8 to the Agents page if needed, then the card scrolls into
    /// view (Pixel: in one frame; Classic: over 333 ms) and opens with M9;
    /// Classic then lights it with the agent's tint, back to paper over 900
    /// ms. A hidden agent's card waits at the bottom of the list.
    private void Focus(DisplayProvider provider, bool animate)
    {
        _arrive?.Stop();
        _arrive = null;
        animate &= !Motion.ReducedMotion;
        if (_page == Page.Agents)
        {
            Arrive(provider, animate);
            return;
        }
        if (animate) _switched = () => Arrive(provider, animate: true);
        SelectPage(Page.Agents);
        if (!animate) Arrive(provider, animate: false);
    }

    private void Arrive(DisplayProvider provider, bool animate)
    {
        if (!_agents.TryGetValue(provider, out var section)) return;
        var expander = section.Expander;
        Control lit = expander;
        if (!animate)
        {
            expander.IsExpanded = true;
            // After the opened rows lay out, or the scroll falls short.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                var (inView, target) = Place(expander);
                if (!inView) _scroll.ChangeView(null, target, null, disableAnimation: true);
                FocusFirst(expander);
            });
            return;
        }
        void Open()
        {
            _arrive = null;
            expander.IsExpanded = true;
            FocusFirst(expander);
            if (!_pixel) Glow(lit, provider);
        }
        var (shown, to) = Place(expander);
        if (shown)
        {
            Open();
            return;
        }
        if (_pixel)
        {
            _scroll.ChangeView(null, to, null, disableAnimation: true);
            Open();
            return;
        }
        var from = _scroll.VerticalOffset;
        _arrive = UiMoment.Play(333, t =>
            _scroll.ChangeView(null, from + (to - from) * Motion.Out(t / 333), null, disableAnimation: true), Open);
    }

    /// Whether a card's header shows in the page, and the scroll that puts
    /// it 20 DIP below the top.
    private (bool InView, double Target) Place(FrameworkElement card)
    {
        var top = card.TransformToVisual(_scroll).TransformPoint(default).Y;
        var inView = top >= 0 && top + 64 <= _scroll.ViewportHeight;
        return (inView, Math.Clamp(_scroll.VerticalOffset + top - 20, 0, _scroll.ScrollableHeight));
    }

    /// Focus goes to the card for the keyboard's sake, programmatically: a
    /// ring shows only once the keyboard is in use, not after a click.
    private static void FocusFirst(DependencyObject card)
    {
        if (FocusManager.FindFirstFocusableElement(card) is Control control) control.Focus(FocusState.Programmatic);
    }

    /// Classic's arrival glow: the card's fill from the agent's tint back to
    /// paper over 900 ms, once, so the eye lands on it.
    private static void Glow(Control card, DisplayProvider provider)
    {
        var dark = Brand.IsDark();
        var tint = ClassicTokens.Tint(ClassicTokens.Identity(provider, dark), dark);
        var paper = ClassicTokens.Get("Paper", dark);
        var fill = new SolidColorBrush(tint);
        card.Background = fill;
        UiMoment.Play(900, t => fill.Color = Mix(tint, paper, Motion.InOut(t / 900)), () => card.ClearValue(Control.BackgroundProperty));
    }

    private static Color Mix(Color from, Color to, double share)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * share);
        return Color.FromArgb(Channel(from.A, to.A), Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }
}
