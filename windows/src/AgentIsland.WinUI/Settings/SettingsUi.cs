using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AgentIsland.WinUI.Settings;

/// Building blocks for the settings pages, in the Windows 11 Settings
/// idiom: a page title, section headers, and setting cards, in the current
/// look's kit (Shell/PixelTheme.xaml or Shell/ClassicTheme.xaml).
internal static class SettingsUi
{
    /// The glyph at 16 DIP, or under Pixel the kit's drawing of it.
    public static IconElement Icon(string glyph) =>
        Looks.Pixel && PixelIcons.NameFor(glyph) is { } name ? PixelIcons.Icon(name) : new FontIcon { Glyph = glyph, FontSize = 16 };

    /// Cards and rows stand this far apart on a page.
    public static double CardGap => Looks.Pixel ? 4 : 6;

    public static T Res<T>(string key) => (T)Looks.Resource(key);

    // Margins add to the page's card gap: Classic's title sits 4 above the
    // intro, the intro 6 above the first section, a section 22 below the
    // last card and 8 above its own.
    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        Style = Res<Style>("TitleTextBlockStyle"),
        Margin = new Thickness(0, 0, 0, Looks.Pixel ? 4 : -2),
    };

    public static TextBlock PageIntro(string text) => new()
    {
        Text = text,
        Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, Looks.Pixel ? 8 : 0),
    };

    /// Under Pixel: in the accent, after a small accent square.
    public static FrameworkElement Section(string text)
    {
        if (!Looks.Pixel)
        {
            return StartsSection(new TextBlock
            {
                Text = text,
                Style = Res<Style>("BodyStrongTextBlockStyle"),
                Margin = new Thickness(1, 16, 0, 2),
            });
        }
        var accent = Res<Brush>("PixelAccent");
        return StartsSection(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 24, 0, 4),
            Children =
            {
                new Rectangle { Width = 4, Height = 4, Fill = accent, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, Foreground = accent, Style = Res<Style>("PixelBodyTextStyle") },
            },
        });
    }

    // Where a page's sections start, for its entrance (M8).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> SectionStarts = new();

    public static T StartsSection<T>(T element) where T : UIElement
    {
        SectionStarts.AddOrUpdate(element, element);
        return element;
    }

    public static bool IsSectionStart(UIElement element) => SectionStarts.TryGetValue(element, out _);

    /// A page that scrolls up and down only.
    public static ScrollViewer PageScroller()
    {
        var scroll = new ScrollViewer { HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // Pixel type is crisp only on whole pixels. WinUI draws the scrolled
        // page snapped, but a drop-down's list takes the page's exact place,
        // so a scroll that settles between pixels blurs it: settle on one.
        if (Looks.Pixel)
        {
            scroll.ViewChanged += (_, e) =>
            {
                if (e.IsIntermediate) return;
                var scale = scroll.XamlRoot?.RasterizationScale ?? 1;
                var exact = scroll.VerticalOffset * scale;
                var whole = Math.Round(exact) / scale > scroll.ScrollableHeight ? Math.Floor(exact) : Math.Round(exact);
                if (Math.Abs(whole - exact) > 0.001) scroll.ChangeView(null, whole / scale, null, disableAnimation: true);
            };
        }
        return scroll;
    }

    /// Centred by a grid as wide as the viewport, not by the ScrollViewer,
    /// which misplaced a page narrower than itself once the window was widened.
    public static void SetPage(ScrollViewer scroll, UIElement page) => scroll.Content = new Grid { Children = { page } };

    public static SettingsCard Card(string header, string? description, string? glyph, object? content)
    {
        var card = new SettingsCard { Header = header };
        SetDescription(card, description);
        if (glyph is not null) card.HeaderIcon = Icon(glyph);
        if (content is not null) card.Content = content;
        return card;
    }

    /// A card whose content runs full width under its header, like a list.
    public static SettingsCard VerticalCard(string header, string? description, string? glyph, object content)
    {
        var card = Card(header, description, glyph, null);
        card.Style = Looks.Kit<Style>("SettingsCardVerticalStyle");
        card.Content = content;
        return card;
    }

    /// Several controls side by side in one card.
    public static StackPanel SideBySide(params UIElement[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    /// Every switch that shows or hides an agent, named and explained alike:
    /// hiding one takes it out of the drawer, its menus and Usage & cost.
    public static void NameShownSwitch(ToggleSwitch toggle, string agent)
    {
        // Once: an order row is renamed as it moves, and a tooltip replaced
        // while open would stay on screen.
        if (ToolTipService.GetToolTip(toggle) is null)
        {
            SetTip(toggle, L10n.Tr("Show in the drawer, its menus and Usage & cost"));
        }
        AutomationProperties.SetName(toggle, L10n.TrFormat("Show {0}", agent));
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
        UseKit(combo);
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
        UseKit(toggle);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    /// Setting cards hand toggles, drop-downs and text boxes styles of their
    /// own; an explicit one keeps the kit's.
    public static void UseKit(ToggleSwitch toggle) => toggle.Style = Looks.Kit<Style>("ToggleSwitchStyle");

    public static void UseKit(ComboBox combo) => combo.Style = Looks.Kit<Style>("ComboBoxStyle");

    public static void UseKit(TextBox box) => box.Style = Looks.Kit<Style>("TextBoxStyle");

    public static Button Button(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = Res<Style>("AccentButtonStyle");
        button.Click += (_, _) => click();
        return button;
    }

    /// A small tag for plan tiers in the agent's tint: an 18-DIP pill, or
    /// under Pixel a square chip.
    public static Border Badge(TextBlock text, DisplayProvider provider)
    {
        if (Looks.Pixel)
        {
            var dark = Brand.IsDark();
            var color = PixelTokens.Agent(provider, dark);
            text.Style = Res<Style>("PixelBodyTextStyle");
            text.Foreground = new SolidColorBrush(color);
            text.VerticalAlignment = VerticalAlignment.Center;
            return new Border
            {
                Child = text,
                Height = 20,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(PixelTokens.Tint(color, dark)),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        var isDark = Brand.IsDark();
        Chip(text);
        return new Border
        {
            Child = text,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(ClassicTokens.Tint(ClassicTokens.Identity(provider, isDark), isDark)),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// Chip type: 10/14, semibold, spaced out, in ink.
    public static void Chip(TextBlock text)
    {
        text.FontSize = 10;
        text.LineHeight = 14;
        text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        text.CharacterSpacing = 40;
        text.Foreground = Res<Brush>("TextFillColorPrimaryBrush");
        text.VerticalAlignment = VerticalAlignment.Center;
    }

    /// Secondary text with an optional warning after it: the warning glyph
    /// and its text in the caution ink. The pixel face has no warning sign,
    /// so under Pixel the warning keeps only its colour.
    public static void SetStatus(TextBlock block, string status, string? warning)
    {
        block.Inlines.Clear();
        block.Inlines.Add(new Run { Text = status });
        if (warning is null) return;
        var caution = Res<Brush>("SystemFillColorCautionBrush");
        block.Inlines.Add(new Run { Text = "  ·  " });
        if (!Looks.Pixel)
        {
            block.Inlines.Add(new Run
            {
                Text = "\uE7BA ",
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = caution,
            });
        }
        block.Inlines.Add(new Run { Text = warning, Foreground = caution });
    }

    /// The pixel kit's status line: rich text, so the warning can carry the
    /// kit's warning sign.
    public static RichTextBlock PixelStatusText() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = PixelTheme.Font,
        FontSize = Res<double>("PixelBodySize"),
        LineHeight = 20,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    /// Secondary text, then the warning after the pixel warning sign, both
    /// in the caution ink.
    public static void SetStatus(RichTextBlock block, string status, string? warning)
    {
        var line = new Paragraph();
        line.Inlines.Add(new Run { Text = status });
        if (warning is not null)
        {
            var caution = Res<Brush>("SystemFillColorCautionBrush");
            var sign = PixelIcons.Icon("warning");
            sign.Foreground = caution;
            // Down onto the baseline, by whole cells, without growing the line.
            sign.Margin = new Thickness(0, 0, 6, -4);
            line.Inlines.Add(new Run { Text = "  ·  " });
            line.Inlines.Add(new InlineUIContainer { Child = sign });
            line.Inlines.Add(new Run { Text = warning, Foreground = caution });
        }
        block.Blocks.Clear();
        block.Blocks.Add(line);
    }

    /// A tooltip in the current look (tooltips open in popups, beyond the
    /// window's styles) that closes as the pointer leaves its owner.
    public static void SetTip(FrameworkElement owner, string text)
    {
        var tip = new ToolTip { Content = text, Style = Looks.Kit<Style>("ToolTipStyle") };
        ToolTipService.SetToolTip(owner, tip);
        tip.Opened += (_, _) => TipGuard.Watch(tip, owner);
    }

    public static async Task Alert(XamlRoot root, string title, string message)
    {
        var dialog = Dialog(root);
        dialog.Title = title;
        dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        dialog.CloseButtonText = L10n.Tr("OK");
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    /// A dialog in the current look: a dialog lives outside the window's
    /// tree, so it carries a kit of its own.
    public static ContentDialog Dialog(XamlRoot root)
    {
        var dialog = new ContentDialog { XamlRoot = root };
        if (root.Content is FrameworkElement content)
        {
            dialog.Resources.MergedDictionaries.Add(Looks.NewKit(root.RasterizationScale));
            dialog.Style = Looks.Kit<Style>("ContentDialogStyle");
            dialog.PrimaryButtonStyle = Looks.Kit<Style>("AccentButtonStyle");
            dialog.CloseButtonStyle = Looks.Kit<Style>("ButtonStyle");
            dialog.RequestedTheme = content.ActualTheme;
        }
        return dialog;
    }
}
