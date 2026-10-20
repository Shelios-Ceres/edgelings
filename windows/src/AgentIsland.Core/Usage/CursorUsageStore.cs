using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Usage;

/// Cursor's slice of the panel: the last fetched billing-period snapshot plus
/// identity from the editor's own store. Timer-free — it rides
/// UsageStore.Refresh()'s cadence via KickRefresh() behind the slot
/// selection, with an attempt floor so kick bursts never add polling.
public sealed class CursorUsageStore : INotifyPropertyChanged
{
    private static readonly Lazy<CursorUsageStore> Instance = new(() => new CursorUsageStore());
    public static CursorUsageStore Shared => Instance.Value;

    private const string CacheKey = "CursorUsageStore.lastSnapshot.v1";
    private static TimeSpan CacheMaxAge => UsageCachePolicy.KeepFor;

    /// De-dupes kick bursts (poll + reset boundary + manual refresh landing
    /// together); the real cadence stays whatever UsageStore runs.
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromMinutes(2);

    /// One included-usage pool per billing cycle (~30 days). Cursor reports
    /// the cycle END, never its length, so this is what the window label
    /// renders from — including before the first successful fetch.
    public const double CycleSeconds = 30 * 24 * 60 * 60;

    private CursorUsageSnapshot? _snapshot;
    private string? _snapshotUserId;
    private readonly Action<Cached?> _save;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private string? _accountEmail;
    private string? _localPlan;
    private bool _loading;
    private DateTimeOffset? _lastAttempt;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Persisted shape of the last good fetch. Public because the JSON
    /// serializer behind Preferences has to reach it.
    public sealed class Cached
    {
        public CursorUsageSnapshot? Snapshot { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string? UserId { get; init; }
    }

    private CursorUsageStore() : this(
        Preferences.Get<Cached?>(CacheKey),
        cached =>
        {
            if (cached is null) Preferences.Remove(CacheKey);
            else Preferences.Set(CacheKey, cached);
        })
    {
    }

    internal CursorUsageStore(Cached? stored, Action<Cached?> save)
    {
        _save = save;
        // Database I/O stays off the UI thread, including at first startup.
        if (stored is { Snapshot: { } cached }
            && DateTimeOffset.Now - stored.UpdatedAt <= CacheMaxAge)
        {
            _snapshot = cached;
            _lastUpdated = stored.UpdatedAt;
            _snapshotUserId = stored.UserId;
        }
    }

    public CursorUsageSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            _snapshot = value;
            Raise(nameof(Snapshot));
            Raise(nameof(Window));
            Raise(nameof(PlanBadge));
        }
    }

    /// Non-null while the latest fetch failed. Values in Snapshot are the
    /// preserved last-good numbers in that case, same policy as UsageStore.
    public string? ErrorCaption
    {
        get => _errorCaption;
        private set { _errorCaption = value; Raise(nameof(ErrorCaption)); Raise(nameof(Window)); }
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

    /// "FREE" / "PRO" chip text for the Settings row. Reads two cached
    /// fields, never the state db — the badge is bound by the UI.
    public string? PlanBadge => (_snapshot?.PlanName ?? _localPlan)?.ToUpperInvariant();

    /// The island renders Cursor through the same WindowUsage the two fully
    /// monitored providers use: one pool, a ~30-day cycle, and the store's
    /// caption riding along so a stale strip admits it.
    public WindowUsage Window =>
        new(_snapshot?.UsedPercent ?? 0, _snapshot?.PeriodEnd, _errorCaption ?? (_snapshot is null ? "no data" : null), CycleSeconds)
        {
            Observed = _snapshot is not null,
            Kept = _snapshot is not null && _errorCaption is not null,
        };

    /// No-ops when Cursor isn't on the island, when a fetch is already in
    /// flight, or when the last attempt is younger than the floor.
    public void KickRefresh()
    {
        if (!ProviderVisibilityStore.Shared.IsShown(DisplayProvider.Cursor)) return;
        if (Loading) return;
        if (_lastAttempt is { } last && DateTimeOffset.Now - last < MinAttemptGap) return;
        _lastAttempt = DateTimeOffset.Now;
        Loading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var credentials = CursorCredentials.ReadSnapshot();
                var outcome = await CursorUsageFetcher.Fetch(credentials);
                await UiThread.InvokeAsync(() => Apply(outcome, credentials));
            }
            catch
            {
                // A faulted attempt must never leave Loading latched — that
                // would wedge every later kick behind the in-flight guard.
                await UiThread.InvokeAsync(ApplyReadFailure);
            }
        });
    }

    internal void Apply(CursorUsageFetcher.Outcome outcome, CursorCredentials.Snapshot? credentials)
    {
        Loading = false;
        var userId = credentials?.Session?.UserId;
        // A retained quota belongs to the account that produced it, not to
        // whichever login happens to be in Cursor after a failed fetch.
        if (userId is null || (_snapshot is not null && _snapshotUserId != userId)) ClearQuota();
        AccountEmail = userId is null ? null : credentials?.Email;
        _localPlan = userId is null ? null : credentials?.Plan;
        Raise(nameof(PlanBadge));
        switch (outcome)
        {
            case CursorUsageFetcher.Outcome.Success success when userId is not null:
                _snapshotUserId = userId;
                Snapshot = success.Snapshot;
                ErrorCaption = null;
                LastUpdated = DateTimeOffset.Now;
                _save(new Cached { Snapshot = success.Snapshot, UpdatedAt = LastUpdated.Value, UserId = userId });
                break;
            case CursorUsageFetcher.Outcome.Success:
            case CursorUsageFetcher.Outcome.ReauthRequired:
                ErrorCaption = L10n.Tr("sign in again — open Cursor");
                break;
            case CursorUsageFetcher.Outcome.Failed failed:
                // Keep the last good numbers; the caption admits staleness.
                ErrorCaption = failed.Message;
                break;
            case CursorUsageFetcher.Outcome.NotInstalled:
                ErrorCaption = null;
                break;
        }
    }

    internal void ApplyReadFailure()
    {
        Loading = false;
        // Legacy snapshots have no account identity. A read failure cannot
        // establish who owns those numbers, so do not display them as current.
        if (_snapshotUserId is null) ClearQuota();
        ErrorCaption = L10n.Tr("Cursor data unavailable");
    }

    private void ClearQuota()
    {
        var hadQuota = _snapshot is not null || _lastUpdated is not null || _snapshotUserId is not null;
        _snapshotUserId = null;
        Snapshot = null;
        LastUpdated = null;
        if (hadQuota) _save(null);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
