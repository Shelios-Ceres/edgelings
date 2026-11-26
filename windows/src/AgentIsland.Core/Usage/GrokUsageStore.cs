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
    private static readonly TimeSpan SignInAttemptGap = TimeSpan.FromSeconds(10);

    private bool _awaitingSync;
    private string? _credentialStamp;
    private GrokBillingSnapshot? _snapshot;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private string? _accountEmail;
    private string? _authModeBadge;

    /// Test seams: where the login is, whether the card is shown and the CLI seen,
    /// and what a fetch does. Shared uses the real ones.
    internal Func<string> AuthPath = () => GrokAuthFile.DefaultPath;
    internal Func<bool> IsShown = () => ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Grok);
    internal Func<bool> IsDetected = () => ProviderVisibilityStore.Shared.IsDetected(DisplayProvider.Grok);
    internal Action Redetect = () => ProviderVisibilityStore.Shared.Redetect(DisplayProvider.Grok);
    internal Func<string, CancellationToken, Task<GrokUsageFetcher.Outcome>> Fetcher =
        (path, ct) => GrokUsageFetcher.Fetch(path, Http.Client, ct);

    /// The fetch the last kick started.
    internal Task? LastRun { get; private set; }

    internal GrokUsageStore()
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

    public void KickRefresh() => Kick(MinAttemptGap);

    /// While the card waits for the grok CLI to sync, a change to auth.json is
    /// when the quota can be read again: checked on the monitor's tick, one
    /// file stamp and no request, so opening grok catches up within seconds
    /// instead of at the next refresh. The fetcher decides whether to send.
    public void NoticeCredentialChange()
    {
        if (!_awaitingSync) return;
        if (!IsShown()) return;
        if (GrokAuthFile.Stamp(AuthPath()) is not { } stamp || stamp == _credentialStamp) return;
        // A file that was gone and is back: detection last saw it gone, and would
        // only look again at the next poll. Look at Grok alone, now.
        if (!IsDetected()) Redetect();
        // Taken as seen only once a fetch really went out: a kick held back by the
        // attempt floor or by detection must find the change again on the next tick.
        if (Kick(SignInAttemptGap)) _credentialStamp = stamp;
    }

    /// Whether a fetch was started.
    private bool Kick(TimeSpan gap)
    {
        if (!IsShown()) return false;
        if (!IsDetected()) return false;
        if (!TryStart(gap)) return false;
        var path = AuthPath();
        LastRun = RunVisibleFetch<GrokUsageFetcher.Outcome>(ct => Fetcher(path, ct), message => new GrokUsageFetcher.Outcome.Failed(message),
            IsShown, Apply);
        return true;
    }

    internal void Apply(GrokUsageFetcher.Outcome outcome)
    {
        Loading = false;
        // Signed out, or not signed in, is waited on too: the file coming back or
        // gaining a login is a change.
        _awaitingSync = outcome is GrokUsageFetcher.Outcome.SyncRequired or GrokUsageFetcher.Outcome.NoLogin
            or GrokUsageFetcher.Outcome.NotInstalled;
        // A later change is measured against the file the fetch itself saw: none when it was not there.
        _credentialStamp = outcome switch
        {
            GrokUsageFetcher.Outcome.SyncRequired sync => sync.Stamp,
            GrokUsageFetcher.Outcome.NoLogin none => none.Stamp,
            _ => null,
        };
        switch (outcome)
        {
            case GrokUsageFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                ErrorCaption = null;
                LastUpdated = DateTimeOffset.Now;
                // The user may have signed in as someone else since launch —
                // keep the identity row honest.
                LoadIdentity();
                Persist(success.Snapshot);
                break;
            case GrokUsageFetcher.Outcome.SyncRequired:
                // Always a caption, even with numbers in hand: ProviderUsage reads a
                // window with an error as unknown, so a stale full pool never puts
                // the character to sleep. The card keeps showing the kept numbers.
                ErrorCaption = L10n.Tr("Open grok to sync");
                break;
            case GrokUsageFetcher.Outcome.Failed failed:
                // Keep the last good numbers; the caption admits staleness.
                ErrorCaption = failed.Message;
                break;
            case GrokUsageFetcher.Outcome.NotInstalled:
            case GrokUsageFetcher.Outcome.NoLogin:
                Snapshot = null;
                ErrorCaption = null;
                break;
        }
    }

    private void LoadIdentity()
    {
        if (GrokAuthFile.LoadEntry(AuthPath()) is not { } entry)
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
