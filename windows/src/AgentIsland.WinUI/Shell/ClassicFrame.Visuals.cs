using System.Numerics;
using AgentIsland.WinUI.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// What Classic's frame draws: the opaque body on the desk, clipped to its
/// rounded shape, a one-pixel outline over it, a soft two-layer shadow in the
/// transparent margin, and the three caption buttons.
internal sealed partial class ClassicFrame
{
    private readonly Grid _host = new();
    private readonly Grid _body = new();
    private readonly Border _outline = new() { IsHitTestVisible = false };
    private readonly Rectangle _shadowShape = new();
    private readonly SoftShadow _shadowActive = new() { Kind = "WindowActive" };
    private readonly SoftShadow _shadowInactive = new() { Kind = "WindowInactive", Opacity = 0 };
    private readonly Dictionary<NonClientRegionKind, CaptionCell> _cells = new();
    private Grid? _shadow;
    private UiMoment? _fade;
    private bool _wired;

    /// Wraps the window's content (title bar in row 0, the page in row 1) in
    /// the frame. The host is what the window shows.
    public UIElement Wrap(Grid content)
    {
        _host.Children.Clear();
        _body.Children.Clear();
        _body.Children.Add(content);
        // The shadow sits behind the body, shaped like it; built once, since
        // the frame is wrapped again each time the window's pages are rebuilt.
        if (_shadow is null)
        {
            _shadowShape.Fill = new SolidColorBrush(Microsoft.UI.Colors.Black);
            _shadow = new Grid { IsHitTestVisible = false };
            _shadow.Children.Add(_shadowActive);
            _shadow.Children.Add(_shadowInactive);
            _shadow.Children.Add(_shadowShape);
            _shadowActive.Target = _shadowShape;
            _shadowInactive.Target = _shadowShape;
        }
        _host.Children.Add(_shadow);
        _host.Children.Add(_body);
        _host.Children.Add(_outline);
        _body.SizeChanged += (_, _) => Reshape();
        if (!_wired)
        {
            _wired = true;
            ShapeChanged += Reshape;
            WireButtons();
        }
        Reshape();
        return _host;
    }

    /// The window's frame, in the theme's colours; the theme can change
    /// while the window is open.
    public void Restyle()
    {
        var dark = Brand.IsDark();
        _body.Background = ClassicTokens.Brush("Desk", dark);
        _outline.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb((byte)Math.Round((_active ? 0.12 : 0.08) * 255), 255, 255, 255)
            : Color.FromArgb((byte)Math.Round((_active ? 0.18 : 0.11) * 255), 0x17, 0x19, 0x1F));
        foreach (var cell in _cells.Values) cell.Refresh(dark, _active, _square);
    }

    /// Margin, radius, outline and shadow for the body's current shape: round
    /// with a margin and a shadow, or square and flush when maximized or snapped.
    private void Reshape()
    {
        var square = _square;
        var scale = Scale;
        // The margin is the host's padding: the shadow, the body and the outline
        // all sit inside it.
        _host.Padding = Margin;
        _outline.CornerRadius = new CornerRadius(square ? 0 : Radius);
        _outline.BorderThickness = new Thickness(_square && Maximized ? 0 : 1 / scale);
        _shadowShape.RadiusX = _shadowShape.RadiusY = square ? 0 : Radius;
        _shadowActive.Visibility = _shadowInactive.Visibility = square ? Visibility.Collapsed : Visibility.Visible;
        ClipBody();
        Restyle();
        FadeShadow(_active);
        SetRegions();
    }

    /// The body is clipped to its rounded shape by a composition clip, which
    /// antialiases (XAML's own Clip takes only a plain rectangle).
    private void ClipBody()
    {
        var w = _body.ActualWidth;
        var h = _body.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(_body);
        var compositor = visual.Compositor;
        var r = (float)(_square ? 0 : Radius);
        var shape = compositor.CreateRoundedRectangleGeometry();
        shape.Size = new Vector2((float)w, (float)h);
        shape.CornerRadius = new Vector2(r, r);
        visual.Clip = compositor.CreateGeometricClip(shape);
    }

    /// The shadow crossfades between active and inactive over 167 ms, easing
    /// out; reduced motion switches at once.
    private void FadeShadow(bool active)
    {
        _fade?.Stop();
        var from = _shadowActive.Opacity;
        var to = active ? 1.0 : 0.0;
        if (Math.Abs(from - to) < 0.001) return;
        if (Motion.ReducedMotion)
        {
            _shadowActive.Opacity = to;
            _shadowInactive.Opacity = 1 - to;
            return;
        }
        _fade = UiMoment.Play(167, t =>
        {
            var k = Motion.Out(t / 167);
            _shadowActive.Opacity = from + (to - from) * k;
            _shadowInactive.Opacity = 1 - _shadowActive.Opacity;
        }, () =>
        {
            _shadowActive.Opacity = to;
            _shadowInactive.Opacity = 1 - to;
        });
    }

    /// The three caption cells, 46 x 32 DIP, flush with the body's top-right.
    public FrameworkElement CreateButtons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        foreach (var (kind, glyph) in new[]
                 {
                     (NonClientRegionKind.Minimize, ""),
                     (NonClientRegionKind.Maximize, ""),
                     (NonClientRegionKind.Close, ""),
                 })
        {
            var cell = new CaptionCell(kind, glyph);
            _cells[kind] = cell;
            row.Children.Add(cell);
        }
        Restyle();
        return row;
    }

    private void WireButtons()
    {
        void Over(NonClientPointerEventArgs e, CaptionState state)
        {
            if (!_cells.TryGetValue(e.RegionKind, out var cell)) return;
            _window.DispatcherQueue.TryEnqueue(() => cell.Set(state, Brand.IsDark(), _active));
        }
        NonClient.PointerEntered += (_, e) => Over(e, CaptionState.Hover);
        NonClient.PointerExited += (_, e) => Over(e, CaptionState.Rest);
        NonClient.PointerPressed += (_, e) => Over(e, CaptionState.Pressed);
        NonClient.PointerReleased += (_, e) => Over(e, e.IsPointInRegion ? CaptionState.Hover : CaptionState.Rest);
    }

    /// For the verification films: a caption cell in rest, hover or pressed.
    public void ShowButton(NonClientRegionKind kind, string state)
    {
        if (_cells.TryGetValue(kind, out var cell))
            cell.Set(state switch { "hover" => CaptionState.Hover, "pressed" => CaptionState.Pressed, _ => CaptionState.Rest }, Brand.IsDark(), _active);
    }

    private enum CaptionState
    {
        Rest,
        Hover,
        Pressed,
    }

    /// One caption cell: a pill inset 3 DIP in the cell, so close's pill sits
    /// concentric with the window's corner, and a Segoe Fluent glyph. The pill
    /// fades in over 83 ms and out over 167, and the pill and glyph shrink to
    /// 0.96 while pressed, as Classic's buttons do; reduced motion does neither.
    private sealed class CaptionCell : Grid
    {
        private readonly NonClientRegionKind _kind;
        private readonly Grid _face = new();
        private readonly Border _pill = new() { Margin = new Thickness(3), CornerRadius = new CornerRadius(13), Opacity = 0 };
        private readonly FontIcon _icon = new() { FontSize = 10, FontFamily = new FontFamily("Segoe Fluent Icons"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private CaptionState _state;
        private bool _square;
        private UiMoment? _fade;

        public CaptionCell(NonClientRegionKind kind, string glyph)
        {
            _kind = kind;
            Width = ButtonWidth;
            Height = TitleHeight;
            _icon.Glyph = glyph;
            _face.Children.Add(_pill);
            _face.Children.Add(_icon);
            Children.Add(_face);
        }

        public void Set(CaptionState state, bool dark, bool active)
        {
            var was = _state;
            _state = state;
            Refresh(dark, active, _square);
            if (state == CaptionState.Pressed != (was == CaptionState.Pressed)) ControlMotion.Press(_face, down: state == CaptionState.Pressed);
            var to = state == CaptionState.Rest ? 0.0 : 1.0;
            var from = _pill.Opacity;
            _fade?.Stop();
            if (Math.Abs(from - to) < 0.001) return;
            if (Motion.ReducedMotion)
            {
                _pill.Opacity = to;
                return;
            }
            var length = to > from ? 83.0 : 167.0;
            _fade = UiMoment.Play(length, t => _pill.Opacity = from + (to - from) * Motion.Linear(t / length), () => _pill.Opacity = to);
        }

        public void Refresh(bool dark, bool active, bool square)
        {
            _square = square;
            if (_kind == NonClientRegionKind.Maximize) _icon.Glyph = square ? "" : "";
            var close = _kind == NonClientRegionKind.Close;
            // At rest the pill keeps its last colour while it fades out.
            if (_state != CaptionState.Rest)
            {
                _pill.Background = close
                    ? new SolidColorBrush(_state == CaptionState.Hover ? Color.FromArgb(255, 0xC4, 0x2B, 0x1C) : Color.FromArgb(255, 0xA8, 0x23, 0x17))
                    : new SolidColorBrush(dark
                        ? (_state == CaptionState.Hover ? Color.FromArgb(255, 0x2E, 0x2F, 0x35) : Color.FromArgb(255, 0x26, 0x27, 0x2C))
                        : (_state == CaptionState.Hover ? Color.FromArgb(255, 0xE2, 0xE4, 0xE9) : Color.FromArgb(255, 0xD7, 0xDA, 0xE0)));
            }
            _icon.Foreground = close && _state != CaptionState.Rest
                ? new SolidColorBrush(_state == CaptionState.Pressed ? Color.FromArgb(217, 255, 255, 255) : Microsoft.UI.Colors.White)
                : ClassicTokens.Brush(_state == CaptionState.Pressed ? "Text2" : _state == CaptionState.Hover || active ? "Text" : "Text3", dark);
        }
    }
}
