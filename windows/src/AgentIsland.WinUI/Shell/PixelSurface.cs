using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AgentIsland.WinUI.Shell;

public enum PixelShape
{
    /// Cards, menus, dialogs: corners stepped two cells outside, one inside.
    Surface,

    /// Buttons, fields, toggles: only the outline's corner cell is notched.
    Control,
}

/// The pixel kit's frame, drawn behind a control's content on 2-DIP cells: a
/// one-cell outline around a flat fill, an optional ledge along the inside
/// bottom, and an optional hard shadow one cell right and down (the plate
/// then leaves that cell free). Templates set its brushes per state.
public sealed class PixelSurface : Grid
{
    private const double C = PixelPlate.Cell;

    public static readonly DependencyProperty FillProperty = Brush(nameof(Fill), 2);
    public static readonly DependencyProperty OutlineProperty = Brush(nameof(Outline), 1);
    public static readonly DependencyProperty LedgeProperty = Brush(nameof(Ledge), 3);
    public static readonly DependencyProperty HardShadowProperty = Brush(nameof(HardShadow), 0);
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(PixelShape), typeof(PixelSurface), new PropertyMetadata(PixelShape.Surface, (d, _) => ((PixelSurface)d).Draw()));
    public static readonly DependencyProperty OutlineOnlyProperty = DependencyProperty.Register(
        nameof(OutlineOnly), typeof(bool), typeof(PixelSurface), new PropertyMetadata(false, (d, _) => ((PixelSurface)d).Draw()));

    private readonly Path[] _layers = { new(), new(), new(), new() };

    public PixelSurface()
    {
        IsHitTestVisible = false;
        foreach (var layer in _layers) Children.Add(layer);
        SizeChanged += (_, _) => Draw();
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Outline
    {
        get => (Brush?)GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    /// Null draws no ledge.
    public Brush? Ledge
    {
        get => (Brush?)GetValue(LedgeProperty);
        set => SetValue(LedgeProperty, value);
    }

    /// Null draws no shadow, and the plate fills the whole element.
    public Brush? HardShadow
    {
        get => (Brush?)GetValue(HardShadowProperty);
        set => SetValue(HardShadowProperty, value);
    }

    public PixelShape Shape
    {
        get => (PixelShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    /// Only the outline's ring, to lie over content that fills the plate
    /// (a dialog's footer band) without covering its stepped corners.
    public bool OutlineOnly
    {
        get => (bool)GetValue(OutlineOnlyProperty);
        set => SetValue(OutlineOnlyProperty, value);
    }

    /// A backdrop takes the room its control gives it and asks for none: its
    /// paths measure to the last drawing, which would hold a card whose
    /// content shrank (a collapsed expander) at its old height.
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(0, 0);
    }

    private static DependencyProperty Brush(string name, int layer) => DependencyProperty.Register(
        name, typeof(Brush), typeof(PixelSurface), new PropertyMetadata(null, (d, e) =>
        {
            var surface = (PixelSurface)d;
            surface._layers[layer].Fill = (Brush?)e.NewValue;
            // A shadow appearing or leaving changes the plate's size; a ledge
            // appearing (a button enabled after its first layout) needs its shape.
            if (layer is 0 or 3 && (e.OldValue is null) != (e.NewValue is null)) surface.Draw();
        }));

    private void Draw()
    {
        foreach (var layer in _layers) layer.Data = null;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 4 * C || height < 4 * C) return;
        // A cell is 3 px at 150 %; at 125 and 175 % it alternates 2 and 3 px
        // rather than blurring across a pixel.
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var c = Math.Round(C * scale) / scale;
        var shadowed = HardShadow is not null;
        var w = shadowed ? width - c : width;
        var h = shadowed ? height - c : height;
        if (shadowed) _layers[0].Data = Outer(c, c, w, h, c);
        Microsoft.UI.Xaml.Media.Geometry Inner() => Shape == PixelShape.Surface
            ? Geometry(Stepped(c, c, w - 2 * c, h - 2 * c, 1, c))
            : new RectangleGeometry { Rect = new Rect(c, c, w - 2 * c, h - 2 * c) };
        if (OutlineOnly)
        {
            _layers[1].Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { Outer(0, 0, w, h, c), Inner() } };
            return;
        }
        _layers[1].Data = Outer(0, 0, w, h, c);
        _layers[2].Data = Inner();
        if (Ledge is not null) _layers[3].Data = new RectangleGeometry { Rect = new Rect(c, h - 2 * c, w - 2 * c, c) };
    }

    private PathGeometry Outer(double left, double top, double w, double h, double c) => Shape == PixelShape.Surface
        ? Geometry(Stepped(left, top, w, h, 2, c))
        : Geometry(Notched(left, top, w, h, c));

    /// PixelPlate's staircase corners, with the cell measured in this
    /// window's whole pixels.
    private static PathFigure Stepped(double left, double top, double w, double h, int steps, double c)
    {
        var points = new List<Point>();
        var right = left + w;
        var bottom = top + h;
        for (var i = 0; i < steps; i++)
        {
            points.Add(new Point(right - (steps - i) * c, top + i * c));
            points.Add(new Point(right - (steps - i) * c, top + (i + 1) * c));
        }
        points.Add(new Point(right, top + steps * c));
        points.Add(new Point(right, bottom - steps * c));
        for (var i = 0; i < steps; i++)
        {
            points.Add(new Point(right - (i + 1) * c, bottom - (steps - i) * c));
            points.Add(new Point(right - (i + 1) * c, bottom - (steps - i - 1) * c));
        }
        for (var i = 0; i < steps; i++)
        {
            points.Add(new Point(left + (steps - i) * c, bottom - i * c));
            points.Add(new Point(left + (steps - i) * c, bottom - (i + 1) * c));
        }
        points.Add(new Point(left, bottom - steps * c));
        points.Add(new Point(left, top + steps * c));
        for (var i = 0; i < steps; i++)
        {
            points.Add(new Point(left + (i + 1) * c, top + (steps - i) * c));
            points.Add(new Point(left + (i + 1) * c, top + (steps - i - 1) * c));
        }
        return PixelPlate.Figure(points);
    }

    /// A rectangle without its four corner cells.
    private static PathFigure Notched(double left, double top, double w, double h, double c)
    {
        var right = left + w;
        var bottom = top + h;
        return PixelPlate.Figure(new List<Point>
        {
            new(left + c, top), new(right - c, top), new(right - c, top + c), new(right, top + c),
            new(right, bottom - c), new(right - c, bottom - c), new(right - c, bottom), new(left + c, bottom),
            new(left + c, bottom - c), new(left, bottom - c), new(left, top + c), new(left + c, top + c),
        });
    }

    private static PathGeometry Geometry(PathFigure figure)
    {
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
