using System.Windows.Media;
using AgentIsland.Model;

namespace AgentIsland.UI.Theme;

/// WPF colours for the core library's 0xRRGGBB brand values.
public static class ProviderColors
{
    public static Color Accent(DisplayProvider provider) => FromRgb(ProviderIdentity.Accent(provider));

    public static IReadOnlyList<Color> BrandStops(DisplayProvider provider) =>
        ProviderIdentity.BrandRamp(provider).Select(FromRgb).ToList();

    private static Color FromRgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
