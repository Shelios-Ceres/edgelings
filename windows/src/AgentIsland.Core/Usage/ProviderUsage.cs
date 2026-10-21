using AgentIsland.Model;

namespace AgentIsland.Usage;

/// One AppUsage per provider, whatever store and snapshot shape backs it:
/// Claude and Codex come straight from UsageStore, the guests are mapped
/// from their own stores into the shared window shape. A store that kept its
/// last numbers through a failure marks them Kept.
public static class ProviderUsage
{
    public static AppUsage For(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.Claude,
        DisplayProvider.Codex => UsageStore.Shared.Codex,
        DisplayProvider.Antigravity => Antigravity(),
        DisplayProvider.Grok => Grok(),
        DisplayProvider.Cursor => Cursor(),
        DisplayProvider.Kimi => Kimi(),
        _ => AppUsage.Empty,
    };

    private static AppUsage Antigravity()
    {
        var store = AntigravityUsageStore.Shared;
        return AntigravityFrom(store.Snapshot, store.StatusCaption, store.TierBadge);
    }

    /// Antigravity's Gemini pools: the main group's 5-hour window first, then
    /// its longer one. The shared Claude/GPT pool is real data but belongs to
    /// other providers' tiles (owner call, 2026-08-09: 只搞 Gemini). A
    /// snapshot with only one window (older caches, other tiers) fills the
    /// first slot and leaves the second the "no data" sentinel, which is what
    /// marks a provider single-window.
    internal static AppUsage AntigravityFrom(AntigravityQuotaSnapshot? snapshot, string? caption, string? tierBadge)
    {
        var pools = snapshot?.GeminiPools;
        var (first, second) = pools?.ShortWindow is { } shortWindow
            ? (shortWindow, pools.LongWindow)
            : (pools?.LongWindow, null);
        return new AppUsage(
            AntigravityWindow(first, caption),
            second is null ? WindowUsage.Unknown : AntigravityWindow(second, caption),
            tierBadge?.ToLowerInvariant());
    }

    /// One bucket as a window; a failed refresh keeps the last numbers under
    /// its caption.
    internal static WindowUsage AntigravityWindow(AntigravityQuotaBucket? pool, string? caption) =>
        new(pool?.UsedPercent ?? 0, pool?.ResetAt, caption ?? (pool is null ? "no data" : null), pool?.PeriodSeconds)
        {
            Kept = pool is not null && caption is not null,
            Observed = pool is not null,
        };

    /// Grok meters one weekly credit pool; the monthly dollar budget is a
    /// caption elsewhere, not a second quota window.
    private static AppUsage Grok()
    {
        var grok = GrokUsageStore.Shared;
        var snapshot = grok.Snapshot;
        return new AppUsage(
            new WindowUsage(snapshot?.WeeklyUsedPercent ?? 0, snapshot?.WeeklyPeriodEnd, grok.ErrorCaption ?? (snapshot is null ? "no data" : null))
            {
                Kept = snapshot is not null && grok.ErrorCaption is not null,
                Observed = snapshot is not null,
            },
            WindowUsage.Unknown,
            grok.AuthModeBadge?.ToLowerInvariant());
    }

    /// Kimi Code: the 5-hour window first, then the weekly one when the plan
    /// has it, else the monthly total — the window that runs out last.
    private static AppUsage Kimi()
    {
        var kimi = KimiUsageStore.Shared;
        var snapshot = kimi.Snapshot;
        WindowUsage Slot(KimiQuotaWindow? window, double period) => window is { } w
            ? new WindowUsage(w.UsedPercent, w.ResetAt, kimi.ErrorCaption, period) { Kept = kimi.ErrorCaption is not null, Observed = true }
            : snapshot is null ? new WindowUsage(0, null, kimi.ErrorCaption ?? "no data") : WindowUsage.Unknown;
        var longer = snapshot?.Weekly is { } week ? Slot(week, 7 * 86400) : Slot(snapshot?.Month, 30 * 86400);
        return new AppUsage(Slot(snapshot?.FiveHour, 5 * 3600), longer, kimi.PlanBadge?.ToLowerInvariant());
    }

    /// Cursor reports a single included-usage pool per ~30-day billing cycle.
    private static AppUsage Cursor()
    {
        var cursor = CursorUsageStore.Shared;
        var snapshot = cursor.Snapshot;
        return new AppUsage(
            new WindowUsage(snapshot?.UsedPercent ?? 0, snapshot?.PeriodEnd, cursor.ErrorCaption ?? (snapshot is null ? "no data" : null))
            {
                Kept = snapshot is not null && cursor.ErrorCaption is not null,
                Observed = snapshot is not null,
            },
            WindowUsage.Unknown,
            cursor.PlanBadge?.ToLowerInvariant());
    }
}
