using System.Numerics;
using System.Diagnostics;
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
            Present(request);
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
        var drift = request.CharacterRight ? 8.0 : -8.0;
        Play(250, t =>
        {
            _host.Opacity = (float)Motion.Linear(t / 167);
            _host.Scale = Uniform(Lerp(0.9, 1, Motion.SoftPop(t / 250)));
            _host.Translation = Target + new Vector3((float)Lerp(drift, 0, Motion.Out(t / 250)), 0, 0);
        }, CompletePresentation);
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
                }
                var to = _bounds.Top;
                var y = k >= 2 ? to : to - Motion.Cell((to - from) * 0.35);
                _host.Translation = new Vector3((float)_bounds.Left, (float)y, 0);
                DrawPlate(request, _width, _contentSize.Height, _aim - y);
            }, CompletePresentation);
            return;
        }
        var top = (double)_host.Translation.Y;
        var height = _plateHeight;
        var aim = _aim;
        var reach = _reach;
        var outgoing = Detach();
        Present(request);
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

    private void Present(Request request)
    {
        _pixelPlate.Visibility = request.Pixel ? Visibility.Visible : Visibility.Collapsed;
        _paper.Visibility = request.Pixel ? Visibility.Collapsed : Visibility.Visible;
        if (_contentKey != request.Key)
        {
            _dark = request.Dark;
            _paperInk = !request.Pixel;
            _card.Padding = request.Pixel ? PixelPadding : PaperPadding;
            _width = request.Pixel ? PixelCardWidth : CardWidth;
            _card.Width = _host.Width = _width;
            using (request.Pixel ? L10n.English() : null) Build(request.Sections, request.Pixel);
            if (request.Pixel) UsePixelFace(_body, PixelPlate.FontSize(request.Scale));
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
        if (_outgoing is { } layer) Drop(layer);
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

    private void Build(IReadOnlyList<(StatusCard Card, uint Swatch)> sections, bool pixel)
    {
        _body.Children.Clear();
        for (var i = 0; i < sections.Count; i++)
        {
            var (card, swatch) = sections[i];
            if (i > 0)
            {
                _body.Children.Add(pixel
                    ? new Line
                    {
                        X2 = _width - PixelPadding.Left - PixelPadding.Right,
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
            _body.Children.Add(Section(card, swatch, pixel));
        }
    }

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

    private static UIElement Section(StatusCard card, uint swatch, bool pixel)
    {
        // Rows indent past the face; the pixel panel keeps whole cells.
        var indent = pixel ? 14.0 : 24.0;
        var labelWidth = pixel ? PixelLabelWidth : LabelWidth;
        var valueWidth = pixel ? PixelValueWidth : ValueWidth;
        var panel = new StackPanel();
        // Marker, name and plan. Pixel puts the phase after them, and only
        // the phase shortens; Classic starts line 2 with it, since English
        // phases rarely fit beside a name and plan at 264 DIP.
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(head, pixel
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
            }, 0, 0);
        Place(head, new TextBlock
        {
            Text = card.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0, 1);
        if (card.Plan is { Length: > 0 } plan)
        {
            var chip = PlanChip(plan, swatch, pixel);
            chip.Margin = new Thickness(6, 0, 0, 0);
            Place(head, chip, 0, 2);
        }
        if (pixel)
        {
            Place(head, new TextBlock
            {
                Text = " · " + card.Phase,
                FontSize = 13,
                Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }, 0, 3);
        }
        panel.Children.Add(head);
        if (!pixel)
        {
            // The phase, then the detail, which is what shortens.
            var status = Caption(card.Phase, "TextFillColorPrimaryBrush", new Thickness(indent, 1, 0, 0));
            if (card.Detail is { } line)
            {
                status.Inlines.Add(new Run { Text = " · " + line, Foreground = Res<Brush>("TextFillColorSecondaryBrush") });
            }
            panel.Children.Add(TwoLines(status));
        }
        else if (card.Detail is { } detail)
        {
            panel.Children.Add(TwoLines(Caption(detail, "TextFillColorTertiaryBrush", new Thickness(indent, 1, 0, 0))));
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
            if (pixel)
            {
                var blocks = new PixelBar { VerticalAlignment = VerticalAlignment.Center };
                blocks.Set(meter.Shown, Brand.Rgb(swatch), warn, _dark);
                Place(meters, blocks, row, 1);
            }
            else
            {
                var bar = new QuotaBar(6) { VerticalAlignment = VerticalAlignment.Center };
                bar.Set(meter.Shown, Brand.Rgb(swatch), warn, _dark);
                Place(meters, bar, row, 1);
            }
            var value = Caption(meter.Value, warn ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            value.HorizontalAlignment = HorizontalAlignment.Right;
            if (warn && !pixel) value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            Place(meters, value, row, 2);
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
            var note = new TextBlock
            {
                FontSize = 12,
                Foreground = Res<Brush>("TextFillColorTertiaryBrush"),
                Margin = new Thickness(indent, pixel ? 5 : 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            for (var i = 0; i < card.Note.Count; i++)
            {
                if (i > 0) note.Inlines.Add(new Run { Text = " · " });
                Append(note, card.Note[i].Text, card.Note[i].Numbers);
            }
            panel.Children.Add(TwoLines(note));
            // Classic notes get a third line before they shorten.
            if (!pixel) note.MaxLines = 3;
        }
        return panel;
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

    private static TextBlock Caption(string text, string brush, Thickness margin = default) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res<Brush>(brush),
        Margin = margin,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// Details and notes carry warnings (a dropped connection, stale quota),
    /// so they get a second line before they shorten.
    private static TextBlock TwoLines(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxLines = 2;
        return text;
    }

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
        _size.InsertScalar("Height", (float)Math.Max(1, _contentSize.Height));
    }

    /// Plays a card moment (M3), replacing any in flight; `completed` runs
    /// when it lands unless another replaces it first. The preview harness
    /// slows it with DebugDurationScale.
    private void Play(double ms, Action<double> frame, Action completed)
    {
        _motion?.Stop();
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
