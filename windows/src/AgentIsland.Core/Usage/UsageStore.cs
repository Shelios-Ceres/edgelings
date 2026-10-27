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
    private string? _refreshWarning;
    private bool _loading;
    private bool _claudeReauthInProgress;
    private bool _codexReauthInProgress;
    private string? _codexAutoSwitched;

    /// The login the Codex numbers were fetched for.
    private string? _codexLogin;

    private bool _refreshFetchesClaude;

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
        if ((LastAttemptedAt ?? LastUpdated) is { } last && DateTimeOffset.Now - last < interval) return;
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
        Refresh(fetchClaude: claudeToo);
    }

    public void Refresh() => Refresh(fetchClaude: true);

    /// Codex alone when only Codex asked for it — a switch, an auto-switch,
    /// a Codex reset — so Claude keeps its poll cadence: Anthropic
    /// rate-limits the usage endpoint hard, and walking three spent Codex
    /// accounts used to ask it four times in ten seconds.
    private void Refresh(bool fetchClaude)
    {
        // Self-heal instead of early-return when a refresh has been "in
        // flight" for minutes: a wedged one (faulted task, socket dead after
        // sleep) used to latch Loading forever, freezing usage — and with it
        // reset detection and auto-resume — until the app was relaunched.
        if (Loading && DateTimeOffset.Now - _refreshStartedAt < TimeSpan.FromMinutes(2)) return;

        BeforeRefresh();

        Loading = true;
        _refreshStartedAt = DateTimeOffset.Now;
        _refreshFetchesClaude = fetchClaude;
        if (fetchClaude) LastAttemptedAt = _refreshStartedAt;
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        // The Codex numbers are for the login in auth.json as the fetch
        // starts; if another is signed in by the time they land, they are
        // another account's.
        var codexLogin = CodexLogin();
        _refreshTask = Task.Run(async () =>
        {
            try
            {
                // Thread the token into the HTTP calls so a superseding refresh
                // (network-up mid-flight on a dead path) actually aborts the dead
                // request instead of letting it run to its own timeout. The
                // fetchers retry a transient network failure themselves; a
                // failed reply (429, 403, a lost sign-in) is never asked again.
                var codexTask = CodexFetcher(cts.Token);
                var claudeTask = fetchClaude ? ClaudeFetcher(cts.Token) : null;
                var codexResult = await codexTask;
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

                    var codexCurrent = string.Equals(codexLogin, CodexLogin(), StringComparison.Ordinal);
                    if (codexCurrent) _codexFailed = IsErrorOnly(codexResult);
                    if (claudeResult is not null) _claudeFailed = IsErrorOnly(claudeResult);
                    var mergedCodex = codexCurrent ? CommitCodex(codexResult, codexLogin) : Codex;
                    if (claudeResult is not null) Claude = MergedUsage(Claude, claudeResult);
                    SaveSnapshot(Claude, mergedCodex, claudeResult is not null, codexCurrent, _codexLogin);
                    RefreshWarning = WarningFor(_codexFailed, _claudeFailed);
                    var now = DateTimeOffset.Now;
                    if (claudeResult is not null) RecordSuccessfulUpdate(claude: true, claudeResult, now);
                    if (codexCurrent) RecordSuccessfulUpdate(claude: false, codexResult, now);
                    Loading = false;
                    if (codexCurrent) AutoSwitch(mergedCodex);
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

    /// Grok, Kimi Code, Antigravity and Cursor ride this exact cadence (poll /
    /// wake / unlock / network / manual) instead of owning timers; their stores
    /// no-op when the provider is undetected or when kicked again inside their
    /// own attempt floors. Guests re-probe every refresh cycle (macOS
    /// redetectGuests): signing into agy/grok/Cursor while the app runs claims
    /// the slot without a relaunch.
    private static void KickOtherProviders()
    {
        Model.ProviderVisibilityStore.Shared.RedetectGuests();
        GrokUsageStore.Shared.KickRefresh();
        AntigravityUsageStore.Shared.KickRefresh();
        CursorUsageStore.Shared.KickRefresh();
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
        Refresh(fetchClaude: false);
    }

    internal enum CodexQuotaReading
    {
        /// Both windows have errors: nothing to go on.
        Unreadable,
        /// A readable window is used up (the rule that puts the bot to sleep).
        Spent,
        /// Readable, and nothing is spent.
        Clear,
    }

    /// Whether the account has run dry, by the same rule as
    /// `AppUsage.IsExhausted`: any window without an error, at its reading
    /// for `now` (one past its reset counts as reset), is at 100%. A window
    /// with an error is not read, so the other one decides.
    internal static CodexQuotaReading ReadCodexQuota(AppUsage usage, DateTimeOffset now)
    {
        if (usage.FiveHour.HasError && usage.Weekly.HasError) return CodexQuotaReading.Unreadable;
        return usage.IsExhausted(now) ? CodexQuotaReading.Spent : CodexQuotaReading.Clear;
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
        if (!TerminalLauncher.RunVisible(cli, "/login", "Agent Island — Claude login")) return false;
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
            LastAttemptedAt = DateTimeOffset.Now;
            RecordSuccessfulUpdate(claude: false, fetched, LastAttemptedAt.Value);
            CodexReauthInProgress = false;
        });
    }

    // MARK: - Auto refresh

    public void StartAutoRefresh()
    {
        StopAutoRefresh();
        Refresh();
        ArmTimer();
        ArmResetEdgeTimer();
        RefreshIntervalStore.Shared.PropertyChanged += OnIntervalChanged;
        StartNetworkMonitor();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        _powerMonitorArmed = true;
    }

    public void StopAutoRefresh()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _resetEdgeTimer?.Stop();
        _resetEdgeTimer = null;
        RefreshIntervalStore.Shared.PropertyChanged -= OnIntervalChanged;
        if (_networkMonitorArmed)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _networkMonitorArmed = false;
        }
        if (_powerMonitorArmed)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            _powerMonitorArmed = false;
        }
    }

    /// Refresh on unlock, the macOS screenIsUnlocked mirror: the timers keep
    /// running while locked on Windows, but modern-standby machines may still
    /// have dozed midway — a stale-gated refresh at unlock is cheap insurance
    /// that a reset which landed during the lock is caught the moment you're
    /// back, not up to a poll interval later.
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        UiThread.Post(RefreshIfStale);
    }

    /// Refresh right after waking from sleep — the poll timer's schedule
    /// slid while suspended, and any in-flight request died with the network
    /// stack. Without this, a machine that slept through a reset boundary
    /// shows the expired countdown until the next poll (up to 30 minutes).
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        UiThread.Post(() =>
        {
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
            if (Passed(Claude.FiveHour.ResetAt) || Passed(Claude.Weekly.ResetAt))
            {
                Refresh();
            }
            else if (Passed(Codex.FiveHour.ResetAt) || Passed(Codex.Weekly.ResetAt))
            {
                Refresh(fetchClaude: false);
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
        UiThread.Post(async () =>
        {
            // This lambda is async void on the dispatcher: anything it throws
            // past the first await lands on the dispatcher as an unhandled
            // exception, and the app's handler logs without marking it
            // handled — so a hiccup on a network transition would take the
            // whole app down. Reconnect recovery is best-effort by nature.
            try
            {
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
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
