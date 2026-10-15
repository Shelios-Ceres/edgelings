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
/// provider-specific note. Plan is the subscription tier, as the Settings
/// badge shows it ("MAX"), shown beside the name when the provider reports one.
public sealed record StatusCard(
    DisplayProvider Provider,
    string Name,
    string Phase,
    string? Detail,
    StatusTasks? Tasks,
    IReadOnlyList<StatusMeter> Meters,
    StatusTokens? Tokens,
    IReadOnlyList<StatusPhrase> Note)
{
    public string? Plan { get; init; }
}

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
        CardNotes(provider))
    {
        Plan = AgentReports.Plan(provider),
    };

    private static IReadOnlyList<StatusPhrase> CardNotes(DisplayProvider provider)
    {
        var parts = NoteParts(provider).ToList();
        if (AgentReports.Warning(provider) is { } warning) parts.Insert(0, Phrase(warning));
        return parts;
    }

    /// Grok and Cursor on one X Subscription (SharedSubscription): one card
    /// for both apps, with their one pool as Grok reports it, both apps'
    /// tokens added up, and either app's warning.
    public static StatusCard ForGrokWithCursor(AgentPhase phase, string? detail, int running)
    {
        var notes = NoteParts(DisplayProvider.Grok).ToList();
        notes.InsertRange(0, new[] { AgentReports.Warning(DisplayProvider.Grok), AgentReports.Warning(DisplayProvider.Cursor) }
            .OfType<string>()
            .Distinct()
            .Select(warning => Phrase(warning)));
        var totals = TokenTotals.Sum(CostStore.Shared.Totals(DisplayProvider.Grok), CostStore.Shared.Totals(DisplayProvider.Cursor));
        return new StatusCard(
            DisplayProvider.Grok,
            SharedSubscription.Name(),
            DrawerLooks.PhaseLabel(phase),
            detail,
            Tasks(running),
            Meters(DisplayProvider.Grok),
            TokensFrom(totals, TokenCountModeStore.Shared.Mode, DateTimeOffset.Now),
            notes)
        {
            Plan = SharedSubscription.Plan(GrokUsageStore.Shared.AuthModeBadge, CursorUsageStore.Shared.PlanBadge),
        };
    }

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
        var remaining = QuotaDisplayModeStore.Shared.ShowsRemaining;
        var now = DateTimeOffset.Now;
        StatusMeter Meter(WindowUsage window, string label) => MeterFor(window, L10n.Tr(label), remaining, now);
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

    /// One window as the card shows it. Numbers kept through a failed refresh
    /// or a closed app still show until their observed window ends. A window
    /// that never loaded or has reset awaits a fresh reading.
    internal static StatusMeter MeterFor(WindowUsage window, string label, bool showsRemaining, DateTimeOffset now)
    {
        var current = window.At(now);
        if (current.AwaitingSync) return new StatusMeter(label, "—", null, null, L10n.Tr("Awaiting sync"));
        if (current.HasError && !current.Kept) return new StatusMeter(label, "—", null, null);
        var shown = QuotaDisplayModeStore.DisplayValue(current.UsedPercent, showsRemaining);
        var value = Math.Round(shown);
        var resets = current.ResetAt is { } at && at > now
            ? L10n.TrFormat("Resets {0}", Formatting.LongCountdown(at - now, L10n.IsChinese))
            : null;
        return new StatusMeter(
            label,
            showsRemaining ? L10n.TrFormat("{0}% left", value) : $"{value}%",
            shown / 100,
            current.UsedPercent,
            resets);
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
        // A calendar month has no fixed length.
        void Add(KimiQuotaWindow? window, string label, double? period)
        {
            if (window is { } w) meters.Add(meter(new WindowUsage(w.UsedPercent, w.ResetAt, store.ErrorCaption, period)
            {
                Kept = store.ErrorCaption is not null,
                Observed = true,
            }, label));
        }
        Add(snapshot.FiveHour, "5h", 5 * 3600);
        Add(snapshot.Weekly, "week", 7 * 86400);
        Add(snapshot.Month, "month", null);
        return meters;
    }

    /// Today's and this month's tokens from the local logs, counted the way
    /// the Token counting setting says. Antigravity keeps no local log.
    public static StatusTokens? Tokens(DisplayProvider provider) => provider == DisplayProvider.Antigravity
        ? null
        : TokensFrom(CostStore.Shared.Totals(provider), TokenCountModeStore.Shared.Mode, DateTimeOffset.Now);

    /// Counts from an earlier day or month say nothing about this one, so a
    /// figure kept across midnight or a restart shows a dash until the next
    /// scan lands.
    internal static StatusTokens TokensFrom(TokenTotals? totals, TokenCountMode mode, DateTimeOffset now)
    {
        if (totals is null) return new StatusTokens(L10n.Tr("Tokens"), L10n.Tr("Not scanned yet"), "", "—", "");
        var billable = mode == TokenCountMode.Billable;
        var counted = totals.AsOf.ToLocalTime();
        var local = now.ToLocalTime();
        var today = counted.Date == local.Date
            ? Formatting.CompactTokens(billable ? totals.TodayBillable : totals.Today)
            : "—";
        var month = counted.Year == local.Year && counted.Month == local.Month
            ? Formatting.CompactTokens(billable ? totals.MonthBillable : totals.Month)
            : "—";
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
        var parts = new List<StatusPhrase>();
        if (QuotaAge(provider, DateTimeOffset.Now) is { } age) parts.Add(age);
        if (provider == DisplayProvider.Kimi) parts.AddRange(KimiNote());
        if (provider != DisplayProvider.Codex) return parts;
        if (ProviderUsage.For(provider).ResetCards is > 0 and var cards)
        {
            var count = cards.ToString(CultureInfo.InvariantCulture);
            parts.Add(Phrase(L10n.TrFormat(cards == 1 ? "{0} reset card" : "{0} reset cards", count), count));
        }
        if (UsageStore.Shared.CodexAutoSwitched is { } account) parts.Add(Phrase(L10n.TrFormat("Auto-switched to {0}", account)));
        return parts;
    }

    /// Explain the wait at a reset boundary before showing an age for old
    /// numbers; the prior window cannot describe the new balance.
    private static StatusPhrase? QuotaAge(DisplayProvider provider, DateTimeOffset now)
    {
        var usage = ProviderUsage.For(provider);
        if (usage.FiveHour.At(now).AwaitingSync || usage.Weekly.At(now).AwaitingSync
            || (provider == DisplayProvider.Kimi && KimiUsageStore.Shared.Snapshot?.Month?.ResetAt is { } monthReset && monthReset <= now))
        {
            return Phrase(L10n.Tr("Awaiting sync"));
        }
        var interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds);
        if (QuotaUpdatedAt(provider) is not { } at
            || !QuotaIsStale(usage.FiveHour.Kept || usage.Weekly.Kept, at, now, interval))
        {
            return null;
        }
        return Phrase(L10n.TrFormat("Quota updated {0}", Formatting.RelativeAgo(now - at, L10n.IsChinese)));
    }

    /// When a provider's quota numbers last came from a successful refresh.
    private static DateTimeOffset? QuotaUpdatedAt(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.ClaudeUpdatedAt,
        DisplayProvider.Codex => UsageStore.Shared.CodexUpdatedAt,
        DisplayProvider.Antigravity => AntigravityUsageStore.Shared is { Snapshot: not null } antigravity ? antigravity.LastUpdated : null,
        DisplayProvider.Grok => GrokUsageStore.Shared is { Snapshot: not null } grok ? grok.LastUpdated : null,
        DisplayProvider.Cursor => CursorUsageStore.Shared is { Snapshot: not null } cursor ? cursor.LastUpdated : null,
        DisplayProvider.Kimi => KimiUsageStore.Shared is { Snapshot: not null } kimi ? kimi.LastUpdated : null,
        _ => null,
    };

    /// Not live: the last refresh failed, or the numbers have not moved for
    /// two refresh rounds (Antigravity's quota only updates while its app runs).
    internal static bool QuotaIsStale(bool kept, DateTimeOffset updatedAt, DateTimeOffset now, TimeSpan interval) =>
        kept || now - updatedAt > interval * 2 + TimeSpan.FromMinutes(1);

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
