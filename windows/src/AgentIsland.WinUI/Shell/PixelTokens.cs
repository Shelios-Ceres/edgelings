using AgentIsland.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// The pixel kit's design tokens (the approved handoff in
/// windows/notes/pixel-settings-20261014): colours for light and dark, each
/// agent's readable colour, and a type ramp whose sizes keep the pixel face's
/// pixels whole.
internal static class PixelTokens
{
    public const double Cell = PixelPlate.Cell;

    /// Body, subtitle and title line heights: whole cells, and at 150 % they
    /// put the baseline on a whole pixel.
    public const double BodyLine = 20;
    public const double SubtitleLine = 28;
    public const double TitleLine = 36;

    private static readonly (string Name, uint Light, uint Dark)[] Colors =
    {
        ("Desk", 0xEFE9DC, 0x141519),
        ("Layer", 0xF7F3EA, 0x1A1B21),
        ("Paper", 0xFFFDF7, 0x1F2027),
        ("Field", 0xFAF6EC, 0x262731),
        ("FieldHover", 0xF1EBDD, 0x2E2F3A),
        ("FieldPressed", 0xE8E1D0, 0x22232B),
        ("Hover", 0xECE5D6, 0x24252E),
        ("Pressed", 0xE3DBC9, 0x2A2B35),
        ("Line", 0x2B2B33, 0x5A5B66),
        ("LineControl", 0x2B2B33, 0x8E8F9C),
        ("LineSoft", 0xCFC6B3, 0x34353F),
        ("Shadow", 0xD6CDB9, 0x0B0B0F),
        ("Text", 0x2B2B33, 0xECE9E1),
        ("Text2", 0x5A5865, 0xB8B5C1),
        ("Text3", 0x6B6976, 0x9D9AA8),
        ("Disabled", 0xA6A2AC, 0x5E5D68),
        ("DisabledFill", 0xEEE9DF, 0x22232A),
        ("Accent", 0x6B4EE6, 0xA58CFF),
        ("AccentHover", 0x5B3FD8, 0xB6A1FF),
        ("AccentPressed", 0x4B32BE, 0x9177F2),
        ("AccentDeep", 0x3B2794, 0x6E55D6),
        ("OnAccent", 0xFFFFFF, 0x1B1238),
        ("AccentTint", 0xE9E3FF, 0x2E2852),
        ("Caution", 0x8A5300, 0xF2B544),
        ("Critical", 0xC42B1C, 0xFF99A4),
        ("Smoke", 0x2B2B33, 0x000000),
        // The toggle's knob when on: paper in light, onAccent in dark.
        ("KnobOn", 0xFFFDF7, 0x1B1238),
    };

    public static Color Get(string name, bool dark)
    {
        foreach (var (key, light, darkValue) in Colors)
        {
            if (key == name) return Brand.Rgb(dark ? darkValue : light);
        }
        throw new ArgumentException($"no pixel token {name}", nameof(name));
    }

    public static SolidColorBrush Brush(string name, bool dark) => new(Get(name, dark));

    /// Every token as a brush keyed "Pixel" + name, for templates to reach
    /// with ThemeResource.
    public static void Fill(ResourceDictionary light, ResourceDictionary dark)
    {
        foreach (var (name, lightValue, darkValue) in Colors)
        {
            light["Pixel" + name] = new SolidColorBrush(Brand.Rgb(lightValue));
            dark["Pixel" + name] = new SolidColorBrush(Brand.Rgb(darkValue));
        }
        // A dialog's backdrop: smoke at 40 % in light, 55 % in dark.
        light["PixelSmokeFill"] = new SolidColorBrush(Smoke(dark: false, 0.40));
        dark["PixelSmokeFill"] = new SolidColorBrush(Smoke(dark: true, 0.55));
    }

    private static Color Smoke(bool dark, double alpha)
    {
        var smoke = Get("Smoke", dark);
        return Color.FromArgb((byte)Math.Round(alpha * 255), smoke.R, smoke.G, smoke.B);
    }

    /// An agent's colour, made readable on paper, for bars, chips and charts.
    /// Each default look has its hand-tuned pair; another look's colour is
    /// pulled to the same lightness band (LookCatalog.OnPaper).
    public static Color Agent(DisplayProvider provider, bool dark)
    {
        var look = LookCatalog.Of(CharacterTheme.Pixel, provider);
        if (look != LookCatalog.For(CharacterTheme.Pixel, provider)[0]) return Brand.Rgb(LookCatalog.OnPaper(look.Swatch, dark));
        return Brand.Rgb(provider.Character() switch
        {
            DisplayProvider.Claude => dark ? 0xE8916Eu : 0xC2603F,
            DisplayProvider.Codex => dark ? 0xB9BCC6u : 0x6E717C,
            DisplayProvider.Antigravity => dark ? 0x7FA6FFu : 0x2F6BE0,
            DisplayProvider.Grok => dark ? 0xD9D9E0u : 0x2B2B33,
            DisplayProvider.Kimi => dark ? 0x6FB2FFu : 0x1F72E0,
            _ => dark ? 0x8EA0FFu : 0x4058E8,
        });
    }

    /// A tint of `color` over paper: 18 % in light, 25 % in dark.
    public static Color Tint(Color color, bool dark) => OverPaper(color, dark ? 0.25 : 0.18, dark);

    /// `color` at `share` over paper, as one opaque colour.
    public static Color OverPaper(Color color, double share, bool dark)
    {
        var paper = Get("Paper", dark);
        byte Mix(byte over, byte under) => (byte)Math.Round(over * share + under * (1 - share));
        return Color.FromArgb(255, Mix(color.R, paper.R), Mix(color.G, paper.G), Mix(color.B, paper.B));
    }

    /// Body type: 13⅓ DIP at 150 %.
    public static double Body(double scale) => PixelPlate.FontSize(scale);

    /// Dialog titles and big Cost numbers: 20 DIP at 150 %.
    public static double Subtitle(double scale) => Whole(20, scale);

    /// Page titles: 26⅔ DIP at 150 %.
    public static double Title(double scale) => Whole(80.0 / 3, scale);

    private static double Whole(double dip, double scale) => 10 * Math.Max(1, Math.Round(dip * scale / 10)) / scale;
}
