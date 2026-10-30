using AgentIsland.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgentIsland.WinUI;

public static class Program
{
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

    /// Two live copies sharing %APPDATA%\Edgelings corrupt each other's
    /// preferences, so a second launch bows out. The WPF app used the
    /// same mutex, so the two never ran together.
    private static bool ClaimSingleInstance()
    {
        // The isolated motion check and the frame film and spike create no
        // stores or background readers.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_HOVER_PREVIEW") == "1"
            || Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FRAME_FILM") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FRAME_SPIKE") == "1") return true;
        // This copy holds both names. The first is what Agent Island's builds
        // (this app before its rename) use: one of them that is still running
        // makes this launch bow out, and one started later bows out against us.
        foreach (var name in MutexNames)
        {
            var mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                if (!mutex.WaitOne(0))
                {
                    // The running copy brings its drawer back (it may be hidden
                    // from the tray), so the launch is not lost; a copy that is
                    // quitting hands over within the wait.
                    Knock();
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died without releasing it.
            }
            _singleInstance.Add(mutex);
        }
        // Made while the mutexes are held, so a later launch finds them; a knock
        // left for a copy that has since quit is not for this one.
        foreach (var name in RelaunchNames)
        {
            var relaunch = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            relaunch.Reset();
            _relaunch.Add(relaunch);
        }
        return true;
    }

    // Agent Island's names first, then Edgelings'.
    private static readonly string[] MutexNames = { @"Local\AgentIsland.SingleInstance", @"Local\Edgelings.SingleInstance" };
    private static readonly string[] RelaunchNames = { @"Local\AgentIsland.Relaunch", @"Local\Edgelings.Relaunch" };
    private static readonly List<Mutex> _singleInstance = new();
    private static readonly List<EventWaitHandle> _relaunch = new();
    // Held so the waits go on for the life of the app.
    private static readonly List<RegisteredWaitHandle> _relaunchWaits = new();

    /// Knocks on every copy that listens: an Agent Island build on its old name, an Edgelings one on either.
    private static void Knock()
    {
        foreach (var name in RelaunchNames)
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(name, out var relaunch)) continue;
                using (relaunch) relaunch.Set();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// Answers a later launch's knock with reveal, on the UI thread.
    internal static void AnswerRelaunches(Action reveal)
    {
        if (_relaunch.Count == 0) return;
        var queue = DispatcherQueue.GetForCurrentThread();
        foreach (var relaunch in _relaunch)
        {
            _relaunchWaits.Add(ThreadPool.RegisterWaitForSingleObject(relaunch, (_, _) => queue.TryEnqueue(() => reveal()),
                null, Timeout.Infinite, executeOnlyOnce: false));
        }
    }
}
