using System.Windows;
using AgentIsland.Core;
using AgentIsland.UI;
using AgentIsland.Usage;

namespace AgentIsland;

public partial class App : System.Windows.Application
{
    public static App Instance => (App)Current;

    private UI.Drawer.DrawerWindow? _drawer;
    private TrayIcon? _tray;
    private System.Threading.Mutex? _singleInstance;

    /// Two live copies sharing %APPDATA%\AgentIsland corrupt each other's
    /// preferences (each periodic save resurrects that instance's stale
    /// snapshot), so a second launch bows out. The wait is generous because
    /// the auto-updater's relaunch overlaps the old process by design —
    /// the new exe must outwait the old one's exit, not give up. Demo/debug
    /// copies skip the gate: running one beside the real app is a supported
    /// verification flow.
    private bool ClaimSingleInstance()
    {
        if (AppEnvironment.Current != AppMode.Normal) return true;
        // One-shot headless card renders run beside the live instance and
        // exit on their own; preference writes merge (P15), so this is safe.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_REPORT_SNAPSHOT") is not null
            || Environment.GetEnvironmentVariable("AGENTISLAND_MONTHLY_SNAPSHOT") is not null)
        {
            return true;
        }
        _singleInstance = new System.Threading.Mutex(
            initiallyOwned: false, @"Local\AgentIsland.SingleInstance");
        try
        {
            return _singleInstance.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (System.Threading.AbandonedMutexException)
        {
            return true; // previous holder died without releasing — ours now
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!ClaimSingleInstance())
        {
            Shutdown();
            return;
        }
        InstallCrashLogger();
        // Before any store singleton reads a key: settings written by pre-1.7
        // builds carry the MacIsland.* prefix and must land on AgentIsland.*.
        Core.Preferences.MigrateLegacyPrefix();
        Model.AppLanguageStore.ApplyAtStartup();

        if (AppEnvironment.IsDemo)
        {
            ActivityMonitor.Shared.Demo(ActivityState.Working);
        }

        _drawer = new UI.Drawer.DrawerWindow();
        _drawer.Show();
        _tray = CreateTray();

        ActivityMonitor.Shared.Start();
        UsageStore.Shared.StartAutoRefresh();
        Cost.CostStore.Shared.StartAutoRefresh();

        // Release card: once per version, shortly after the drawer lands
        // (WhatsNewGate is its own suppressor for demo/snapshot runs).
        var whatsNewTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.2),
        };
        whatsNewTimer.Tick += (_, _) =>
        {
            whatsNewTimer.Stop();
            UI.WhatsNewGate.MaybeShow();
        };
        whatsNewTimer.Start();

        var reportSnapshot = Environment.GetEnvironmentVariable("AGENTISLAND_REPORT_SNAPSHOT");
        var monthlySnapshot = Environment.GetEnvironmentVariable("AGENTISLAND_MONTHLY_SNAPSHOT");

        // Headless card renders for tooling/screenshots, mirroring macOS.
        // Data-driven, not a fixed delay: a cache-version bump forces a full
        // rescan that can take well past any polite sleep (a 5s beat wrote
        // all-zero cards the day the Codex cache went v2). Render on the
        // first completed scan; a 90s failsafe keeps a wedged scan from
        // leaving the process running forever.
        if (!string.IsNullOrEmpty(reportSnapshot) || !string.IsNullOrEmpty(monthlySnapshot))
        {
            var rendered = false;
            void RenderAndQuit()
            {
                if (rendered) return;
                rendered = true;
                if (!string.IsNullOrEmpty(reportSnapshot))
                    UI.Report.ReportWindow.WritePng(UI.Report.ReportWindow.Kind.Weekly, reportSnapshot!);
                if (!string.IsNullOrEmpty(monthlySnapshot))
                    UI.Report.ReportWindow.WritePng(UI.Report.ReportWindow.Kind.Monthly, monthlySnapshot!);
                Shutdown();
            }
            if (Cost.CostStore.Shared.LastUpdated is not null)
            {
                RenderAndQuit();
            }
            else
            {
                Cost.CostStore.Shared.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(Cost.CostStore.LastUpdated))
                    {
                        Dispatcher.BeginInvoke(RenderAndQuit);
                    }
                };
                var failsafe = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(90),
                };
                failsafe.Tick += (_, _) => { failsafe.Stop(); RenderAndQuit(); };
                failsafe.Start();
            }
        }

        // Scripted-verification hooks, mirroring the demo-only buttons on
        // macOS: never set in normal use.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_OPEN_SETTINGS") == "1")
        {
            UI.SettingsWindow.Open();
        }
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_OPEN_COST") == "1")
        {
            UI.CostWindow.Open();
        }
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DIALOG") == "1")
        {
            IslandDialog.Show(
                TriggerTool.Claude,
                Localization.L10n.Tr("Re-authenticate"),
                Localization.L10n.Tr("Claude Code CLI not found. Log in from a terminal with: claude /login"),
                meta: new[]
                {
                    (Localization.L10n.Tr("Alarm provider"), "Claude"),
                    (Localization.L10n.Tr("Alarm thread"), "Agent Island Windows"),
                    (Localization.L10n.Tr("Alarm project"), "Agent Island"),
                },
                primaryLabel: Localization.L10n.Tr("Retry"),
                secondaryLabel: Localization.L10n.Tr("I know"));
        }
    }

    /// Rebuild the drawer and tray menu so a language change lands
    /// everywhere immediately — the stores and monitors keep running
    /// untouched, only the labels are re-created in the new language.
    public void RebuildForLanguageChange()
    {
        _drawer?.Close();
        _drawer = new UI.Drawer.DrawerWindow();
        _drawer.Show();
        _tray?.Dispose();
        _tray = CreateTray();
    }

    private TrayIcon CreateTray() => new(
        showDrawer: () => _drawer?.Reveal(),
        toggleDrawer: () => _drawer?.ToggleDeliberately(),
        openCost: UI.CostWindow.Open,
        openSettings: UI.SettingsWindow.Open,
        exit: () =>
        {
            _tray?.Dispose();
            Shutdown();
        });

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    private static void InstallCrashLogger()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception, "AppDomain");
        Current.DispatcherUnhandledException += (_, args) =>
            LogCrash(args.Exception, "Dispatcher");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, "Task");
            args.SetObserved();
        };
    }

    private static void LogCrash(Exception? error, string source)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Core.IslandPaths.AppSupportDir);
            var path = System.IO.Path.Combine(Core.IslandPaths.AppSupportDir, "crash.log");
            System.IO.File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {error}\n\n");
        }
        catch
        {
        }
    }
}
