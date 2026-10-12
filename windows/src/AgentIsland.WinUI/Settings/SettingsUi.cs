using AgentIsland.Localization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Settings;

/// Building blocks for the settings pages, in the Windows 11 Settings
/// idiom: a page title, section headers, and setting cards.
internal static class SettingsUi
{
    public static FontIcon Icon(string glyph) => new() { Glyph = glyph };

    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        Style = Res<Style>("TitleTextBlockStyle"),
        Margin = new Thickness(0, 0, 0, 4),
    };

    public static TextBlock PageIntro(string text) => new()
    {
        Text = text,
        Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
    };

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        Style = Res<Style>("BodyStrongTextBlockStyle"),
        Margin = new Thickness(1, 24, 0, 6),
    };

    public static SettingsCard Card(string header, string? description, string? glyph, object? content)
    {
        var card = new SettingsCard { Header = header };
        SetDescription(card, description);
        if (glyph is not null) card.HeaderIcon = Icon(glyph);
        if (content is not null) card.Content = content;
        return card;
    }

    /// No description at all rather than an empty line.
    public static void SetDescription(SettingsCard card, string? description)
    {
        if (description is null) card.ClearValue(SettingsCard.DescriptionProperty);
        else card.Description = description;
    }

    public static ComboBox Combo(IReadOnlyList<string> items, int selected, Action<int> changed)
    {
        var combo = new ComboBox { MinWidth = 180 };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex);
        };
        return combo;
    }

    public static ToggleSwitch Toggle(bool on, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = L10n.Tr("On"), OffContent = L10n.Tr("Off") };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    public static Button Button(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = Res<Style>("AccentButtonStyle");
        button.Click += (_, _) => click();
        return button;
    }

    /// A small rounded tag, for plan tiers.
    public static Border Badge(TextBlock text)
    {
        text.FontSize = 11;
        text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        text.Foreground = Res<Brush>("TextFillColorSecondaryBrush");
        return new Border
        {
            Child = text,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Res<Brush>("SubtleFillColorSecondaryBrush"),
            BorderBrush = Res<Brush>("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// Secondary text with an optional warning after it.
    public static void SetStatus(TextBlock block, string status, string? warning)
    {
        block.Inlines.Clear();
        block.Inlines.Add(new Run { Text = status });
        if (warning is null) return;
        block.Inlines.Add(new Run { Text = "  ·  " });
        block.Inlines.Add(new Run
        {
            Text = "⚠ " + warning,
            Foreground = Res<Brush>("SystemFillColorCautionBrush"),
        });
    }

    public static async Task Alert(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = L10n.Tr("OK"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
