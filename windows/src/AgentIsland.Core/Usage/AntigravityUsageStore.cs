using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Usage;

/// Antigravity's slice of the panel: the last quota snapshot read from its
/// local language server, plus account identity. Timer-free — it rides
/// UsageStore.Refresh()'s cadence via KickRefresh() behind a 120s attempt
/// floor, with a Preferences snapshot cache so relaunch (or Antigravity not
/// running, the only time its quota is unreadable) doesn't blank the strip.
public sealed class AntigravityUsageStore : INotifyPropertyChanged
{
    public static AntigravityUsageStore Shared { get; } = new();

    private const string CacheKey = "AntigravityUsageStore.lastSnapshot.v1";
    // A property, not a static readonly field: Shared is constructed before
    // later static fields are set, so the restore below read a zero age
    // limit and dropped every saved snapshot.
    private static TimeSpan CacheMaxAge => TimeSpan.FromDays(8);
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromSeconds(120);

    private AntigravityQuotaSnapshot? _snapshot;
    private string? _statusCaption;
    private DateTimeOffset? _lastUpdated;
    private string? _accountEmail;
    private bool _loading;
    private DateTimeOffset? _lastAttempt;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Detection is launch-static, same as the other providers.
    public bool Detected { get; }

    private AntigravityUsageStore()
    {
        Detected = AntigravityCredentials.Detected;
        if (!Detected) return;
        if (Preferences.Get<CachedSnapshot?>(CacheKey) is not { } cached) return;
        if (cached.Snapshot is not { } stored) return;
        if (DateTimeOffset.Now - cached.UpdatedAt > CacheMaxAge) return;
        _snapshot = stored;
        _lastUpdated = cached.UpdatedAt;
        _accountEmail = cached.Email;
    }

    public AntigravityQuotaSnapshot? Snapshot
    {
        get => _snapshot;
        private set { _snapshot = value; Raise(nameof(Snapshot)); Raise(nameof(TierBadge)); }
    }

    /// Non-null while the latest fetch ended in anything but data. Values in
    /// Snapshot are the preserved last-good numbers in that case.
    public string? StatusCaption
    {
        get => _statusCaption;
        private set { _statusCaption = value; Raise(nameof(StatusCaption)); }
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set { _lastUpdated = value; Raise(nameof(LastUpdated)); }
    }

    public string? AccountEmail
    {
        get => _accountEmail;
        private set { _accountEmail = value; Raise(nameof(AccountEmail)); }
    }

    public bool Loading
    {
        get => _loading;
        private set { _loading = value; Raise(nameof(Loading)); }
    }

    /// Tier chip for the Settings row / strip. Google's tier names are
    /// sentences ("Antigravity Starter Quota"); the compactor keeps the part
    /// that identifies the plan ("STARTER").
    public string? TierBadge =>
        AntigravityQuotaParser.CompactTierBadge(Snapshot?.TierLabel, Snapshot?.TierId);

    public void KickRefresh()
    {
        if (!Detected) return;
        if (!ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Antigravity)) return;
        if (Loading) return;
        if (_lastAttempt is { } last && DateTimeOffset.Now - last < MinAttemptGap) return;

        _lastAttempt = DateTimeOffset.Now;
        Loading = true;
        _ = Task.Run(async () =>
        {
            AntigravityUsageFetcher.Outcome outcome;
            try
            {
                outcome = await AntigravityUsageFetcher.Fetch();
            }
            catch (Exception error)
            {
                // A faulted fetch must never leave Loading latched — that
                // would freeze the strip until the app is relaunched.
                outcome = new AntigravityUsageFetcher.Outcome.Failed(error.Message);
            }
            await UiThread.InvokeAsync(() => Apply(outcome));
        });
    }

    private void Apply(AntigravityUsageFetcher.Outcome outcome)
    {
        Loading = false;
        switch (outcome)
        {
            case AntigravityUsageFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                if (success.Email is { Length: > 0 } email) AccountEmail = email;
                StatusCaption = null;
                LastUpdated = DateTimeOffset.Now;
                Persist(success.Snapshot, AccountEmail);
                break;
            case AntigravityUsageFetcher.Outcome.NotRunning:
                // Not an error: the embedded server is the only quota
                // source. Keep the last good numbers; the caption explains
                // why they aren't moving.
                StatusCaption = Snapshot is null
                    ? L10n.Tr("Installed — run agy to sign in")
                    : null;
                break;
            case AntigravityUsageFetcher.Outcome.Failed failed:
                // Keep the last good numbers; the caption admits staleness.
                StatusCaption = failed.Message;
                break;
            case AntigravityUsageFetcher.Outcome.NotInstalled:
                Snapshot = null;
                StatusCaption = null;
                break;
        }
    }

    private static void Persist(AntigravityQuotaSnapshot fresh, string? email) =>
        Preferences.Set(CacheKey, new CachedSnapshot(fresh, DateTimeOffset.Now, email));

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// Cache envelope. Public so the JSON serializer binds it the same way
    /// it binds UsageCacheSnapshot.
    public sealed record CachedSnapshot(
        AntigravityQuotaSnapshot? Snapshot, DateTimeOffset UpdatedAt, string? Email = null);
}
