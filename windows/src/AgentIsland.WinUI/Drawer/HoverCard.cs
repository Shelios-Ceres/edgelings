using System.Numerics;
using System.Diagnostics;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// The drawer's hover card: a pane of frosted glass beside the character it
/// describes, each quota window as a filled bar in that character's colour.
/// It is a windowed popup, so it can extend past the drawer's narrow window,
/// and the glass is the system's own acrylic, blurring whatever is behind.
/// With the pixel bots it is a pixel panel instead: an opaque plate on their
/// 2-DIP grid, outlined in the character's colour and pointing at it, with
/// block bars, its English set in a pixel face.
internal sealed class HoverCard : IDisposable
{
    private const double CardWidth = 252;
    // English in the pixel face runs wider than the system font.
    private const double PixelCardWidth = 272;
    private const double PixelLabelWidth = 52;
    private const double PixelValueWidth = 72;
    private const double Gap = 10;
    private const double LabelWidth = 44;
    private const double ValueWidth = 64;
    private static readonly Thickness GlassPadding = new(14, 12, 14, 13);
    private static readonly Thickness PixelPadding = new(16, 14, 16, 14);

    // Motion: the card grows out of the side facing the character and drifts
    // in from it on an ease-out cubic — no overshoot, and gentle enough that
    // the growth reads instead of popping in the first few frames.
    private const float StartScale = 0.95f;
    private const float Drift = 8;
    private const double MotionMargin = 10;
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SwitchDuration = TimeSpan.FromMilliseconds(260);

    private readonly Popup _popup;
    private readonly Canvas _stage = new() { IsHitTestVisible = false };
    private readonly Grid _host = new();
    private readonly Grid _plate = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly SystemBackdropElement _glass = new() { CornerRadius = new CornerRadius(8) };
    private readonly Border _wash = new() { CornerRadius = new CornerRadius(8) };
    private readonly PixelPlate _pixelPlate = new() { Visibility = Visibility.Collapsed };
    private readonly HoverWashBrush _washBrush;
    private readonly Border _card;
    private readonly StackPanel _body = new();
    private readonly Panel _root;
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _easeOut;
    private readonly CompositionPropertySet _size;
    private readonly ExpressionAnimation _plateSizeAnimation;
    private readonly InsetClip _contentClip;
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<string, CompositionAnimation> _animations = new();
    private CompositionScopedBatch? _batch;
    private TypedEventHandler<object, CompositionBatchCompletedEventArgs>? _batchCompleted;
    private EventHandler<object>? _firstFrame;
    private int _generation;
    private bool _closing;
    private bool _animating;
    private bool _disposed;
    private double _width = CardWidth;
    private GlassBackdrop? _backdrop;
    private static bool _dark;
    private Request? _displayed;
    private Request? _refresh;
    private string? _contentKey;
    private Size _contentSize;
    private Rect _bounds;
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

    public HoverCard(Panel root)
    {
        _root = root;
        _card = new Border
        {
            Child = _body,
            Width = CardWidth,
            Padding = GlassPadding,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _host.Width = CardWidth;
        _plate.Children.Add(_glass);
        _plate.Children.Add(_wash);
        _plate.Children.Add(_pixelPlate);
        _host.Children.Add(_plate);
        _host.Children.Add(_card);
        _stage.Children.Add(_host);
        // The element's own Scale/Translation/Opacity are animated, so its
        // composition visual must not be handed off; any compositor will do.
        _compositor = CompositionTarget.GetCompositorForCurrentThread();
        _easeOut = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f));
        _washBrush = new HoverWashBrush(_compositor);
        _wash.Background = _washBrush;
        _size = _compositor.CreatePropertySet();
        _size.InsertScalar("Height", 1);
        _size.InsertScalar("TargetHeight", 1);
        _plateSizeAnimation = _compositor.CreateExpressionAnimation("Vector3(1, size.Height / size.TargetHeight, 1)");
        _plateSizeAnimation.Target = "Scale";
        _plateSizeAnimation.SetReferenceParameter("size", _size);
        _plate.StartAnimation(_plateSizeAnimation);
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
            ShouldConstrainToRootBounds = false,
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
    /// draws the pixel panel instead of the glass.
    /// The transparent popup is a staging area; glass and content move as
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
                AnimateIn(fromHidden: false, move: !request.ReducedMotion);
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

    /// animated fades the card out; otherwise it goes at once (drags, menus,
    /// the drawer hiding).
    public void Hide(bool animated = false)
    {
        if (!_popup.IsOpen) return;
        if (!animated) { Close(); return; }
        if (_closing) return;
        _closing = true;
        _refresh = null;
        Animate(() =>
        {
            Fade(0, ExitDuration);
            if (_displayed?.ReducedMotion == false) Move("Scale", new Vector3(0.97f, 0.97f, 1), ExitDuration);
        }, Close);
    }

    private static bool SameFrame(Request a, Request b) =>
        a.CharacterRight == b.CharacterRight && a.Scale == b.Scale && a.Area == b.Area
        && a.ReducedMotion == b.ReducedMotion;

    private static bool SamePresentation(Request a, Request b) =>
        a.Key == b.Key && a.Anchor == b.Anchor && SameFrame(a, b);

    private void Open(Request request)
    {
        Present(request);
        Rest();
        _host.Opacity = 0;
        if (!request.ReducedMotion)
        {
            _host.Scale = new Vector3(StartScale, StartScale, 1);
            _host.Translation = Target + new Vector3(request.CharacterRight ? Drift : -Drift, 0, 0);
        }
        _popup.IsOpen = true;
        _animating = true;
        var generation = ++_generation;
        _firstFrame = (_, _) =>
        {
            if (_disposed || generation != _generation) return;
            CancelFirstFrame();
            AnimateIn(fromHidden: true, move: !request.ReducedMotion);
        };
        CompositionTarget.Rendering += _firstFrame;
    }

    private void Switch(Request request)
    {
        _closing = false;
        _refresh = null;
        Animate(() =>
        {
            Present(request, animateWash: !request.ReducedMotion);
            // The same pane stays visible. Replacing an animation samples
            // its current value, including when the pointer changes mid-move.
            Fade(1, SwitchDuration);
            Move("Scale", Vector3.One, SwitchDuration);
            if (request.ReducedMotion)
            {
                StopAnimation("Translation");
                _host.Translation = Target;
                SettleHeight();
            }
            else
            {
                Move("Translation", Target, SwitchDuration);
                // A pixel panel snaps to its new height: stretching it would
                // smear its one-cell outline mid-way.
                if (request.Pixel) SettleHeight();
                else MorphHeight(SwitchDuration);
            }
        }, CompletePresentation);
    }

    private void Present(Request request, bool animateWash = false)
    {
        if (request.Pixel)
        {
            _glass.Visibility = _wash.Visibility = Visibility.Collapsed;
            _pixelPlate.Visibility = Visibility.Visible;
        }
        else
        {
            if (_backdrop is null)
            {
                _backdrop = new GlassBackdrop(request.Dark);
                _glass.SystemBackdrop = _backdrop;
            }
            else _backdrop.UpdateTheme(request.Dark);
            _glass.Visibility = _wash.Visibility = Visibility.Visible;
            _pixelPlate.Visibility = Visibility.Collapsed;
        }
        if (_contentKey != request.Key)
        {
            _dark = request.Dark;
            _washBrush.Set(request.Sections.Count == 1 ? request.Sections[0].Swatch : null,
                request.Dark, animateWash ? Duration(SwitchDuration) : TimeSpan.Zero, _easeOut);
            _card.Padding = request.Pixel ? PixelPadding : GlassPadding;
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
        var aim = request.Anchor.Y + request.Anchor.Height / 2;
        var top = Math.Clamp(aim - _contentSize.Height / 2, request.Area.Top + 6,
            Math.Max(request.Area.Top + 6, request.Area.Bottom - 6 - _contentSize.Height));
        var left = request.CharacterRight ? request.Anchor.Left - Gap - _width : request.Anchor.Right + Gap;
        left = Math.Clamp(left, request.Area.Left + 6, Math.Max(request.Area.Left + 6, request.Area.Right - 6 - _width));
        _bounds = new Rect(GlassBackdrop.Snap(left, request.Scale), GlassBackdrop.Snap(top, request.Scale), _width, _contentSize.Height);
        _travelTop = Math.Min(_bounds.Top, _popup.IsOpen ? _travelTop : _bounds.Top);
        _travelBottom = Math.Max(_bounds.Bottom, _popup.IsOpen ? _travelBottom : _bounds.Bottom);
        ExtendStage(new Rect(_bounds.Left, _travelTop, _width, _travelBottom - _travelTop), request);
        _host.CenterPoint = new Vector3(request.CharacterRight ? (float)_width : 0,
            (float)Math.Clamp(aim - _bounds.Top, 0, _bounds.Height), 0);
        if (request.Pixel) DrawPlate(request, aim - _bounds.Top);
        _displayed = request;
    }

    /// One character's card is outlined in its colour and points at it; the
    /// all-characters card is neutral and points nowhere.
    private void DrawPlate(Request request, double aim)
    {
        var single = request.Sections.Count == 1;
        var outline = single
            ? Brand.Rgb(request.Sections[0].Swatch)
            : Brand.Rgb(request.Dark ? 0x5A5B66u : 0x2B2B33u);
        _pixelPlate.Set(_width, _contentSize.Height, outline, PixelFill(request.Dark),
            pointLeft: !request.CharacterRight, pointerY: single ? aim : null);
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
        left = GlassBackdrop.Snap(left, request.Scale);
        top = GlassBackdrop.Snap(top, request.Scale);
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
        StopAnimation("Opacity");
        StopAnimation("Scale");
        StopAnimation("Translation");
        _host.Opacity = 1;
        _host.Scale = Vector3.One;
        _host.Translation = Target;
        SettleHeight();
        _washBrush.Settle();
        _travelTop = _bounds.Top;
        _travelBottom = _bounds.Bottom;
    }

    private void Close()
    {
        CancelCompletion();
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
        _glass.SystemBackdrop = null;
        _popup.Child = null;
        _root.Children.Remove(_popup);
        _backdrop?.Dispose();
        _backdrop = null;
        _wash.Background = null;
        _washBrush.Dispose();
        _plate.StopAnimation(_plateSizeAnimation);
        _plateSizeAnimation.Dispose();
        ElementCompositionPreview.GetElementVisual(_card).Clip = null;
        _contentClip.Dispose();
        _size.Dispose();
        _easeOut.Dispose();
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
                        Margin = new Thickness(0, 9, 0, 9),
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

    /// The plan as a small chip tinted with the agent's colour, like the
    /// Settings badge; square on the pixel panel.
    private static Border PlanChip(string plan, uint swatch, bool pixel)
    {
        var ink = Brand.Rgb(swatch);
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(_dark ? 0x40 : 0x2E), ink.R, ink.G, ink.B)),
            CornerRadius = new CornerRadius(pixel ? 0 : 4),
            Padding = pixel ? new Thickness(PixelPlate.Cell * 2, 0, PixelPlate.Cell * 2, 0) : new Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = plan,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 40,
                Foreground = Res<Brush>("TextFillColorPrimaryBrush"),
            },
        };
    }

    private static UIElement Section(StatusCard card, uint swatch, bool pixel)
    {
        // Rows indent past the colour chip; the pixel panel keeps whole cells.
        var indent = pixel ? 14.0 : 13.0;
        var labelWidth = pixel ? PixelLabelWidth : LabelWidth;
        var valueWidth = pixel ? PixelValueWidth : ValueWidth;
        var panel = new StackPanel();
        // Marker, name and plan, then the phase: only the phase shortens when
        // the line runs long, never the name or the plan.
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
            : new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brand.Brush(swatch),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 6, 0),
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
        Place(head, new TextBlock
        {
            Text = " · " + card.Phase,
            FontSize = 13,
            Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0, 3);
        panel.Children.Add(head);
        if (card.Detail is { } detail)
        {
            panel.Children.Add(Caption(detail, "TextFillColorTertiaryBrush", new Thickness(indent, 1, 0, 0)));
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
        var meters = new Grid { Margin = new Thickness(indent, top, 0, 0), RowSpacing = pixel ? 6 : 4, ColumnSpacing = 8 };
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
                Margin = new Thickness(indent, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            for (var i = 0; i < card.Note.Count; i++)
            {
                if (i > 0) note.Inlines.Add(new Run { Text = " · " });
                Append(note, card.Note[i].Text, card.Note[i].Numbers);
            }
            panel.Children.Add(note);
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

    private void AnimateIn(bool fromHidden, bool move)
    {
        Animate(() =>
        {
            Fade(1, FadeDuration, fromHidden ? 0 : null);
            if (move)
            {
                Move("Scale", Vector3.One, EnterDuration);
                Move("Translation", Target, EnterDuration);
            }
        }, CompletePresentation);
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

    private void Fade(float to, TimeSpan duration, float? from = null)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        if (from is { } start) fade.InsertKeyFrame(0, start);
        else fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, to, _easeOut);
        fade.Duration = Duration(duration);
        StartAnimation(fade);
    }

    private void Move(string property, Vector3 to, TimeSpan duration)
    {
        var move = _compositor.CreateVector3KeyFrameAnimation();
        move.Target = property;
        move.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        move.InsertExpressionKeyFrame(0, "this.StartingValue");
        move.InsertKeyFrame(1, to, _easeOut);
        move.Duration = Duration(duration);
        StartAnimation(move);
    }

    private void MorphHeight(TimeSpan duration)
    {
        using var resize = _compositor.CreateScalarKeyFrameAnimation();
        resize.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        resize.Duration = Duration(duration);
        resize.InsertExpressionKeyFrame(0, "this.StartingValue");
        resize.InsertKeyFrame(1, (float)_contentSize.Height, _easeOut);
        _size.StartAnimation("Height", resize);
    }

    private void SettleHeight()
    {
        _size.StopAnimation("Height");
        _size.InsertScalar("Height", (float)Math.Max(1, _contentSize.Height));
    }

    private void StartAnimation(CompositionAnimation animation)
    {
        _animations.TryGetValue(animation.Target, out var previous);
        try { _host.StartAnimation(animation); }
        catch { animation.Dispose(); throw; }
        _animations[animation.Target] = animation;
        // Starting the replacement samples the current value. Stopping the
        // old animation afterward would also stop the new one on that target.
        previous?.Dispose();
    }

    private void StopAnimation(string property)
    {
        if (!_animations.Remove(property, out var animation)) return;
        _host.StopAnimation(animation);
        animation.Dispose();
    }

    private TimeSpan Duration(TimeSpan duration) =>
        TimeSpan.FromTicks((long)(duration.Ticks * Math.Clamp(DebugDurationScale, 0.1, 8)));

    private void Animate(Action start, Action completed)
    {
        CancelCompletion();
        _animating = true;
        _transitions++;
        var generation = _generation;
        var batch = _batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batchCompleted = (_, _) => _queue.TryEnqueue(() =>
        {
            if (_disposed || generation != _generation || _batch != batch) return;
            CancelCompletion();
            completed();
        });
        batch.Completed += _batchCompleted;
        start();
        batch.End();
    }

    private void CancelCompletion()
    {
        _generation++;
        CancelFirstFrame();
        if (_batch is not null)
        {
            if (_batchCompleted is not null) _batch.Completed -= _batchCompleted;
            _batch.Dispose();
            _batch = null;
            _batchCompleted = null;
        }
    }

    private void CancelFirstFrame()
    {
        if (_firstFrame is null) return;
        CompositionTarget.Rendering -= _firstFrame;
        _firstFrame = null;
    }

    private static T Res<T>(string key) => Fluent.Resource<T>(key, _dark);
}
