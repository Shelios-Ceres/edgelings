using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace AgentIsland.UI.Drawer;

/// Draws the small SVG subset the Grok Bot engine exports, straight into a
/// WPF DrawingContext. Verified subset across all 25 shapes x 53 states:
/// svg, defs, g, path (M L C A Z), circle, use, clipPath, mask; transform
/// (translate/scale/rotate/matrix), opacity, fill, fill-rule, stroke,
/// stroke-width, stroke-dasharray, stroke-linecap/linejoin. The engine's masks
/// are always a white-filled copy of the body, so they clip to that geometry.
public sealed partial class SvgRenderer
{
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);

    public void Render(string svg, DrawingContext dc)
    {
        var root = XElement.Parse(svg);
        var frame = new Frame();
        foreach (var element in root.Descendants())
        {
            if (element.Attribute("id")?.Value is { } id) frame.ById[id] = element;
        }
        foreach (var child in root.Elements()) Draw(child, dc, frame, Paint.Default);
    }

    private sealed class Frame
    {
        public Dictionary<string, XElement> ById { get; } = new();
        public Dictionary<XElement, Geometry> Shapes { get; } = new();
        public Dictionary<string, Geometry> Clips { get; } = new();
    }

    /// Inherited presentation properties (SVG inherits these; opacity is not
    /// inherited and is pushed per element instead).
    private readonly record struct Paint(
        string Fill, string FillRule, string? Stroke, double StrokeWidth,
        string? Dash, string? Cap, string? Join)
    {
        public static Paint Default => new("#000", "nonzero", null, 1, null, null, null);

        public Paint With(XElement e) => new(
            Attr(e, "fill") ?? Fill,
            Attr(e, "fill-rule") ?? FillRule,
            Attr(e, "stroke") ?? Stroke,
            Attr(e, "stroke-width") is { } w ? Number(w) : StrokeWidth,
            Attr(e, "stroke-dasharray") ?? Dash,
            Attr(e, "stroke-linecap") ?? Cap,
            Attr(e, "stroke-linejoin") ?? Join);
    }

    private void Draw(XElement e, DrawingContext dc, Frame frame, Paint inherited)
    {
        var name = e.Name.LocalName;
        if (name is "defs" or "clipPath" or "mask") return;

        var paint = inherited.With(e);
        var pushes = Push(e, dc, frame);
        switch (name)
        {
            case "g":
                foreach (var child in e.Elements()) Draw(child, dc, frame, paint);
                break;
            case "use":
                if (Reference(e, frame) is { } target) Draw(target, dc, frame, paint);
                break;
            case "path":
            case "circle":
                DrawShape(dc, Shape(e, frame, paint.FillRule), paint);
                break;
        }
        for (var i = 0; i < pushes; i++) dc.Pop();
    }

    /// Transform first: clip-path and mask live in the element's own user space.
    private int Push(XElement e, DrawingContext dc, Frame frame)
    {
        var pushes = 0;
        if (Attr(e, "transform") is { } transform)
        {
            dc.PushTransform(new MatrixTransform(ParseTransform(transform)));
            pushes++;
        }
        if (Attr(e, "opacity") is { } opacity && Number(opacity) < 1)
        {
            dc.PushOpacity(Number(opacity));
            pushes++;
        }
        foreach (var attribute in new[] { "clip-path", "mask" })
        {
            if (Attr(e, attribute) is { } url && Clip(UrlId(url), frame) is { } clip)
            {
                dc.PushClip(clip);
                pushes++;
            }
        }
        return pushes;
    }

    private void DrawShape(DrawingContext dc, Geometry geometry, Paint paint)
    {
        var fill = paint.Fill == "none" ? null : Brush(paint.Fill);
        Pen? pen = null;
        if (paint.Stroke is { } stroke && stroke != "none" && paint.StrokeWidth > 0)
        {
            var round = paint.Cap == "round";
            pen = new Pen(Brush(stroke), paint.StrokeWidth)
            {
                StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                DashCap = round ? PenLineCap.Round : PenLineCap.Flat,
                LineJoin = paint.Join == "round" ? PenLineJoin.Round : PenLineJoin.Miter,
            };
            if (paint.Dash is { } dash && dash != "none")
            {
                // SVG dashes are user units; WPF dashes are multiples of the pen width.
                var dashes = Numbers(dash).Select(v => v / paint.StrokeWidth);
                pen.DashStyle = new DashStyle(dashes, 0);
            }
            pen.Freeze();
        }
        dc.DrawGeometry(fill, pen, geometry);
    }

    private Geometry Shape(XElement e, Frame frame, string fillRule)
    {
        if (frame.Shapes.TryGetValue(e, out var cached)) return cached;
        Geometry geometry;
        if (e.Name.LocalName == "circle")
        {
            var r = Number(Attr(e, "r") ?? "0");
            geometry = new EllipseGeometry(new Point(Number(Attr(e, "cx") ?? "0"), Number(Attr(e, "cy") ?? "0")), r, r);
            geometry.Freeze();
        }
        else
        {
            // The engine memoises body contours by rotation, so calm bots
            // repeat identical path strings: reuse the parsed geometry too.
            var key = (fillRule == "evenodd" ? "E" : "N") + (Attr(e, "d") ?? "");
            if (!_paths.TryGetValue(key, out geometry!))
            {
                if (_paths.Count >= PathCacheLimit) _paths.Clear();
                geometry = SvgPath.Parse(key[1..], fillRule == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero);
                _paths[key] = geometry;
            }
        }
        frame.Shapes[e] = geometry;
        return geometry;
    }

    private const int PathCacheLimit = 256;
    private readonly Dictionary<string, Geometry> _paths = new();

    /// A clipPath or mask flattened to one clip geometry in referencing space.
    private Geometry? Clip(string id, Frame frame)
    {
        if (frame.Clips.TryGetValue(id, out var cached)) return cached;
        if (!frame.ById.TryGetValue(id, out var element)) return null;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var child in element.Elements()) Collect(child, frame, Matrix.Identity, group);
        group.Freeze();
        frame.Clips[id] = group;
        return group;
    }

    private void Collect(XElement e, Frame frame, Matrix parent, GeometryGroup into)
    {
        var matrix = parent;
        if (Attr(e, "transform") is { } transform) matrix = ParseTransform(transform) * parent;
        switch (e.Name.LocalName)
        {
            case "g":
                foreach (var child in e.Elements()) Collect(child, frame, matrix, into);
                break;
            case "use":
                if (Reference(e, frame) is { } target) Collect(target, frame, matrix, into);
                break;
            case "path":
            case "circle":
                var shape = Shape(e, frame, Attr(e, "fill-rule") ?? "nonzero");
                if (!matrix.IsIdentity)
                {
                    shape = shape.CloneCurrentValue();
                    shape.Transform = new MatrixTransform(matrix);
                }
                into.Children.Add(shape);
                break;
        }
    }

    private static XElement? Reference(XElement use, Frame frame)
    {
        var href = Attr(use, "href") ?? use.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))?.Value;
        return href is not null && href.StartsWith('#') && frame.ById.TryGetValue(href[1..], out var target)
            ? target
            : null;
    }

    private Brush Brush(string value)
    {
        if (_brushes.TryGetValue(value, out var brush)) return brush;
        var color = value.StartsWith('#') ? Hex(value) : (Color)ColorConverter.ConvertFromString(value);
        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[value] = brush;
        return brush;
    }

    private static Color Hex(string value)
    {
        var hex = value[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        var rgb = Convert.ToInt32(hex, 16);
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// SVG order "A B C" applies C first; WPF row vectors therefore prepend.
    private static Matrix ParseTransform(string value)
    {
        var result = Matrix.Identity;
        foreach (Match match in TransformPattern().Matches(value))
        {
            var args = Numbers(match.Groups[2].Value).ToArray();
            var step = Matrix.Identity;
            switch (match.Groups[1].Value)
            {
                case "translate":
                    step.Translate(args[0], args.Length > 1 ? args[1] : 0);
                    break;
                case "scale":
                    step.Scale(args[0], args.Length > 1 ? args[1] : args[0]);
                    break;
                case "rotate":
                    if (args.Length >= 3) step.RotateAt(args[0], args[1], args[2]);
                    else step.Rotate(args[0]);
                    break;
                case "matrix":
                    step = new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]);
                    break;
            }
            result.Prepend(step);
        }
        return result;
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static string UrlId(string url) => url.Trim()[5..^1];

    private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static IEnumerable<double> Numbers(string value) =>
        value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(Number);

    [GeneratedRegex(@"(\w+)\s*\(([^)]*)\)")]
    private static partial Regex TransformPattern();
}

/// Absolute-command SVG path data (M L H V C A Z) to a frozen StreamGeometry,
/// the lightweight geometry WPF provides for exactly this kind of churn.
internal static class SvgPath
{
    public static Geometry Parse(string data, FillRule fillRule)
    {
        var geometry = new StreamGeometry { FillRule = fillRule };
        using (var ctx = geometry.Open())
        {
            var reader = new Reader(data);
            var open = false;
            var start = new Point();
            var current = new Point();
            var command = ' ';

            while (reader.More())
            {
                if (reader.AtCommand()) command = reader.Command();
                else if (command == 'M') command = 'L'; // implicit lineto after moveto

                if (command == 'Z')
                {
                    open = false;
                    current = start;
                    continue;
                }

                if (command == 'M')
                {
                    current = start = reader.Point();
                    ctx.BeginFigure(start, true, reader.FigureCloses());
                    open = true;
                    continue;
                }

                if (!open)
                {
                    // Drawing after Z without a moveto restarts at the subpath start.
                    start = current;
                    ctx.BeginFigure(start, true, reader.FigureCloses());
                    open = true;
                }
                switch (command)
                {
                    case 'L':
                        current = reader.Point();
                        ctx.LineTo(current, true, false);
                        break;
                    case 'H':
                        current = new Point(reader.Number(), current.Y);
                        ctx.LineTo(current, true, false);
                        break;
                    case 'V':
                        current = new Point(current.X, reader.Number());
                        ctx.LineTo(current, true, false);
                        break;
                    case 'C':
                        var c1 = reader.Point();
                        var c2 = reader.Point();
                        current = reader.Point();
                        ctx.BezierTo(c1, c2, current, true, false);
                        break;
                    case 'A':
                        var rx = Math.Abs(reader.Number());
                        var ry = Math.Abs(reader.Number());
                        var rotation = reader.Number();
                        var large = reader.Flag();
                        var sweep = reader.Flag();
                        current = reader.Point();
                        if (rx == 0 || ry == 0) ctx.LineTo(current, true, false);
                        else ctx.ArcTo(current, new Size(rx, ry), rotation, large,
                            sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
                        break;
                    default:
                        throw new FormatException($"Unsupported SVG path command '{command}'.");
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<char> _s;
        private int _i;

        public Reader(string s)
        {
            _s = s;
            _i = 0;
        }

        /// StreamGeometry fixes "closed" when a figure begins: look ahead for a
        /// Z before the next moveto.
        public bool FigureCloses()
        {
            for (var j = _i; j < _s.Length; j++)
            {
                if (_s[j] is 'Z' or 'z') return true;
                if (_s[j] is 'M' or 'm') return false;
            }
            return false;
        }

        public bool More()
        {
            Skip();
            return _i < _s.Length;
        }

        public bool AtCommand()
        {
            Skip();
            return _i < _s.Length && char.IsLetter(_s[_i]) && _s[_i] is not ('e' or 'E');
        }

        public char Command() => _s[_i++];

        public Point Point() => new(Number(), Number());

        public bool Flag()
        {
            Skip();
            return _s[_i++] == '1';
        }

        public double Number()
        {
            Skip();
            var start = _i;
            if (_i < _s.Length && _s[_i] is '+' or '-') _i++;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i < _s.Length && _s[_i] is 'e' or 'E')
            {
                _i++;
                if (_i < _s.Length && _s[_i] is '+' or '-') _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            return double.Parse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void Skip()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
        }
    }
}
