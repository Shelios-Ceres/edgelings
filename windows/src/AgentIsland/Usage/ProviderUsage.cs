using AgentIsland.Model;

namespace AgentIsland.Usage;

/// One AppUsage per provider, whatever store and snapshot shape backs it:
/// Claude and Codex come straight from UsageStore, the guests are mapped
/// from their own stores into the shared window shape.
public static class ProviderUsage
{
    public static AppUsage For(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.Claude,
        DisplayProvider.Codex => UsageStore.Shared.Codex,
        DisplayProvider.Antigravity => Antigravity(),
        DisplayProvider.Grok => Grok(),
        DisplayProvider.Cursor => Cursor(),
        _ => AppUsage.Empty,
    };

    /// Antigravity surfaces ONE pool — Gemini's weekly bucket. The shared
    /// Claude/GPT pool is real data but belongs to other providers' tiles
    /// (owner call, 2026-08-09: 只搞 Gemini). The secondary slot stays the
    /// "no data" sentinel, which is what marks a provider single-window.
    private static AppUsage Antigravity()
    {
        var store = AntigravityUsageStore.Shared;
        var pool = store.Snapshot?.Primary;
        return new AppUsage(
            new WindowUsage(pool?.UsedPercent ?? 0, pool?.ResetAt, store.StatusCaption, pool?.PeriodSeconds),
            WindowUsage.Unknown,
            store.TierBadge?.ToLowerInvariant());
    }

    /// Grok meters one weekly credit pool; the monthly dollar budget is a
    /// caption elsewhere, not a second quota window.
    private static AppUsage Grok()
    {
        var grok = GrokUsageStore.Shared;
        var snapshot = grok.Snapshot;
        return new AppUsage(
            new WindowUsage(snapshot?.WeeklyUsedPercent ?? 0, snapshot?.WeeklyPeriodEnd, grok.ErrorCaption),
            WindowUsage.Unknown,
            grok.AuthModeBadge?.ToLowerInvariant());
    }

    /// Cursor reports a single included-usage pool per ~30-day billing cycle.
    private static AppUsage Cursor()
    {
        var cursor = CursorUsageStore.Shared;
        var snapshot = cursor.Snapshot;
        return new AppUsage(
            new WindowUsage(snapshot?.UsedPercent ?? 0, snapshot?.PeriodEnd, cursor.ErrorCaption),
            WindowUsage.Unknown,
            cursor.PlanBadge?.ToLowerInvariant());
    }
}
