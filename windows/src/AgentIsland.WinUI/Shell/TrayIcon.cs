using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using AgentIsland.WinUI.Platform;
using AgentIsland.WinUI.Settings;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentIsland.WinUI.Shell;

/// The notification-area icon: a click brings the drawer back with every
/// character's status; the menu opens the app's windows.
internal sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private System.Drawing.Icon _image;
    private (string File, int Size) _imageKey;
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();

    public TrayIcon()
    {
        _imageKey = ImageKey();
        _image = Load(_imageKey);
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
        DrawerStore.Shared.PropertyChanged += OnLookChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Update();
    }

    /// Classic's icon is drawn smooth, so the shell may shrink its 200 %
    /// size. The pixel icon turns soft when scaled, so it comes at exactly
    /// the small-icon size of the primary display, where the notification
    /// area lives.
    private static (string File, int Size) ImageKey() => Looks.Pixel
        ? ("AgentIsland-pixel.ico", Win32.GetSystemMetricsForDpi(Win32.SmCxSmIcon, Win32.PrimaryDpi()))
        : ("AgentIsland.ico", Win32.GetSystemMetricsForDpi(Win32.SmCxSmIcon, 96 * 2));

    private static System.Drawing.Icon Load((string File, int Size) key)
    {
        var image = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", key.File), key.Size, key.Size);
        DebugTrace.Write($"tray icon {key.File} {image.Width}x{image.Height}");
        return image;
    }

    private void OnLookChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DrawerStore.Theme)) _queue.TryEnqueue(Reload);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _queue.TryEnqueue(Reload);

    /// The other character style, or a new scale on the primary display.
    private void Reload()
    {
        var key = ImageKey();
        if (key == _imageKey) return;
        var old = _image;
        _image = Load(key);
        _imageKey = key;
        _icon.Icon = _image;
        old.Dispose();
    }

    public void ShowMenu(bool keepOpen = false)
    {
        var menu = new MenuFlyout();
        var hidden = App.Current.Drawer?.DeliberatelyHidden ?? false;
        menu.Items.Add(MenuHost.Item(L10n.Tr(hidden ? "Show drawer" : "Hide drawer"), hidden ? "\uE890" : "\uED1A",
            () => App.Current.ToggleDrawer()));
        var agents = new MenuFlyoutSubItem { Text = L10n.Tr("Agents"), Icon = MenuHost.Glyph("\uE99A") };
        AutomationProperties.SetAutomationId(agents, "Tray.Agents");
        // In the drawer's order, without the agents the user hid.
        foreach (var provider in DrawerStore.Shared.Order.Where(provider => !ProviderVisibilityStore.Shared.IsHidden(provider)))
        {
            var phase = App.Current.Drawer?.PhaseOf(provider).Phase ?? AgentPhase.Idle;
            var item = MenuHost.Item($"{ProviderIdentity.DisplayName(provider)} · {DrawerLooks.PhaseLabel(phase)}", null,
                () => SettingsWindow.Open(provider));
            AutomationProperties.SetAutomationId(item, $"Tray.Agent.{provider.RawValue()}");
            agents.Items.Add(item);
        }
        menu.Items.Add(agents);
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
        foreach (var provider in DrawerStore.Shared.Order.Where(ProviderVisibilityStore.Shared.IsShown))
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
        DrawerStore.Shared.PropertyChanged -= OnLookChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
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
