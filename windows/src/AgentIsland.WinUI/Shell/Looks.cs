using AgentIsland.Localization;
using AgentIsland.Model;
using Microsoft.UI.Xaml;

namespace AgentIsland.WinUI.Shell;

/// Which look Settings, the Cost window and the drawer menu wear: the pixel
/// kit while Character style is Pixel, else the Classic kit.
internal static class Looks
{
    // Fluent styles the code asks for by name, and their stand-ins per kit.
    private static readonly Dictionary<string, string> PixelAliases = new()
    {
        ["AccentButtonStyle"] = "PixelAccentButtonStyle",
        ["TitleTextBlockStyle"] = "PixelTitleTextStyle",
        ["SubtitleTextBlockStyle"] = "PixelSubtitleTextStyle",
        ["BodyStrongTextBlockStyle"] = "PixelBodyTextStyle",
        ["BodyTextBlockStyle"] = "PixelBodyTextStyle",
        ["CaptionTextBlockStyle"] = "PixelBodyTextStyle",
    };

    private static readonly Dictionary<string, string> ClassicAliases = new()
    {
        ["AccentButtonStyle"] = "ClassicAccentButtonStyle",
    };

    public static bool Pixel => DrawerStore.Shared.Theme == CharacterTheme.Pixel;

    /// The prefix of the current kit's own keys: "Pixel" or "Classic".
    public static string Prefix => Pixel ? "Pixel" : "Classic";

    /// Under Pixel these surfaces read in English, as the pixel cards and
    /// bubbles do; null (no scope) under Classic.
    public static IDisposable? English() => Pixel ? L10n.English() : null;

    // The kits of the windows built last, for code that picks styles and
    // brushes by name; every window's kit holds the same values.
    private static PixelTheme? _pixelKit;
    private static ClassicTheme? _classicKit;

    private static bool _pixelTokensShared;
    private static bool _classicTokensShared;

    /// The current look's kit for a window at this scale.
    public static ResourceDictionary NewKit(double scale)
    {
        if (Pixel)
        {
            if (!_pixelTokensShared)
            {
                _pixelTokensShared = true;
                Application.Current.Resources.MergedDictionaries.Add(PixelTheme.Tokens(scale));
            }
            return _pixelKit = new PixelTheme(scale);
        }
        if (!_classicTokensShared)
        {
            _classicTokensShared = true;
            Application.Current.Resources.MergedDictionaries.Add(ClassicTheme.Tokens());
        }
        return _classicKit = new ClassicTheme();
    }

    /// A resource from the current kit (a Fluent key may name its stand-in),
    /// else from the app's Fluent resources.
    public static object Resource(string key)
    {
        // Code can ask before any window has built a kit (the drawer's menu
        // at startup); the primary display's scale serves until one does.
        var kit = (Pixel ? (ResourceDictionary?)_pixelKit : _classicKit)
            ?? NewKit(Platform.Win32.PrimaryDpi() / 96.0);
        var name = (Pixel ? PixelAliases : ClassicAliases).GetValueOrDefault(key, key);
        if (kit.ThemeDictionaries.TryGetValue(Brand.IsDark() ? "Default" : "Light", out var themed)
            && themed is ResourceDictionary theme
            && theme.TryGetValue(name, out var tokened))
        {
            return tokened;
        }
        if (kit.TryGetValue(name, out var styled)) return styled;
        // The dictionary's own miss is a bare "element not found".
        return Application.Current.Resources.TryGetValue(key, out var fluent)
            ? fluent
            : throw new KeyNotFoundException($"No resource {key} in the {Prefix} kit or the app's resources");
    }

    /// The current kit's own resource by its unprefixed name, as
    /// Kit&lt;Style&gt;("ToggleSwitchStyle") for "PixelToggleSwitchStyle" or
    /// "ClassicToggleSwitchStyle".
    public static T Kit<T>(string name) => (T)Resource(Prefix + name);
}
