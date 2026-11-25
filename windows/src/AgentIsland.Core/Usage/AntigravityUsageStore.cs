using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Usage;

/// Antigravity's slice of the panel: the last quota snapshot read from its
/// local language server or CLI, plus account identity when available. It rides
/// UsageStore.Refresh()'s cadence via KickRefresh() behind a 120s attempt
/// floor, with a Preferences snapshot cache so relaunch (or Antigravity not
/// running, the only time its quota is unreadable) doesn't blank the strip.
public sealed class AntigravityUsageStore : GuestUsageStore
{
    public static AntigravityUsageStore Shared { get; } = new();

    private const string CacheKey = "AntigravityUsageStore.lastSnapshot.v1";
    // A property, not a static readonly field: Shared is constructed before
    // later static fields are set, so the restore below read a zero age
    // limit and dropped every saved snapshot.
    private static TimeSpan CacheMaxAge => TimeSpan.FromDays(8);
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ManualMinGap = TimeSpan.FromMinutes(1);

    private AntigravityQuotaSnapshot? _snapshot;
    private string? _statusCaption;
    private DateTimeOffset? _lastUpdated;
    private string? _accountEmail;
    private bool _deferred;
    private bool _unlockPending;

    // The fetch may start agy.
    protected override bool CancelKeepsFloor => true;

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
        private set => Change(ref _statusCaption, value, nameof(StatusCaption));
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set => Change(ref _lastUpdated, value, nameof(LastUpdated));
    }

    public string? AccountEmail
    {
        get => _accountEmail;
        private set => Change(ref _accountEmail, value, nameof(AccountEmail));
    }

    /// Tier chip for the Settings row / strip. Google's tier names are
    /// sentences ("Antigravity Starter Quota"); the compactor keeps the part
    /// that identifies the plan ("STARTER").
    public string? TierBadge =>
        AntigravityQuotaParser.CompactTierBadge(Snapshot?.TierLabel, Snapshot?.TierId);

    internal static TimeSpan EffectiveFloor(bool manual) => manual ? ManualMinGap : MinAttemptGap;

    public void KickRefresh(bool manual = false)
    {
        if (!Detected && !ProviderVisibilityStore.Shared.IsDetected(DisplayProvider.Antigravity)) return;
        if (!ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Antigravity)) return;
        if (!TryStart(EffectiveFloor(manual))) return;
        _ = RunVisibleFetch<AntigravityUsageFetcher.Outcome>(AntigravityUsageFetcher.Fetch, message => new AntigravityUsageFetcher.Outcome.Failed(message),
            () => ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Antigravity), Apply);
    }

    internal void RetryAfterUnlock()
    {
        if (Loading)
        {
            _unlockPending = true;
            return;
        }
        if (_deferred) ClearAttemptFloor();
        KickRefresh(manual: true);
    }

    private void Apply(AntigravityUsageFetcher.Outcome outcome)
    {
        var retry = _unlockPending;
        _unlockPending = false;
        _deferred = outcome is AntigravityUsageFetcher.Outcome.Deferred;
        Loading = false;
        switch (outcome)
        {
            case AntigravityUsageFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                // /usage does not identify the CLI account. It may differ
                // from the desktop account recorded with the last snapshot.
                if (success.FromCli || !string.IsNullOrEmpty(success.Email)) AccountEmail = success.Email;
                StatusCaption = null;
                LastUpdated = DateTimeOffset.Now;
                Persist(success.Snapshot, AccountEmail);
                break;
            case AntigravityUsageFetcher.Outcome.NotRunning:
                // No source is available. Keep the last successful reading.
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
            case AntigravityUsageFetcher.Outcome.Deferred:
                // Unlock may have arrived while the preceding attempt was settling.
                if (retry) RetryAfterUnlock();
                break;
        }
    }

    private static void Persist(AntigravityQuotaSnapshot fresh, string? email) =>
        Preferences.Set(CacheKey, new CachedSnapshot(fresh, DateTimeOffset.Now, email));

    /// Cache envelope. Public so the JSON serializer binds it the same way
    /// it binds UsageCacheSnapshot.
    public sealed record CachedSnapshot(
        AntigravityQuotaSnapshot? Snapshot, DateTimeOffset UpdatedAt, string? Email = null);
}
