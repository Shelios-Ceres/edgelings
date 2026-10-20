using System.Numerics;
using System.Diagnostics;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// The drawer's hover card beside the character it describes, each quota
/// window as a filled bar in that character's colour. It is a windowed
/// popup, so it can extend past the drawer's narrow window. With the classic
/// characters it is paper: an opaque pane with a soft tail aimed at the
/// character and a soft shadow. With the pixel bots it is a pixel panel: an
/// opaque plate on their 2-DIP grid, outlined in the character's colour and
/// pointing at it, with block bars, its English set in a pixel face.
internal sealed partial class HoverCard : IDisposable
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
    public void Show(IReadOnlyList<(StatusCard Card, uint Swatch)> sections, bool pixel, bool characterRight, Rect anchor, Rect area, bool reducedMotion)
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
}
