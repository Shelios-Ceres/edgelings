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
    // Properties, not static readonly fields: Shared is constructed before
    // later static fields are set, so the restore below read a zero age
    // limit and dropped every saved snapshot.
    private static TimeSpan CacheMaxAge => UsageCachePolicy.KeepFor;
    private static TimeSpan MinAttemptGap => TimeSpan.FromSeconds(120);
    private static TimeSpan SignInAttemptGap => TimeSpan.FromSeconds(10);

    private KimiUsageSnapshot? _snapshot;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private bool _loading;
    private DateTimeOffset? _lastAttempt;
    private string? _signInStamp;

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

    public void KickRefresh() => Kick(MinAttemptGap);

    /// Kimi Code signing in again is when its usage can be read: the CLI's
    /// access tokens last 15 minutes and are renewed only while it works,
    /// and the desktop app's server answers only while it is open. Checked
    /// on the monitor's tick — two file stamps, no request — so opening
    /// either one syncs within seconds instead of at the next refresh.
    public void NoticeSignIn()
    {
        if (KimiUsageFetcher.SignInStamp() is not { } stamp || stamp == _signInStamp) return;
        var first = _signInStamp is null;
        _signInStamp = stamp;
        // The first look only learns the stamp; the regular refresh covers start-up.
        if (!first) Kick(SignInAttemptGap);
    }

    private void Kick(TimeSpan gap)
    {
        if (!ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Kimi)) return;
        if (Loading) return;
        if (_lastAttempt is { } last && DateTimeOffset.Now - last < gap) return;

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
                // With numbers in hand the row already says when they were
                // synced, and the next time Kimi Code signs in syncs them
                // again; the tip is only for a card with nothing to show.
                ErrorCaption = Snapshot is null ? L10n.Tr("open Kimi Code to sync its plan usage") : null;
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
