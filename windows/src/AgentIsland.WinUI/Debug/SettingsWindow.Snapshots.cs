using AgentIsland.Model;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Settings;

// The AGENTISLAND_DEBUG_* snapshots and the frame-cycle check of this window: every page, the Usage & cost periods, the kit's control states and the GDI count over repeated looks.
internal sealed partial class SettingsWindow
{
    /// AGENTISLAND_DEBUG_FRAME_CYCLE: the window's frame must leak no GDI or
    /// USER objects over repeated look switches and closes.
    public static async Task FrameCycleAsync()
    {
        try
        {
            await FrameCycleCoreAsync();
        }
        catch (Exception error)
        {
            DebugTrace.Write("frame cycle failed: " + error);
        }
    }

    private static async Task FrameCycleCoreAsync()
    {
        string Counts() => $"GDI {Platform.Win32.GuiObjects(0)}, USER {Platform.Win32.GuiObjects(1)}";
        Open();
        await Task.Delay(3000);
        var store = DrawerStore.Shared;
        DebugTrace.Write("frame cycle start: " + Counts());
        for (var i = 0; i < 8; i++)
        {
            store.Theme = i % 2 == 0 ? CharacterTheme.Pixel : CharacterTheme.Classic;
            await Task.Delay(700);
            if (i % 4 == 3) DebugTrace.Write($"after {i + 1} look switches: " + Counts());
        }
        store.Theme = CharacterTheme.Classic;
        await Task.Delay(1000);
        for (var i = 0; i < 40; i++)
        {
            _shared?.AppWindow.Hide();
            await Task.Delay(250);
            Open();
            await Task.Delay(450);
            if (i % 10 == 9) DebugTrace.Write($"after {i + 1} hide/open: " + Counts());
        }
        DebugTrace.Write("frame cycle end: " + Counts());
    }

    /// AGENTISLAND_DEBUG_SNAPSHOT: every page, rendered by the app itself.
    public static async Task SnapshotPagesAsync(string directory, DisplayProvider? focus)
    {
        Open(focus);
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        foreach (var page in Enum.GetValues<Page>())
        {
            if (page != Page.Agents || focus is null) window.SelectPage(page);
            if (page == Page.Agents && focus is { } provider) window.Focus(provider, animate: false);
            await Task.Delay(1500);
            if (window.Content is { } content) await Shell.Snapshot.SaveAsync(content, Path.Combine(directory, $"settings-{page}.png"));
            if (window._scroll.Content is UIElement body) await Shell.Snapshot.SaveAsync(body, Path.Combine(directory, $"settings-{page}-full.png"));
        }
    }

    /// AGENTISLAND_DEBUG_OPEN_COST with AGENTISLAND_DEBUG_SNAPSHOT: the Usage &
    /// cost page in each period, rendered by the app itself.
    public static async Task SnapshotCostAsync(string directory)
    {
        OpenUsage();
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        var periods = new[] { "Today", "Week", "Month" };
        for (var i = 0; i < periods.Length; i++)
        {
            window._costPage?.DebugPeriod(i);
            await Task.Delay(1200);
            if (window.Content is { } content) await Shell.Snapshot.SaveAsync(content, Path.Combine(directory, $"cost-{periods[i]}.png"));
            if (window._scroll.Content is UIElement body) await Shell.Snapshot.SaveAsync(body, Path.Combine(directory, $"cost-{periods[i]}-full.png"));
        }
    }

    /// AGENTISLAND_DEBUG_STATES: the kit's controls at rest, in hover, pressed
    /// and keyboard focus, and an agent's card in hand, rendered by the app
    /// itself; each with its card, so a focus ring outside the control shows.
    public static async Task SnapshotStatesAsync(string directory)
    {
        Open();
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        async Task Shoot(UIElement frame, string name) =>
            await Shell.Snapshot.SaveAsync(frame, Path.Combine(directory, name + ".png"));
        async Task States(Control control, string name, UIElement? around = null)
        {
            var frame = around ?? (UIElement?)Above<SettingsCard>(control) ?? control;
            await Shoot(frame, $"{name}-rest");
            foreach (var state in new[] { "PointerOver", "Pressed" })
            {
                VisualStateManager.GoToState(control, state, false);
                await Task.Delay(200);
                await Shoot(frame, $"{name}-{state.ToLowerInvariant()}");
            }
            VisualStateManager.GoToState(control, "Normal", false);
            control.Focus(FocusState.Keyboard);
            await Task.Delay(400);
            await Shoot(frame, $"{name}-focus");
        }

        window.SelectPage(Page.Drawer);
        await Task.Delay(1500);
        var page = (DependencyObject)window._scroll.Content;
        if (Below<ToggleSwitch>(page) is { } toggle) await States(toggle, "toggle");
        if (Below<ComboBox>(page) is { } combo) await States(combo, "combo");

        // An agent card's arrow, and the card in hand (M11).
        window.SelectPage(Page.Agents);
        await Task.Delay(1500);
        if (window._agents.Values.FirstOrDefault(agent => agent.Down.IsEnabled) is { } first && window._agentList is { } list)
        {
            await States(first.Down, "icon-button", first.Expander);
            first.Hold(1);
            await Task.Delay(200);
            await Shoot(list, "card-in-hand");
            first.Hold(0);
        }

        window.SelectPage(Page.Usage);
        await Task.Delay(1500);
        page = (DependencyObject)window._scroll.Content;
        if (Below<Button>(page) is { } button)
        {
            await States(button, "button");
            if (Below<Button>(page, other => other != button) is { } accent) await States(accent, "accent-button");
        }
        // The page list: another page's item in hover and pressed, the open
        // page's item in keyboard focus.
        if (window._pages.ContainerFromIndex(0) is ListViewItem item)
        {
            foreach (var state in new[] { "PointerOver", "Pressed" })
            {
                VisualStateManager.GoToState(item, state, false);
                await Task.Delay(200);
                await Shoot(window._pages, $"nav-{state.ToLowerInvariant()}");
            }
            VisualStateManager.GoToState(item, "Normal", false);
        }
        if (window._pages.ContainerFromIndex((int)Page.Usage) is ListViewItem open)
        {
            open.Focus(FocusState.Keyboard);
            await Task.Delay(400);
            await Shoot(window._pages, "nav-focus");
        }
    }

    private static T? Below<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && (match is null || match(found))) return found;
            if (Below(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    private static T? Above<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T found) return found;
        }
        return null;
    }
}
