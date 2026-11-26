using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Usage;

namespace AgentIsland.Model;

public enum SignInOffer
{
    None,
    Available,
    Waiting,
}

/// Everything Settings says about one agent: whether this machine has it,
/// its plan and account, how fresh its numbers are, what is wrong if
/// anything, its quota windows and tokens, and whether the app can start a
/// sign-in for it.
public sealed record AgentReport(
    DisplayProvider Provider,
    bool Detected,
    string? Plan,
    string? Account,
    string Status,
    string? Warning,
    IReadOnlyList<StatusMeter> Meters,
    StatusTokens? Tokens,
    string? Note,
    SignInOffer SignIn,
    string SignInHint);

public static class AgentReports
{
    /// Grok's report covers Cursor too, which works under its character.
    public static AgentReport For(DisplayProvider provider)
    {
        var detected = ProviderVisibilityStore.Shared.IsCharacterDetected(provider);
        var warnings = detected ? Warnings(provider) : Array.Empty<string>();
        return new AgentReport(
            provider,
            detected,
            detected ? Plan(provider) : null,
            detected ? Account(provider) : null,
            detected ? Status(provider) : L10n.Tr(Missing(provider)),
            warnings.Count > 0 ? string.Join(" · ", warnings) : null,
            detected ? StatusCards.Meters(provider) : Array.Empty<StatusMeter>(),
            detected ? StatusCards.Tokens(provider) : null,
            detected ? StatusCards.Note(provider) : null,
            SignIn(provider),
            L10n.Tr(SignInHint(provider)));
    }

    private static string Missing(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => "Not detected — install Claude Code and sign in",
        DisplayProvider.Codex => "Not detected — install the Codex CLI and sign in",
        DisplayProvider.Antigravity => "Not detected — sign in with the antigravity CLI",
        DisplayProvider.Grok => "Not detected — sign in with the grok CLI or inside Grok Build desktop (Cursor)",
        DisplayProvider.Cursor => "Not detected — sign in inside Grok Build desktop (Cursor)",
        DisplayProvider.Kimi => "Not detected — open Kimi once",
        _ => "Not detected — open DeepSeek Harness once",
    };

    /// Where signing in happens. Only Claude and Codex can be started from
    /// here; the others own their login flows.
    private static string SignInHint(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => "Opens a terminal running claude /login",
        DisplayProvider.Codex => "Opens a terminal running codex login",
        DisplayProvider.Antigravity => "Sign in with the antigravity CLI or the Antigravity app",
        DisplayProvider.Grok => "Sign in with the grok CLI, or inside Grok Build desktop (Cursor)",
        DisplayProvider.Cursor => "Sign in inside Grok Build desktop (Cursor)",
        DisplayProvider.Kimi => "Sign in inside Kimi Code; its plan usage syncs whenever the app or the CLI signs in",
        _ => "Read from DeepSeek Harness's local sessions; no sign-in needed",
    };

    internal static string? Plan(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.Claude.Plan?.ToUpperInvariant(),
        DisplayProvider.Codex => UsageStore.Shared.Codex.Plan?.ToUpperInvariant(),
        DisplayProvider.Antigravity => AntigravityUsageStore.Shared.TierBadge,
        DisplayProvider.Grok => GrokPlan(ProviderVisibilityStore.Shared.IsDetected(DisplayProvider.Grok),
            GrokUsageStore.Shared.AuthModeBadge, CursorUsageStore.Shared.PlanBadge, SharedSubscription.GrokWithCursor()),
        DisplayProvider.Cursor => CursorUsageStore.Shared.PlanBadge,
        DisplayProvider.Kimi => KimiUsageStore.Shared.PlanBadge,
        _ => null,
    };

    private static string? Account(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Codex => CodexAccountSwitcher.ActiveLabel(),
        DisplayProvider.Antigravity => AntigravityUsageStore.Shared.AccountEmail,
        DisplayProvider.Grok => GrokAccount(GrokSides.Now()),
        DisplayProvider.Cursor => CursorUsageStore.Shared.AccountEmail,
        _ => null,
    };

    /// Cursor 按 Grok Build 桌面版处理（用户 2026-11-26 定）。同池就是同一个账号，
    /// 分开计费时两个登录可能不是同一个人，所以两个都列出来。
    /// 和 GrokSides 一样：grok CLI 没检测到时，不用它缓存里的旧标签，也不算同池。
    internal static string? GrokPlan(bool grokDetected, string? grokBadge, string? desktopBadge, bool sameReset) =>
        SharedSubscription.Plan(grokDetected ? grokBadge : null, desktopBadge, grokDetected && sameReset);

    internal static string? GrokAccount(GrokSides sides) => GrokAccount(sides.Grok?.Account, sides.Desktop?.Account, sides.Shared);

    internal static string? GrokAccount(string? grok, string? desktop, bool shared)
    {
        if (shared) return grok ?? desktop;
        var accounts = new[] { grok, desktop }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return accounts.Count > 0 ? string.Join(" · ", accounts) : null;
    }

    /// Freshness first: when the numbers were last synced (Grok's, or
    /// Cursor's for a character with only Cursor), or that only local logs
    /// are read.
    private static string Status(DisplayProvider provider)
    {
        DateTimeOffset? synced = provider switch
        {
            DisplayProvider.Claude => UsageStore.Shared.ClaudeUpdatedAt,
            DisplayProvider.Codex => UsageStore.Shared.CodexUpdatedAt,
            DisplayProvider.Antigravity => AntigravityUsageStore.Shared.LastUpdated,
            DisplayProvider.Grok => (ProviderVisibilityStore.Shared.IsDetected(DisplayProvider.Grok) ? GrokUsageStore.Shared.LastUpdated : null)
                ?? CursorUsageStore.Shared.LastUpdated,
            DisplayProvider.Cursor => CursorUsageStore.Shared.LastUpdated,
            DisplayProvider.Kimi => KimiUsageStore.Shared.LastUpdated,
            _ => null,
        };
        if (provider is DisplayProvider.DeepSeek) return L10n.Tr("Local sessions only — no quota to sync");
        return synced is { } stamp
            ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - stamp, L10n.IsChinese))
            : L10n.Tr("Waiting for the first sync");
    }

    internal static IReadOnlyList<string> Warnings(DisplayProvider provider) => provider == DisplayProvider.Grok
        ? GrokWarnings(GrokSides.Now())
        : Warning(provider) is { } own ? new[] { own } : Array.Empty<string>();

    internal static IReadOnlyList<string> GrokWarnings(GrokSides sides) =>
        GrokWarnings(sides.Grok?.Warning, sides.Desktop?.Warning, sides.Grok is not null);

    internal static IReadOnlyList<string> GrokWarnings(string? grok, string? desktop, bool grokDetected)
    {
        var warnings = new List<string>();
        if (grok is not null) warnings.Add(grok);
        if (desktop is not null && !warnings.Contains(desktop)) warnings.Add(DesktopNamed(desktop, grokDetected));
        return warnings;
    }

    /// 和 grok CLI 的内容并列时，“http 500”这类原始文字分不清是哪个入口的，
    /// 所以冠上桌面版的名字；文字里已经写了这个名字就不再重复，只有桌面版时也不用冠。
    /// 两种语言的名字都比对，因为文字可能是在另一种语言下生成的。
    internal static string DesktopNamed(string text, bool grokDetected) =>
        grokDetected && !text.Contains("Grok Build desktop", StringComparison.Ordinal)
            && !text.Contains("Grok Build 桌面版", StringComparison.Ordinal)
            ? L10n.TrFormat("Grok Build desktop: {0}", text)
            : text;

    internal static string? Warning(DisplayProvider provider)
    {
        string? raw = provider switch
        {
            DisplayProvider.Claude => UsageStore.Shared.ClaudeReauthInProgress
                ? null
                : WindowError(UsageStore.Shared.Claude),
            DisplayProvider.Codex => WindowError(UsageStore.Shared.Codex),
            DisplayProvider.Antigravity => AntigravityUsageStore.Shared.StatusCaption,
            DisplayProvider.Grok => GrokUsageStore.Shared.ErrorCaption,
            DisplayProvider.Cursor => CursorUsageStore.Shared.ErrorCaption,
            DisplayProvider.Kimi => KimiUsageStore.Shared.ErrorCaption,
            _ => null,
        };
        // Some stores localize their caption when they set it; the pixel
        // panels read in English, so it is relocalized as it is shown.
        return raw is null ? null : L10n.Relocalize(ErrorDisplay.Localize(raw));
    }

    /// A failed refresh still needs an explanation when old numbers survive.
    internal static string? WindowError(AppUsage usage)
    {
        foreach (var window in new[] { usage.FiveHour, usage.Weekly })
        {
            if (window.Error is { Length: > 0 } error && error != "no data") return error;
        }
        return null;
    }

    private static SignInOffer SignIn(DisplayProvider provider)
    {
        var store = UsageStore.Shared;
        switch (provider)
        {
            case DisplayProvider.Claude:
                if (store.ClaudeReauthInProgress) return SignInOffer.Waiting;
                var claude = store.Claude;
                return ClaudeCredentials.IsAuthRecoverableError(claude.FiveHour.Error)
                    || ClaudeCredentials.IsAuthRecoverableError(claude.Weekly.Error)
                    || !ProviderVisibilityStore.Shared.ClaudeDetected
                    ? SignInOffer.Available
                    : SignInOffer.None;
            case DisplayProvider.Codex:
                if (store.CodexReauthInProgress) return SignInOffer.Waiting;
                var codex = store.Codex;
                return MentionsAuthFailure(codex.FiveHour.Error) || MentionsAuthFailure(codex.Weekly.Error)
                    || !ProviderVisibilityStore.Shared.CodexDetected
                    ? SignInOffer.Available
                    : SignInOffer.None;
            default:
                return SignInOffer.None;
        }
    }

    private static bool MentionsAuthFailure(string? message) =>
        message is not null
        && (message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("403", StringComparison.Ordinal));

    /// Starts the provider's own sign-in; false when its CLI is missing.
    public static bool StartSignIn(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.SignInClaudeInTerminal(),
        DisplayProvider.Codex => UsageStore.Shared.ReauthenticateCodex(),
        _ => false,
    };

    /// A fetch now instead of at the next poll, for the provider's own store.
    public static void RefreshNow(DisplayProvider provider)
    {
        if (!ProviderVisibilityStore.Shared.IsShown(provider)) return;
        switch (provider)
        {
            case DisplayProvider.Claude:
                // Anthropic rate-limits the usage endpoint hard: a manual
                // refresh still asks at most once a minute.
                if (UsageStore.Shared.LastAttemptedAt is not { } lastClaude || DateTimeOffset.Now - lastClaude > TimeSpan.FromMinutes(1))
                {
                    UsageStore.Shared.Refresh(fetchClaude: true, fetchCodex: false);
                }
                break;
            case DisplayProvider.Codex:
                if (UsageStore.Shared.CodexAttemptedAt is not { } lastCodex || DateTimeOffset.Now - lastCodex > TimeSpan.FromMinutes(1))
                {
                    UsageStore.Shared.Refresh(fetchClaude: false, fetchCodex: true);
                }
                break;
            case DisplayProvider.Antigravity:
                AntigravityUsageStore.Shared.KickRefresh(manual: true);
                break;
            case DisplayProvider.Grok:
                GrokUsageStore.Shared.KickRefresh();
                CursorUsageStore.Shared.KickRefresh();
                break;
            case DisplayProvider.Cursor:
                CursorUsageStore.Shared.KickRefresh();
                break;
            case DisplayProvider.Kimi:
                KimiUsageStore.Shared.KickRefresh();
                break;
        }
        Cost.CostStore.Shared.Refresh();
    }
}
