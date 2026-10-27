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
    private Window? _frameSpike;

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
        UiThread.Capture();
        CoreLog.Sink = CrashLog.Write;
        Preferences.MigrateLegacyPrefix();
        AppLanguageStore.ApplyAtStartup();
        // The test window's cards read in the app's language too.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FRAME_FILM") is { Length: > 0 } frameFilm)
        {
            _frameSpike = new FrameFilm(frameFilm);
            return;
        }
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FRAME_SPIKE") == "1")
        {
            _frameSpike = new FrameSpike();
            return;
        }
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_HOVER_PREVIEW") == "1")
        {
            _hoverPreview = new HoverCardPreview();
            _hoverPreview.Activate();
            return;
        }
#if !DEBUG
        // Debug runs stay out of the Run key.
        LaunchAtLogin.Repoint();
#endif

        _drawer = new DrawerWindow();
        _tray = new TrayIcon();

        ActivityMonitor.Shared.Start();
        UsageStore.Shared.StartAutoRefresh();
        CostStore.Shared.StartAutoRefresh();
        FollowSystemTheme();

        DebugHooks.Schedule(_drawer, _tray);
    }

    // Held so its event keeps coming.
    private readonly Windows.UI.ViewManagement.UISettings _colors = new();
    private (bool Dark, bool HighContrast) _scheme;

    /// Templates repaint themselves when Windows turns light or dark (by hand
    /// or on a schedule), but what code painted in the theme of the moment
    /// does not: a switch rebuilds those surfaces, as a language switch does.
    private void FollowSystemTheme()
    {
        static (bool, bool) Scheme() => (Brand.IsDark(), new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
        _scheme = Scheme();
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _colors.ColorValuesChanged += (_, _) => queue.TryEnqueue(() =>
        {
            var scheme = Scheme();
            if (scheme == _scheme) return;
            _scheme = scheme;
            Relocalize();
        });
    }

    internal DrawerWindow? Drawer => _drawer;

    public void RevealDrawer() => _drawer?.Reveal();

    public void ToggleDrawer() => _drawer?.ToggleDeliberately();

    /// A language change rebuilds the surfaces whose labels were baked in
    /// (and a theme switch those whose colours were).
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
