namespace AgentIsland.Usage;

/// One rate-limit window as rendered in the UI. UsedPercent is normalized to
/// [0, 1]; Error carries the caption shown in place of a value.
/// PeriodSeconds is the window length as reported by the provider (Codex
/// sends window_minutes; Claude's windows are the documented 5h/7d).
/// It drives display labels, so when a provider changes its quota model —
/// Codex replaced its 5-hour window with a single weekly one in July 2026 —
/// the tile relabels itself instead of lying under a hardcoded "5h". null on
/// old cached snapshots; labels fall back to the slot name.
public sealed record WindowUsage(
    double UsedPercent, DateTimeOffset? ResetAt, string? Error, double? PeriodSeconds = null)
{
    public static WindowUsage Unknown { get; } = new(0, null, "no data");

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// The last good numbers, kept through a failed refresh or a closed app;
    /// Error then says why they are not live rather than that none exist.
    public bool Kept { get; init; }

    /// A provider explicitly supplied this number, including a genuine zero
    /// without a reset date. Older caches omit this flag and retain the
    /// existing nonzero/reset-date evidence rules.
    public bool Observed { get; init; }

    public bool AwaitingSync { get; init; }

    /// A reset ends the observed window; it does not tell us how much of the
    /// next window has already been used. Await a new reading instead of
    /// inventing a full balance and a future reset date.
    public WindowUsage At(DateTimeOffset now)
    {
        if (ResetAt is not { } reset || reset > now) return this;
        return this with { UsedPercent = 0, ResetAt = null, AwaitingSync = true };
    }
}

/// Usage for one provider: the two window slots plus the plan tier ("max",
/// "pro", ...) surfaced as the chip badge next to the provider name.
/// ObservedAt is when the provider itself reported these numbers, for a
/// source that is read from disk and can be days old (Codex rollouts); null
/// when the numbers were just fetched.
public sealed record AppUsage(
    WindowUsage FiveHour,
    WindowUsage Weekly,
    string? Plan = null)
{
    public DateTimeOffset? ObservedAt { get; init; }

    public static AppUsage Empty { get; } = new(WindowUsage.Unknown, WindowUsage.Unknown);

    /// 提供方没有任何读数（不是出错）：两个槽位都是 "no data"。
    public static AppUsage NoData => Empty;

    /// 两个槽位都是 "no data"：提供方没有任何读数，不是出错。
    public bool IsNoData => FiveHour.Error == "no data" && Weekly.Error == "no data";

    public static AppUsage ErrorPair(string message) => new(
        new WindowUsage(0, null, message),
        new WindowUsage(0, null, message));

    /// True when this fetch produced nothing usable — both windows errored
    /// and no percentage survived. Used to keep showing the last good value.
    public bool IsErrorOnly =>
        FiveHour.HasError && Weekly.HasError && FiveHour.UsedPercent == 0 && Weekly.UsedPercent == 0;

    /// True when the provider reported only ONE window on a healthy fetch —
    /// Codex's July 2026 shape (a single weekly quota, secondary_window
    /// gone). The UI hides the empty tile instead of pinning "no data".
    /// Cold start (both unknown) and fetch failures don't qualify, because
    /// the primary slot carries an error too, unless it holds kept numbers.
    public bool SecondaryMissing => Weekly.Error == "no data" && (FiveHour.Error is null || FiveHour.Kept);
}
