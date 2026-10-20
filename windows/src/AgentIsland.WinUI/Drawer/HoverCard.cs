using System.Numerics;
using System.Diagnostics;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// The drawer's hover card beside the character it describes, each quota
/// window as a filled bar in that character's colour. It is a windowed
/// popup, so it can extend past the drawer's narrow window. With the classic
/// characters it is paper: an opaque pane with a soft tail aimed at the
/// character and a soft shadow. With the pixel bots it is a pixel panel: an
/// opaque plate on their 2-DIP grid, outlined in the character's colour and
/// pointing at it, with block bars, its English set in a pixel face.
internal sealed class HoverCard : IDisposable
{
    private const double CardWidth = 264;
    // English in the pixel face runs wider than the system font.
    private const double PixelCardWidth = 272;
    private const double PixelLabelWidth = 52;
    private const double PixelValueWidth = 72;
    private const double Gap = 10;
    private const double LabelWidth = 40;
    private const double ValueWidth = 58;
    private static readonly Thickness PaperPadding = new(16, 13, 16, 14);
    private static readonly Thickness PixelPadding = new(16, 14, 16, 14);

    // The paper's tail: how far it reaches past the card's side.
    private const double TailReach = 9;
    // Room around the card for its motion and the paper's shadow.
    private const double MotionMargin = 24;

    private readonly Popup _popup;
    private readonly Canvas _stage = new() { IsHitTestVisible = false };
    private readonly Grid _host = new();
    private readonly Grid _plate = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly PaperPlate _paper = new();
    // Pinned to the card's corner: ghosts are placed from it.
    private readonly PixelPlate _pixelPlate = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly Border _card;
    private StackPanel _body = new();
    private readonly Panel _root;
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _size;
    private readonly InsetClip _contentClip;
    // The moment playing (M3), and the old content fading out in a switch.
    private UiMoment? _motion;
    private Border? _outgoing;
    // The agents whose phase has needed a line of its own (Pixel) since the
    // card opened: it keeps the line until the card closes, so the card
    // doesn't gain and lose a line as phases come and go.
    private readonly HashSet<DisplayProvider> _phaseBelow = new();
    // The texts a Classic refresh is swapping in (M4), with their own margins.
    private readonly List<(FrameworkElement Text, Thickness Margin)> _swapping = new();

    // M4: each meter's share as the card last showed it, to count from when
    // the card refreshes in place; the values counting now.
    private Dictionary<string, (double Share, bool Warn)> _shares = new();
    private Dictionary<string, (double Share, bool Warn)> _built = new();
    private readonly HashSet<UIElement> _counting = new();
    private bool _recount;
    private int _generation;
    private bool _closing;
    private bool _animating;
    private bool _disposed;
    private double _width = CardWidth;
    private static bool _dark;
    // The card being built is paper (Classic): its inks are Classic's tokens.
    private static bool _paperInk;
    private Request? _displayed;
    private Request? _refresh;
    private string? _contentKey;
    private Size _contentSize;
    private Rect _bounds;
    // The card's aim at its bot's centre, in the drawer's DIPs.
    private double _aim;
    private Rect? _stageBounds;
    private double _travelTop;
    private double _travelBottom;
    private int _requests;
    private int _builds;
    private int _measures;
    private int _skips;
    private int _transitions;
    private double _lastShowMs;

    private sealed record Request(
        IReadOnlyList<(StatusCard Card, uint Swatch)> Sections, bool Pixel,
        bool CharacterRight, Rect Anchor, Rect Area, bool ReducedMotion,
        double Scale, bool Dark, string Key, string Identity);

    internal double DebugDurationScale { get; set; } = 1;

    internal string DiagnosticsSummary =>
        $"requests={_requests} builds={_builds} measures={_measures} skips={_skips} transitions={_transitions} lastShow={_lastShowMs:F2}ms";

    /// inWindow keeps the card inside root's window (the preview harness,
    /// whose prints then show the paper's shadow on its surface).
    public HoverCard(Panel root, bool inWindow = false)
    {
        _root = root;
        _card = new Border
        {
            Child = _body,
            Width = CardWidth,
            Padding = PaperPadding,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _host.Width = CardWidth;
        _plate.Children.Add(_paper);
        _plate.Children.Add(_pixelPlate);
        _host.Children.Add(_plate);
        _host.Children.Add(_card);
        _stage.Children.Add(_host);
        // The content is clipped to the plate while a switch changes its
        // height; any compositor will do.
        _compositor = CompositionTarget.GetCompositorForCurrentThread();
        _size = _compositor.CreatePropertySet();
        _size.InsertScalar("Height", 1);
        _size.InsertScalar("TargetHeight", 1);
        _contentClip = _compositor.CreateInsetClip();
        using (var clip = _compositor.CreateExpressionAnimation("Max(0, size.TargetHeight - size.Height)"))
        {
            clip.SetReferenceParameter("size", _size);
            _contentClip.StartAnimation("BottomInset", clip);
        }
        ElementCompositionPreview.GetElementVisual(_card).Clip = _contentClip;
        _popup = new Popup
        {
            Child = _stage,
            ShouldConstrainToRootBounds = inWindow,
            IsHitTestVisible = false,
        };
        root.Children.Add(_popup);
    }

    /// Open and not on its way out.
    public bool IsOpen => _popup.IsOpen && !_closing;

    /// The card's content, for the verification snapshot.
    public UIElement Visual => _host;

    /// Saves the card as shown. Its Translation places it in drawer
    /// coordinates, which leaves a rendered bitmap empty, so the snapshot
    /// draws it at the origin for the moment it takes.
    internal async Task SnapshotAsync(string path)
    {
        var translation = _host.Translation;
        _host.Translation = Vector3.Zero;
        try { await Snapshot.SaveAsync(_host, path); }
        finally { _host.Translation = translation; }
    }

    /// anchor and area are in the drawer window's DIPs; characterRight means
    /// the character is to the card's right (drawer on the right edge); pixel
    /// draws the pixel panel instead of the paper.
    /// The transparent popup is a staging area; plate and content move as
    /// one element within it, without moving a native window on every frame.
    public void Show(IReadOnlyList<(StatusCard Card, uint Swatch)> sections, bool pixel, bool characterRight, Rect anchor, Rect area, bool reducedMotion, bool retarget = false)
    {
        var started = Stopwatch.GetTimestamp();
        _requests++;
        try
        {
            if (_disposed || _root.XamlRoot is not { } xamlRoot) return;
            if (sections.Count == 0) { Hide(); return; }
            _popup.XamlRoot = xamlRoot;
            var dark = Brand.IsDark();
            var request = new Request(sections.ToArray(), pixel, characterRight, anchor, area, reducedMotion,
                xamlRoot.RasterizationScale, dark, HoverCardContentKey.Create(sections, dark, pixel, xamlRoot.RasterizationScale),
                string.Join(",", sections.Select(s => (int)s.Card.Provider)));
            if (_displayed is { } before && !SameFrame(before, request)) Hide();
            if (!_popup.IsOpen)
            {
                Open(request);
                return;
            }
            if (_displayed?.Identity != request.Identity)
            {
                Switch(request);
                return;
            }
            if (_closing)
            {
                _closing = false;
                _refresh = request;
                Return(request);
                return;
            }
            if (SamePresentation(_displayed!, request)) { _refresh = null; _skips++; return; }
            if (_animating)
            {
                _refresh = request;
                _skips++;
                return;
            }
            // M4: the same card with new content. Classic glides and swaps
            // what changed; the meters count in both looks.
            if (!request.Pixel && !request.ReducedMotion)
            {
                Refresh(request);
                return;
            }
            _recount = true;
            Present(request);
            _recount = false;
            Rest();
        }
        finally { _lastShowMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    /// animated plays its close (M3); otherwise it goes at once (drags,
    /// menus, a click, the drawer hiding).
    public void Hide(bool animated = false)
    {
        if (!_popup.IsOpen) return;
        if (!animated || _displayed is not { } shown) { Close(); return; }
        if (_closing) return;
        _closing = true;
        _refresh = null;
        if (shown.Pixel)
        {
            // The content goes and a ghost at half the card is left at the
            // pointer for a tick, then nothing.
            if (shown.ReducedMotion) { Close(); return; }
            Play(2 * Motion.TickMs, t =>
            {
                if (Motion.Tick(t) >= 1) Ghost(0.5);
            }, Close);
            return;
        }
        // Classic fades and shrinks a little toward the tail; reduced, it fades.
        var opacity = _host.Opacity;
        var scale = _host.Scale.X;
        var reduced = shown.ReducedMotion;
        Play(reduced ? 83 : 167, t =>
        {
            _host.Opacity = (float)(opacity * (1 - (reduced ? Motion.Linear(t / 83) : Motion.In(t / 167))));
            if (!reduced) _host.Scale = Uniform(Lerp(scale, 0.96, Motion.In(t / 167)));
        }, Close);
    }

    private static bool SameFrame(Request a, Request b) =>
        a.CharacterRight == b.CharacterRight && a.Scale == b.Scale && a.Area == b.Area
        && a.ReducedMotion == b.ReducedMotion;

    private static bool SamePresentation(Request a, Request b) =>
        a.Key == b.Key && a.Anchor == b.Anchor && SameFrame(a, b);

    /// M3 open. Pixel grows ghosts of the card from the pointer, a third and
    /// two thirds of it, then shows it whole; Classic comes out of the bot,
    /// growing from the tail's tip. Reduced, Pixel shows at once and Classic
    /// fades in.
    private void Open(Request request)
    {
        Present(request);
        Rest();
        _popup.IsOpen = true;
        if (request.Pixel)
        {
            if (request.ReducedMotion) return;
            Play(3 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k == 0) _host.Opacity = 0;
                else if (k < 3)
                {
                    _host.Opacity = 1;
                    Ghost(k / 3.0);
                }
                else Whole();
            }, CompletePresentation);
            return;
        }
        if (request.ReducedMotion)
        {
            Play(83, t => _host.Opacity = (float)Motion.Linear(t / 83), CompletePresentation);
            return;
        }
        Play(250, t => Opening(request, t), CompletePresentation);
    }

    /// Classic's M3 open, ms in: it fades in over 167 ms and grows from 0.9
    /// over 250, drifting in 8 DIP from the bot's side.
    private void Opening(Request request, double ms)
    {
        var drift = request.CharacterRight ? 8.0 : -8.0;
        _host.Opacity = (float)Motion.Linear(ms / 167);
        _host.Scale = Uniform(Lerp(0.9, 1, Motion.SoftPop(ms / 250)));
        _host.Translation = Target + new Vector3((float)Lerp(drift, 0, Motion.Out(ms / 250)), 0, 0);
    }

    /// A card on its way out comes back, from the frame on screen.
    private void Return(Request request)
    {
        if (request.Pixel || request.ReducedMotion)
        {
            CompletePresentation();
            return;
        }
        var opacity = _host.Opacity;
        var scale = _host.Scale.X;
        Play(250, t =>
        {
            _host.Opacity = (float)Lerp(opacity, 1, Motion.Linear(t / 167));
            _host.Scale = Uniform(Lerp(scale, 1, Motion.SoftPop(t / 250)));
        }, CompletePresentation);
    }

    /// M3 switch: the pointer moved to another bot while the card was open.
    /// Pixel swaps in the new card on the first tick and steps its y to the
    /// new bot (35 % of the way left, then there). Classic glides the paper's
    /// top, height and tail to the new bot while the old content fades out
    /// and the new fades in over it. Reduced, it swaps at once.
    private void Switch(Request request)
    {
        _closing = false;
        _refresh = null;
        if (request.ReducedMotion)
        {
            Present(request);
            CompletePresentation();
            return;
        }
        if (request.Pixel)
        {
            var from = _host.Translation.Y;
            var swapped = false;
            Play(2 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k < 1) return;
                if (!swapped)
                {
                    swapped = true;
                    Present(request);
                    Whole();
                    SettleHeight();
                }
                var to = _bounds.Top;
                var y = k >= 2 ? to : to - Motion.Cell((to - from) * 0.35);
                _host.Translation = new Vector3((float)_bounds.Left, (float)y, 0);
                DrawPlate(request, _width, _contentSize.Height, _aim - y);
            }, CompletePresentation);
            return;
        }
        var left = (double)_host.Translation.X;
        var top = (double)_host.Translation.Y;
        var width = _width;
        var height = _plateHeight;
        var aim = _aim;
        var reach = _reach;
        var outgoing = Detach();
        Present(request);
        if (Math.Abs(_width - width) > 0.5)
        {
            SwitchWidth(request, outgoing, new Rect(left, top, width, height), aim - top, reach);
            return;
        }
        Whole();
        _card.Opacity = 0;
        var top1 = _bounds.Top;
        var height1 = _contentSize.Height;
        var aim1 = _aim;
        var reach1 = request.Sections.Count == 1 ? TailReach : 0;
        Play(250, t =>
        {
            var e = Motion.InOut(t / 250);
            var y = Lerp(top, top1, e);
            var h = Lerp(height, height1, e);
            _host.Translation = new Vector3((float)_bounds.Left, (float)y, 0);
            DrawPaper(request, h, Lerp(aim, aim1, e) - y, Lerp(reach, reach1, e));
            _size.InsertScalar("Height", (float)Math.Max(1, h));
            outgoing.Opacity = (float)(1 - Motion.Linear(t / 83));
            _card.Opacity = (float)Motion.Linear((t - 42) / 167);
        }, () =>
        {
            Drop(outgoing);
            CompletePresentation();
        });
    }

    /// M3 switch to a card of another width under Classic (to or from the
    /// all-agents card in columns): the paper can't glide between widths, so
    /// the old card fades out over 83 ms on its own paper while the new one
    /// opens (M3) from 42 ms, the new over the old, and the card never reads
    /// blank. old is the old paper in drawer DIPs, aim and reach its tail's.
    private void SwitchWidth(Request request, Border outgoing, Rect old, double aim, double reach)
    {
        _host.Children.Remove(outgoing);
        _outgoing = null;
        var paper = new PaperPlate();
        paper.Set(old.Width, old.Height, radius: 16, halfBase: 10, reach: reach, pointLeft: !request.CharacterRight,
            tailY: reach > 0.01 ? aim : null, ClassicTokens.Get("Paper", request.Dark), ClassicTokens.Get("Hairline", request.Dark));
        var leaving = new Grid
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Translation = new Vector3((float)old.Left, (float)old.Top, 0),
            Children = { paper, outgoing },
        };
        Canvas.SetLeft(leaving, Canvas.GetLeft(_host));
        Canvas.SetTop(leaving, Canvas.GetTop(_host));
        _stage.Children.Insert(0, leaving);
        Rest();
        Play(42 + 250, t =>
        {
            leaving.Opacity = (float)(1 - Motion.Linear(t / 83));
            Opening(request, t - 42);
        }, CompletePresentation);
        _leaving = leaving;
    }

    // The old card fading out of a switch between widths.
    private Grid? _leaving;

    private void EndLeaving()
    {
        if (_leaving is not { } leaving) return;
        _leaving = null;
        _stage.Children.Remove(leaving);
        foreach (var layer in leaving.Children.OfType<Border>()) layer.Child = null;
        leaving.Children.Clear();
    }

    /// M4 under Classic: the paper glides to the new content's height and
    /// tail over 250 ms on inOut; each text that changed fades out over 83 ms
    /// as its new reading fades in after it over 167, settling 3 DIP, while
    /// its block eases from its old height to its new one with the paper, so
    /// the rows below glide and the bottom padding holds. The meters count by
    /// themselves. Content laid out anew cross-fades whole. Either way, while
    /// the paper glides, the content stays inside it, clear of its padding.
    private void Refresh(Request request)
    {
        var top = (double)_host.Translation.Y;
        var width = _width;
        var height = _plateHeight;
        var aim = _aim;
        var reach = _reach;
        // Read before the old content leaves the card, which resets its sizes.
        var olds = Texts(_body).Select(text => (Text: text, Slot: text.DesiredSize.Height)).ToList();
        var outgoing = Detach();
        _recount = true;
        Present(request);
        _recount = false;
        if (Math.Abs(_width - width) > 0.5)
        {
            // The all-agents card gained or lost a column: it swaps at once.
            Drop(outgoing);
            Rest();
            return;
        }
        Whole();
        var top1 = _bounds.Top;
        var height1 = _contentSize.Height;
        var aim1 = _aim;
        var reach1 = request.Sections.Count == 1 ? TailReach : 0;
        // The old texts against the new, in reading order, with the layout
        // slots (rounded, margins in) of those that changed.
        var news = Texts(_body).ToList();
        var swaps = new List<(FrameworkElement Old, FrameworkElement New, double From, double To, Thickness Margin)>();
        var whole = olds.Count != news.Count;
        if (!whole)
        {
            for (var i = 0; i < olds.Count; i++)
            {
                var (old, fresh) = (olds[i].Text, news[i]);
                if (Said(old) == Said(fresh) || _counting.Contains(fresh)) continue;
                swaps.Add((old, fresh, olds[i].Slot, fresh.DesiredSize.Height, fresh.Margin));
            }
            // A change in height the changed texts don't account for is a new layout.
            whole = Math.Abs(swaps.Sum(swap => swap.To - swap.From) - (height1 - height)) > 1;
        }
        if (whole) _card.Opacity = 0;
        else
        {
            Keep(outgoing, swaps.Select(swap => (DependencyObject)swap.Old).ToHashSet());
            foreach (var swap in swaps)
            {
                swap.New.Opacity = 0;
                _swapping.Add((swap.New, swap.Margin));
            }
        }
        // Room for the taller of the two, so a shrinking card isn't cut to its new height.
        _host.Height = _plate.Height = Math.Max(height, height1);
        var padding = _card.Padding.Bottom;
        Play(250, t =>
        {
            var e = Motion.InOut(t / 250);
            var y = Lerp(top, top1, e);
            var h = Lerp(height, height1, e);
            _host.Translation = new Vector3((float)_bounds.Left, (float)y, 0);
            DrawPaper(request, h, Lerp(aim, aim1, e) - y, Lerp(reach, reach1, e));
            // Both layers end above the paper's bottom padding.
            _size.InsertScalar("TargetHeight", (float)Math.Max(1, whole ? height1 : h));
            _size.InsertScalar("Height", (float)Math.Max(1, h - padding));
            outgoing.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _width, Math.Max(0, h - padding)) };
            var fadeOut = 1 - Motion.Linear(t / 83);
            var fadeIn = Motion.Linear((t - 83) / 167);
            if (whole)
            {
                outgoing.Opacity = fadeOut;
                _card.Opacity = fadeIn;
                return;
            }
            var rise = 3 * (1 - Motion.Out((t - 83) / 167));
            foreach (var (old, fresh, from, to, margin) in swaps)
            {
                old.Opacity = fadeOut;
                fresh.Opacity = fadeIn;
                fresh.Translation = new Vector3(0, (float)rise, 0);
                if (Math.Abs(to - from) < 0.5) continue;
                // The block between the rows eases from the old text's slot
                // to the new's; each text shows only inside it. The shift is
                // whole pixels: off by a hair, a slot on a half pixel rounds
                // the other way and the rows hop a pixel.
                var shift = Math.Round((from + (to - from) * e - to) * request.Scale) / request.Scale;
                fresh.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + shift);
                var block = to + shift - margin.Top - margin.Bottom;
                fresh.Clip = new RectangleGeometry { Rect = new Rect(-8, -rise, 10000, block) };
                old.Clip = new RectangleGeometry { Rect = new Rect(-8, 0, 10000, block) };
            }
        }, () =>
        {
            Drop(outgoing);
            CompletePresentation();
        });
    }

    /// The card's texts in reading order. A flow of phrases is one text:
    /// whatever it says, it swaps whole, its block easing with the paper.
    private static IEnumerable<FrameworkElement> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock or PhraseFlow) yield return (FrameworkElement)child;
            else foreach (var deeper in Texts(child)) yield return deeper;
        }
    }

    private static string Said(FrameworkElement text) => text switch
    {
        TextBlock block => block.Text,
        _ => string.Join("\n", Texts(text).Select(Said)),
    };

    /// The old content's layer keeps only `kept`: everything else is hidden,
    /// since the new content already shows it.
    private static void Keep(DependencyObject element, HashSet<DependencyObject> kept)
    {
        if (kept.Contains(element)) return;
        if (!Holds(element, kept))
        {
            if (element is UIElement shown) shown.Opacity = 0;
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Keep(VisualTreeHelper.GetChild(element, i), kept);
    }

    private static bool Holds(DependencyObject element, HashSet<DependencyObject> kept)
    {
        if (kept.Contains(element)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (Holds(VisualTreeHelper.GetChild(element, i), kept)) return true;
        }
        return false;
    }

    private void Present(Request request)
    {
        _pixelPlate.Visibility = request.Pixel ? Visibility.Visible : Visibility.Collapsed;
        _paper.Visibility = request.Pixel ? Visibility.Collapsed : Visibility.Visible;
        var room = Room(request);
        if (_contentKey != request.Key || _room != room)
        {
            _dark = request.Dark;
            _paperInk = !request.Pixel;
            _card.Padding = request.Pixel ? PixelPadding : PaperPadding;
            using (request.Pixel ? L10n.English() : null) Build(request);
            _card.Width = _host.Width = _width;
            _room = room;
            _builds++;
            _card.Measure(new Size(_width, double.PositiveInfinity));
            _contentSize = _card.DesiredSize;
            _measures++;
            _contentKey = request.Key;
            _host.Height = _contentSize.Height;
            _plate.Height = _contentSize.Height;
            _size.InsertScalar("TargetHeight", (float)Math.Max(1, _contentSize.Height));
        }
        var aim = _aim = request.Anchor.Y + request.Anchor.Height / 2;
        var top = Math.Clamp(aim - _contentSize.Height / 2, request.Area.Top + 6,
            Math.Max(request.Area.Top + 6, request.Area.Bottom - 6 - _contentSize.Height));
        var left = request.CharacterRight ? request.Anchor.Left - Gap - _width : request.Anchor.Right + Gap;
        left = Math.Clamp(left, request.Area.Left + 6, Math.Max(request.Area.Left + 6, request.Area.Right - 6 - _width));
        _bounds = new Rect(Pixels.Snap(left, request.Scale), Pixels.Snap(top, request.Scale), _width, _contentSize.Height);
        _travelTop = Math.Min(_bounds.Top, _popup.IsOpen ? _travelTop : _bounds.Top);
        _travelBottom = Math.Max(_bounds.Bottom, _popup.IsOpen ? _travelBottom : _bounds.Bottom);
        ExtendStage(new Rect(_bounds.Left, _travelTop, _width, _travelBottom - _travelTop), request);
        // Classic grows and shrinks about the tail's tip.
        var tip = request.Sections.Count == 1 && !request.Pixel ? TailReach : 0;
        _host.CenterPoint = new Vector3((float)(request.CharacterRight ? _width + tip : -tip),
            (float)Math.Clamp(aim - _bounds.Top, 0, _bounds.Height), 0);
        _displayed = request;
        DrawRest(request);
    }

    /// The plate at the card's own size, aimed at its bot.
    private void DrawRest(Request request)
    {
        var aim = _aim - _bounds.Top;
        if (request.Pixel) DrawPlate(request, _width, _contentSize.Height, aim);
        else DrawPaper(request, _contentSize.Height, aim, request.Sections.Count == 1 ? TailReach : 0);
        _pixelPlate.RenderTransform = null;
    }

    // The paper as last drawn: its height and tail reach, for a switch.
    private double _plateHeight;
    private double _reach;

    /// One character's card has a tail aimed at it; the all-characters card
    /// points nowhere (no reach). aim is from the plate's top.
    private void DrawPaper(Request request, double height, double aim, double reach)
    {
        _plateHeight = height;
        _reach = reach;
        _paper.Set(_width, height, radius: 16, halfBase: 10, reach: reach, pointLeft: !request.CharacterRight,
            tailY: reach > 0.01 ? aim : null, ClassicTokens.Get("Paper", request.Dark), ClassicTokens.Get("Hairline", request.Dark));
    }

    /// One character's card is outlined in its colour and points at it; the
    /// all-characters card is neutral and points nowhere. aim is from the
    /// plate's top.
    private void DrawPlate(Request request, double width, double height, double aim)
    {
        var single = request.Sections.Count == 1;
        var outline = single
            ? Brand.Rgb(request.Sections[0].Swatch)
            : Brand.Rgb(request.Dark ? 0x5A5B66u : 0x2B2B33u);
        _pixelPlate.Set(width, height, outline, PixelFill(request.Dark),
            pointLeft: !request.CharacterRight, pointerY: single ? aim : null);
    }

    /// A pixel ghost: the plate at a share of the card's size and no content,
    /// grown from the pointer, on the edge facing the bot and centred on it.
    /// Never under three cells a side, so its corners still draw.
    private void Ghost(double share)
    {
        if (_displayed is not { } shown) return;
        _card.Visibility = Visibility.Collapsed;
        var cell = PixelPlate.Cell;
        var width = Math.Max(3 * cell, Motion.Cell(_width * share));
        var height = Math.Max(3 * cell, Motion.Cell(_contentSize.Height * share));
        var aim = _aim - _bounds.Top;
        var x = shown.CharacterRight ? _width - width : 0;
        var y = Math.Clamp(Motion.Cell(aim - height / 2), 0, Math.Max(0, _contentSize.Height - height));
        DrawPlate(shown, width, height, aim - y);
        _pixelPlate.RenderTransform = new TranslateTransform { X = x, Y = y };
    }

    /// The card whole, with its content.
    private void Whole()
    {
        _card.Visibility = Visibility.Visible;
        _host.Opacity = 1;
        if (_displayed is { } shown) DrawRest(shown);
    }

    /// The content as it is, lifted onto a layer of its own to fade out while
    /// new content is built in its place.
    private Border Detach()
    {
        if (_outgoing is { } previous) Drop(previous);
        var old = _body;
        _card.Child = _body = new StackPanel();
        var layer = new Border
        {
            Child = old,
            Width = _width,
            Padding = _card.Padding,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _host.Children.Add(layer);
        _contentKey = null;
        return _outgoing = layer;
    }

    private void Drop(Border layer)
    {
        _host.Children.Remove(layer);
        layer.Child = null;
        if (_outgoing == layer) _outgoing = null;
    }

    /// The pixel panels' paper: warm white, or a deep ink in dark mode.
    internal static Windows.UI.Color PixelFill(bool dark) => Brand.Rgb(dark ? 0x1F2027u : 0xFFFDF7u);

    private void ExtendStage(Rect bounds, Request request)
    {
        var left = Math.Max(request.Area.Left, bounds.Left - MotionMargin);
        var top = Math.Max(request.Area.Top, bounds.Top - MotionMargin);
        var right = Math.Min(request.Area.Right, bounds.Right + MotionMargin);
        var bottom = Math.Max(top + bounds.Height, Math.Min(request.Area.Bottom, bounds.Bottom + MotionMargin));
        if (_stageBounds is { } old)
        {
            left = Math.Min(left, old.Left);
            top = Math.Min(top, old.Top);
            right = Math.Max(right, old.Right);
            bottom = Math.Max(bottom, old.Bottom);
        }
        left = Pixels.Snap(left, request.Scale);
        top = Pixels.Snap(top, request.Scale);
        var stage = new Rect(left, top, Math.Max(_width, right - left), Math.Max(bounds.Height, bottom - top));
        if (_stageBounds == stage) return;
        _stageBounds = stage;
        _stage.Width = stage.Width;
        _stage.Height = stage.Height;
        // Translation remains in drawer coordinates. Moving the stage origin
        // and applying its inverse here preserves an in-flight card's position.
        Canvas.SetLeft(_host, -stage.Left);
        Canvas.SetTop(_host, -stage.Top);
        _popup.HorizontalOffset = stage.Left;
        _popup.VerticalOffset = stage.Top;
    }

    private Vector3 Target => new((float)_bounds.Left, (float)_bounds.Top, 0);

    private void Rest()
    {
        _motion?.Stop();
        _motion = null;
        EndLeaving();
        if (_outgoing is { } layer) Drop(layer);
        // A refresh cut short (a close, a switch) leaves its texts mid-swap;
        // the same content shown again isn't rebuilt.
        foreach (var (text, margin) in _swapping)
        {
            text.Margin = margin;
            text.Clip = null;
            text.Opacity = 1;
            text.Translation = Vector3.Zero;
        }
        _swapping.Clear();
        _host.Height = _plate.Height = _contentSize.Height;
        _host.Opacity = 1;
        _host.Scale = Vector3.One;
        _host.Translation = Target;
        _card.Opacity = 1;
        _card.Visibility = Visibility.Visible;
        if (_displayed is { } shown) DrawRest(shown);
        SettleHeight();
        _travelTop = _bounds.Top;
        _travelBottom = _bounds.Bottom;
    }

    private void Close()
    {
        _generation++;
        _popup.IsOpen = false;
        _phaseBelow.Clear();
        Rest();
        _closing = _animating = false;
        _refresh = null;
        _stageBounds = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _popup.Child = null;
        _root.Children.Remove(_popup);
        ElementCompositionPreview.GetElementVisual(_card).Clip = null;
        _contentClip.Dispose();
        _size.Dispose();
    }

    /// The room the all-agents card has: its height on the screen, and its
    /// width beside the drawer. One character's card needs none.
    private static Size Room(Request request)
    {
        if (request.Sections.Count < 2) return default;
        var beside = request.CharacterRight
            ? request.Anchor.Left - Gap - request.Area.Left
            : request.Area.Right - request.Anchor.Right - Gap;
        return new Size(Math.Max(0, beside - 6), Math.Max(0, request.Area.Height - 12));
    }

    // The room the content was built for.
    private Size _room;

    /// The card's sections, divided. The all-agents card stands in one
    /// column while it fits the screen's height; past it, its sections run
    /// down one column and on to the next, in as few columns as fit, cut so
    /// the tallest is as short as it can be, each column as wide as one
    /// character's card, with a divider between them.
    private void Build(Request request)
    {
        var pixel = request.Pixel;
        _body.Children.Clear();
        _built = new();
        _counting.Clear();
        var padding = pixel ? PixelPadding : PaperPadding;
        var column = (pixel ? PixelCardWidth : CardWidth) - padding.Left - padding.Right;
        var sections = request.Sections.Select(section =>
        {
            var built = Section(section.Card, section.Swatch, pixel);
            if (pixel) ApplyPixelFace(built, PixelPlate.FontSize(request.Scale));
            return built;
        }).ToList();
        var columns = Columns(sections, column, pixel, Room(request));
        _width = padding.Left + columns.Count * column + (columns.Count - 1) * Gutter(pixel) + padding.Right;
        if (columns.Count == 1)
        {
            Stack(_body, sections, column, pixel);
            _shares = _built;
            return;
        }
        var grid = new Grid();
        var tallest = 0.0;
        for (var c = 0; c < columns.Count; c++)
        {
            if (c > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter(pixel)) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column) });
            var stack = new StackPanel();
            Stack(stack, columns[c], column, pixel);
            Grid.SetColumn(stack, 2 * c);
            grid.Children.Add(stack);
            tallest = Math.Max(tallest, Tall(columns[c], pixel));
        }
        for (var c = 1; c < columns.Count; c++)
        {
            var rule = Rule(pixel, tallest);
            Grid.SetColumn(rule, 2 * c - 1);
            grid.Children.Add(rule);
        }
        _body.Children.Add(grid);
        _shares = _built;
    }

    /// The sections in columns, as few as fit the room's height and width,
    /// each run as short as the cut allows; one column if none fits beside
    /// the drawer.
    private static List<List<UIElement>> Columns(List<UIElement> sections, double column, bool pixel, Size room)
    {
        var one = new List<List<UIElement>> { sections };
        if (sections.Count < 2) return one;
        foreach (var section in sections) section.Measure(new Size(column, double.PositiveInfinity));
        var padding = pixel ? PixelPadding : PaperPadding;
        var height = room.Height - padding.Top - padding.Bottom;
        if (Tall(sections, pixel) <= height) return one;
        List<List<UIElement>>? best = null;
        var shortest = double.MaxValue;
        for (var count = 2; count <= sections.Count; count++)
        {
            if (padding.Left + count * column + (count - 1) * Gutter(pixel) + padding.Right > room.Width) break;
            var (cuts, tallest) = Cut(sections, 0, count, pixel);
            if (tallest >= shortest) continue;
            shortest = tallest;
            best = new List<List<UIElement>>();
            var from = 0;
            foreach (var to in cuts.Append(sections.Count))
            {
                best.Add(sections.GetRange(from, to - from));
                from = to;
            }
            if (tallest <= height) break;
        }
        return best ?? one;
    }

    /// Where to cut the sections from `from` on into `count` runs so the
    /// tallest is as short as it can be: the index each later run starts
    /// at. Of equal cuts the earlier runs take more.
    private static (List<int> Cuts, double Tallest) Cut(List<UIElement> sections, int from, int count, bool pixel)
    {
        if (count == 1) return (new List<int>(), Tall(sections.GetRange(from, sections.Count - from), pixel));
        (List<int> Cuts, double Tallest) best = (new List<int>(), double.MaxValue);
        for (var cut = from + 1; cut <= sections.Count - count + 1; cut++)
        {
            var rest = Cut(sections, cut, count - 1, pixel);
            var tallest = Math.Max(Tall(sections.GetRange(from, cut - from), pixel), rest.Tallest);
            if (tallest <= best.Tallest) best = (rest.Cuts.Prepend(cut).ToList(), tallest);
        }
        return best;
    }

    /// A column's height: its measured sections and the dividers between.
    private static double Tall(List<UIElement> sections, bool pixel) =>
        sections.Sum(section => section.DesiredSize.Height) + (sections.Count - 1) * (pixel ? 2 * 9 + PixelPlate.Cell : 2 * 10 + 1);

    /// Between columns: the padding on each side of a divider.
    private static double Gutter(bool pixel) => (pixel ? PixelPadding.Left : PaperPadding.Left) * 2 + (pixel ? PixelPlate.Cell : 1);

    /// Sections one under another, a divider between each two.
    private static void Stack(Panel panel, List<UIElement> sections, double width, bool pixel)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(pixel
                    ? new Line
                    {
                        X2 = width,
                        StrokeThickness = PixelPlate.Cell,
                        StrokeDashArray = new DoubleCollection { 1, 1 },
                        Stroke = Res<Brush>("DividerStrokeColorDefaultBrush"),
                        Margin = new Thickness(0, 9, 0, 9),
                    }
                    : new Rectangle
                    {
                        Height = 1,
                        Fill = Res<Brush>("DividerStrokeColorDefaultBrush"),
                        Margin = new Thickness(0, 10, 0, 10),
                    });
            }
            panel.Children.Add(sections[i]);
        }
    }

    /// The divider between columns, down the tallest: Pixel's dashed a cell
    /// wide on whole cells, Classic's a hairline.
    private static FrameworkElement Rule(bool pixel, double height) => pixel
        ? new Line
        {
            X1 = PixelPlate.Cell / 2,
            X2 = PixelPlate.Cell / 2,
            Y2 = Math.Floor(height / PixelPlate.Cell) * PixelPlate.Cell,
            Width = PixelPlate.Cell,
            StrokeThickness = PixelPlate.Cell,
            StrokeDashArray = new DoubleCollection { 1, 1 },
            Stroke = Res<Brush>("DividerStrokeColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        }
        : new Rectangle
        {
            Width = 1,
            Fill = Res<Brush>("DividerStrokeColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

    /// Every line of a pixel panel in the pixel face, at a size that keeps
    /// its pixels whole. Weights stay regular: the face has no bold, and a
    /// synthetic one smears it; emphasis is the primary ink alone.
    internal static void UsePixelFace(Panel panel, double size)
    {
        foreach (var child in panel.Children) ApplyPixelFace(child, size);
    }

    private static void ApplyPixelFace(UIElement element, double size)
    {
        switch (element)
        {
            case Panel panel:
                UsePixelFace(panel, size);
                break;
            case Border { Child: { } child }:
                ApplyPixelFace(child, size);
                break;
            case TextBlock text:
                text.FontFamily = PixelPlate.Font;
                text.FontSize = size;
                text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                text.CharacterSpacing = 0;
                foreach (var inline in text.Inlines) inline.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                break;
        }
    }

    /// The plan as a small chip in the agent's tint, like the Settings
    /// badge: an 18-DIP pill, square on the pixel panel.
    private static Border PlanChip(string plan, uint swatch, bool pixel)
    {
        var ink = Brand.Rgb(swatch);
        return new Border
        {
            Background = new SolidColorBrush(pixel ? Windows.UI.Color.FromArgb((byte)(_dark ? 0x40 : 0x2E), ink.R, ink.G, ink.B) : ClassicTokens.Tint(ink, _dark)),
            CornerRadius = new CornerRadius(pixel ? 0 : 9),
            Height = pixel ? double.NaN : 18,
            Padding = pixel ? new Thickness(PixelPlate.Cell * 2, 0, PixelPlate.Cell * 2, 0) : new Thickness(7, 0, 7, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = plan,
                FontSize = 10,
                LineHeight = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res<Brush>("TextFillColorPrimaryBrush"),
            },
        };
    }

    private UIElement Section(StatusCard card, uint swatch, bool pixel)
    {
        // Rows indent past the face; the pixel panel keeps whole cells.
        var indent = pixel ? 14.0 : 24.0;
        var labelWidth = pixel ? PixelLabelWidth : LabelWidth;
        var valueWidth = pixel ? PixelValueWidth : ValueWidth;
        var panel = new StackPanel();
        // Marker, name and plan. Pixel follows them with the phase when it
        // fits, else gives it the next line; Classic starts line 2 with it,
        // since English phases rarely fit beside a name and plan at 264 DIP.
        var who = new StackPanel { Orientation = Orientation.Horizontal };
        who.Children.Add(pixel
            ? new Rectangle
            {
                Width = 3 * PixelPlate.Cell,
                Height = 3 * PixelPlate.Cell,
                Fill = Brand.Brush(swatch),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 8, 0),
            }
            : new FaceChip(16, Brand.Rgb(swatch))
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
        who.Children.Add(new TextBlock
        {
            Text = card.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (card.Plan is { Length: > 0 } plan)
        {
            var chip = PlanChip(plan, swatch, pixel);
            chip.Margin = new Thickness(6, 0, 0, 0);
            who.Children.Add(chip);
        }
        // A phrase that breaks carries on about a space in (two whole cells
        // under Pixel), so it reads as one.
        const double carry = 4;
        if (pixel)
        {
            var head = PhraseFlow.Of(new FrameworkElement[] { who, Phrase(card.Phase, "TextFillColorSecondaryBrush", carry, 13) },
                () => Separator("TextFillColorSecondaryBrush", 13), hang: indent);
            head.BreakFirst = _phaseBelow.Contains(card.Provider);
            head.Broke = () => _phaseBelow.Add(card.Provider);
            panel.Children.Add(head);
            if (card.Detail is { } detail)
            {
                panel.Children.Add(Flow(Parts(detail).Select(part => Phrase(part, "TextFillColorTertiaryBrush", carry)), "TextFillColorTertiaryBrush", new Thickness(indent, 1, 0, 0)));
            }
        }
        else
        {
            panel.Children.Add(who);
            // The phase, then the detail's parts; what doesn't fit moves to the next line.
            var status = new List<FrameworkElement> { Phrase(card.Phase, "TextFillColorPrimaryBrush", carry) };
            if (card.Detail is { } detail) status.AddRange(Parts(detail).Select(part => Phrase(part, "TextFillColorSecondaryBrush", carry)));
            panel.Children.Add(Flow(status, "TextFillColorSecondaryBrush", new Thickness(indent, 1, 0, 0)));
        }

        // Fixed label and value columns line the rows up across sections.
        var top = 7.0;
        if (card.Tasks is { } tasks)
        {
            var line = Line(top, indent, labelWidth);
            Place(line, Caption(tasks.Label, "TextFillColorSecondaryBrush"), 0, 0);
            Place(line, Emphasised(tasks.Text, tasks.Number), 0, 1);
            panel.Children.Add(line);
            top = 4;
        }
        if (card.Waiting.Rows.Count > 0)
        {
            panel.Children.Add(WaitingList(card.Waiting, indent, top, pixel, swatch));
            top = 4;
        }
        var meters = new Grid { Margin = new Thickness(indent, top, 0, 0), RowSpacing = 6, ColumnSpacing = 8 };
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
        var row = 0;
        foreach (var meter in card.Meters)
        {
            meters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var warn = meter.Used >= 0.9;
            Place(meters, Caption(meter.Label, "TextFillColorSecondaryBrush"), row, 0);
            FrameworkElement bar = pixel
                ? new PixelBar { VerticalAlignment = VerticalAlignment.Center }
                : new QuotaBar(6) { VerticalAlignment = VerticalAlignment.Center };
            Place(meters, bar, row, 1);
            var value = Caption(meter.Value, warn ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            value.HorizontalAlignment = HorizontalAlignment.Right;
            if (warn && !pixel) value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            Place(meters, value, row, 2);
            Count((IQuotaBar)bar, bar, value, $"{(int)card.Provider}|{meter.Label}", meter, warn, Brand.Rgb(swatch), pixel);
            row++;
        }
        if (row > 0)
        {
            panel.Children.Add(meters);
            top = 4;
        }
        if (card.Tokens is { } tokens)
        {
            var line = new Grid { Margin = new Thickness(indent, top, 0, 0), ColumnSpacing = 8, RowSpacing = 2 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
            line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Caption(tokens.Label, "TextFillColorSecondaryBrush");
            label.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRowSpan(label, 2);
            Place(line, label, 0, 0);
            if (tokens.TodayNumber.Length == 0)
            {
                var pending = Caption(tokens.Today, "TextFillColorTertiaryBrush");
                Grid.SetColumnSpan(pending, 2);
                Place(line, pending, 0, 1);
            }
            else
            {
                Place(line, Caption(L10n.Tr("Today"), "TextFillColorSecondaryBrush"), 0, 1);
                Place(line, Caption(L10n.Tr("This month"), "TextFillColorSecondaryBrush"), 1, 1);
                var today = Emphasised(tokens.TodayNumber, tokens.TodayNumber);
                today.HorizontalAlignment = HorizontalAlignment.Right;
                Place(line, today, 0, 2);
                var month = Emphasised(tokens.MonthNumber, tokens.MonthNumber);
                month.HorizontalAlignment = HorizontalAlignment.Right;
                Place(line, month, 1, 2);
            }
            panel.Children.Add(line);
        }
        if (card.Note.Count > 0)
        {
            // The notes flow, but for one with a lead: its lead on a line,
            // each of its parts on a line under it.
            var notes = new StackPanel { Margin = new Thickness(indent, pixel ? 5 : 6, 0, 0) };
            var run = new List<FrameworkElement>();
            void EndRun()
            {
                if (run.Count > 0) notes.Children.Add(Flow(run, "TextFillColorTertiaryBrush", default));
                run = new List<FrameworkElement>();
            }
            foreach (var note in card.Note)
            {
                var parts = Parts(note.Text).Select(part => Phrase(part, "TextFillColorTertiaryBrush", carry, numbers: note.Numbers));
                if (note.Lead is not { } lead)
                {
                    run.AddRange(parts);
                    continue;
                }
                EndRun();
                notes.Children.Add(Phrase(lead, "TextFillColorTertiaryBrush", carry));
                foreach (var part in parts) notes.Children.Add(part);
            }
            EndRun();
            panel.Children.Add(notes);
        }
        return panel;
    }

    /// The sessions that wait on the user: a title and what it waits for on
    /// each row. A title is the user's own text, so it trims after two lines
    /// rather than flows.
    private static StackPanel WaitingList(StatusWaitingList waiting, double indent, double top, bool pixel, uint swatch)
    {
        var list = new StackPanel { Margin = new Thickness(indent, top, 0, 0), Spacing = 2 };
        foreach (var row in waiting.Rows)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Caption(row.Title, "TextFillColorSecondaryBrush");
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxLines = 2;
            Place(line, title, 0, 0);
            var word = Caption(row.Word, row.Kind == SessionEventKind.Error ? "SystemFillColorCriticalBrush" : "TextFillColorTertiaryBrush");
            if (!pixel)
            {
                // Classic's words are coloured by what they ask for.
                word.Foreground = row.Kind switch
                {
                    SessionEventKind.Approval => ClassicTokens.Brush("Caution", _dark),
                    SessionEventKind.Question => ClassicTokens.Brush("AccentText", _dark),
                    SessionEventKind.Error or SessionEventKind.TaskSkipped => ClassicTokens.Brush("Critical", _dark),
                    _ => ClassicTokens.Brush("Text2", _dark),
                };
                title.Foreground = ClassicTokens.Brush("Text", _dark);
            }
            word.HorizontalAlignment = HorizontalAlignment.Right;
            word.VerticalAlignment = VerticalAlignment.Top;
            Place(line, word, 0, 1);
            list.Children.Add(line);
        }
        if (waiting.More > 0)
        {
            list.Children.Add(Caption(string.Format(L10n.Tr("+{0} more"), waiting.More), pixel ? "TextFillColorSecondaryBrush" : "TextFillColorTertiaryBrush"));
        }
        return list;
    }

    /// A label column, a stretching value and an end-aligned value.
    private static Grid Line(double top, double indent, double labelWidth)
    {
        var line = new Grid { Margin = new Thickness(indent, top, 0, 0), ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return line;
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// A meter's bar and value. A card refreshing in place counts them from
    /// what it showed before (M4), once the new bar is on screen; otherwise
    /// they show their share at once.
    private void Count(IQuotaBar bar, FrameworkElement element, TextBlock value, string key, StatusMeter meter, bool warn, Windows.UI.Color color, bool pixel)
    {
        var share = meter.Shown ?? 0;
        _built[key] = (share, warn);
        if (!_recount || meter.Shown is null || !_shares.TryGetValue(key, out var was) || Math.Abs(was.Share - share) < 0.0005)
        {
            bar.Set(meter.Shown, color, warn, _dark);
            return;
        }
        void Follow(double shown, bool warning)
        {
            value.Text = QuotaCount.Percent(shown, share, meter.Value);
            value.Foreground = Res<Brush>(warning ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            if (!pixel) value.FontWeight = warning ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        bar.Set(was.Share, color, was.Warn, _dark);
        Follow(was.Share, was.Warn);
        _counting.Add(value);
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            element.Loaded -= loaded;
            bar.Set(meter.Shown, color, warn, _dark, Follow);
        };
        element.Loaded += loaded;
    }

    private static TextBlock Caption(string text, string brush, Thickness margin = default) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res<Brush>(brush),
        Margin = margin,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// Details and notes carry warnings (a dropped connection, stale quota):
    /// their phrases flow whole from line to line, never shortened.
    private static PhraseFlow Flow(IEnumerable<FrameworkElement> phrases, string separator, Thickness margin)
    {
        var flow = PhraseFlow.Of(phrases.ToList(), () => Separator(separator));
        flow.Margin = margin;
        return flow;
    }

    /// One phrase as its words, its numbers lifted to primary ink: whole
    /// where a line holds it, else breaking only at its spaces, carrying on
    /// a step in; a word longer than a line breaks inside.
    private static PhraseFlow Phrase(string text, string brush, double carry, double size = 12, IReadOnlyList<string>? numbers = null) =>
        PhraseFlow.Words(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word =>
        {
            var block = new TextBlock { FontSize = size, Foreground = Res<Brush>(brush), TextWrapping = TextWrapping.Wrap };
            Append(block, word, numbers?.Where(word.Contains).ToList() ?? new List<string>());
            return block;
        }), carry);

    private static TextBlock Separator(string brush, double size = 12) => new()
    {
        // Room as margins: a text block measures without trailing spaces.
        Text = "·",
        Margin = new Thickness(4, 0, 4, 0),
        FontSize = size,
        Foreground = Res<Brush>(brush),
    };

    /// A detail's or a note's parts, as they are joined ("Cursor · Bash").
    private static IEnumerable<string> Parts(string detail) =>
        detail.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// A tertiary phrase whose numbers are lifted to primary ink.
    private static TextBlock Emphasised(string phrase, params string[] numbers)
    {
        var text = new TextBlock { FontSize = 12, Foreground = Res<Brush>("TextFillColorTertiaryBrush") };
        Append(text, phrase, numbers);
        return text;
    }

    /// Adds phrase to text, lifting each number, found in order, to primary ink.
    private static void Append(TextBlock text, string phrase, IReadOnlyList<string> numbers)
    {
        var from = 0;
        foreach (var number in numbers)
        {
            var at = phrase.IndexOf(number, from, StringComparison.Ordinal);
            if (at < 0 || number.Length == 0) continue;
            if (at > from) text.Inlines.Add(new Run { Text = phrase[from..at] });
            text.Inlines.Add(new Run
            {
                Text = number,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Res<Brush>("TextFillColorPrimaryBrush"),
            });
            from = at + number.Length;
        }
        if (from < phrase.Length) text.Inlines.Add(new Run { Text = phrase[from..] });
    }

    private void CompletePresentation()
    {
        _animating = false;
        Rest();
        if (_refresh is not { } refresh) return;
        _refresh = null;
        Present(refresh);
        Rest();
    }

    private void SettleHeight()
    {
        _size.InsertScalar("TargetHeight", (float)Math.Max(1, _contentSize.Height));
        _size.InsertScalar("Height", (float)Math.Max(1, _contentSize.Height));
    }

    /// Plays a card moment (M3), replacing any in flight; `completed` runs
    /// when it lands unless another replaces it first. The preview harness
    /// slows it with DebugDurationScale.
    private void Play(double ms, Action<double> frame, Action completed)
    {
        _motion?.Stop();
        EndLeaving();
        _animating = true;
        _transitions++;
        var generation = ++_generation;
        var slow = Math.Clamp(DebugDurationScale, 0.1, 8);
        _motion = UiMoment.Play(ms * slow, t => frame(t / slow), () =>
        {
            if (_disposed || generation != _generation) return;
            _motion = null;
            completed();
        });
    }

    private static Vector3 Uniform(double scale) => new((float)scale, (float)scale, 1);

    private static double Lerp(double from, double to, double share) => from + (to - from) * share;

    /// Fluent's inks, or Classic's on the paper card.
    private static T Res<T>(string key) => _paperInk && Paper(key) is T paper ? paper : Fluent.Resource<T>(key, _dark);

    private static object? Paper(string key) => key switch
    {
        "TextFillColorPrimaryBrush" => ClassicTokens.Brush("Text", _dark),
        "TextFillColorSecondaryBrush" => ClassicTokens.Brush("Text2", _dark),
        "TextFillColorTertiaryBrush" => ClassicTokens.Brush("Text3", _dark),
        "SystemFillColorCriticalBrush" => ClassicTokens.Brush("Critical", _dark),
        "DividerStrokeColorDefaultBrush" => ClassicTokens.Brush("LineSoft", _dark),
        _ => null,
    };
}
