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
        Windows.Graphics.PointInt32? at = null)
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
        MenuHost.Show(menu, keepOpen, at, kit: true);
    }
}
