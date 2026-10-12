using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Usage;

/// The 24h cache envelope for the last good Kimi Code usage snapshot.
public sealed class KimiCachedSnapshot
{
    public KimiUsageSnapshot? Snapshot { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// Kimi Code's plan quotas and membership level. Timer-free like the other
/// guests: it rides UsageStore.Refresh()'s cadence via KickRefresh(), behind
/// an attempt floor.
public sealed class KimiUsageStore : INotifyPropertyChanged
{
    public static KimiUsageStore Shared { get; } = new();

    private const string CacheKey = "KimiUsageStore.lastSnapshot.v1";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromSeconds(120);

    private KimiUsageSnapshot? _snapshot;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private bool _loading;
    private DateTimeOffset? _lastAttempt;

    public event PropertyChangedEventHandler? PropertyChanged;

    private KimiUsageStore()
    {
        if (Preferences.Get<KimiCachedSnapshot?>(CacheKey) is not { Snapshot: { } restored } cached) return;
        if (DateTimeOffset.Now - cached.UpdatedAt > CacheMaxAge) return;
        _snapshot = restored;
        _lastUpdated = cached.UpdatedAt;
    }

    public KimiUsageSnapshot? Snapshot
    {
        get => _snapshot;
        private set { _snapshot = value; Raise(nameof(Snapshot)); }
    }

    /// Non-null while the latest fetch failed; Snapshot keeps the last good
    /// numbers meanwhile.
    public string? ErrorCaption
    {
        get => _errorCaption;
        private set { _errorCaption = value; Raise(nameof(ErrorCaption)); }
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set { _lastUpdated = value; Raise(nameof(LastUpdated)); }
    }

    /// Kimi's membership level name ("Max"), uppercased for the badge.
    public string? PlanBadge => _snapshot?.Plan?.ToUpperInvariant();

    public bool Loading
    {
        get => _loading;
        private set { _loading = value; Raise(nameof(Loading)); }
    }

    public void KickRefresh()
    {
        if (!ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Kimi)) return;
        if (Loading) return;
        if (_lastAttempt is { } last && DateTimeOffset.Now - last < MinAttemptGap) return;

        _lastAttempt = DateTimeOffset.Now;
        Loading = true;
        _ = Task.Run(async () =>
        {
            KimiUsageFetcher.Outcome outcome;
            try
            {
                outcome = await KimiUsageFetcher.Fetch();
            }
            catch (Exception error)
            {
                // A faulted fetch must never leave Loading latched.
                outcome = new KimiUsageFetcher.Outcome.Failed(error.Message);
            }
            await UiThread.InvokeAsync(() => Apply(outcome));
        });
    }

    private void Apply(KimiUsageFetcher.Outcome outcome)
    {
        Loading = false;
        switch (outcome)
        {
            case KimiUsageFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                ErrorCaption = null;
                LastUpdated = DateTimeOffset.Now;
                Preferences.Set(CacheKey, new KimiCachedSnapshot { Snapshot = success.Snapshot, UpdatedAt = DateTimeOffset.Now });
                break;
            case KimiUsageFetcher.Outcome.SignInStale:
                ErrorCaption = L10n.Tr("open Kimi Code to sync its plan usage");
                break;
            case KimiUsageFetcher.Outcome.Failed failed:
                ErrorCaption = failed.Message;
                break;
            case KimiUsageFetcher.Outcome.NotInstalled:
                Snapshot = null;
                ErrorCaption = null;
                break;
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
