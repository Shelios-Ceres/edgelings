using System.Globalization;
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

/// How many of the agent's threads are mid-turn; Number is the part of Text
/// to emphasise.
public sealed record StatusTasks(string Label, string Text, string Number);

/// One phrase of a card's note and the numbers in it to emphasise.
public sealed record StatusPhrase(string Text, IReadOnlyList<string> Numbers);

/// What a character's hover card says: who, what it is doing, how many of
/// its tasks are running, its quota windows, its tokens, and any
/// provider-specific note.
public sealed record StatusCard(
    DisplayProvider Provider,
    string Name,
    string Phase,
    string? Detail,
    StatusTasks? Tasks,
    IReadOnlyList<StatusMeter> Meters,
    StatusTokens? Tokens,
    IReadOnlyList<StatusPhrase> Note);

public static class StatusCards
{
    public static StatusCard For(DisplayProvider provider, AgentPhase phase, string? detail, int running) => new(
        provider,
        ProviderIdentity.DisplayName(provider),
        DrawerLooks.PhaseLabel(phase),
        detail,
        Tasks(running),
        Meters(provider),
        Tokens(provider),
        NoteParts(provider));

    public static StatusTasks? Tasks(int running)
    {
        if (running <= 0) return null;
        var count = running.ToString(CultureInfo.InvariantCulture);
        return new StatusTasks(L10n.Tr("Tasks"), L10n.TrFormat("{0} running", count), count);
    }

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
    public static IReadOnlyList<StatusPhrase> NoteParts(DisplayProvider provider)
    {
        if (provider == DisplayProvider.Kimi) return KimiNote();
        if (provider != DisplayProvider.Codex) return Array.Empty<StatusPhrase>();
        var parts = new List<StatusPhrase>();
        if (ProviderUsage.For(provider).ResetCards is > 0 and var cards)
        {
            var count = cards.ToString(CultureInfo.InvariantCulture);
            parts.Add(Phrase(L10n.TrFormat("{0} reset cards", count), count));
        }
        if (UsageStore.Shared.CodexAutoSwitched is { } account) parts.Add(Phrase(L10n.TrFormat("Auto-switched to {0}", account)));
        return parts;
    }

    /// The note on one line, where it shows as plain text.
    public static string? Note(DisplayProvider provider) =>
        NoteParts(provider) is { Count: > 0 } parts ? string.Join(" · ", parts.Select(p => p.Text)) : null;

    private static IReadOnlyList<StatusPhrase> KimiNote()
    {
        if (KimiUsageStore.Shared.Snapshot is not { } snapshot) return Array.Empty<StatusPhrase>();
        var parts = new List<StatusPhrase>();
        if (snapshot.MonthCode is { } code && snapshot.Month is { } total && total.UsedPercent > 0)
        {
            var codeShare = Math.Min(code.UsedPercent, total.UsedPercent);
            var codePercent = Formatting.PercentInt(codeShare);
            var appPercent = Formatting.PercentInt(total.UsedPercent - codeShare);
            parts.Add(Phrase(L10n.TrFormat("Month: Kimi Code {0}% · Kimi app {1}%", codePercent, appPercent),
                $"{codePercent}%", $"{appPercent}%"));
        }
        if (snapshot.BoosterCents is > 0 and var cents)
        {
            var balance = Money(cents, snapshot.BoosterCurrency);
            parts.Add(Phrase(L10n.TrFormat("Booster balance {0}", balance), balance));
        }
        return parts;
    }

    private static StatusPhrase Phrase(string text, params string[] numbers) => new(text, numbers);

    private static string Money(long cents, string? currency) => (currency ?? "USD").ToUpperInvariant() switch
    {
        "CNY" or "RMB" => "¥" + (cents / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        _ => Formatting.Money(cents / 100.0),
    };
}
