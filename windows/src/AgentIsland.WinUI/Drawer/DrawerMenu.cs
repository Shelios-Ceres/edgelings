using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Settings;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Drawer;

/// A character's right-click menu: which characters the drawer shows, which
/// edge it docks to, and the app's windows.
internal static class DrawerMenu
{
    /// Under Classic the title's mark loads first: an icon swapped in while
    /// the menu is open closes it.
    public static async Task ShowAsync(
        DisplayProvider provider,
        IReadOnlyList<(DisplayProvider Provider, bool Requested)> requestedVisibility,
        bool keepOpen = false,
        Windows.Graphics.PointInt32? at = null,
        double scale = 1)
    {
        var pixel = Looks.Pixel;
        var dark = Brand.IsDark();
        var mark = pixel ? null : await Brand.MarkAsync(provider, dark);
        using var english = Looks.English();
        var menu = new MenuFlyout();
        var title = new MenuFlyoutItem { Text = ProviderIdentity.DisplayName(provider) };
        title.Click += (_, _) => SettingsWindow.Open(provider);
        if (pixel)
        {
            title.Icon = new ImageIcon { Source = PixelIcons.Face(provider) };
        }
        else
        {
            // The agent's puck: its tint behind the mark, through Tag.
            title.Style = Looks.Kit<Style>("MenuTitleItemStyle");
            Puck.SetTint(title, new SolidColorBrush(ClassicTokens.Tint(ClassicTokens.Identity(provider, dark), dark)));
            if (mark is not null) title.Icon = new ImageIcon { Source = mark, Width = 13, Height = 13 };
        }
        menu.Items.Add(title);
        menu.Items.Add(new MenuFlyoutSeparator());
        if (LookMenu(provider, pixel, scale) is { } looks) menu.Items.Add(looks);

        // Hiding one takes it out of the menus and Usage & cost as well.
        var shownMenu = new MenuFlyoutSubItem { Text = L10n.Tr("Shown agents"), Icon = MenuHost.Glyph("\uE890") };
        foreach (var (other, on) in DrawerVisibility.Switches(requestedVisibility))
        {
            var item = new ToggleMenuFlyoutItem { Text = ProviderIdentity.DisplayName(other), IsChecked = on };
            AutomationProperties.SetAutomationId(item, $"Drawer.Show.{other.RawValue()}");
            item.Click += (_, _) => ProviderVisibilityStore.Shared.SetShown(other, item.IsChecked);
            shownMenu.Items.Add(item);
        }
        menu.Items.Add(shownMenu);
        var locked = new ToggleMenuFlyoutItem
        {
            Text = L10n.Tr("Lock position"),
            Icon = MenuHost.Glyph("\uE72E"),
            IsChecked = DrawerStore.Shared.Locked,
        };
        AutomationProperties.SetAutomationId(locked, "Drawer.Locked");
        locked.Click += (_, _) => DrawerStore.Shared.Locked = locked.IsChecked;
        menu.Items.Add(locked);
        var right = DrawerStore.Shared.Side == DrawerSide.Right;
        menu.Items.Add(MenuHost.Item(
            L10n.Tr(right ? "Dock to the left edge" : "Dock to the right edge"), "\uE8AB",
            () => DrawerStore.Shared.Side = right ? DrawerSide.Left : DrawerSide.Right));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuHost.Item(L10n.Tr("Usage & cost…"), "\uE8C7", () => SettingsWindow.OpenUsage()));
        menu.Items.Add(MenuHost.Item(L10n.Tr("Settings…"), "\uE713", () => SettingsWindow.Open()));
        // The previews were drawn for the menu's icons: nothing keeps their engines.
        menu.Closed += (_, _) => LookPreviews.Release();
        MenuHost.Show(menu, keepOpen, at, kit: true);
    }

    /// "Look": one radio item per look the agent can wear, its face (Pixel) or idle
    /// frame (Classic) for an icon, the worn one checked; a pick applies at once.
    /// Only for agents with more than one look; Cursor's is Grok's.
    private static MenuFlyoutSubItem? LookMenu(DisplayProvider provider, bool pixel, double scale)
    {
        var theme = pixel ? CharacterTheme.Pixel : CharacterTheme.Classic;
        var looks = LookCatalog.For(theme, provider);
        if (!provider.HasCharacter() || looks.Count < 2) return null;
        var worn = LookCatalog.Of(theme, provider).Id;
        var sub = new MenuFlyoutSubItem { Text = L10n.Tr("Look"), Icon = MenuHost.Glyph("\uE771") };
        AutomationProperties.SetAutomationId(sub, "Drawer.Look");
        foreach (var look in looks)
        {
            // Pixel marks the worn look with the kit's check, as Shown agents does; Classic with its radio dot.
            MenuFlyoutItem item = pixel
                ? new ToggleMenuFlyoutItem { Text = L10n.Tr(look.Name), IsChecked = look.Id == worn }
                : new RadioMenuFlyoutItem { Text = L10n.Tr(look.Name), GroupName = "look", IsChecked = look.Id == worn };
            if (pixel) item.Icon = new ImageIcon { Source = PixelIcons.Face(provider, look.Id) };
            else if (LookPreviews.Image(theme, provider, look.Id, 20, scale) is { } frame) item.Icon = new ImageIcon { Source = frame, Width = 20, Height = 20 };
            AutomationProperties.SetAutomationId(item, $"Drawer.Look.{look.Id}");
            item.Click += (_, _) => DrawerStore.Shared.SetLookId(theme, provider, look.Id);
            sub.Items.Add(item);
        }
        return sub;
    }
}
