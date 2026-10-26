using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.Cost;

/// A card's token figures and when they were counted. Saved after every scan
/// so a restart shows the last counts at once instead of nothing.
public sealed record TokenTotals(DateTimeOffset AsOf, long Today, long TodayBillable, long Month, long MonthBillable)
{
    /// Two agents' figures as one: the counts add up, dated by the older of
    /// the two, so a stale part never passes for today's.
    public static TokenTotals? Sum(TokenTotals? a, TokenTotals? b) => (a, b) switch
    {
        ({ } x, { } y) => new TokenTotals(x.AsOf < y.AsOf ? x.AsOf : y.AsOf,
            x.Today + y.Today, x.TodayBillable + y.TodayBillable, x.Month + y.Month, x.MonthBillable + y.MonthBillable),
        _ => a ?? b,
    };
}

/// Publishes per-provider cost rollups from the local logs. Scans run in
/// parallel off the UI thread on the shared poll cadence.
public sealed class CostStore : INotifyPropertyChanged
{
    public static CostStore Shared { get; } = new();

    // Replaced wholesale, never mutated: the drawer reads it from its own UI
    // thread while commits land on the app thread.
    private volatile Dictionary<DisplayProvider, ProviderCostSummary> _summaries = new();
    private const string TotalsKey = "CostStore.cardTotals.v1";
    // The last saved figures by provider name, replaced wholesale like
    // _summaries; a provider's live summary wins once its first scan commits.
    private volatile Dictionary<string, TokenTotals> _savedTotals;
    private DateTimeOffset? _lastUpdated;
    private bool _scanning;
    private DateTimeOffset _scanStartedAt;
    private UiTimer? _pollTimer;

    public event PropertyChangedEventHandler? PropertyChanged;

    private CostStore()
    {
        _summaries = DisplayProviders.All.ToDictionary(p => p, _ => ProviderCostSummary.Empty);
        _savedTotals = Preferences.Get<Dictionary<string, TokenTotals>>(TotalsKey) ?? new Dictionary<string, TokenTotals>();
    }

    /// The figures a card shows: the live scan's, or until a provider's first
    /// scan of this run commits, the ones the last run saved.
    public TokenTotals? Totals(DisplayProvider provider)
    {
        var summary = Summary(provider);
        if (summary.AsOf is { } asOf)
        {
            return new TokenTotals(asOf, summary.TodayTokens, summary.TodayBillableTokens,
                summary.MonthTokens, summary.MonthBillableTokens);
        }
        return _savedTotals.TryGetValue(provider.ToString(), out var saved) ? saved : null;
    }

    private void SaveTotals()
    {
        var totals = new Dictionary<string, TokenTotals>(_savedTotals);
        foreach (var provider in DisplayProviders.All)
        {
            if (Summary(provider).AsOf is not null && Totals(provider) is { } live) totals[provider.ToString()] = live;
        }
        _savedTotals = totals;
        Preferences.Set(TotalsKey, totals);
    }

    /// One provider's rollup, keyed by DisplayProvider. Empty until its first
    /// scan commits, so a caller never sees null and a provider with no local
    /// log reads as honest zeros rather than a fabricated $0.
    public ProviderCostSummary Summary(DisplayProvider provider) =>
        _summaries.TryGetValue(provider, out var summary) ? summary : ProviderCostSummary.Empty;

    public ProviderCostSummary Claude => Summary(DisplayProvider.Claude);
    public ProviderCostSummary Codex => Summary(DisplayProvider.Codex);
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set { _lastUpdated = value; Raise(nameof(LastUpdated)); } }

    private void SetSummary(DisplayProvider provider, ProviderCostSummary summary)
    {
        _summaries = new Dictionary<DisplayProvider, ProviderCostSummary>(_summaries) { [provider] = summary };
        // Keep the two named accessors' change notifications so existing
        // subscribers (OverviewPage, report cards) refresh exactly as before;
        // guest tiles ride the LastUpdated notification the commit also raises.
        if (provider == DisplayProvider.Claude) Raise(nameof(Claude));
        else if (provider == DisplayProvider.Codex) Raise(nameof(Codex));
    }

    public void StartAutoRefresh()
    {
        Refresh();
        _pollTimer?.Stop();
        _pollTimer = new UiTimer
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds),
        };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();
        RefreshIntervalStore.Shared.PropertyChanged += (_, _) =>
        {
            if (_pollTimer is { } timer)
            {
                timer.Interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds);
            }
        };
    }

    public void Refresh()
    {
        // The in-flight latch gets a 10-minute wedge escape (macOS 28c9b4c):
        // one scan that never completes must not freeze cost data for the
        // process lifetime — panel numbers were observed stuck six hours
        // behind a hung latch.
        if (_scanning && DateTimeOffset.Now - _scanStartedAt < TimeSpan.FromMinutes(10)) return;
        _scanning = true;
        _scanStartedAt = DateTimeOffset.Now;
        var now = DateTimeOffset.Now;
        var lookback = CostSummarizer.YearHistoryDays(now);
        // Seven readers, one gate. Grok self-reports dollars, Cursor yields token
        // counts only and the rest are table-priced — each summarized off the UI
        // thread and committed together so a slow scan never blocks a fast one
        // twice. Antigravity's may ask its language servers for each step's
        // model, so it can take a few seconds the first time.
        var claudeTask = Task.Run(() => CostSummarizer.Summarize(ClaudeLogReader.Scan(lookback), now));
        var codexTask = Task.Run(() => CostSummarizer.Summarize(CodexLogReader.Scan(lookback), now));
        var antigravityTask = Task.Run(() => CostSummarizer.Summarize(AntigravityLogReader.Scan(lookback), now) with { ModelNames = AntigravityLogReader.ModelNames() });
        var grokTask = Task.Run(() => CostSummarizer.Summarize(GrokLogReader.Scan(lookback), now));
        var cursorTask = Task.Run(() => CostSummarizer.Summarize(CursorLogReader.Scan(lookback), now));
        var kimiTask = Task.Run(() => CostSummarizer.Summarize(KimiLogReader.Scan(lookback), now) with { ModelNames = KimiLogReader.ModelNames() });
        var deepSeekTask = Task.Run(() => CostSummarizer.Summarize(DeepSeekLogReader.Scan(lookback), now));
        _ = Task.WhenAll(claudeTask, codexTask, antigravityTask, grokTask, cursorTask, kimiTask, deepSeekTask).ContinueWith(_ =>
        {
            UiThread.Post(() =>
            {
                if (claudeTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Claude, claudeTask.Result);
                if (codexTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Codex, codexTask.Result);
                if (antigravityTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Antigravity, antigravityTask.Result);
                if (grokTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Grok, grokTask.Result);
                if (cursorTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Cursor, cursorTask.Result);
                if (kimiTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Kimi, kimiTask.Result);
                if (deepSeekTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.DeepSeek, deepSeekTask.Result);
                SaveTotals();
                LastUpdated = DateTimeOffset.Now;
                _scanning = false;
            });
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
