using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Model;

/// Grok and Cursor under Grok's character (DisplayProviderExtensions.Character).
/// On one X Subscription (October 2026) they share one bill and one usage
/// pool for Grok models in the Grok apps, on X and in Cursor: their usage
/// APIs then report that pool to the same reset instant, and Grok's card
/// shows it once. Plans billed apart keep a quota line each.
public static class SharedSubscription
{
    public static bool GrokWithCursor() =>
        GrokWithCursor(GrokUsageStore.Shared.Snapshot?.WeeklyPeriodEnd, CursorUsageStore.Shared.Snapshot?.PeriodEnd);

    /// The same reset, give or take API rounding: two independent cycles
    /// ending within a minute of each other is no coincidence worth fearing.
    public static bool GrokWithCursor(DateTimeOffset? grokReset, DateTimeOffset? cursorReset) =>
        grokReset is { } grok && cursorReset is { } cursor && (grok - cursor).Duration() < TimeSpan.FromMinutes(1);

    /// The character's tier. On a shared subscription Cursor reports its own
    /// ("PREMIUM"); Grok's badge names how it signed in ("SUPERGROK" for any
    /// account login), so it only stands in when Cursor has none.
    public static string? Plan(string? grokBadge, string? cursorBadge, bool shared) =>
        shared ? cursorBadge ?? grokBadge : grokBadge ?? cursorBadge;

    /// The character plays the more urgent of the two apps' phases, Grok's on
    /// a tie; work done in Cursor says so in its detail. Billed apart, one
    /// app's spent quota or lapsed sign-in is its own: it never puts the
    /// character to sleep over the other app's work or news. shared is asked
    /// only then.
    public static (AgentPhase Phase, string? Detail) Pick(
        (AgentPhase Phase, string? Detail) grok, (AgentPhase Phase, string? Detail) cursor, Func<bool> shared)
    {
        static bool Standing(AgentPhase phase) => phase is AgentPhase.RateLimited or AgentPhase.AuthRequired;
        static bool Busy(AgentPhase phase) => AgentPhases.IsWork(phase) || phase == AgentPhase.Finished;
        var fromCursor = Standing(grok.Phase) != Standing(cursor.Phase)
            && Busy(Standing(grok.Phase) ? cursor.Phase : grok.Phase) && !shared()
            ? Standing(grok.Phase)
            : AgentPhases.Priority(cursor.Phase) < AgentPhases.Priority(grok.Phase);
        var (phase, detail) = fromCursor ? cursor : grok;
        if (phase == AgentPhase.Idle) return (phase, null);
        if (!fromCursor) return (phase, detail);
        var app = ProviderIdentity.DisplayName(DisplayProvider.Cursor);
        return (phase, detail is null ? app : $"{app} · {detail}");
    }
}
