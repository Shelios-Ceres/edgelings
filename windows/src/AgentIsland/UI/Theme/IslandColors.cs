using System.Windows.Media;

namespace AgentIsland.UI.Theme;

/// Colour helpers shared by every surface. The app's own palette lives in
/// GrokTheme; White() remains for the dark share-card artwork.
public static class IslandColors
{
    public static Color White(double opacity) => Color.FromArgb((byte)(opacity * 255), 0xFF, 0xFF, 0xFF);

    public static Color Alpha(Color color, double opacity) =>
        Color.FromArgb((byte)(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B);

    public static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
