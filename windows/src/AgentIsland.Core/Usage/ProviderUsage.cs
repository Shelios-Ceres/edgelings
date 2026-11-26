using AgentIsland.Model;

namespace AgentIsland.Usage;

/// One AppUsage per provider. Claude and Codex come straight from UsageStore,
/// Kimi from its own store; Antigravity, Grok and Cursor have no quota any
/// more and read as Empty. A store that kept its last numbers through a
/// failure marks them Kept.
public static class ProviderUsage
{
    public static AppUsage For(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.Claude,
        DisplayProvider.Codex => UsageStore.Shared.Codex,
        DisplayProvider.Kimi => Kimi(),
        _ => AppUsage.Empty,
    };

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
}
