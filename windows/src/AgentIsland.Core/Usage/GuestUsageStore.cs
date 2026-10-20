using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// What the stores of the guest providers (Grok, Kimi Code, Cursor,
/// Antigravity) have in common. Each is timer-free: it rides
/// UsageStore.Refresh()'s cadence through its KickRefresh(), behind an
/// attempt floor so bursty kick sources never turn into extra polling, and
/// keeps the last good numbers when a fetch fails. A store keeps what is its
/// own: its snapshot, its captions, its cache and how it reads an outcome.
public abstract class GuestUsageStore : INotifyPropertyChanged
{
    private bool _loading;
    private DateTimeOffset? _lastAttempt;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Loading
    {
        get => _loading;
        protected set => Change(ref _loading, value, nameof(Loading));
    }

    /// Sets a field and announces it, whether or not it changed.
    protected void Change<T>(ref T field, T value, string name)
    {
        field = value;
        Raise(name);
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// Starts an attempt unless one is in flight or the last one is younger
    /// than the floor, which de-dupes bursts of kicks (poll, reset boundary
    /// and manual refresh landing together).
    protected bool TryStart(TimeSpan floor)
    {
        if (Loading) return false;
        if (_lastAttempt is { } last && DateTimeOffset.Now - last < floor) return false;
        _lastAttempt = DateTimeOffset.Now;
        Loading = true;
        return true;
    }

    /// Fetches off the UI thread and applies the outcome on it. A fetch that
    /// throws is applied as a failure: it must never leave Loading latched,
    /// which would freeze the row until the app relaunches.
    protected void RunFetch<TOutcome>(Func<Task<TOutcome>> fetch, Func<string, TOutcome> failed, Action<TOutcome> apply)
    {
        _ = Task.Run(async () =>
        {
            TOutcome outcome;
            try
            {
                outcome = await fetch();
            }
            catch (Exception error)
            {
                outcome = failed(error.Message);
            }
            await UiThread.InvokeAsync(() => apply(outcome));
        });
    }
}
