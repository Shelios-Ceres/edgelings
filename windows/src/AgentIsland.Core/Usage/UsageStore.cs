using System.ComponentModel;
using System.Net.NetworkInformation;
using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// Live provider usage published to the UI and the activity monitor. Direct
/// port of the macOS UsageStore: parallel fetch, error-merge that never
/// clobbers good values, 24h provider-stamped cache, re-auth polling, and a
/// refresh-on-reconnect network monitor.
public sealed class UsageStore : INotifyPropertyChanged
{
    public static UsageStore Shared { get; } = new();

    private const string CacheKey = "UsageStore.lastSuccessfulUsage.v1";
    // A property, not a static readonly field: Shared is constructed before
    // later static fields are set, so the restore below read a zero age
    // limit and dropped every saved snapshot.
    private static TimeSpan CacheMaxAge => UsageCachePolicy.KeepFor;

    private AppUsage _claude = AppUsage.Empty;
    private AppUsage _codex = AppUsage.Empty;
    private DateTimeOffset? _lastUpdated;
    private DateTimeOffset? _claudeUpdatedAt;
    private DateTimeOffset? _codexUpdatedAt;
    private DateTimeOffset? _lastAttemptedAt;
    private DateTimeOffset? _codexAttemptedAt;
    private DateTimeOffset? _lastPollAttemptedAt;
    private string? _refreshWarning;
    private bool _loading;
    private bool _claudeReauthInProgress;
    private bool _codexReauthInProgress;
    private string? _codexAutoSwitched;

    /// The login the Codex numbers were fetched for.
    private string? _codexLogin;

    private bool _refreshFetchesClaude;
    private bool _refreshFetchesCodex;
    private Dictionary<Model.DisplayProvider, bool>? _lastShown;
    private bool _visibilityMonitorArmed;

    /// Whether each provider's latest fetch came back empty-handed; the
    /// footer warning is made from both, whichever one was just fetched.
    private bool _claudeFailed;
    private bool _codexFailed;

    /// Accounts tried since the current exhaustion episode began; cleared the
    /// moment a reading comes back under 100%, so each episode walks the pool
    /// at most once and a fully-exhausted pool goes quiet instead of thrashing
    /// auth.json forever.
    private readonly HashSet<string> _codexAutoSwitchTried = new(StringComparer.Ordinal);

    /// The parked labels as they stood when the episode began. Activate parks
    /// the OUTGOING login under a fresh `previous-<stamp>` name whenever it
    /// matches nothing on disk — and a Codex CLI that rewrites auth.json
    /// between polls (token refresh) makes that happen on every rotation. The
    /// pool would then grow exactly as fast as the tried set, so a candidate
    /// would always exist and the rotation would rewrite auth.json forever.
    /// Anything that appears after the snapshot is one of those copies and is
    /// excluded for the rest of the episode.
    private List<string>? _codexAutoSwitchPool;

    private UiTimer? _pollTimer;
    private UiTimer? _resetEdgeTimer;
    private DateTimeOffset _lastResetEdgeCheck = DateTimeOffset.Now;
    private DateTimeOffset _refreshStartedAt;

    /// Wake and network recovery may start a refresh no sooner than this after
    /// the last one began. They used to cancel the one in flight and start over
    /// at once, which a machine flapping between sleep or networks turned into
    /// a burst against rate-limited endpoints.
    internal static readonly TimeSpan EventRefreshFloor = TimeSpan.FromMinutes(2);

    /// Test seam: the clock the floor is read against.
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    private bool WithinEventRefreshFloor() =>
        _refreshStartedAt != default && Clock() - _refreshStartedAt < EventRefreshFloor;

    /// Test seam: how the make-up refresh waits.
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    private CancellationTokenSource? _deferred;

    /// A wake or a network recovery the floor held back is made up for once
    /// the floor ends, so a launch with no network that gets one a moment
    /// later waits at most two minutes, not a whole poll interval. One at a
    /// time; a refresh of everything shown makes it unnecessary.
    private void DeferEventRefresh()
    {
        if (_deferred is not null) return;
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _deferred = cts;
        var wait = EventRefreshFloor - (Clock() - _refreshStartedAt);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > EventRefreshFloor) wait = EventRefreshFloor;
        _ = Task.Run(async () =>
        {
            try { await Delay(wait, token); }
            catch { return; }
            UiThread.Post(() =>
            {
                if (!ReferenceEquals(_deferred, cts) || cts.IsCancellationRequested) return;
                _deferred = null;
                cts.Dispose();
                // Refresh's own guard against overlap would swallow this one:
                // wait again rather than lose it.
                if (Loading && Clock() - _refreshStartedAt < TimeSpan.FromMinutes(2)
                    && (!ClaudeShown() || _refreshFetchesClaude) && (!CodexShown() || _refreshFetchesCodex))
                {
                    DeferEventRefresh();
                    return;
                }
                Refresh();
            });
        });
    }

    private void CancelDeferred()
    {
        var cts = _deferred;
        _deferred = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    private CancellationTokenSource? _codexReauthCts;
    private bool _networkMonitorArmed;
    private bool _powerMonitorArmed;
    private bool _lastNetworkAvailable = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    private UsageStore()
        : this(restore: true)
    {
    }

    /// A store of its own for tests, which also set the seams below: nothing
    /// restored from, or saved to, the user's settings.
    internal UsageStore(bool restore)
    {
        if (restore)
        {
            ClaudeShown = () => Model.ProviderVisibilityStore.Shared.IsShown(Model.DisplayProvider.Claude);
            CodexShown = () => Model.ProviderVisibilityStore.Shared.IsShown(Model.DisplayProvider.Codex);
            SaveSnapshot = SaveCachedSnapshot;
            AutoSwitch = MaybeAutoSwitchCodex;
            BeforeRefresh = KickOtherProviders;
        }
        if (restore && LoadCachedSnapshot() is { } snapshot)
        {
            var login = CodexLogin();
            // Numbers cached for another Codex login than auth.json holds now
            // are that account's, not this one's.
            var codexOwned = UsageCachePolicy.SameCodexLogin(snapshot.CodexLogin, login);
            _claude = snapshot.Claude;
            _codex = codexOwned ? snapshot.Codex : AppUsage.Empty;
            _codexLogin = codexOwned ? snapshot.CodexLogin ?? login : null;
            _lastUpdated = snapshot.UpdatedAt;
            _claudeUpdatedAt = UsageCachePolicy.CacheableCopy(snapshot.Claude) is null ? null : snapshot.ClaudeUpdatedAt;
            _codexUpdatedAt = !codexOwned || UsageCachePolicy.CacheableCopy(snapshot.Codex) is null ? null : snapshot.CodexUpdatedAt;
        }
    }

    // Test seams: what a refresh calls out to.
    internal Func<CancellationToken, Task<AppUsage>> CodexFetcher { get; set; } = UsageFetcher.FetchCodex;
    internal Func<CancellationToken, Task<AppUsage>> ClaudeFetcher { get; set; } = UsageFetcher.FetchClaude;
    internal Func<string?> CodexLogin { get; set; } = CodexCredentials.CurrentLogin;
    internal Func<bool> ClaudeShown { get; set; } = () => true;
    internal Func<bool> CodexShown { get; set; } = () => true;
    internal Action<AppUsage, AppUsage, bool, bool, string?> SaveSnapshot { get; set; } = (_, _, _, _, _) => { };
    internal Action<AppUsage> AutoSwitch { get; set; } = _ => { };
    internal Action BeforeRefresh { get; set; } = () => { };
    internal Task? RefreshTask => _refreshTask;

    public AppUsage Claude { get => _claude; private set { _claude = value; Raise(nameof(Claude)); } }
    public AppUsage Codex { get => _codex; private set { _codex = value; Raise(nameof(Codex)); } }
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set { _lastUpdated = value; Raise(nameof(LastUpdated)); } }
    public DateTimeOffset? ClaudeUpdatedAt { get => _claudeUpdatedAt; private set { _claudeUpdatedAt = value; Raise(nameof(ClaudeUpdatedAt)); } }
    public DateTimeOffset? CodexUpdatedAt { get => _codexUpdatedAt; private set { _codexUpdatedAt = value; Raise(nameof(CodexUpdatedAt)); } }
    public DateTimeOffset? LastAttemptedAt { get => _lastAttemptedAt; private set { _lastAttemptedAt = value; Raise(nameof(LastAttemptedAt)); } }
    public DateTimeOffset? CodexAttemptedAt { get => _codexAttemptedAt; private set { _codexAttemptedAt = value; Raise(nameof(CodexAttemptedAt)); } }
    public string? RefreshWarning { get => _refreshWarning; private set { _refreshWarning = value; Raise(nameof(RefreshWarning)); } }
    public bool Loading { get => _loading; private set { _loading = value; Raise(nameof(Loading)); } }

    /// Set while `claude /login` runs in a terminal; the UI hides the
    /// re-auth button during this window so users don't double-tap.
    public bool ClaudeReauthInProgress { get => _claudeReauthInProgress; private set { _claudeReauthInProgress = value; Raise(nameof(ClaudeReauthInProgress)); } }
    public bool CodexReauthInProgress { get => _codexReauthInProgress; private set { _codexReauthInProgress = value; Raise(nameof(CodexReauthInProgress)); } }

    /// Label the auto-switcher rotated to most recently, shown on the Codex
    /// card until the next manual action. Real state, not explanation.
    public string? CodexAutoSwitched { get => _codexAutoSwitched; set { _codexAutoSwitched = value; Raise(nameof(CodexAutoSwitched)); } }

    /// Refresh only when the last successful update is older than the poll
    /// interval — the panel-open freshness hook. Capped by the user's refresh
    /// interval so opening the island never polls the rate-limited endpoints
    /// any faster than the background schedule already would.
    public void RefreshIfStale()
    {
        var interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds);
        if ((_lastPollAttemptedAt ?? LastAttemptedAt ?? LastUpdated) is { } last && DateTimeOffset.Now - last < interval) return;
        Refresh();
    }

    /// The Codex login was just switched: the refresh in flight was fetched
    /// for the old account, so it is cancelled (its result is dropped) and a
    /// new one starts at once, past the usual guard against overlapping.
    /// Only Codex is fetched, unless the cancelled refresh was also Claude's.
    public void RefreshAfterCodexSwitch()
    {
        var claudeToo = Loading && _refreshFetchesClaude;
        _refreshCts?.Cancel();
        Loading = false;
        Refresh(fetchClaude: claudeToo, fetchCodex: true);
    }

    public void Refresh() => Refresh(fetchClaude: true, fetchCodex: true);

    /// Fetches only the shown providers among those asked for. A hidden agent's
    /// quota queries, token refreshes and account rotations stay completely off.
    internal void Refresh(bool fetchClaude, bool fetchCodex = true)
    {
        var wantClaude = fetchClaude && ClaudeShown();
        var wantCodex = fetchCodex && CodexShown();

        // Self-heal instead of early-return when a refresh has been "in
        // flight" for minutes: a wedged one (faulted task, socket dead after
        // sleep) used to latch Loading forever, freezing usage — and with it
        // reset detection and auto-resume — until the app was relaunched.
        // A single-provider refresh in flight is superseded when the other
        // shown provider now needs a fetch too.
        if (Loading
            && (!wantClaude || _refreshFetchesClaude)
            && (!wantCodex || _refreshFetchesCodex)
            && Clock() - _refreshStartedAt < TimeSpan.FromMinutes(2))
        {
            return;
        }
        if (Loading)
        {
            wantClaude |= _refreshFetchesClaude && ClaudeShown();
            wantCodex |= _refreshFetchesCodex && CodexShown();
        }

        if (fetchClaude && fetchCodex) _lastPollAttemptedAt = DateTimeOffset.Now;
        BeforeRefresh();

        if (!wantClaude && !wantCodex)
        {
            if (Loading && !_refreshFetchesClaude && !_refreshFetchesCodex) Loading = false;
            RefreshWarning = WarningFor(_codexFailed, _claudeFailed);
            return;
        }

        Loading = true;
        _refreshStartedAt = Clock();
        // Only a refresh of everything the make-up would fetch makes it unnecessary.
        if ((!ClaudeShown() || wantClaude) && (!CodexShown() || wantCodex)) CancelDeferred();
        _refreshFetchesClaude = wantClaude;
        _refreshFetchesCodex = wantCodex;
        if (wantClaude) LastAttemptedAt = _refreshStartedAt;
        if (wantCodex) CodexAttemptedAt = _refreshStartedAt;
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        // The Codex numbers are for the login in auth.json as the fetch
        // starts; if another is signed in by the time they land, they are
        // another account's.
        var codexLogin = wantCodex ? CodexLogin() : null;
        _refreshTask = Task.Run(async () =>
        {
            try
            {
                // Thread the token into the HTTP calls so a superseding refresh
                // (network-up mid-flight on a dead path) actually aborts the dead
                // request instead of letting it run to its own timeout. The
                // fetchers retry a transient network failure themselves; a
                // failed reply (429, 403, a lost sign-in) is never asked again.
                var codexTask = wantCodex ? CodexFetcher(cts.Token) : null;
                var claudeTask = wantClaude ? ClaudeFetcher(cts.Token) : null;
                var codexResult = codexTask is null ? null : await codexTask;
                var claudeResult = claudeTask is null ? null : await claudeTask;

                await UiThread.InvokeAsync(() =>
                {
                    // Cancellation = a superseding refresh took over (network
                    // came up, or the watchdog replaced a wedged one). Only
                    // the CURRENT refresh may touch Loading — a late loser
                    // clearing it would let a third refresh start mid-flight.
                    if (cts.IsCancellationRequested)
                    {
                        if (ReferenceEquals(_refreshCts, cts)) Loading = false;
                        return;
                    }

                    var commitClaude = claudeResult is not null && ClaudeShown();
                    var commitCodex = codexResult is not null && CodexShown()
                        && string.Equals(codexLogin, CodexLogin(), StringComparison.Ordinal);
                    if (commitCodex) _codexFailed = IsErrorOnly(codexResult!);
                    if (commitClaude) _claudeFailed = IsErrorOnly(claudeResult!);
                    var mergedCodex = commitCodex ? CommitCodex(codexResult!, codexLogin) : Codex;
                    if (commitClaude) Claude = MergedUsage(Claude, claudeResult!);
                    if (commitClaude || commitCodex)
                    {
                        SaveSnapshot(Claude, mergedCodex, commitClaude, commitCodex, _codexLogin);
                    }
                    RefreshWarning = WarningFor(_codexFailed, _claudeFailed);
                    var now = DateTimeOffset.Now;
                    if (commitClaude) RecordSuccessfulUpdate(claude: true, claudeResult!, now);
                    if (commitCodex) RecordSuccessfulUpdate(claude: false, codexResult!, now);
                    Loading = false;
                    if (commitCodex) AutoSwitch(mergedCodex);
                });
            }
            catch
            {
                // A faulted refresh must never leave Loading latched.
                await UiThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_refreshCts, cts)) Loading = false;
                });
            }
        }, CancellationToken.None);
    }

    /// Kimi Code rides this exact cadence (poll / wake / unlock / network /
    /// manual) instead of owning a timer; its store no-ops when the provider is
    /// undetected or when kicked again inside its own attempt floors. Guests
    /// re-probe every refresh cycle (macOS redetectGuests).
    private static void KickOtherProviders()
    {
        Model.ProviderVisibilityStore.Shared.RedetectGuests();
        KimiUsageStore.Shared.KickRefresh();
    }

    /// AUTO mode of `CodexAccountSwitcher` (the codex-auto borrow, driven by
    /// the real usage numbers instead of scraped terminal text). Runs on every
    /// fresh Codex reading: exhausted + enabled + a candidate exists → swap and
    /// immediately re-poll so the island shows the incoming account's numbers,
    /// not a stale 100%.
    ///
    /// The tried set is what stops an infinite credential-rewrite loop: the
    /// outgoing account is recorded before each swap, so once every parked
    /// login has been walked the rotation stops and auth.json is left alone
    /// until a reading drops back under 100%.
    private void MaybeAutoSwitchCodex(AppUsage usage)
    {
        if (!CodexShown()) return;
        if (!CodexAccountSwitcher.AutoSwitchEnabled) return;
        switch (ReadCodexQuota(usage, DateTimeOffset.Now))
        {
            case CodexQuotaReading.Unreadable:
                return;
            case CodexQuotaReading.Clear:
                _codexAutoSwitchTried.Clear();
                _codexAutoSwitchPool = null;
                return;
        }
        var pool = _codexAutoSwitchPool ??= CodexAccountSwitcher.Accounts()
            .Select(account => account.Label)
            .ToList();
        // Retire every label the episode did not start with, so the rotation
        // can only ever walk the snapshot and always runs out of candidates.
        foreach (var account in CodexAccountSwitcher.Accounts())
        {
            if (!pool.Contains(account.Label, StringComparer.Ordinal))
            {
                _codexAutoSwitchTried.Add(account.Label);
            }
        }
        if (CodexAccountSwitcher.ActiveLabel() is { } active) _codexAutoSwitchTried.Add(active);
        if (CodexAccountSwitcher.RotationCandidate(_codexAutoSwitchTried) is not { } next) return;
        if (!CodexAccountSwitcher.Activate(next)) return;
        _codexAutoSwitchTried.Add(next.Label);
        CodexAutoSwitched = next.Label;
        Refresh(fetchClaude: false, fetchCodex: true);
    }

    internal enum CodexQuotaReading
    {
        /// Both windows have errors: nothing to go on.
        Unreadable,
        /// A readable window is used up.
        Spent,
        /// Readable, and nothing is spent.
        Clear,
    }

    /// Whether the account has run dry: any window without an error, at its
    /// reading for `now` (one past its reset counts as reset), is at 100%. A
    /// window with an error is not read, so the other one decides.
    internal static CodexQuotaReading ReadCodexQuota(AppUsage usage, DateTimeOffset now)
    {
        if (usage.FiveHour.HasError && usage.Weekly.HasError) return CodexQuotaReading.Unreadable;
        var spent = (!usage.FiveHour.HasError && usage.FiveHour.At(now).UsedPercent >= 1)
            || (!usage.Weekly.HasError && usage.Weekly.At(now).UsedPercent >= 1);
        return spent ? CodexQuotaReading.Spent : CodexQuotaReading.Clear;
    }

    /// True when both windows have errors and zero values — nothing useful
    /// to show, so we keep whatever we had before.
    private static bool IsErrorOnly(AppUsage usage) =>
        usage.FiveHour.Error is not null && usage.Weekly.Error is not null
        && usage.FiveHour.UsedPercent == 0 && usage.Weekly.UsedPercent == 0;

    private static AppUsage MergedUsage(AppUsage existing, AppUsage fetched) =>
        UsageCachePolicy.Merged(existing, fetched);

    /// Shows a Codex result fetched for `login`. The numbers held for
    /// another login are not kept through its failure: after a switch whose
    /// first fetch fails, the error shows instead of the previous account's
    /// quota under the new login.
    private AppUsage CommitCodex(AppUsage fetched, string? login)
    {
        var sameLogin = UsageCachePolicy.SameCodexLogin(_codexLogin, login);
        if (!sameLogin) CodexUpdatedAt = null;
        var merged = MergedUsage(sameLogin ? Codex : AppUsage.Empty, fetched);
        if (login is not null) _codexLogin = login;
        Codex = merged;
        return merged;
    }

    private static string? WarningFor(bool codexFailed, bool claudeFailed)
    {
        // A provider the user hid from the drawer cannot nag from the
        // footer — "Claude stale" while only Grok + Cursor are selected reads
        // as a bug, because it was one (owner report, 2026-08-08).
        var visibility = Model.ProviderVisibilityStore.Shared;
        var claude = claudeFailed && visibility.IsShown(Model.DisplayProvider.Claude);
        var codex = codexFailed && visibility.IsShown(Model.DisplayProvider.Codex);
        return (claude, codex) switch
        {
            // Both down says nothing about WHY — two expired logins look
            // exactly like a dead uplink from here. "network drop" is the
            // transport-layer caption UsageFetcher hands back when it really
            // saw one; naming it from this summary would invent a cause.
            (true, true) => L10n.Tr("Usage refresh failed"),
            (true, false) => L10n.Tr("Claude stale"),
            (false, true) => L10n.Tr("Codex stale"),
            _ => null,
        };
    }

    // MARK: - Cache

    private void RecordSuccessfulUpdate(bool claude, AppUsage fetched, DateTimeOffset now)
    {
        var previous = claude ? ClaudeUpdatedAt : CodexUpdatedAt;
        var updated = UsageCachePolicy.SuccessfulUpdateAt(fetched, previous, now);
        if (updated == previous) return;
        if (claude) ClaudeUpdatedAt = updated;
        else CodexUpdatedAt = updated;
        LastUpdated = now;
    }

    private static UsageCacheSnapshot? LoadCachedSnapshot()
    {
        var snapshot = Preferences.Get<UsageCacheSnapshot?>(CacheKey);
        if (snapshot is null) return null;
        return UsageCachePolicy.RestoredSnapshot(snapshot, DateTimeOffset.Now, CacheMaxAge);
    }

    private static void SaveCachedSnapshot(
        AppUsage claude,
        AppUsage codex,
        bool fetchedClaude,
        bool fetchedCodex,
        string? codexLogin)
    {
        var existing = LoadCachedSnapshot();
        var snapshot = UsageCachePolicy.SnapshotForSave(
            claude, codex, existing, DateTimeOffset.Now, fetchedClaude, fetchedCodex, codexLogin);
        if (snapshot is null) return;
        Preferences.Set(CacheKey, snapshot);
    }

    // MARK: - Re-auth

    public bool ReauthenticateCodex()
    {
        if (CodexReauthInProgress) return true;
        var initialStamp = CodexCredentials.AuthModificationStamp();
        if (!CodexCredentials.SpawnReauth()) return false;
        CodexReauthInProgress = true;
        _codexReauthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _codexReauthCts = cts;
        _ = Task.Run(async () =>
        {
            // Same latch hazard as the Claude flow: nobody observes this task,
            // and a stuck CodexReauthInProgress makes every later attempt
            // return early without doing anything.
            try
            {
                for (var i = 0; i < 40; i++)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token); }
                    catch (TaskCanceledException) { return; }
                    var currentStamp = CodexCredentials.AuthModificationStamp();
                    if (currentStamp is null || currentStamp == initialStamp) continue;
                    await FinishCodexReauth();
                    return;
                }
                await FinishCodexReauth();
            }
            catch
            {
                try { await UiThread.InvokeAsync(() => { CodexReauthInProgress = false; }); }
                catch { }
            }
        });
        return true;
    }

    /// Sign-in through Claude Code itself: `claude /login` in a visible
    /// terminal re-mints the token with the scope set the CLI currently
    /// requests (the 403 fix), then the credentials file's mtime signals the
    /// new login. False when no CLI is found.
    public bool SignInClaudeInTerminal()
    {
        if (ClaudeReauthInProgress) return true;
        if (Trigger.CLILocator.PathFor(TriggerTool.Claude) is not { } cli) return false;
        var initialStamp = ClaudeCredentials.CredentialsModificationStamp();
        if (!TerminalLauncher.RunVisible(cli, "/login", "Limen — Claude login")) return false;
        ClaudeReauthInProgress = true;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 60; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    var currentStamp = ClaudeCredentials.CredentialsModificationStamp();
                    if (currentStamp is null || currentStamp == initialStamp) continue;
                    await FinishClaudeReauth();
                    return;
                }
                await FinishClaudeReauth();
            }
            catch
            {
                try { await UiThread.InvokeAsync(() => { ClaudeReauthInProgress = false; }); }
                catch { }
            }
        });
        return true;
    }

    private async Task FinishClaudeReauth()
    {
        var fetched = await UsageFetcher.FetchClaude();
        await UiThread.InvokeAsync(() =>
        {
            var merged = MergedUsage(Claude, fetched);
            Claude = merged;
            SaveCachedSnapshot(merged, Codex, fetchedClaude: true, fetchedCodex: false, _codexLogin);
            _claudeFailed = IsErrorOnly(fetched);
            RefreshWarning = WarningFor(_codexFailed, _claudeFailed);
            LastAttemptedAt = DateTimeOffset.Now;
            RecordSuccessfulUpdate(claude: true, fetched, LastAttemptedAt.Value);
            ClaudeReauthInProgress = false;
        });
    }

    private async Task FinishCodexReauth()
    {
        // `codex login` may well sign in another account.
        var login = CodexLogin();
        var fetched = await UsageFetcher.FetchCodex();
        await UiThread.InvokeAsync(() =>
        {
            var merged = CommitCodex(fetched, login);
            SaveCachedSnapshot(Claude, merged, fetchedClaude: false, fetchedCodex: true, _codexLogin);
            _codexFailed = IsErrorOnly(fetched);
            RefreshWarning = WarningFor(_codexFailed, _claudeFailed);
            CodexAttemptedAt = DateTimeOffset.Now;
            RecordSuccessfulUpdate(claude: false, fetched, CodexAttemptedAt.Value);
            CodexReauthInProgress = false;
        });
    }

    // MARK: - Auto refresh

    public void StartAutoRefresh()
    {
        StopAutoRefresh();
        _lastShown = SnapshotShown();
        Model.ProviderVisibilityStore.Shared.PropertyChanged += OnVisibilityChanged;
        _visibilityMonitorArmed = true;
        if (OperatingSystem.IsWindows()) ArmSystemEvents();
        Refresh();
        ArmTimer();
        ArmResetEdgeTimer();
        RefreshIntervalStore.Shared.PropertyChanged += OnIntervalChanged;
        StartNetworkMonitor();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ArmSystemEvents()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        _powerMonitorArmed = true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void DisarmSystemEvents()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _powerMonitorArmed = false;
    }

    public void StopAutoRefresh()
    {
        CancelDeferred();
        _pollTimer?.Stop();
        _pollTimer = null;
        _resetEdgeTimer?.Stop();
        _resetEdgeTimer = null;
        RefreshIntervalStore.Shared.PropertyChanged -= OnIntervalChanged;
        if (_visibilityMonitorArmed)
        {
            Model.ProviderVisibilityStore.Shared.PropertyChanged -= OnVisibilityChanged;
            _visibilityMonitorArmed = false;
        }
        if (_networkMonitorArmed)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _networkMonitorArmed = false;
        }
        if (_powerMonitorArmed && OperatingSystem.IsWindows()) DisarmSystemEvents();
    }

    private static Dictionary<Model.DisplayProvider, bool> SnapshotShown() =>
        Model.DisplayProviders.All.ToDictionary(p => p, Model.ProviderVisibilityStore.Shared.IsShown);

    private void OnVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        var current = SnapshotShown();
        UiThread.Post(() => HandleVisibilityChange(current));
    }

    /// Aborts in-flight requests whose providers were just hidden, and fetches
    /// any provider that just went from hidden to shown.
    private void HandleVisibilityChange(Dictionary<Model.DisplayProvider, bool> current)
    {
        CancelIfHidden();
        RefreshWarning = WarningFor(_codexFailed, _claudeFailed);
        var previous = _lastShown;
        _lastShown = current;
        if (previous is null) return;
        foreach (var (provider, shown) in current)
        {
            if (shown && previous.TryGetValue(provider, out var wasShown) && !wasShown)
            {
                Model.AgentReports.RefreshNow(provider);
            }
        }
    }

    internal void CancelIfHidden()
    {
        if (!Loading) return;
        var stillWanted = (_refreshFetchesClaude && ClaudeShown()) || (_refreshFetchesCodex && CodexShown());
        if (stillWanted) return;
        _refreshCts?.Cancel();
        Loading = false;
    }

    /// Refresh on unlock, the macOS screenIsUnlocked mirror: the timers keep
    /// running while locked on Windows, but modern-standby machines may still
    /// have dozed midway — a stale-gated refresh at unlock is cheap insurance
    /// that a reset which landed during the lock is caught the moment you're
    /// back, not up to a poll interval later.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        HandleUnlock();
    }

    /// The screen unlocked (the Mac's session-active notification calls this).
    public void HandleUnlock() => UiThread.Post(RefreshIfStale);

    /// Refresh right after waking from sleep — the poll timer's schedule
    /// slid while suspended, and any in-flight request died with the network
    /// stack. Without this, a machine that slept through a reset boundary
    /// shows the expired countdown until the next poll (up to 30 minutes).
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        HandleWake();
    }

    /// The computer woke from sleep (the Mac's didWake notification calls this).
    public void HandleWake()
    {
        UiThread.Post(() =>
        {
            // Not within two minutes of the last attempt, whatever woke us.
            if (WithinEventRefreshFloor()) { DeferEventRefresh(); return; }
            // The dead in-flight request would block Refresh's early-return
            // for up to 2 minutes — supersede it outright.
            _refreshCts?.Cancel();
            Loading = false;
            Refresh();
        });
    }

    /// The moment any known reset boundary passes, fetch fresh windows —
    /// don't sit on 100%-with-expired-countdown until the next scheduled
    /// poll. This is also what lets AfterReset auto-resume triggers fire
    /// within a minute of the reset instead of up to a poll interval late.
    private void ArmResetEdgeTimer()
    {
        _lastResetEdgeCheck = DateTimeOffset.Now;
        _resetEdgeTimer = new UiTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _resetEdgeTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.Now;
            var previous = _lastResetEdgeCheck;
            _lastResetEdgeCheck = now;
            bool Passed(DateTimeOffset? reset) => reset is { } at && previous < at && at <= now;
            var claudeReset = ClaudeShown() && (Passed(Claude.FiveHour.ResetAt) || Passed(Claude.Weekly.ResetAt));
            var codexReset = CodexShown() && (Passed(Codex.FiveHour.ResetAt) || Passed(Codex.Weekly.ResetAt));
            if (claudeReset || codexReset)
            {
                Refresh(fetchClaude: claudeReset, fetchCodex: codexReset);
            }
        };
        _resetEdgeTimer.Start();
    }

    private void OnIntervalChanged(object? sender, PropertyChangedEventArgs e) => ArmTimer();

    private void ArmTimer()
    {
        _pollTimer?.Stop();
        _pollTimer = new UiTimer
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds),
        };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();
    }

    /// Trigger an immediate refresh when connectivity returns — closes the
    /// launch-at-login race where Wi-Fi is still associating when the first
    /// refresh fires. Without this, the panel sits at the cold-start state
    /// until the next scheduled poll (5–30 minutes away).
    private void StartNetworkMonitor()
    {
        _lastNetworkAvailable = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _networkMonitorArmed = true;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        var was = _lastNetworkAvailable;
        _lastNetworkAvailable = e.IsAvailable;
        if (!e.IsAvailable || was) return;
        UiThread.Post(async () => await HandleNetworkRecovered());
    }

    internal async Task HandleNetworkRecovered()
    {
        // This runs async void on the dispatcher: anything it throws
        // past the first await lands on the dispatcher as an unhandled
        // exception, and the app's handler logs without marking it
        // handled — so a hiccup on a network transition would take the
        // whole app down. Reconnect recovery is best-effort by nature.
        try
        {
            if (WithinEventRefreshFloor()) { DeferEventRefresh(); return; }
            // Cancel any in-flight refresh — it was started on the dead
            // path and will return an error. Wait for it to finalize so
            // its loading=false lands before the replacement starts.
            _refreshCts?.Cancel();
            if (_refreshTask is { } task)
            {
                try { await task; } catch { }
            }
            Refresh();
        }
        catch
        {
            // The poll timer is still the backstop.
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
