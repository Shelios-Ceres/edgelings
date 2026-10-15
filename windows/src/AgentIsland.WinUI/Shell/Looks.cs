using AgentIsland.Localization;
using AgentIsland.Model;
using Microsoft.UI.Xaml;

namespace AgentIsland.WinUI.Shell;

/// Which look Settings, the Cost window and the drawer menu wear: the pixel
/// kit while Character style is Pixel, else Windows 11's Fluent.
internal static class Looks
{
    // Fluent styles the code asks for by name, and their pixel stand-ins.
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["AccentButtonStyle"] = "PixelAccentButtonStyle",
        ["TitleTextBlockStyle"] = "PixelTitleTextStyle",
        ["SubtitleTextBlockStyle"] = "PixelSubtitleTextStyle",
        ["BodyStrongTextBlockStyle"] = "PixelBodyTextStyle",
        ["BodyTextBlockStyle"] = "PixelBodyTextStyle",
        ["CaptionTextBlockStyle"] = "PixelBodyTextStyle",
    };

    public static bool Pixel => DrawerStore.Shared.Theme == CharacterTheme.Pixel;

    /// Under Pixel these surfaces read in English, as the pixel cards and
    /// bubbles do; null (no scope) under Classic.
    public static IDisposable? English() => Pixel ? L10n.English() : null;

    /// The pixel kit of the window built last, for code that picks styles
    /// and brushes by name; every window's kit holds the same values.
    public static PixelTheme? Kit { get; private set; }

    private static bool _tokensShared;

    public static PixelTheme NewKit(double scale)
    {
        if (!_tokensShared)
        {
            _tokensShared = true;
            Application.Current.Resources.MergedDictionaries.Add(PixelTheme.Tokens(scale));
        }
        return Kit = new PixelTheme(scale);
    }

    /// A resource from the pixel kit under Pixel (a Fluent key may name its
    /// pixel stand-in), else from the app's Fluent resources.
    public static object Resource(string key)
    {
        if (Pixel && Kit is { } kit)
        {
            var name = Aliases.GetValueOrDefault(key, key);
            if (kit.ThemeDictionaries.TryGetValue(Brand.IsDark() ? "Default" : "Light", out var themed)
                && themed is ResourceDictionary theme
                && theme.TryGetValue(name, out var tokened))
            {
                return tokened;
            }
            if (kit.TryGetValue(name, out var styled)) return styled;
        }
        return Application.Current.Resources[key];
    }
}
