using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Shell;

/// The Classic kit's resources for one window. The XAML holds the control
/// styles; this adds what they reach with ThemeResource: the token brushes
/// per theme, and the Fluent keys the lightly restyled controls (buttons,
/// drop-downs, text boxes, tooltips) and the app's code read.
public sealed partial class ClassicTheme : ResourceDictionary
{
    internal ClassicTheme()
    {
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        ClassicTokens.Fill(light, dark);
        Fluent(light, dark: false);
        Fluent(dark, dark: true);
        ThemeDictionaries["Light"] = light;
        ThemeDictionaries["Default"] = dark;
        InitializeComponent();
    }

    /// The tokens alone, for the app's own resources: templates in popups
    /// (menus, tooltips, dialogs) look their brushes up there when their
    /// window's kit is out of reach. Every key is the kit's own, so Pixel
    /// surfaces never read one.
    internal static ResourceDictionary Tokens()
    {
        var tokens = new ResourceDictionary();
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        ClassicTokens.Fill(light, dark);
        tokens.ThemeDictionaries["Light"] = light;
        tokens.ThemeDictionaries["Default"] = dark;
        return tokens;
    }

    private static void Fluent(ResourceDictionary theme, bool dark)
    {
        void Use(string key, string token) => theme[key] = ClassicTokens.Brush(token, dark);
        // What code and Fluent templates read for ink and state colours.
        Use("TextFillColorPrimaryBrush", "Text");
        Use("TextFillColorSecondaryBrush", "Text2");
        Use("TextFillColorTertiaryBrush", "Text3");
        Use("TextFillColorDisabledBrush", "Disabled");
        Use("AccentFillColorDefaultBrush", "Accent");
        Use("AccentTextFillColorPrimaryBrush", "AccentText");
        Use("SystemFillColorCautionBrush", "Caution");
        Use("SystemFillColorCriticalBrush", "Critical");
        Use("DividerStrokeColorDefaultBrush", "LineSoft");
        Use("SystemControlFocusVisualPrimaryBrush", "Accent");
        Use("SystemControlFocusVisualSecondaryBrush", "Paper");

        // Buttons: pills on field with the card edge; the accent one in Sky.
        Use("ButtonBackground", "Field");
        Use("ButtonBackgroundPointerOver", "FieldHover");
        Use("ButtonBackgroundPressed", "FieldPressed");
        Use("ButtonBackgroundDisabled", "DisabledFill");
        Use("ButtonForeground", "Text");
        Use("ButtonForegroundPointerOver", "Text");
        Use("ButtonForegroundPressed", "Text2");
        Use("ButtonForegroundDisabled", "Disabled");
        theme["ButtonBorderBrush"] = ClassicTokens.Edge(dark);
        theme["ButtonBorderBrushPointerOver"] = ClassicTokens.Edge(dark);
        theme["ButtonBorderBrushPressed"] = ClassicTokens.Edge(dark);
        theme["ButtonBorderBrushDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Use("AccentButtonBackground", "Accent");
        Use("AccentButtonBackgroundPointerOver", "AccentHover");
        Use("AccentButtonBackgroundPressed", "AccentPressed");
        Use("AccentButtonBackgroundDisabled", "DisabledFill");
        Use("AccentButtonForeground", "OnAccent");
        Use("AccentButtonForegroundPointerOver", "OnAccent");
        Use("AccentButtonForegroundPressed", "OnAccent");
        Use("AccentButtonForegroundDisabled", "Disabled");
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            theme["AccentButtonBorderBrush" + state] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        // Drop-downs: a pill on paper (field in dark) with a control outline.
        var box = dark ? "Field" : "Paper";
        Use("ComboBoxBackground", box);
        Use("ComboBoxBackgroundUnfocused", box);
        // Keyboard focus is the kit's ring (the style turns system focus
        // visuals on); Fluent's own focus plate, border and pill stay clear.
        theme["ComboBoxBackgroundFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        theme["ComboBoxBackgroundBorderBrushFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        theme["ComboBoxItemPillFillBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Use("ComboBoxBackgroundPointerOver", "FieldHover");
        Use("ComboBoxBackgroundPressed", "FieldPressed");
        Use("ComboBoxBackgroundDisabled", "DisabledFill");
        Use("ComboBoxBorderBrush", "LineControl");
        Use("ComboBoxBorderBrushPointerOver", "LineControl");
        Use("ComboBoxBorderBrushPressed", "LineControl");
        Use("ComboBoxBorderBrushDisabled", "Disabled");
        Use("ComboBoxForeground", "Text");
        Use("ComboBoxForegroundFocused", "Text");
        Use("ComboBoxForegroundFocusedPressed", "Text2");
        Use("ComboBoxForegroundPointerOver", "Text");
        Use("ComboBoxForegroundPressed", "Text2");
        Use("ComboBoxForegroundDisabled", "Disabled");
        Use("ComboBoxDropDownGlyphForeground", "Text2");
        Use("ComboBoxDropDownGlyphForegroundFocused", "Text2");
        Use("ComboBoxDropDownGlyphForegroundFocusedPressed", "Text2");
        Use("ComboBoxDropDownGlyphForegroundDisabled", "Disabled");
        Use("ComboBoxDropDownBackground", "Paper");
        Use("ComboBoxDropDownBorderBrush", "Hairline");
        Use("ComboBoxDropDownForeground", "Text");
        theme["ComboBoxDropdownBorderPadding"] = new Thickness(4);
        theme["ComboBoxDropdownContentMargin"] = new Thickness(0);

        // Text boxes: a pill whose outline turns to a 2-DIP accent ring while typing.
        Use("TextControlBackground", box);
        Use("TextControlBackgroundPointerOver", "FieldHover");
        Use("TextControlBackgroundFocused", box);
        Use("TextControlBackgroundDisabled", "DisabledFill");
        Use("TextControlBorderBrush", "LineControl");
        Use("TextControlBorderBrushPointerOver", "LineControl");
        Use("TextControlBorderBrushFocused", "Accent");
        Use("TextControlBorderBrushDisabled", "Disabled");
        Use("TextControlElevationBorderBrush", "LineControl");
        Use("TextControlElevationBorderFocusedBrush", "Accent");
        Use("TextControlForeground", "Text");
        Use("TextControlForegroundPointerOver", "Text");
        Use("TextControlForegroundFocused", "Text");
        Use("TextControlForegroundDisabled", "Disabled");
        Use("TextControlPlaceholderForeground", "Text3");
        Use("TextControlPlaceholderForegroundPointerOver", "Text3");
        Use("TextControlPlaceholderForegroundFocused", "Text3");
        Use("TextControlPlaceholderForegroundDisabled", "Disabled");
        theme["TextControlBorderThemeThicknessFocused"] = new Thickness(2);

        // The Fluent pieces left: the overlay corner Fluent popups read.
        theme["OverlayCornerRadius"] = new CornerRadius(16);
    }
}
