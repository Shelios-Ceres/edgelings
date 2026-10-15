using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Settings;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Drawer;

/// A character's right-click menu: its look, which characters the drawer
/// shows, which edge it docks to, and the app's windows.
internal static class DrawerMenu
{
    public static void Show(
        DisplayProvider provider,
        string shape,
        string color,
        IReadOnlyList<string>? shapes,
        IReadOnlyList<string>? colors,
        IReadOnlyList<(DisplayProvider Provider, bool Requested)> requestedVisibility,
        bool merged,
        Action<string, string> changeLook,
        bool keepOpen = false,
        Windows.Graphics.PointInt32? at = null)
    {
        using var english = Looks.English();
        var pixel = Looks.Pixel;
        var dark = Brand.IsDark();
        var menu = new MenuFlyout();
        var title = new MenuFlyoutItem { Text = ProviderIdentity.DisplayName(provider) };
        title.Click += (_, _) => SettingsWindow.Open(provider);
        if (pixel) title.Icon = new ImageIcon { Source = PixelIcons.Face(provider) };
        else _ = SetMarkAsync(title, provider, dark);
        menu.Items.Add(title);
        menu.Items.Add(new MenuFlyoutSeparator());

        if (shapes is not null && colors is not null)
        {
            var shapeMenu = new MenuFlyoutSubItem { Text = L10n.Tr("Shape"), Icon = MenuHost.Glyph("\uE76E") };
            foreach (var option in shapes)
            {
                var item = new RadioMenuFlyoutItem { Text = DrawerLooks.ShapeLabel(option), GroupName = "shape", IsChecked = option == shape };
                item.Click += (_, _) => changeLook(option, color);
                shapeMenu.Items.Add(item);
            }
            var colorMenu = new MenuFlyoutSubItem { Text = L10n.Tr("Color"), Icon = MenuHost.Glyph("\uE790") };
            foreach (var option in colors)
            {
                var item = new RadioMenuFlyoutItem
                {
                    Text = DrawerLooks.ColorLabel(option),
                    GroupName = "color",
                    IsChecked = option == color,
                    Icon = Dot(Brand.Readable(DrawerLooks.Swatch(option), dark)),
                };
                item.Click += (_, _) => changeLook(shape, option);
                colorMenu.Items.Add(item);
            }
            menu.Items.Add(shapeMenu);
            menu.Items.Add(colorMenu);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var shownMenu = new MenuFlyoutSubItem { Text = L10n.Tr("Show in drawer"), Icon = MenuHost.Glyph("\uE890") };
        foreach (var (other, on) in DrawerVisibility.Switches(requestedVisibility, merged))
        {
            var item = new ToggleMenuFlyoutItem { Text = ProviderIdentity.DisplayName(other), IsChecked = on };
            AutomationProperties.SetAutomationId(item, $"Drawer.Show.{other.RawValue()}");
            item.Click += (_, _) =>
            {
                foreach (var choice in DrawerVisibility.SetBy(other, merged)) DrawerStore.Shared.SetShown(choice, item.IsChecked);
            };
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
        menu.Items.Add(MenuHost.Item(L10n.Tr("Cost…"), "\uE8C7", () => Cost.CostWindow.Open()));
        menu.Items.Add(MenuHost.Item(L10n.Tr("Settings…"), "\uE713", () => SettingsWindow.Open()));
        MenuHost.Show(menu, keepOpen, at, pixel);
    }

    private static PathIcon Dot(uint rgb) => new()
    {
        Data = new EllipseGeometry { Center = new Windows.Foundation.Point(8, 8), RadiusX = 5.5, RadiusY = 5.5 },
        Foreground = Brand.Brush(rgb),
    };

    private static async Task SetMarkAsync(MenuFlyoutItem item, DisplayProvider provider, bool dark)
    {
        if (await Brand.MarkAsync(provider, dark) is { } mark) item.Icon = new ImageIcon { Source = mark };
    }
}
