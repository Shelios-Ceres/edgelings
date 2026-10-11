using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// System tray entry point. The icon visualizes status (a usage ring tinted
/// by the most urgent state of the providers the drawer shows), a left click
/// reveals the drawer with everyone's status, and the menu holds show/hide,
/// cost, reports, settings and quit.
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private System.Drawing.Icon? _rendered;

    public TrayIcon(Action showDrawer, Action toggleDrawer, Action openCost, Action openSettings, Action exit)
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Localization.L10n.Tr("Show / Hide drawer"), null, (_, _) => toggleDrawer());
        menu.Items.Add(Localization.L10n.Tr("Cost…"), null, (_, _) => openCost());
        menu.Items.Add(Localization.L10n.Tr("Share weekly report…"), null,
            (_, _) => Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly));
        menu.Items.Add(Localization.L10n.Tr("Share monthly report…"), null,
            (_, _) => Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly));
        menu.Items.Add(Localization.L10n.Tr("Settings…"), null, (_, _) => openSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Localization.L10n.Tr("Quit Agent Island"), null, (_, _) => exit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Agent Island",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) showDrawer();
        };

        UsageStore.Shared.PropertyChanged += OnDataChanged;
        ActivityMonitor.Shared.PropertyChanged += OnDataChanged;
        ProviderVisibilityStore.Shared.PropertyChanged += OnDataChanged;
        Update();               // paints the first icon
        _icon.Visible = true;   // then show it
    }

    private void OnDataChanged(object? sender, PropertyChangedEventArgs e) =>
        _dispatcher.BeginInvoke(Update);

    private void Update()
    {
        var monitor = ActivityMonitor.Shared;
        double used = 0;
        var worst = ActivityState.Idle;
        var parts = new List<string>();
        foreach (var provider in DisplayProviders.All.Where(ProviderVisibilityStore.Shared.IsShown))
        {
            var window = ProviderUsage.For(provider).FiveHour;
            if (!window.HasError)
            {
                used = Math.Max(used, window.UsedPercent);
                parts.Add($"{ProviderIdentity.DisplayName(provider)} {Percent(window.UsedPercent)}");
            }
            var state = monitor.StateFor(provider.ToTriggerTool());
            if (state > worst) worst = state;
        }

        var next = TrayIconRenderer.Render(used, worst);
        _icon.Icon = next;
        _rendered?.Dispose();
        _rendered = next;

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
        ActivityState.Stalled => Localization.L10n.Tr("Needs attention"),
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
        _rendered?.Dispose();
    }
}
