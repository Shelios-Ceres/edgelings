using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Model;

/// Grok and Cursor under Grok's character (DisplayProviderExtensions.Character).
/// On one X Subscription (October 2026) they share one bill and one usage
/// pool for Grok models in the Grok apps, on X and in Cursor: their usage
/// APIs then report that pool to the same reset instant, and Grok's card
/// shows it once. Plans billed apart keep a quota line each.
/// Grok 卡读到的一个入口：额度窗口、读数时间、账号和告警。
public sealed record GrokSide(WindowUsage Window, DateTimeOffset? UpdatedAt, string? Account, string? Warning);

/// Grok 卡上的两个入口。Cursor 按 Grok Build 桌面版处理（用户 2026-11-26 定）。
/// 不在场的入口为 null，它在缓存里的旧读数、账号和告警都不上卡：
/// grok CLI 没检测到；桌面版没装，或装了但既没有会话也没有快照。
public sealed record GrokSides(GrokSide? Grok, GrokSide? Desktop, bool SameReset)
{
    public bool Shared => Grok is not null && Desktop is not null && SameReset;

    public static bool DesktopPresent(bool detected, bool hasSnapshot, bool signedIn) => detected && (hasSnapshot || signedIn);

    public static GrokSides Now()
    {
        var visibility = ProviderVisibilityStore.Shared;
        var grok = GrokUsageStore.Shared;
        var cursor = CursorUsageStore.Shared;
        return new GrokSides(
            visibility.IsDetected(DisplayProvider.Grok)
                ? new GrokSide(ProviderUsage.For(DisplayProvider.Grok).FiveHour, grok.Snapshot is null ? null : grok.LastUpdated,
                    grok.AccountEmail, AgentReports.Warning(DisplayProvider.Grok))
                : null,
            DesktopPresent(visibility.IsDetected(DisplayProvider.Cursor), cursor.Snapshot is not null, cursor.SignedIn)
                ? new GrokSide(ProviderUsage.For(DisplayProvider.Cursor).FiveHour, cursor.Snapshot is null ? null : cursor.LastUpdated,
                    cursor.AccountEmail, AgentReports.Warning(DisplayProvider.Cursor))
                : null,
            SharedSubscription.GrokWithCursor());
    }

    /// 同池时只有一条线，固定用 Grok 的读数：两个 store 同一轮刷新，按先后挑会让数字来回跳。
    /// 只有 Grok 的读数不是新的（失败后保留的旧数、已过重置时刻或没有读数）而桌面版的是新的时，
    /// 才改用桌面版的。
    public (WindowUsage Window, bool FromDesktop)? SharedReading(DateTimeOffset now)
    {
        if (!Shared || Grok is not { } grok || Desktop is not { } desktop) return null;
        return !Fresh(grok.Window, now) && Fresh(desktop.Window, now) ? (desktop.Window, true) : (grok.Window, false);
    }

    private static bool Fresh(WindowUsage window, DateTimeOffset now) =>
        window.Observed && !window.HasError && !window.At(now).AwaitingSync;
}

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
    /// a tie, its detail without naming the app. Only the task phases decide:
    /// quota and billing play no part.
    public static (AgentPhase Phase, string? Detail) Pick(
        (AgentPhase Phase, string? Detail) grok, (AgentPhase Phase, string? Detail) cursor)
    {
        var fromCursor = AgentPhases.Priority(cursor.Phase) < AgentPhases.Priority(grok.Phase);
        var (phase, detail) = fromCursor ? cursor : grok;
        // Grok's card names no other app (the user's call, 2026-11-26).
        return phase == AgentPhase.Idle ? (phase, null) : (phase, detail);
    }
}
