using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Shell;

/// The pixel kit's resources for one window. The XAML holds the control
/// styles; this adds what they reach with ThemeResource before they load:
/// the token brushes per theme, the pixel face and the type sizes for the
/// window's scale, and Fluent keys the remaining system controls read.
public sealed partial class PixelTheme : ResourceDictionary
{
    private static FontFamily? _font;

    /// The pixel face, with Microsoft YaHei UI for Chinese in user content
    /// (folder names, account labels) that the face can't draw.
    internal static FontFamily Font =>
        _font ??= new FontFamily("ms-appx:///Assets/Fonts/ark-pixel-10px-proportional-latin.ttf#Ark Pixel 10px Prop latin, Microsoft YaHei UI");

    internal PixelTheme(double scale)
    {
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        PixelTokens.Fill(light, dark);
        Fluent(light, dark: false);
        Fluent(dark, dark: true);
        ThemeDictionaries["Light"] = light;
        ThemeDictionaries["Default"] = dark;
        this["PixelFontFamily"] = Font;
        this["PixelBodySize"] = PixelTokens.Body(scale);
        this["PixelSubtitleSize"] = PixelTokens.Subtitle(scale);
        this["PixelTitleSize"] = PixelTokens.Title(scale);
        this["ContentControlThemeFontFamily"] = Font;
        this["ControlContentThemeFontSize"] = PixelTokens.Body(scale);
        this["ControlCornerRadius"] = new CornerRadius(0);
        this["OverlayCornerRadius"] = new CornerRadius(0);
        InitializeComponent();
    }

    /// The tokens alone, for the app's own resources: templates in popups
    /// (menus, tooltips, dialogs) look their brushes up there when their
    /// window's kit is out of reach. Every key is the kit's own, so Classic
    /// surfaces never read one.
    internal static ResourceDictionary Tokens(double scale)
    {
        var tokens = new ResourceDictionary();
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        PixelTokens.Fill(light, dark);
        tokens.ThemeDictionaries["Light"] = light;
        tokens.ThemeDictionaries["Default"] = dark;
        tokens["PixelFontFamily"] = Font;
        tokens["PixelBodySize"] = PixelTokens.Body(scale);
        tokens["PixelSubtitleSize"] = PixelTokens.Subtitle(scale);
        tokens["PixelTitleSize"] = PixelTokens.Title(scale);
        return tokens;
    }

    private static void Fluent(ResourceDictionary theme, bool dark)
    {
        void Use(string key, string token) => theme[key] = PixelTokens.Brush(token, dark);
        Use("TextFillColorPrimaryBrush", "Text");
        Use("TextFillColorSecondaryBrush", "Text2");
        Use("TextFillColorTertiaryBrush", "Text3");
        Use("TextFillColorDisabledBrush", "Disabled");
        Use("AccentFillColorDefaultBrush", "Accent");
        Use("SystemFillColorCautionBrush", "Caution");
        Use("SystemFillColorCriticalBrush", "Critical");
        Use("SystemControlFocusVisualPrimaryBrush", "Accent");
        Use("SystemControlFocusVisualSecondaryBrush", "Paper");
        var smoke = PixelTokens.Get("Smoke", dark);
        theme["SmokeFillColorDefaultBrush"] = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(dark ? 140 : 102), smoke.R, smoke.G, smoke.B));
    }
}
