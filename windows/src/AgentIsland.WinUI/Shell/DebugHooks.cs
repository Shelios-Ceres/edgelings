using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;
using AgentIsland.WinUI.Settings;

namespace AgentIsland.WinUI.Shell;

/// Verification entry points, driven by environment variables.
internal static class DebugHooks
{
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    /// Runs once the drawer's content has loaded.
    public static void Schedule(DrawerWindow drawer, TrayIcon tray)
    {
        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.5);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Run(drawer, tray);
        _timer.Start();
    }

    private static void Run(DrawerWindow drawer, TrayIcon tray)
    {
        // AGENTISLAND_DEBUG_DRAWER_HIDDEN=1: measure without the drawer; the
        // tray menu then offers to show it.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DRAWER_HIDDEN") == "1") drawer.ToggleDeliberately();
        // AGENTISLAND_DEBUG_OPEN_SETTINGS=1 or a provider name to focus it;
        // with AGENTISLAND_DEBUG_SNAPSHOT=<dir> every page is saved as a PNG.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_OPEN_SETTINGS") is { Length: > 0 } settings)
        {
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SNAPSHOT") is { Length: > 0 } directory)
            {
                _ = SettingsWindow.SnapshotPagesAsync(directory, DisplayProviders.Parse(settings));
            }
            else
            {
                SettingsWindow.Open(DisplayProviders.Parse(settings));
            }
        }
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_OPEN_COST") == "1")
        {
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SNAPSHOT") is { Length: > 0 } directory)
            {
                _ = Cost.CostWindow.SnapshotAsync(directory);
            }
            else
            {
                Cost.CostWindow.Open();
            }
        }
        // AGENTISLAND_DEBUG_STATES=<dir>: the pixel kit's control states.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_STATES") is { Length: > 0 } states)
        {
            _ = SettingsWindow.SnapshotStatesAsync(states);
        }
        // AGENTISLAND_DEBUG_MENU=tray|drawer: open that menu at the pointer.
        switch (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_MENU"))
        {
            case "tray":
                tray.ShowMenu(keepOpen: true);
                break;
            case "drawer":
                drawer.DebugMenu();
                break;
        }
        var say = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SAY");
        if (say == "Locked") drawer.DebugSayLocked();
        else if (Enum.TryParse<AgentIsland.Core.AgentPhase>(say, out var phase)) drawer.DebugSay(phase);
        // AGENTISLAND_DEBUG_DRAWER_FILM=<dir>: the drawer moments frame by frame.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DRAWER_FILM") is { Length: > 0 } film) drawer.DebugFilm(film);
        // AGENTISLAND_DEBUG_CONTROL_FILM=<dir>: a tooltip's, a drop-down's and a dialog's motion.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_CONTROL_FILM") is { Length: > 0 } controlFilm) _ = SettingsWindow.FilmControlsAsync(controlFilm);
        // AGENTISLAND_DEBUG_COST_FILM=<dir>: the Cost window's period switches and a scan's count.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_COST_FILM") is { Length: > 0 } costFilm) _ = Cost.CostWindow.FilmAsync(costFilm);
        // AGENTISLAND_DEBUG_SETTINGS_FILM=<dir>: Settings' page switch, expanders, regrouping and arrival.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SETTINGS_FILM") is { Length: > 0 } settingsFilm) _ = SettingsWindow.FilmSettingsAsync(settingsFilm);
        // AGENTISLAND_DEBUG_MENU_FILM=<dir>: the drawer menu's motion frame by frame.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_MENU_FILM") is { Length: > 0 } menuFilm) drawer.DebugMenuFilm(menuFilm);
        // AGENTISLAND_DEBUG_DRAWER_MOMENTS=1: every drawer moment, live.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DRAWER_MOMENTS") == "1") drawer.DebugMoments();
        // AGENTISLAND_DEBUG_BUBBLE=all|<n>: open the hover card.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_BUBBLE") is { Length: > 0 } bubble)
        {
            drawer.DebugCard(int.TryParse(bubble, out var n) ? n : null,
                Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SNAPSHOT") is { Length: > 0 } cardSnapshot ? cardSnapshot : null);
        }
    }
}
