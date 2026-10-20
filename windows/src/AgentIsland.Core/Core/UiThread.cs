namespace AgentIsland.Core;

/// The app's UI thread, captured once at startup. Stores raise their change
/// notifications on it, so the UI (WinUI installs a SynchronizationContext on
/// its UI thread) can bind them directly. Uncaptured — the console test
/// runner — work runs inline.
public static class UiThread
{
    private static SynchronizationContext? _context;
    private static TaskScheduler? _scheduler;

    /// Call on the UI thread before any store starts.
    public static void Capture()
    {
        _context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("UiThread.Capture must run on a thread with a SynchronizationContext.");
        _scheduler = TaskScheduler.FromCurrentSynchronizationContext();
    }

    /// Task continuations that must land on the UI thread.
    public static TaskScheduler Scheduler => _scheduler ?? TaskScheduler.Default;

    public static void Post(Action action)
    {
        if (_context is { } context) context.Post(static state => ((Action)state!)(), action);
        else action();
    }

    /// Runs the action on the UI thread; the task completes once it has run.
    public static Task InvokeAsync(Action action)
    {
        if (_context is not { } context)
        {
            action();
            return Task.CompletedTask;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        }, null);
        return done.Task;
    }
}

/// A periodic timer that ticks on the UI thread — the framework-neutral
/// stand-in for a dispatcher timer, with the same shape (Interval, Tick,
/// Start, Stop). Ticks never pile up: while one is queued on a busy UI
/// thread, later ones are dropped, as a dispatcher timer does.
public sealed class UiTimer : IDisposable
{
    private Timer? _timer;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private int _queued;

    public event EventHandler? Tick;

    public bool IsEnabled { get; private set; }

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value;
            if (IsEnabled) _timer?.Change(value, value);
        }
    }

    public void Start()
    {
        IsEnabled = true;
        _timer ??= new Timer(_ => Queue());
        _timer.Change(_interval, _interval);
    }

    public void Stop()
    {
        IsEnabled = false;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private void Queue()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        UiThread.Post(() =>
        {
            Volatile.Write(ref _queued, 0);
            if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty);
        });
    }
}
