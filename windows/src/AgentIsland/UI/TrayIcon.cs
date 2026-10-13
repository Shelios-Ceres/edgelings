using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// System tray entry point. The icon is the plain mark; the tooltip carries
/// the most urgent state of the providers the drawer shows and their 5-hour
/// usage. A left click reveals the drawer with everyone's status; a right
/// click opens the same Grok menu the drawer's characters use (show/hide,
/// cost, reports, settings, quit) rather than a WinForms menu that could
/// never quite match it.
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly System.Drawing.Icon _rendered;
    private readonly Action _toggleDrawer;
    private readonly Action _openCost;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private System.Windows.Window? _menuHost;

    public TrayIcon(Action showDrawer, Action toggleDrawer, Action openCost, Action openSettings, Action exit)
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _toggleDrawer = toggleDrawer;
        _openCost = openCost;
        _openSettings = openSettings;
        _exit = exit;

        _rendered = TrayIconRenderer.Render();
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Agent Island",
            Icon = _rendered,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) showDrawer();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
        };

        UsageStore.Shared.PropertyChanged += OnDataChanged;
        ActivityMonitor.Shared.PropertyChanged += OnDataChanged;
        ProviderVisibilityStore.Shared.PropertyChanged += OnDataChanged;
        Update();               // writes the first tooltip
        _icon.Visible = true;   // then show it

        // AGENTISLAND_DEBUG_MENU=tray: show the menu at the cursor for verification.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_MENU") == "tray")
        {
            _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => ShowMenu(keepOpen: true));
        }
    }

    /// A WPF menu only dismisses on an outside click when its window is the
    /// foreground one, so it opens from an invisible host window that is
    /// activated first — the WPF form of the SetForegroundWindow rule every
    /// tray menu follows.
    /// keepOpen is for the verification hook, which has no click to grant
    /// the host window the foreground.
    private void ShowMenu(bool keepOpen = false)
    {
        _menuHost ??= new System.Windows.Window
        {
            WindowStyle = System.Windows.WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 1,
            Height = 1,
        };
        var cursor = System.Windows.Forms.Cursor.Position;
        _menuHost.Left = cursor.X;
        _menuHost.Top = cursor.Y;
        _menuHost.Show();
        _menuHost.Activate();

        var menu = Theme.GrokMenu.Create();
        void Add(string label, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = Localization.L10n.Tr(label) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Show / Hide drawer", _toggleDrawer);
        Add("Cost…", _openCost);
        Add("Share weekly report…", () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly));
        Add("Share monthly report…", () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly));
        Add("Settings…", _openSettings);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Quit Agent Island", _exit);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget = _menuHost;
        menu.StaysOpen = keepOpen;
        menu.Closed += (_, _) => _menuHost?.Hide();
        menu.IsOpen = true;
    }

    private void OnDataChanged(object? sender, PropertyChangedEventArgs e) =>
        _dispatcher.BeginInvoke(Update);

    private void Update()
    {
        var monitor = ActivityMonitor.Shared;
        var worst = ActivityState.Idle;
        var parts = new List<string>();
        foreach (var provider in DisplayProviders.All.Where(ProviderVisibilityStore.Shared.IsShown))
        {
            var window = ProviderUsage.For(provider).FiveHour;
            if (!window.HasError)
            {
                parts.Add($"{ProviderIdentity.DisplayName(provider)} {Percent(window.UsedPercent)}");
            }
            var state = monitor.StateFor(provider.ToTriggerTool());
            if (state > worst) worst = state;
        }

        // NotifyIcon.Text is capped at 127 characters.
        var joined = string.Join(" · ", parts);
        var status = StatusWord(worst);
        var text = "Agent Island"
            + (status is null ? "" : " · " + status)
            + (joined.Length == 0 ? "" : " · " + joined);
        _icon.Text = text.Length <= 127 ? text : text[..126] + "…";
    }

    private static string Percent(double fraction) => $"{Formatting.PercentInt(fraction)}%";

    private static string? StatusWord(ActivityState state) => state switch
    {
        ActivityState.AuthRequired => Localization.L10n.Tr("Needs attention"),
        ActivityState.RateLimited => Localization.L10n.Tr("Needs attention"),
        ActivityState.NetworkTrouble => Localization.L10n.Tr("Network trouble"),
        ActivityState.NeedsYou => Localization.L10n.Tr("Your turn"),
        ActivityState.Working => Localization.L10n.Tr("Running"),
        _ => null,
    };

    public void Dispose()
    {
        UsageStore.Shared.PropertyChanged -= OnDataChanged;
        ActivityMonitor.Shared.PropertyChanged -= OnDataChanged;
        ProviderVisibilityStore.Shared.PropertyChanged -= OnDataChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _rendered.Dispose();
        _menuHost?.Close();
    }
}
