using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AgentIsland.UI.Theme;

/// Grok Bot's design language, turned light. Grok layers translucent washes
/// of one base colour over two solid planes (chrome #141414, editor #181818,
/// base #F0F0F0); the light version keeps those alphas and swaps the base
/// for ink and the planes for paper, so every surface reads as one family.
public static class GrokTheme
{
    public static readonly Color Ink = Color.FromRgb(0x14, 0x14, 0x14);

    /// Window chrome: sidebar rails and footers.
    public static readonly Color Chrome = Color.FromRgb(0xF3, 0xF3, 0xF1);

    /// The content plane.
    public static readonly Color Paper = Color.FromRgb(0xFB, 0xFB, 0xFA);

    /// Floating surfaces: menus, popups, dialogs.
    public static readonly Color Elevated = Colors.White;

    public static readonly Color Accent = Color.FromRgb(0x59, 0x9C, 0xE7);
    public static readonly Color AccentHover = Color.FromRgb(0x4A, 0x8D, 0xD9);
    public static readonly Color Green = Color.FromRgb(0x3F, 0xA2, 0x66);
    public static readonly Color Orange = Color.FromRgb(0xD0, 0x87, 0x70);

    /// Warning text on paper: the island's amber is too pale to read there.
    public static readonly Color Amber = Color.FromRgb(0xB0, 0x6D, 0x0B);

    // Layer washes (--cursor-bg-*).
    public const double Field = 0.03;
    public const double Quinary = 0.04;
    public const double Quaternary = 0.06;
    public const double Tertiary = 0.08;
    public const double Secondary = 0.12;
    public const double Active = 0.16;

    // Text (--cursor-text-*).
    public const double TextPrimary = 0.92;
    public const double TextSecondary = 0.70;
    public const double TextTertiary = 0.54;
    public const double TextQuaternary = 0.38;

    // Strokes (--cursor-stroke-*).
    public const double StrokePrimary = 0.20;
    public const double StrokeSecondary = 0.12;
    public const double StrokeTertiary = 0.08;

    /// Brand colour on paper. Grok's steel and Cursor's bone are near-white
    /// and vanish on light surfaces; both brands print their marks in ink there.
    public static Color Brand(Model.DisplayProvider provider) => provider switch
    {
        Model.DisplayProvider.Grok => Ink,
        Model.DisplayProvider.Cursor => Color.FromRgb(0x26, 0x25, 0x1E),
        _ => ProviderColors.Accent(provider),
    };

    public static IReadOnlyList<Color> BrandStops(Model.DisplayProvider provider) =>
        provider == Model.DisplayProvider.Antigravity
            ? ProviderColors.BrandStops(provider)
            : new[] { Brand(provider), Brand(provider) };

    public static LinearGradientBrush BrandGradient(Model.DisplayProvider provider, System.Windows.Point start, System.Windows.Point end)
    {
        var stops = BrandStops(provider);
        var collection = new GradientStopCollection();
        for (var i = 0; i < stops.Count; i++) collection.Add(new GradientStop(stops[i], (double)i / (stops.Count - 1)));
        var brush = new LinearGradientBrush(collection, start, end);
        brush.Freeze();
        return brush;
    }

    public static Color InkA(double alpha) => IslandColors.Alpha(Ink, alpha);

    public static SolidColorBrush InkBrush(double alpha) => IslandColors.Brush(InkA(alpha));

    public static SolidColorBrush Brush(Color color) => IslandColors.Brush(color);

    /// The one soft drop shadow floating surfaces share.
    public static DropShadowEffect Float(double opacity = 0.14) => new()
    {
        ShadowDepth = 3,
        Direction = 270,
        BlurRadius = 18,
        Color = Ink,
        Opacity = opacity,
    };
}
