using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AgentIsland.WinUI.Shell;

/// A pixel-art panel to match the pixel bots: a flat fill inside a one-cell
/// outline whose corners step in, on the bots' grid of 2-DIP cells so every
/// edge lands on whole pixels at 100, 150 and 200 % scaling. A small stepped
/// pointer can stick out of one side toward the character it belongs to; it
/// draws outside the plate, so the plate's parent leaves room for it.
internal sealed class PixelPlate : Canvas
{
    public const double Cell = 2;

    /// How far the pointer reaches past the plate's edge.
    public const double PointerReach = 2 * Cell;

    private static FontFamily? _font;

    /// The pixel face: Ark Pixel's 10-pixel Latin design (OFL-1.1, with its
    /// licence in Assets/Fonts), so the panels read in English.
    public static FontFamily Font =>
        _font ??= new FontFamily("ms-appx:///Assets/Fonts/ark-pixel-10px-proportional-latin.ttf#Ark Pixel 10px Prop latin");

    /// A size for the pixel face that keeps its pixels whole: each of its
    /// 10-pixel design's pixels covers a whole number of screen pixels, as
    /// near to `dip` as that allows (13⅓ DIP at 150 %).
    public static double FontSize(double scale, double dip = 13)
    {
        var perPixel = Math.Max(1, Math.Round(dip * scale / 10));
        return 10 * perPixel / scale;
    }

    private readonly Path _outline = new();
    private readonly Path _fill = new();

    public PixelPlate()
    {
        IsHitTestVisible = false;
        Children.Add(_outline);
        Children.Add(_fill);
    }

    /// pointerY is the pointer's centre measured from the plate's top, on the
    /// left edge when pointLeft, else the right; null draws no pointer.
    public void Set(double width, double height, Color outline, Color fill, bool pointLeft = false, double? pointerY = null)
    {
        Width = width;
        Height = height;
        var outer = new PathGeometry();
        outer.Figures.Add(Stepped(0, 0, width, height, 2));
        if (pointerY is { } y) outer.Figures.Add(Pointer(width, height, pointLeft, y));
        _outline.Data = outer;
        _outline.Fill = new SolidColorBrush(outline);
        var inner = new PathGeometry();
        inner.Figures.Add(Stepped(Cell, Cell, width - 2 * Cell, height - 2 * Cell, 1));
        _fill.Data = inner;
        _fill.Fill = new SolidColorBrush(fill);
    }

    /// A rectangle whose corners are cut in a staircase of `steps` cells,
    /// traced clockwise from the top edge.
    private static PathFigure Stepped(double left, double top, double width, double height, int steps)
    {
        var c = Cell;
        var right = left + width;
        var bottom = top + height;
        var points = new List<Point>();
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
        return Figure(points);
    }

    /// Three cells against the edge, one beyond them: a pixel arrowhead.
    private static PathFigure Pointer(double width, double height, bool pointLeft, double centre)
    {
        var c = Cell;
        // On whole cells, clear of the stepped corners.
        var row = Math.Clamp(Math.Round(centre / c - 0.5) * c, 3 * c, Math.Max(3 * c, height - 5 * c));
        var edge = pointLeft ? 0 : width;
        var out1 = pointLeft ? -c : c;
        var out2 = pointLeft ? -2 * c : 2 * c;
        return Figure(new List<Point>
        {
            new(edge, row - c),
            new(edge + out1, row - c),
            new(edge + out1, row),
            new(edge + out2, row),
            new(edge + out2, row + c),
            new(edge + out1, row + c),
            new(edge + out1, row + 2 * c),
            new(edge, row + 2 * c),
        });
    }

    internal static PathFigure Figure(List<Point> points)
    {
        var segment = new PolyLineSegment();
        foreach (var point in points.Skip(1)) segment.Points.Add(point);
        return new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true, Segments = { segment } };
    }
}

/// A quota bar in pixel blocks, the pixel theme's QuotaBar: the shown share
/// fills whole blocks in the agent's colour (at least one when any is shown)
/// over a track tinted with that colour; the warning ink once nearly used up.
/// A new share steps there on count4 (M16): a quarter of the change a tick,
/// rounded up to whole blocks; the ink turns on the tick it crosses its line.
internal sealed class PixelBar : StackPanel, IQuotaBar
{
    private readonly int _blocks;
    private int? _filled;
    private double _share;
    private bool _warn;
    private UiMoment? _count;

    /// 14 blocks on the drawer's cards, 30 (178 DIP) in Settings.
    public PixelBar(int blocks = 14)
    {
        Orientation = Orientation.Horizontal;
        Spacing = PixelPlate.Cell;
        _blocks = blocks;
        for (var i = 0; i < blocks; i++)
        {
            Children.Add(new Rectangle { Width = 2 * PixelPlate.Cell, Height = 3 * PixelPlate.Cell });
        }
    }

    /// ratio null means the window failed to load: a faded, empty track.
    public void Set(double? ratio, Color color, bool warn, bool dark, Action<double, bool>? follow = null)
    {
        _count?.Stop();
        var to = ratio is { } r ? Math.Clamp((int)Math.Round(r * _blocks), r > 0 ? 1 : 0, _blocks) : 0;
        var critical = Fluent.Resource<SolidColorBrush>("SystemFillColorCriticalBrush", dark).Color;
        var final = Math.Clamp(ratio ?? 0, 0, 1);
        Opacity = ratio is null ? 0.5 : 1;
        if (_filled is not { } from || from == to || Motion.ReducedMotion || !IsLoaded)
        {
            Paint(to, warn ? critical : color, dark);
            _filled = to;
            _share = final;
            _warn = warn;
            follow?.Invoke(final, warn);
            return;
        }
        var line = QuotaCount.Line((double)from / _blocks, final, _warn, warn);
        var step = (int)Math.Ceiling(Math.Abs(to - from) / 4.0);
        var ticks = (int)Math.Ceiling(Math.Abs(to - from) / (double)step);
        var sign = Math.Sign(to - from);
        // The label keeps its old reading on frame 0, steps through the
        // blocks' shares, and lands exact.
        var start = _share;
        _count = UiMoment.Play(ticks * Motion.TickMs, t =>
        {
            var k = Math.Min(Motion.Tick(t), ticks);
            var blocks = k >= ticks ? to : from + sign * k * step;
            var share = (double)blocks / _blocks;
            var past = line is { } at && (sign > 0 ? share >= at : share <= at);
            var shownWarn = line is null ? warn : past ? warn : !warn;
            Paint(blocks, shownWarn ? critical : color, dark);
            _filled = blocks;
            _share = k == 0 ? start : k >= ticks ? final : share;
            follow?.Invoke(_share, shownWarn);
        });
        _warn = warn;
    }

    private void Paint(int filled, Color ink, bool dark)
    {
        var fill = new SolidColorBrush(ink);
        var track = new SolidColorBrush(Color.FromArgb((byte)(dark ? 0x40 : 0x2E), ink.R, ink.G, ink.B));
        for (var i = 0; i < _blocks; i++) ((Rectangle)Children[i]).Fill = i < filled ? fill : track;
    }
}
