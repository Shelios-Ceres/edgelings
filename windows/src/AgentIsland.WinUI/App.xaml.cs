using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Model;
using AgentIsland.Usage;
using AgentIsland.WinUI.Drawer;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;

namespace AgentIsland.WinUI;

public sealed partial class App : Application
{
    private DrawerWindow? _drawer;
    private TrayIcon? _tray;
    private Window? _hoverPreview;

    public App()
    {
        if (Brand.ForcedDark is { } dark) RequestedTheme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        InitializeComponent();
        // The drawer and tray outlive every window; quitting is explicit.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        // A tray app should outlive one bad frame: log it and carry on.
        UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "XAML");
            e.Handled = true;
        };
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_HOVER_PREVIEW") == "1")
        {
            _hoverPreview = new HoverCardPreview();
            _hoverPreview.Activate();
            return;
        }
        UiThread.Capture();
        Preferences.MigrateLegacyPrefix();
        AppLanguageStore.ApplyAtStartup();
#if !DEBUG
        // Debug runs stay out of the Run key.
        LaunchAtLogin.Repoint();
#endif

        _drawer = new DrawerWindow();
        _tray = new TrayIcon();

        ActivityMonitor.Shared.Start();
        UsageStore.Shared.StartAutoRefresh();
        CostStore.Shared.StartAutoRefresh();

        DebugHooks.Schedule(_drawer, _tray);
    }

    internal DrawerWindow? Drawer => _drawer;

    public void RevealDrawer() => _drawer?.Reveal();

    public void ToggleDrawer() => _drawer?.ToggleDeliberately();

    /// A language change rebuilds the surfaces whose labels were baked in.
    public void Relocalize()
    {
        _drawer?.Relocalize();
        _tray?.Relocalize();
        Settings.SettingsWindow.Relocalize();
    }

    public void Quit()
    {
        _tray?.Dispose();
        _drawer?.Shutdown();
        Exit();
    }
}
