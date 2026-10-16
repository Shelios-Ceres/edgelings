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
    public static AgentReport For(DisplayProvider provider)
    {
        var detected = ProviderVisibilityStore.Shared.IsDetected(provider);
        return new AgentReport(
            provider,
            detected,
            detected ? Plan(provider) : null,
            detected ? Account(provider) : null,
            detected ? Status(provider) : L10n.Tr(Missing(provider)),
            detected ? Warning(provider) : null,
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
        DisplayProvider.Grok => "Not detected — sign in with the grok CLI",
        DisplayProvider.Cursor => "Not detected — sign in inside Cursor",
        DisplayProvider.Kimi => "Not detected — open Kimi Code once",
        _ => "Not detected — open DeepSeek Harness once",
    };

    /// Where signing in happens. Only Claude and Codex can be started from
    /// here; the others own their login flows.
    private static string SignInHint(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => "Opens a terminal running claude /login",
        DisplayProvider.Codex => "Opens a terminal running codex login",
        DisplayProvider.Antigravity => "Sign in with the antigravity CLI or the Antigravity app",
        DisplayProvider.Grok => "Sign in with the grok CLI",
        DisplayProvider.Cursor => "Sign in inside the Cursor app",
        DisplayProvider.Kimi => "Sign in inside Kimi Code; its plan usage syncs whenever the app or the CLI signs in",
        _ => "Read from DeepSeek Harness's local sessions; no sign-in needed",
    };

    internal static string? Plan(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.Claude.Plan?.ToUpperInvariant(),
        DisplayProvider.Codex => UsageStore.Shared.Codex.Plan?.ToUpperInvariant(),
        DisplayProvider.Antigravity => AntigravityUsageStore.Shared.TierBadge,
        DisplayProvider.Grok => GrokUsageStore.Shared.AuthModeBadge,
        DisplayProvider.Cursor => CursorUsageStore.Shared.PlanBadge,
        DisplayProvider.Kimi => KimiUsageStore.Shared.PlanBadge,
        _ => null,
    };

    private static string? Account(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Codex => CodexAccountSwitcher.ActiveLabel(),
        DisplayProvider.Antigravity => AntigravityUsageStore.Shared.AccountEmail,
        DisplayProvider.Grok => GrokUsageStore.Shared.AccountEmail,
        DisplayProvider.Cursor => CursorUsageStore.Shared.AccountEmail,
        _ => null,
    };

    /// Freshness first: when the numbers were last synced, or that only
    /// local logs are read.
    private static string Status(DisplayProvider provider)
    {
        DateTimeOffset? synced = provider switch
        {
            DisplayProvider.Claude => UsageStore.Shared.ClaudeUpdatedAt,
            DisplayProvider.Codex => UsageStore.Shared.CodexUpdatedAt,
            DisplayProvider.Antigravity => AntigravityUsageStore.Shared.LastUpdated,
            DisplayProvider.Grok => GrokUsageStore.Shared.LastUpdated,
            DisplayProvider.Cursor => CursorUsageStore.Shared.LastUpdated,
            DisplayProvider.Kimi => KimiUsageStore.Shared.LastUpdated,
            _ => null,
        };
        if (provider is DisplayProvider.DeepSeek) return L10n.Tr("Local sessions only — no quota to sync");
        return synced is { } stamp
            ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - stamp, L10n.IsChinese))
            : L10n.Tr("Waiting for the first sync");
    }

    internal static string? Warning(DisplayProvider provider)
    {
        string? raw = provider switch
        {
            DisplayProvider.Claude => UsageStore.Shared.ClaudeReauthInProgress
                ? null
                : UsageStore.Shared.ClaudeReauthFailureCaption ?? WindowError(UsageStore.Shared.Claude),
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
                    || store.ClaudeReauthFailureCaption is not null
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
        switch (provider)
        {
            case DisplayProvider.Claude:
            case DisplayProvider.Codex:
                // Anthropic rate-limits the usage endpoint hard: a manual
                // refresh still asks at most once a minute.
                if (UsageStore.Shared.LastAttemptedAt is not { } last || DateTimeOffset.Now - last > TimeSpan.FromMinutes(1))
                {
                    UsageStore.Shared.Refresh();
                }
                break;
            case DisplayProvider.Antigravity:
                AntigravityUsageStore.Shared.KickRefresh();
                break;
            case DisplayProvider.Grok:
                GrokUsageStore.Shared.KickRefresh();
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
