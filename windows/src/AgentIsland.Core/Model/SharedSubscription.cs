using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Model;

/// Grok and Cursor on one X Subscription (October 2026): one bill and one
/// usage pool for Grok models in the Grok apps, on X and in Cursor. Their
/// usage APIs then report that pool to the same reset instant, and the drawer
/// shows the two apps as one character, Grok's. Plans still billed apart
/// reset on their own schedules and keep two characters.
public static class SharedSubscription
{
    public static bool GrokWithCursor() =>
        GrokWithCursor(GrokUsageStore.Shared.Snapshot?.WeeklyPeriodEnd, CursorUsageStore.Shared.Snapshot?.PeriodEnd);

    /// The same reset, give or take API rounding: two independent cycles
    /// ending within a minute of each other is no coincidence worth fearing.
    public static bool GrokWithCursor(DateTimeOffset? grokReset, DateTimeOffset? cursorReset) =>
        grokReset is { } grok && cursorReset is { } cursor && (grok - cursor).Duration() < TimeSpan.FromMinutes(1);

    /// The merged character plays the more urgent of the two apps' phases,
    /// Grok's on a tie; its detail names the app the phase comes from.
    public static (AgentPhase Phase, string? Detail) Pick(
        (AgentPhase Phase, string? Detail) grok, (AgentPhase Phase, string? Detail) cursor)
    {
        var fromCursor = AgentPhases.Priority(cursor.Phase) < AgentPhases.Priority(grok.Phase);
        var (phase, detail) = fromCursor ? cursor : grok;
        if (phase == AgentPhase.Idle) return (phase, null);
        var app = ProviderIdentity.DisplayName(fromCursor ? DisplayProvider.Cursor : DisplayProvider.Grok);
        return (phase, detail is null ? app : $"{app} · {detail}");
    }
}
