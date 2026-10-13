using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using AgentIsland.WinUI.Platform;
using AgentIsland.WinUI.Settings;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace AgentIsland.WinUI.Shell;

/// The notification-area icon: a click brings the drawer back with every
/// character's status; the menu opens the app's windows.
internal sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly System.Drawing.Icon _image;
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();

    public TrayIcon()
    {
        var size = Win32.GetSystemMetricsForDpi(49, 96 * 2); // SM_CXSMICON at 200%, downscaled by the shell
        _image = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "AgentIsland.ico"), size, size);
        _icon = new TaskbarIcon
        {
            Icon = _image,
            ToolTipText = "Agent Island",
            NoLeftClickDelay = true,
            LeftClickCommand = new Command(() => App.Current.RevealDrawer()),
            RightClickCommand = new Command(() => ShowMenu()),
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);

        UsageStore.Shared.PropertyChanged += OnDataChanged;
        ActivityMonitor.Shared.PropertyChanged += OnDataChanged;
        ProviderVisibilityStore.Shared.PropertyChanged += OnDataChanged;
        Update();
    }

    public void ShowMenu(bool keepOpen = false)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuHost.Item(L10n.Tr("Show / Hide drawer"), "\uE890", () => App.Current.ToggleDrawer()));
        menu.Items.Add(MenuHost.Item(L10n.Tr("Cost…"), "\uE8C7", () => Cost.CostWindow.Open()));
        menu.Items.Add(MenuHost.Item(L10n.Tr("Settings…"), "\uE713", () => SettingsWindow.Open()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuHost.Item(L10n.Tr("Quit Agent Island"), "\uE7E8", () => App.Current.Quit()));
        MenuHost.Show(menu, keepOpen);
    }

    public void Relocalize() => Update();

    private void OnDataChanged(object? sender, PropertyChangedEventArgs e) => _queue.TryEnqueue(Update);

    /// The tooltip carries the busiest state and each shown provider's
    /// primary quota window.
    private void Update()
    {
        var monitor = ActivityMonitor.Shared;
        var worst = ActivityState.Idle;
        var parts = new List<string>();
        foreach (var provider in DisplayProviders.All.Where(ProviderVisibilityStore.Shared.IsShown))
        {
            var window = ProviderUsage.For(provider).FiveHour;
            if (!window.HasError && StatusCards.Meters(provider).Count > 0)
            {
                parts.Add($"{ProviderIdentity.DisplayName(provider)} {Formatting.PercentInt(window.UsedPercent)}%");
            }
            var state = monitor.StateFor(provider.ToTriggerTool());
            if (state > worst) worst = state;
        }
        var status = worst switch
        {
            ActivityState.AuthRequired or ActivityState.RateLimited => L10n.Tr("Needs attention"),
            ActivityState.NetworkTrouble => L10n.Tr("Network trouble"),
            ActivityState.NeedsYou => L10n.Tr("Your turn"),
            ActivityState.Working => L10n.Tr("Running"),
            _ => null,
        };
        var text = "Agent Island"
            + (status is null ? "" : " · " + status)
            + (parts.Count == 0 ? "" : "\n" + string.Join(" · ", parts));
        // The shell caps tooltips at 127 characters.
        _icon.ToolTipText = text.Length <= 127 ? text : text[..126] + "…";
    }

    public void Dispose()
    {
        UsageStore.Shared.PropertyChanged -= OnDataChanged;
        ActivityMonitor.Shared.PropertyChanged -= OnDataChanged;
        ProviderVisibilityStore.Shared.PropertyChanged -= OnDataChanged;
        _icon.Dispose();
        _image.Dispose();
    }

    private sealed class Command(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
