using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Usage;

namespace AgentIsland.Model;

/// One quota window. Shown is the 0-1 share a bar fills (used or remaining,
/// per the user's mode) and Used drives the warning tint; both null when the
/// window failed to load. Resets is when the window refills, if known.
public sealed record StatusMeter(string Label, string Value, double? Shown, double? Used, string? Resets = null);

/// Local token counts; each Number is the part of its phrase to emphasise.
public sealed record StatusTokens(string Label, string Today, string TodayNumber, string Month, string MonthNumber);

/// What a character's hover card says: who, what it is doing, its quota
/// windows, its tokens, and any provider-specific note.
public sealed record StatusCard(
    DisplayProvider Provider,
    string Name,
    string Phase,
    string? Detail,
    IReadOnlyList<StatusMeter> Meters,
    StatusTokens? Tokens,
    string? Note);

public static class StatusCards
{
    public static StatusCard For(DisplayProvider provider, AgentPhase phase, string? detail) => new(
        provider,
        ProviderIdentity.DisplayName(provider),
        DrawerLooks.PhaseLabel(phase),
        detail,
        Meters(provider),
        Tokens(provider),
        Note(provider));

    /// Quota windows in the user's Used/Remaining mode: the bar fills with
    /// the shown share, the warning tint follows actual use.
    public static IReadOnlyList<StatusMeter> Meters(DisplayProvider provider)
    {
        var usage = ProviderUsage.For(provider);
        var mode = QuotaDisplayModeStore.Shared;
        StatusMeter Meter(WindowUsage window, string label)
        {
            if (window.HasError) return new StatusMeter(L10n.Tr(label), "—", null, null);
            var shown = mode.DisplayValue(window.UsedPercent);
            var value = Math.Round(shown);
            var resets = window.ResetAt is { } at && at > DateTimeOffset.Now
                ? L10n.TrFormat("Resets {0}", Formatting.LongCountdown(at - DateTimeOffset.Now, L10n.IsChinese))
                : null;
            return new StatusMeter(
                L10n.Tr(label),
                mode.ShowsRemaining ? L10n.TrFormat("{0}% left", value) : $"{value}%",
                shown / 100,
                window.UsedPercent,
                resets);
        }
        return provider switch
        {
            DisplayProvider.Claude => new[] { Meter(usage.FiveHour, "5h"), Meter(usage.Weekly, "week") },
            DisplayProvider.Codex when usage.SecondaryMissing => new[] { Meter(usage.FiveHour, "week") },
            DisplayProvider.Codex => new[] { Meter(usage.FiveHour, "5h"), Meter(usage.Weekly, "week") },
            DisplayProvider.Cursor => new[] { Meter(usage.FiveHour, "30d") },
            DisplayProvider.Kimi => KimiMeters(Meter),
            // No quota source: the card shows phases and tokens only.
            DisplayProvider.DeepSeek => Array.Empty<StatusMeter>(),
            _ => new[] { Meter(usage.FiveHour, "week") },
        };
    }

    /// Every window Kimi Code's plan reports: 5 hours, the week, the month.
    private static IReadOnlyList<StatusMeter> KimiMeters(Func<WindowUsage, string, StatusMeter> meter)
    {
        var store = KimiUsageStore.Shared;
        if (store.Snapshot is not { } snapshot)
        {
            return store.ErrorCaption is null
                ? Array.Empty<StatusMeter>()
                : new[] { meter(new WindowUsage(0, null, store.ErrorCaption), "month") };
        }
        var meters = new List<StatusMeter>();
        void Add(KimiQuotaWindow? window, string label)
        {
            if (window is { } w) meters.Add(meter(new WindowUsage(w.UsedPercent, w.ResetAt, null), label));
        }
        Add(snapshot.FiveHour, "5h");
        Add(snapshot.Weekly, "week");
        Add(snapshot.Month, "month");
        return meters;
    }

    /// Today's and this month's tokens from the local logs, counted the way
    /// the Token counting setting says. Antigravity keeps no local log.
    public static StatusTokens? Tokens(DisplayProvider provider)
    {
        if (provider == DisplayProvider.Antigravity) return null;
        var summary = CostStore.Shared.Summary(provider);
        var billable = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
        var today = Formatting.CompactTokens(billable ? summary.TodayBillableTokens : summary.TodayTokens);
        var month = Formatting.CompactTokens(billable ? summary.MonthBillableTokens : summary.MonthTokens);
        return new StatusTokens(
            L10n.Tr("Tokens"),
            $"{L10n.Tr("today")} {today}", today,
            L10n.TrFormat("{0} this month", month), month);
    }

    /// Codex's banked reset cards and the account the auto-switcher last
    /// rotated to; Kimi Code's part of the monthly total it shares with the
    /// Kimi app, and its booster balance.
    public static string? Note(DisplayProvider provider)
    {
        if (provider == DisplayProvider.Kimi) return KimiNote();
        if (provider != DisplayProvider.Codex) return null;
        var parts = new List<string>();
        if (ProviderUsage.For(provider).ResetCards is > 0 and var cards) parts.Add(L10n.TrFormat("{0} reset cards", cards));
        if (UsageStore.Shared.CodexAutoSwitched is { } account) parts.Add(L10n.TrFormat("Auto-switched to {0}", account));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? KimiNote()
    {
        if (KimiUsageStore.Shared.Snapshot is not { } snapshot) return null;
        var parts = new List<string>();
        if (snapshot.MonthCode is { } code && snapshot.Month is { } total && total.UsedPercent > 0)
        {
            var codeShare = Math.Min(code.UsedPercent, total.UsedPercent);
            parts.Add(L10n.TrFormat("Month: Kimi Code {0}% · Kimi app {1}%",
                Formatting.PercentInt(codeShare), Formatting.PercentInt(total.UsedPercent - codeShare)));
        }
        if (snapshot.BoosterCents is > 0 and var cents)
        {
            parts.Add(L10n.TrFormat("Booster balance {0}", Money(cents, snapshot.BoosterCurrency)));
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Money(long cents, string? currency) => (currency ?? "USD").ToUpperInvariant() switch
    {
        "CNY" or "RMB" => "¥" + (cents / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        _ => Formatting.Money(cents / 100.0),
    };
}
