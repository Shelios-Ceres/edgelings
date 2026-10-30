using AgentIsland.Core;
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
        // Only the copy that stays: a second launch that bows out must not
        // make the new folder from settings the running copy is still changing.
        AppDataMigration.Run();

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
    /// preferences, so a second launch bows out. The WPF app used the
    /// same mutex, so the two never ran together.
    private static bool ClaimSingleInstance()
    {
        // The isolated motion check and the frame film and spike create no
        // stores or background readers.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_HOVER_PREVIEW") == "1"
            || Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FRAME_FILM") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FRAME_SPIKE") == "1") return true;
        _singleInstance = new Mutex(initiallyOwned: false, @"Local\AgentIsland.SingleInstance");
        try
        {
            if (!_singleInstance.WaitOne(0))
            {
                // The running copy brings its drawer back (it may be hidden
                // from the tray), so the launch is not lost; a copy that is
                // quitting hands over within the wait.
                Knock();
                if (!_singleInstance.WaitOne(TimeSpan.FromSeconds(10))) return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing it.
        }
        // Made while the mutex is held, so a later launch finds it; a knock
        // left for a copy that has since quit is not for this one.
        _relaunch = new EventWaitHandle(false, EventResetMode.AutoReset, RelaunchName);
        _relaunch.Reset();
        return true;
    }

    private const string RelaunchName = @"Local\AgentIsland.Relaunch";
    private static EventWaitHandle? _relaunch;
    // Held so the wait goes on for the life of the app.
    private static RegisteredWaitHandle? _relaunchWait;

    private static void Knock()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(RelaunchName, out var relaunch)) return;
            using (relaunch) relaunch.Set();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// Answers a later launch's knock with reveal, on the UI thread.
    internal static void AnswerRelaunches(Action reveal)
    {
        if (_relaunch is null) return;
        var queue = DispatcherQueue.GetForCurrentThread();
        _relaunchWait = ThreadPool.RegisterWaitForSingleObject(_relaunch, (_, _) => queue.TryEnqueue(() => reveal()),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }
}
