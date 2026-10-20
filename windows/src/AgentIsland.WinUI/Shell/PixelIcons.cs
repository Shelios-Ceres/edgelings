using System.Text;
using AgentIsland.Model;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace AgentIsland.WinUI.Shell;

/// The pixel kit's icons: one-colour drawings that take their element's
/// foreground, each standing in for a Segoe Fluent glyph, and the agents'
/// faces and the app logo in their own colours.
internal static partial class PixelIcons
{
    private const double C = PixelPlate.Cell;
    private const double Size = 10 * C;

    /// The icon drawn for a Segoe Fluent glyph, or null where the kit has none.
    public static string? NameFor(string glyph)
    {
        if (glyph.Length != 1) return null;
        var code = ((int)glyph[0]).ToString("X4");
        foreach (var (name, (replaces, _)) in Grids)
        {
            if (replaces == code) return name;
        }
        return null;
    }

    public static PathIcon Icon(string name) => new() { Data = Geometry(name), Width = Size, Height = Size };

    /// Whole-cell rectangles, a row's run of filled cells at a time. A
    /// fill-less line pins the bounds to the full 20 DIP, so an icon whose
    /// edge rows are empty still measures, and lines up, like the rest.
    public static Geometry Geometry(string name)
    {
        var group = new GeometryGroup();
        group.Children.Add(new LineGeometry { StartPoint = new Point(0, 0), EndPoint = new Point(Size, Size) });
        foreach (var (x, y, length) in Runs(Grids[name].Rows, '#'))
        {
            group.Children.Add(new RectangleGeometry { Rect = new Rect(x * C, y * C, length * C, C) });
        }
        return group;
    }

    /// The same drawing as path markup, for templates.
    public static string Markup(string name) => string.Concat(Runs(Grids[name].Rows, '#')
        .Select(run => $"M{run.X * C},{run.Y * C}h{run.Length * C}v{C}h-{run.Length * C}z"));

    /// The face of the look the agent wears in the pixel set.
    public static ImageSource Face(DisplayProvider provider) =>
        Face(provider, LookCatalog.Of(CharacterTheme.Pixel, provider).Id);

    /// A look's face; an id with no face of its own (the default) gets the agent's.
    public static ImageSource Face(DisplayProvider provider, string lookId)
    {
        var character = provider.Character();
        return Svg(LookFaceGrids.TryGetValue((character, lookId), out var art) ? art : FaceGrids[character]);
    }

    public static ImageSource Logo() => Svg(LogoGrid);

    private static IEnumerable<(int X, int Y, int Length)> Runs(string[] rows, char key)
    {
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < rows[y].Length;)
            {
                if (rows[y][x] != key)
                {
                    x++;
                    continue;
                }
                var start = x;
                while (x < rows[y].Length && rows[y][x] == key) x++;
                yield return (start, y, x - start);
            }
        }
    }

    /// Vector rather than a bitmap, so the cells stay sharp at any scale.
    private static ImageSource Svg((string[] Rows, (char Key, uint Rgb)[] Colors) art)
    {
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Size}\" height=\"{Size}\" viewBox=\"0 0 {Size} {Size}\" shape-rendering=\"crispEdges\">");
        foreach (var (key, rgb) in art.Colors)
        {
            svg.Append($"<path fill=\"#{rgb:X6}\" d=\"");
            foreach (var (x, y, length) in Runs(art.Rows, key)) svg.Append($"M{x * C} {y * C}h{length * C}v{C}h-{length * C}z");
            svg.Append("\"/>");
        }
        svg.Append("</svg>");
        var source = new SvgImageSource();
        _ = LoadAsync(source, svg.ToString());
        return source;
    }

    private static async Task LoadAsync(SvgImageSource source, string svg)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteString(svg);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        await source.SetSourceAsync(stream);
    }
}
