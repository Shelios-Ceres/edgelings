using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Usage;

/// The 24h cache envelope for the last good Grok billing snapshot.
public sealed class GrokCachedSnapshot
{
    public GrokBillingSnapshot? Snapshot { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// Grok's slice of the panel: the last fetched billing snapshot plus the
/// account identity from auth.json. Deliberately timer-free — it rides
/// UsageStore.Refresh()'s existing cadence (5–30 min polls, wake, unlock,
/// network recovery, manual refresh) via KickRefresh(), with a small attempt
/// floor so bursty kick sources never turn into extra polling of a provider
/// the user may not even be looking at.
public sealed class GrokUsageStore : GuestUsageStore
{
    public static GrokUsageStore Shared { get; } = new();

    private const string CacheKey = "GrokUsageStore.lastSnapshot.v1";
    // A property, not a static readonly field: Shared is constructed before
    // later static fields are set, so the restore below read a zero age
    // limit and dropped every saved snapshot.
    private static TimeSpan CacheMaxAge => UsageCachePolicy.KeepFor;

    /// De-dupes kick bursts (poll + reset boundary + manual refresh all
    /// landing close together); the real cadence stays whatever UsageStore runs.
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromSeconds(120);

    private GrokBillingSnapshot? _snapshot;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private string? _accountEmail;
    private string? _authModeBadge;

    private GrokUsageStore()
    {
        LoadIdentity();
        if (Preferences.Get<GrokCachedSnapshot?>(CacheKey) is not { } cached) return;
        if (cached.Snapshot is not { } restored) return;
        if (DateTimeOffset.Now - cached.UpdatedAt > CacheMaxAge) return;
        _snapshot = restored;
        _lastUpdated = cached.UpdatedAt;
    }

    public GrokBillingSnapshot? Snapshot
    {
        get => _snapshot;
        private set => Change(ref _snapshot, value, nameof(Snapshot));
    }

    /// Non-null while the latest fetch failed. Values in Snapshot are the
    /// preserved last-good numbers in that case, same policy as UsageStore.
    public string? ErrorCaption
    {
        get => _errorCaption;
        private set => Change(ref _errorCaption, value, nameof(ErrorCaption));
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

    /// "SUPERGROK" for OIDC logins, else the raw auth_mode uppercased. This is
    /// the badge the settings row and the panel header show.
    public string? AuthModeBadge
    {
        get => _authModeBadge;
        private set => Change(ref _authModeBadge, value, nameof(AuthModeBadge));
    }

    public void KickRefresh()
    {
        if (!ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Grok)) return;
        if (!TryStart(MinAttemptGap)) return;
        RunFetch<GrokUsageFetcher.Outcome>(() => GrokUsageFetcher.Fetch(), message => new GrokUsageFetcher.Outcome.Failed(message), Apply);
    }

    private void Apply(GrokUsageFetcher.Outcome outcome)
    {
        Loading = false;
        switch (outcome)
        {
            case GrokUsageFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                ErrorCaption = null;
                LastUpdated = DateTimeOffset.Now;
                // The fetch may have rotated tokens, or the user may have
                // re-logged in since launch — keep the identity row honest.
                LoadIdentity();
                Persist(success.Snapshot);
                break;
            case GrokUsageFetcher.Outcome.ReauthRequired:
                ErrorCaption = L10n.Tr("sign in again — run grok login");
                break;
            case GrokUsageFetcher.Outcome.Failed failed:
                // Keep the last good numbers; the caption admits staleness.
                ErrorCaption = failed.Message;
                break;
            case GrokUsageFetcher.Outcome.NotInstalled:
                Snapshot = null;
                ErrorCaption = null;
                break;
        }
    }

    private void LoadIdentity()
    {
        if (GrokAuthFile.LoadEntry() is not { } entry)
        {
            AccountEmail = null;
            AuthModeBadge = null;
            return;
        }
        AccountEmail = entry.Email;
        AuthModeBadge = entry.IsSuperGrok ? "SUPERGROK" : entry.AuthMode?.ToUpperInvariant();
    }

    private static void Persist(GrokBillingSnapshot fresh) => Preferences.Set(
        CacheKey, new GrokCachedSnapshot { Snapshot = fresh, UpdatedAt = DateTimeOffset.Now });
}
