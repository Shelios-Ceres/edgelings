using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgentIsland.WinUI;

public static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "Task");
            e.SetObserved();
        };
        if (!ClaimSingleInstance()) return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// Two live copies sharing %APPDATA%\AgentIsland corrupt each other's
    /// preferences, so a second launch bows out. Same mutex as the WPF build:
    /// the two never run together.
    private static bool ClaimSingleInstance()
    {
        // The isolated motion check creates no stores or background readers.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_HOVER_PREVIEW") == "1") return true;
        _singleInstance = new Mutex(initiallyOwned: false, @"Local\AgentIsland.SingleInstance");
        try
        {
            return _singleInstance.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous holder died without releasing it
        }
    }
}
