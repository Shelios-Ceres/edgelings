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

/// Settings' own motion: the page switch (M8), the Agents page regrouping
/// as an agent is hidden or shown (M10), and an agent opened from the
/// drawer (M19). Expanders open and close by themselves (M9,
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

    // The Agents page's parts that M10 and M19 move.
    private readonly Dictionary<DisplayProvider, RevealPanel> _cards = new();
    private readonly Dictionary<DisplayProvider, SettingsCard> _hiddenRows = new();
    private IReadOnlyList<DisplayProvider> _hiddenAgents = Array.Empty<DisplayProvider>();
    private RevealPanel? _hiddenGroup;
    private SettingsExpander? _hiddenList;
    private IconElement? _hiddenEye;
    private TextBlock? _hiddenCount;

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

    // MARK: - M10 hide and show

    /// Hiding, showing or reordering an agent regroups the Agents page. One
    /// agent hidden or shown plays M10 around the rebuild; anything else, or
    /// a change while M10 plays, rebuilds at once.
    private void Regroup()
    {
        if (_page != Page.Agents || !AppWindow.IsVisible) return;
        var (detected, missing, hidden) = AgentGroups();
        if (GroupsKey(detected, missing, hidden) == _agentGroups) return;
        var hiding = hidden.Except(_hiddenAgents).ToList();
        var showing = _hiddenAgents.Except(hidden).ToList();
        var playing = _regroup is not null;
        _regroup?.Stop();
        _regroup = null;
        if (playing || Motion.ReducedMotion || hiding.Count + showing.Count != 1) Rebuild();
        else if (hiding.Count == 1) Hiding(hiding[0]);
        else Showing(showing[0]);
    }

    /// The Agents page anew in its current groups, at once; the scroll, the
    /// open cards and the hidden list stay as they were.
    private void Rebuild()
    {
        var offset = _scroll.VerticalOffset;
        var open = _agents.Where(pair => pair.Value.Expander.IsExpanded).Select(pair => pair.Key).ToList();
        var hiddenOpen = _hiddenList?.IsExpanded == true;
        Show(Page.Agents, keepOffset: true);
        foreach (var provider in open)
        {
            if (_agents.TryGetValue(provider, out var section)) section.Expander.IsExpanded = true;
        }
        if (hiddenOpen && _hiddenList is { } list) list.IsExpanded = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, offset, null, disableAnimation: true));
    }

    /// M10 hide: the agent's card closes and takes its gap with it (Pixel:
    /// half, then gone; Classic: over 250 ms, fading and shrinking to 0.98),
    /// then the page regroups: the hidden list's eye hops (Pixel) or blinks
    /// as the new count fades in (Classic), or for the first hidden agent
    /// the whole section opens.
    private void Hiding(DisplayProvider provider)
    {
        if (!_cards.TryGetValue(provider, out var host) || !host.IsLoaded || host.Child is not FrameworkElement card)
        {
            Rebuild();
            return;
        }
        var first = _hiddenGroup is null;
        var natural = host.Natural;
        var gap = CardGap;
        void Then()
        {
            _regroup = null;
            Rebuild();
            if (first) OpenHiddenGroup();
            else NoteHiddenCount();
        }
        if (_pixel)
        {
            _regroup = UiMoment.Play(2 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                host.Shown = k == 0 ? natural : k == 1 ? Motion.Cell(natural / 2) : 0;
                host.Margin = new Thickness(0, 0, 0, k == 0 ? 0 : k == 1 ? -Motion.Cell(gap / 2) : -gap);
            }, Then);
            return;
        }
        card.CenterPoint = new Vector3((float)(card.ActualWidth / 2), (float)(natural / 2), 0);
        _regroup = UiMoment.Play(250, t =>
        {
            var e = Motion.InOut(t / 250);
            host.Shown = natural * (1 - e);
            host.Margin = new Thickness(0, 0, 0, -gap * e);
            card.Opacity = 1 - e;
            var scale = (float)(1 - 0.02 * e);
            card.Scale = new Vector3(scale, scale, 1);
        }, Then);
    }

    /// The first hidden agent's section opens like an expander's rows.
    private void OpenHiddenGroup()
    {
        if (_hiddenGroup is not { Child: { } content } group) return;
        group.Shown = 0;
        if (_pixel)
        {
            _regroup = UiMoment.Play(3 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                group.Shown = k >= 3 ? double.NaN : Motion.Cell(group.Natural * k / 3);
            }, () => _regroup = null);
            return;
        }
        content.Opacity = 0;
        _regroup = UiMoment.Play(333, t =>
        {
            var e = Motion.Out(t / 333);
            group.Shown = t >= 333 ? double.NaN : group.Natural * e;
            content.Opacity = e;
        }, () => _regroup = null);
    }

    /// The hidden list's new count: its eye hops a cell (Pixel), or the count
    /// fades in over 167 ms as the eye blinks over 250 (Classic).
    private void NoteHiddenCount()
    {
        if (_hiddenEye is not { } eye) return;
        if (_pixel)
        {
            _regroup = UiMoment.Play(2 * Motion.TickMs, t =>
                eye.Translation = new Vector3(0, Motion.Tick(t) == 1 ? -2 : 0, 0), () => _regroup = null);
            return;
        }
        var count = _hiddenCount;
        if (count is not null) count.Opacity = 0;
        _regroup = UiMoment.Play(250, t =>
        {
            if (count is not null) count.Opacity = Motion.Linear(t / 167);
            var p = Motion.InOut(t / 250);
            eye.CenterPoint = new Vector3((float)(eye.ActualWidth / 2), (float)(eye.ActualHeight / 2), 0);
            eye.Scale = new Vector3(1, (float)(1 - 0.9 * (1 - Math.Abs(2 * p - 1))), 1);
        }, () => _regroup = null);
    }

    /// M10 show: the agent's row leaves the hidden list (the whole section
    /// when it was the last), then the page regroups and the agent's card
    /// opens in its place. Pixel: the row half, then gone; the card a third,
    /// two thirds, all. Classic: the row fades as it closes over 250 ms, the
    /// page regroups at 167 and the card opens over 333, fading in as it
    /// settles 6 DIP.
    private void Showing(DisplayProvider provider)
    {
        var last = _hiddenAgents.Count == 1;
        FrameworkElement? leaving = last ? _hiddenGroup
            : _hiddenList is { IsExpanded: true } && _hiddenRows.TryGetValue(provider, out var row) ? row : null;
        void Then()
        {
            _regroup = null;
            Rebuild();
            OpenCard(provider);
        }
        if (leaving is not { IsLoaded: true } || leaving.ActualHeight <= 0)
        {
            Then();
            return;
        }
        var natural = leaving.ActualHeight;
        var margin = leaving.Margin;
        var gap = CardGap;
        // The last section takes the gap above it; a row only itself.
        void Cut(double visible, double share)
        {
            if (leaving is RevealPanel group)
            {
                group.Shown = visible;
                group.Margin = new Thickness(0, -gap * share, 0, 0);
                return;
            }
            leaving.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom - (natural - visible));
            leaving.Clip = new RectangleGeometry { Rect = new Rect(-16, 0, leaving.ActualWidth + 32, Math.Max(0, visible)) };
        }
        if (_pixel)
        {
            _regroup = UiMoment.Play(2 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                Cut(k == 0 ? natural : k == 1 ? Motion.Cell(natural / 2) : 0, Math.Min(k, 2) / 2.0);
            }, Then);
            return;
        }
        _regroup = UiMoment.Play(167, t =>
        {
            var e = Motion.InOut(t / 250);
            Cut(natural * (1 - e), e);
            leaving.Opacity = 1 - e;
        }, Then);
    }

    /// A card joining the page opens in place: Pixel a third, two thirds,
    /// all; Classic over 333 ms, fading in as it settles 6 DIP.
    private void OpenCard(DisplayProvider provider)
    {
        if (!_cards.TryGetValue(provider, out var host) || host.Child is not { } card) return;
        host.Shown = 0;
        if (_pixel)
        {
            _regroup = UiMoment.Play(3 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                host.Shown = k >= 3 ? double.NaN : Motion.Cell(host.Natural * k / 3);
            }, () => _regroup = null);
            return;
        }
        card.Opacity = 0;
        _regroup = UiMoment.Play(333, t =>
        {
            var e = Motion.Out(t / 333);
            host.Shown = t >= 333 ? double.NaN : host.Natural * e;
            card.Opacity = e;
            card.Translation = new Vector3(0, (float)(6 * (1 - e)), 0);
        }, () => _regroup = null);
    }

    // MARK: - M19 arriving from the drawer

    /// Opens an agent's card. A window that is itself appearing opens with
    /// the card open and in view: nothing animates inside it. An open window
    /// plays M8 to the Agents page if needed, then the card scrolls into
    /// view (Pixel: in one frame; Classic: over 333 ms) and opens with M9;
    /// Classic then lights it with the agent's tint, back to paper over 900
    /// ms. A hidden agent's way in is the hidden list.
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
        var found = _agents.TryGetValue(provider, out var section);
        if ((found ? section!.Expander : _hiddenList) is not { } expander) return;
        Control lit = !found && _hiddenRows.TryGetValue(provider, out var row) ? row : expander;
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
