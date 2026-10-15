using AgentIsland.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// The Classic kit's design tokens (the approved handoff in
/// windows/notes/classic-grok-20261015): ink on paper for light and dark, the
/// Sky accent, and the agents' tints.
internal static class ClassicTokens
{
    private static readonly (string Name, uint Light, uint Dark)[] Colors =
    {
        ("Desk", 0xEDEFF2, 0x1A1B1F),
        ("Layer", 0xF6F7F9, 0x202126),
        ("Paper", 0xFFFFFF, 0x2A2B31),
        ("Field", 0xF1F2F5, 0x34353C),
        ("FieldHover", 0xE9EBEF, 0x3C3D45),
        ("FieldPressed", 0xE1E4E9, 0x2F3036),
        ("Hover", 0xF1F2F5, 0x313238),
        ("Pressed", 0xE8EAEE, 0x2C2D33),
        ("LineSoft", 0xE4E6EB, 0x393A41),
        ("LineControl", 0x868B95, 0x8E929C),
        ("Text", 0x17191F, 0xF3F4F6),
        ("Text2", 0x50545E, 0xC4C7CE),
        ("Text3", 0x62666F, 0xA9ADB5),
        ("Disabled", 0xA7ABB3, 0x62656D),
        ("DisabledFill", 0xF3F4F6, 0x2E2F35),
        ("Critical", 0xC42B1C, 0xFF99A4),
        ("Caution", 0x935600, 0xF2B544),
        ("TipFill", 0x17191F, 0xF3F4F6),
        ("TipText", 0xFFFFFF, 0x17191F),
        ("Smoke", 0x17191F, 0x000000),
        // Sky, the accent the user picked.
        ("Accent", 0x2F6BEB, 0x82AAFF),
        ("AccentHover", 0x295FD4, 0x98B9FF),
        ("AccentPressed", 0x2453BB, 0x6E97EE),
        ("OnAccent", 0xFFFFFF, 0x0D1A3A),
        ("AccentTint", 0xE6EEFE, 0x263354),
        ("AccentText", 0x2559CC, 0x98B9FF),
    };

    // Edges that let the surface show through: ink in light, white or black in dark.
    private static readonly (string Name, uint Light, double LightAlpha, uint Dark, double DarkAlpha)[] Translucent =
    {
        ("Hairline", 0x17191F, 0.08, 0xFFFFFF, 0.07),
        ("HairlineLip", 0x17191F, 0.13, 0x000000, 0.35),
    };

    public static Color Get(string name, bool dark)
    {
        foreach (var (key, light, darkValue) in Colors)
        {
            if (key == name) return Brand.Rgb(dark ? darkValue : light);
        }
        foreach (var (key, light, lightAlpha, darkValue, darkAlpha) in Translucent)
        {
            if (key == name) return Alpha(dark ? darkValue : light, dark ? darkAlpha : lightAlpha);
        }
        throw new ArgumentException($"no classic token {name}", nameof(name));
    }

    public static SolidColorBrush Brush(string name, bool dark) => new(Get(name, dark));

    private static Color Alpha(uint rgb, double alpha) => Brand.Rgb(rgb, (byte)Math.Round(alpha * 255));

    /// Every token as a brush keyed "Classic" + name, for templates to reach
    /// with ThemeResource, and the edge brushes built from them.
    public static void Fill(ResourceDictionary light, ResourceDictionary dark)
    {
        foreach (var (theme, isDark) in new[] { (light, false), (dark, true) })
        {
            foreach (var (name, _, _) in Colors) theme["Classic" + name] = Brush(name, isDark);
            foreach (var (name, _, _, _, _) in Translucent) theme["Classic" + name] = Brush(name, isDark);
            theme["ClassicEdge"] = Edge(isDark);
            // A dialog's backdrop: smoke at 30 % in light, 55 % in dark.
            var smoke = Get("Smoke", isDark);
            theme["ClassicSmokeFill"] = new SolidColorBrush(Color.FromArgb((byte)Math.Round((isDark ? 0.55 : 0.30) * 255), smoke.R, smoke.G, smoke.B));
        }
    }

    /// A card's edge: the hairline all round, its bottom in the lip, as
    /// Fluent's elevation border draws it.
    public static LinearGradientBrush Edge(bool dark) => new()
    {
        MappingMode = BrushMappingMode.Absolute,
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 3),
        RelativeTransform = new ScaleTransform { ScaleY = -1, CenterY = 0.5 },
        GradientStops =
        {
            new GradientStop { Offset = 0.33, Color = Get("HairlineLip", dark) },
            new GradientStop { Offset = 1, Color = Get("Hairline", dark) },
        },
    };

    /// An agent's tint: its colour over paper, 14 % in light and 24 % in dark.
    public static Color Tint(Color color, bool dark)
    {
        var share = dark ? 0.24 : 0.14;
        var paper = Get("Paper", dark);
        byte Mix(byte over, byte under) => (byte)Math.Round(over * share + under * (1 - share));
        return Color.FromArgb(255, Mix(color.R, paper.R), Mix(color.G, paper.G), Mix(color.B, paper.B));
    }

    /// The colour a provider's character wears: the one picked in its menu,
    /// lifted on dark surfaces when it is near black.
    public static Color Identity(DisplayProvider provider, bool dark)
    {
        var swatch = App.Current.Drawer?.SwatchOf(provider)
            ?? DrawerLooks.Swatch(provider, DrawerLooks.For(provider).Color, CharacterTheme.Classic);
        return Brand.Rgb(Brand.Readable(swatch, dark));
    }

    /// Face eyes: white, or ink on a disc too light for white to read.
    public static Color EyesOn(Color disc)
    {
        var luma = 0.2126 * disc.R + 0.7152 * disc.G + 0.0722 * disc.B;
        return luma > 170 ? Brand.Rgb(0x17191F) : Brand.Rgb(0xFFFFFF);
    }
}
