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

    /// The shared subscription's tier. Cursor reports the X Subscription's own
    /// ("PREMIUM"); Grok's badge names how it signed in ("SUPERGROK" for any
    /// account login), so it only stands in when Cursor has none.
    public static string? Plan(string? grokBadge, string? cursorBadge) => cursorBadge ?? grokBadge;

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

    /// Longest name the user can give the shared character, in characters as
    /// a reader counts them (an emoji is one).
    public const int MaxNameLength = 24;

    /// What the drawer calls the shared character: both apps by default, one
    /// of them, or the user's own name for it (back to the default if blank).
    public static string Name(SharedName choice, string? custom)
    {
        var grok = ProviderIdentity.DisplayName(DisplayProvider.Grok);
        var cursor = ProviderIdentity.DisplayName(DisplayProvider.Cursor);
        return choice switch
        {
            SharedName.Grok => grok,
            SharedName.Cursor => cursor,
            SharedName.Custom when Clean(custom) is { Length: > 0 } name => name,
            _ => $"{grok} + {cursor}",
        };
    }

    public static string Name() => Name(DrawerStore.Shared.SharedName, DrawerStore.Shared.SharedCustomName);

    /// A character's name in the drawer: the shared name for Grok's
    /// character while it stands for Cursor too.
    public static string CharacterName(DisplayProvider provider, bool merged) =>
        merged && provider == DisplayProvider.Grok ? Name() : ProviderIdentity.DisplayName(provider);

    /// Trimmed and cut to MaxNameLength without splitting a character.
    public static string Clean(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        var text = new System.Globalization.StringInfo(trimmed);
        return text.LengthInTextElements <= MaxNameLength ? trimmed : text.SubstringByTextElements(0, MaxNameLength).TrimEnd();
    }
}

/// The shared character's name (SharedSubscription.Name).
public enum SharedName
{
    Both,
    Grok,
    Cursor,
    Custom,
}
