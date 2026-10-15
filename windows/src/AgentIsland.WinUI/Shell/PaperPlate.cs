using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AgentIsland.WinUI.Shell;

/// Classic's paper for the drawer's hover card and speech bubbles: one path,
/// a rounded rectangle with a soft tail toward the character, filled with
/// paper, edged with a hairline and lifted by a soft shadow that follows the
/// tail. The tail reaches past the plate's side, so the parent leaves room
/// for it.
internal sealed class PaperPlate : Grid
{
    private readonly Path _path = new() { StrokeThickness = 1 };
    private readonly SoftShadow _shadow = new() { Kind = "Paper" };

    public PaperPlate()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_shadow);
        Children.Add(_path);
        _shadow.Target = _path;
    }

    /// width and height are the plate's without the tail; the tail sits on
    /// the left side when pointLeft, else the right, centred on tailY from
    /// the top (null: no tail). radius gives way on a short plate so the
    /// tail's base always fits between the corners.
    public void Set(double width, double height, double radius, double halfBase, double reach, bool pointLeft, double? tailY, Color fill, Color edge)
    {
        var tail = tailY is not null;
        var room = tail ? reach + 1 : 0;
        Width = width + room;
        Height = height;
        Margin = new Thickness(pointLeft ? -room : 0, 0, pointLeft ? 0 : -room, 0);
        if (tail) radius = Math.Min(radius, (height - 2 * halfBase) / 2);
        radius = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
        _path.Fill = new SolidColorBrush(fill);
        _path.Stroke = new SolidColorBrush(edge);
        // A hairline centred on whole pixels: the outline runs half a DIP in.
        var left = (pointLeft ? room : 0) + 0.5;
        var right = left + width - 1;
        double top = 0.5, bottom = height - 0.5;
        var y = tailY is { } aim ? Math.Clamp(aim, radius + halfBase, height - radius - halfBase) : 0;
        _path.Data = Geometry(left, top, right, bottom, radius, halfBase, reach, pointLeft, tail ? y : null);
    }

    private static PathGeometry Geometry(double left, double top, double right, double bottom, double r, double b, double d, bool pointLeft, double? y)
    {
        var figure = new PathFigure { StartPoint = new Point(left + r, top), IsClosed = true, IsFilled = true };
        var segments = figure.Segments;
        void Line(double x, double yy) => segments.Add(new LineSegment { Point = new Point(x, yy) });
        void Arc(double x, double yy) => segments.Add(new ArcSegment { Point = new Point(x, yy), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
        void Curve(double x1, double y1, double x2, double y2, double x3, double y3) =>
            segments.Add(new BezierSegment { Point1 = new Point(x1, y1), Point2 = new Point(x2, y2), Point3 = new Point(x3, y3) });

        Line(right - r, top);
        Arc(right, top + r);
        if (y is { } down && !pointLeft)
        {
            // The handoff's tail: out to a soft round tip and back.
            Line(right, down - b);
            Curve(right + 0.28 * d, down - 0.60 * b, right + 0.72 * d, down - 0.24 * b, right + 0.98 * d, down - 0.09 * b);
            Curve(right + 1.07 * d, down - 0.04 * b, right + 1.07 * d, down + 0.04 * b, right + 0.98 * d, down + 0.09 * b);
            Curve(right + 0.72 * d, down + 0.24 * b, right + 0.28 * d, down + 0.60 * b, right, down + b);
        }
        Line(right, bottom - r);
        Arc(right - r, bottom);
        Line(left + r, bottom);
        Arc(left, bottom - r);
        if (y is { } up && pointLeft)
        {
            Line(left, up + b);
            Curve(left - 0.28 * d, up + 0.60 * b, left - 0.72 * d, up + 0.24 * b, left - 0.98 * d, up + 0.09 * b);
            Curve(left - 1.07 * d, up + 0.04 * b, left - 1.07 * d, up - 0.04 * b, left - 0.98 * d, up - 0.09 * b);
            Curve(left - 0.72 * d, up - 0.24 * b, left - 0.28 * d, up - 0.60 * b, left, up - b);
        }
        Line(left, top + r);
        Arc(left + r, top);
        return new PathGeometry { Figures = { figure } };
    }
}
