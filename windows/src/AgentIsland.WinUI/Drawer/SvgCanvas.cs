using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace AgentIsland.WinUI.Drawer;

/// Draws the character engines' SVG subset with Win2D: svg, defs, g, path
/// (M L H V C A Z), circle, use, clipPath, mask (as a clip of its shapes);
/// transform, opacity, fill, fill-rule, stroke, stroke-width,
/// stroke-dasharray, stroke-linecap / linejoin.
public sealed partial class SvgCanvas
{
    private const int PathCacheLimit = 256;
    private readonly Dictionary<string, CanvasGeometry> _paths = new();
    private readonly Dictionary<string, Color> _colors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(float, string?, string?, string?), CanvasStrokeStyle> _strokes = new();
    private CanvasDevice? _device;

    public void Render(string svg, CanvasDrawingSession ds)
    {
        if (!ReferenceEquals(ds.Device, _device))
        {
            foreach (var path in _paths.Values) path.Dispose();
            _paths.Clear();
            _device = ds.Device;
        }
        var root = XElement.Parse(svg);
        using var frame = new Frame(ds.Device);
        foreach (var element in root.Descendants())
        {
            if (element.Attribute("id")?.Value is { } id) frame.ById[id] = element;
        }
        var origin = ds.Transform;
        foreach (var child in root.Elements()) Draw(child, ds, frame, Paint.Default, origin);
        ds.Transform = origin;
    }

    /// Per-render state. Clips and transformed copies are built for this
    /// frame only and released with it; cached path geometry is not.
    private sealed class Frame(CanvasDevice device) : IDisposable
    {
        public CanvasDevice Device { get; } = device;
        public Dictionary<string, XElement> ById { get; } = new();
        public Dictionary<string, CanvasGeometry> Clips { get; } = new();
        public List<IDisposable> Owned { get; } = new();

        public T Own<T>(T disposable) where T : IDisposable
        {
            Owned.Add(disposable);
            return disposable;
        }

        public void Dispose()
        {
            foreach (var owned in Owned) owned.Dispose();
        }
    }

    private readonly record struct Paint(
        string Fill, string FillRule, string? Stroke, float StrokeWidth, string? Dash, string? Cap, string? Join)
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

    private void Draw(XElement e, CanvasDrawingSession ds, Frame frame, Paint inherited, Matrix3x2 parent)
    {
        var name = e.Name.LocalName;
        if (name is "defs" or "clipPath" or "mask") return;
        var paint = inherited.With(e);
        var matrix = Attr(e, "transform") is { } transform ? ParseTransform(transform) * parent : parent;
        ds.Transform = matrix;
        var layers = new Stack<IDisposable>();
        var opacity = Attr(e, "opacity") is { } o ? Number(o) : 1f;
        CanvasGeometry? clip = null;
        foreach (var attribute in new[] { "clip-path", "mask" })
        {
            if (Attr(e, attribute) is { } url && Clip(UrlId(url), frame) is { } c)
            {
                clip = clip is null ? c : frame.Own(clip.CombineWith(c, Matrix3x2.Identity, CanvasGeometryCombine.Intersect));
            }
        }
        if (clip is not null) layers.Push(ds.CreateLayer(opacity, clip));
        else if (opacity < 1) layers.Push(ds.CreateLayer(opacity));

        switch (name)
        {
            case "g":
                foreach (var child in e.Elements()) Draw(child, ds, frame, paint, matrix);
                break;
            case "use":
                if (Reference(e, frame) is { } target) Draw(target, ds, frame, paint, matrix);
                break;
            case "path":
            case "circle":
                ds.Transform = matrix;
                DrawShape(ds, Shape(e, frame, paint.FillRule), paint);
                break;
        }
        while (layers.Count > 0) layers.Pop().Dispose();
        ds.Transform = parent;
    }

    private void DrawShape(CanvasDrawingSession ds, CanvasGeometry geometry, Paint paint)
    {
        if (paint.Fill != "none") ds.FillGeometry(geometry, ColorOf(paint.Fill));
        if (paint.Stroke is { } stroke && stroke != "none" && paint.StrokeWidth > 0)
        {
            ds.DrawGeometry(geometry, ColorOf(stroke), paint.StrokeWidth, StrokeStyle(paint));
        }
    }

    private CanvasStrokeStyle StrokeStyle(Paint paint)
    {
        var key = (paint.StrokeWidth, paint.Dash, paint.Cap, paint.Join);
        if (_strokes.TryGetValue(key, out var style)) return style;
        var cap = paint.Cap == "round" ? CanvasCapStyle.Round : CanvasCapStyle.Flat;
        style = new CanvasStrokeStyle
        {
            StartCap = cap,
            EndCap = cap,
            DashCap = cap,
            LineJoin = paint.Join == "round" ? CanvasLineJoin.Round : CanvasLineJoin.Miter,
        };
        if (paint.Dash is { } dash && dash != "none")
        {
            // SVG dashes are user units; Win2D's custom dashes are multiples of the stroke width.
            style.CustomDashStyle = Numbers(dash).Select(v => v / paint.StrokeWidth).ToArray();
        }
        _strokes[key] = style;
        return style;
    }

    private CanvasGeometry Shape(XElement e, Frame frame, string fillRule)
    {
        var device = frame.Device;
        if (e.Name.LocalName == "circle")
        {
            return frame.Own(CanvasGeometry.CreateCircle(device, Number(Attr(e, "cx") ?? "0"), Number(Attr(e, "cy") ?? "0"), Number(Attr(e, "r") ?? "0")));
        }
        var key = (fillRule == "evenodd" ? "E" : "N") + (Attr(e, "d") ?? "");
        if (_paths.TryGetValue(key, out var cached)) return cached;
        if (_paths.Count >= PathCacheLimit)
        {
            foreach (var path in _paths.Values) path.Dispose();
            _paths.Clear();
        }
        var geometry = SvgPath.Parse(device, key[1..], fillRule == "evenodd");
        _paths[key] = geometry;
        return geometry;
    }

    private CanvasGeometry? Clip(string id, Frame frame)
    {
        if (frame.Clips.TryGetValue(id, out var cached)) return cached;
        if (!frame.ById.TryGetValue(id, out var element)) return null;
        var parts = new List<CanvasGeometry>();
        foreach (var child in element.Elements()) Collect(child, frame, Matrix3x2.Identity, parts);
        var group = frame.Own(CanvasGeometry.CreateGroup(frame.Device, parts.ToArray(), CanvasFilledRegionDetermination.Winding));
        frame.Clips[id] = group;
        return group;
    }

    private void Collect(XElement e, Frame frame, Matrix3x2 parent, List<CanvasGeometry> into)
    {
        var matrix = Attr(e, "transform") is { } transform ? ParseTransform(transform) * parent : parent;
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
                into.Add(matrix.IsIdentity ? shape : frame.Own(shape.Transform(matrix)));
                break;
        }
    }

    private static XElement? Reference(XElement use, Frame frame)
    {
        var href = Attr(use, "href") ?? use.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))?.Value;
        return href is not null && href.StartsWith('#') && frame.ById.TryGetValue(href[1..], out var target) ? target : null;
    }

    private Color ColorOf(string value)
    {
        if (_colors.TryGetValue(value, out var color)) return color;
        color = value switch
        {
            "white" => Color.FromArgb(255, 255, 255, 255),
            "black" => Color.FromArgb(255, 0, 0, 0),
            "transparent" => Color.FromArgb(0, 0, 0, 0),
            _ when value.StartsWith('#') => Hex(value),
            _ => Color.FromArgb(255, 0, 0, 0),
        };
        _colors[value] = color;
        return color;
    }

    private static Color Hex(string value)
    {
        var hex = value[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        var rgb = Convert.ToInt32(hex, 16);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// SVG "A B C" applies C first; with row vectors that is C * B * A.
    private static Matrix3x2 ParseTransform(string value)
    {
        var result = Matrix3x2.Identity;
        foreach (Match match in TransformPattern().Matches(value))
        {
            var args = Numbers(match.Groups[2].Value).ToArray();
            var step = match.Groups[1].Value switch
            {
                "translate" => Matrix3x2.CreateTranslation(args[0], args.Length > 1 ? args[1] : 0),
                "scale" => Matrix3x2.CreateScale(args[0], args.Length > 1 ? args[1] : args[0]),
                "rotate" => args.Length >= 3
                    ? Matrix3x2.CreateRotation(args[0] * MathF.PI / 180, new Vector2(args[1], args[2]))
                    : Matrix3x2.CreateRotation(args[0] * MathF.PI / 180),
                "matrix" => new Matrix3x2(args[0], args[1], args[2], args[3], args[4], args[5]),
                _ => Matrix3x2.Identity,
            };
            result = step * result;
        }
        return result;
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static string UrlId(string url) => url.Trim()[5..^1];

    private static float Number(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static IEnumerable<float> Numbers(string value) =>
        value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(Number);

    [GeneratedRegex(@"(\w+)\s*\(([^)]*)\)")]
    private static partial Regex TransformPattern();
}

/// Absolute-command SVG path data (M L H V C A Z) to a Win2D geometry.
internal static class SvgPath
{
    public static CanvasGeometry Parse(CanvasDevice device, string data, bool evenOdd)
    {
        using var builder = new CanvasPathBuilder(device);
        builder.SetFilledRegionDetermination(evenOdd ? CanvasFilledRegionDetermination.Alternate : CanvasFilledRegionDetermination.Winding);
        var reader = new Reader(data);
        var open = false;
        var start = Vector2.Zero;
        var current = Vector2.Zero;
        var command = ' ';
        while (reader.More())
        {
            if (reader.AtCommand()) command = reader.Command();
            else if (command == 'M') command = 'L';
            if (command == 'Z')
            {
                if (open) builder.EndFigure(CanvasFigureLoop.Closed);
                open = false;
                current = start;
                continue;
            }
            if (command == 'M')
            {
                if (open) builder.EndFigure(CanvasFigureLoop.Open);
                current = start = reader.Point();
                builder.BeginFigure(start);
                open = true;
                continue;
            }
            if (!open)
            {
                start = current;
                builder.BeginFigure(start);
                open = true;
            }
            switch (command)
            {
                case 'L':
                    current = reader.Point();
                    builder.AddLine(current);
                    break;
                case 'H':
                    current = new Vector2(reader.Number(), current.Y);
                    builder.AddLine(current);
                    break;
                case 'V':
                    current = new Vector2(current.X, reader.Number());
                    builder.AddLine(current);
                    break;
                case 'C':
                    var c1 = reader.Point();
                    var c2 = reader.Point();
                    current = reader.Point();
                    builder.AddCubicBezier(c1, c2, current);
                    break;
                case 'A':
                    var rx = MathF.Abs(reader.Number());
                    var ry = MathF.Abs(reader.Number());
                    var rotation = reader.Number();
                    var large = reader.Flag();
                    var sweep = reader.Flag();
                    current = reader.Point();
                    if (rx == 0 || ry == 0) builder.AddLine(current);
                    else builder.AddArc(current, rx, ry, rotation * MathF.PI / 180,
                        sweep ? CanvasSweepDirection.Clockwise : CanvasSweepDirection.CounterClockwise,
                        large ? CanvasArcSize.Large : CanvasArcSize.Small);
                    break;
                default:
                    throw new FormatException($"Unsupported SVG path command '{command}'.");
            }
        }
        if (open) builder.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
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

        public Vector2 Point() => new(Number(), Number());

        public bool Flag()
        {
            Skip();
            return _s[_i++] == '1';
        }

        public float Number()
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
            return float.Parse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void Skip()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
        }
    }
}
