using System.Windows.Media;

namespace AgentIsland.UI.Drawer;

/// WPF colour for a drawer palette name.
internal static class DrawerSwatch
{
    public static Color Of(string color)
    {
        var rgb = Model.DrawerLooks.Swatch(color);
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
