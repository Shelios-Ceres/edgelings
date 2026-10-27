using System.Globalization;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Usage;

namespace AgentIsland.Model;

/// One quota window. Shown is the 0-1 share a bar fills (used or remaining,
/// per the user's mode) and Used drives the warning tint; both null when the
/// window failed to load. Resets is when the window refills, if known.
public sealed record StatusMeter(string Label, string Value, double? Shown, double? Used, string? Resets = null)
{
    /// The label on the hover card, whose label column is as wide as "Tokens"
    /// and never trims: Settings keeps Label's full name.
    public string CardLabel { get; init; } = Label;
}

/// Local token counts; each Number is the part of its phrase to emphasise.
public sealed record StatusTokens(string Label, string Today, string TodayNumber, string Month, string MonthNumber);

/// How many of the agent's threads are mid-turn; Number is the part of Text
/// to emphasise.
public sealed record StatusTasks(string Label, string Text, string Number);

/// One session that waits on the user, as a card's list shows it: its title
/// and the word for what it waits for.
public sealed record StatusWaiting(string Title, string Word, SessionEventKind Kind);

/// The sessions that wait on the user: at most three rows (Classic's: four),
/// then how many more.
public sealed record StatusWaitingList(IReadOnlyList<StatusWaiting> Rows, int More)
{
    public static readonly StatusWaitingList Empty = new(Array.Empty<StatusWaiting>(), 0);
    public const int MaxRows = 3;
    public const int ClassicMaxRows = 4;
    /// How long a finished turn or an error stays on Classic's card.
    public static readonly TimeSpan ClassicFor = TimeSpan.FromMinutes(15);

    /// Classic's list: approvals, questions, errors (a scheduled task that
    /// did not run among them) and finished turns, each of the last two for
    /// fifteen minutes.
    public static StatusWaitingList ForClassic(IEnumerable<SessionEvent> waiting, DateTimeOffset now)
    {
        var ordered = waiting
            .Where(e => e.Kind is SessionEventKind.Approval or SessionEventKind.Question || now - e.At < ClassicFor)
            .OrderBy(e => e.Kind switch
            {
                SessionEventKind.Approval => 0,
                SessionEventKind.Question => 1,
                SessionEventKind.Error or SessionEventKind.TaskSkipped => 2,
                _ => 3,
            })
            .ThenByDescending(e => e.At)
            .ToList();
        var rows = ordered.Take(ClassicMaxRows)
            .Select(e => new StatusWaiting(e.Title, BotLines.Word(e.Kind, classic: true), e.Kind))
            .ToList();
        return new StatusWaitingList(rows, ordered.Count - rows.Count);
    }

    /// Approvals first, then questions, errors and finished turns; the newest
    /// first within each. A scheduled task that did not run is a moment, not
    /// a session, so it has no row. A title the pixel face cannot set (it has
    /// no hanzi, kana or hangul) gives way to "A session".
    public static StatusWaitingList Of(IEnumerable<SessionEvent> waiting)
    {
        var ordered = waiting
            .Where(e => e.Kind != SessionEventKind.TaskSkipped)
            .OrderBy(e => e.Kind switch
            {
                SessionEventKind.Approval => 0,
                SessionEventKind.Question => 1,
                SessionEventKind.Error => 2,
                _ => 3,
            })
            .ThenByDescending(e => e.At)
            .ToList();
        var rows = ordered.Take(MaxRows)
            .Select(e => new StatusWaiting(HasCjk(e.Title) ? "A session" : e.Title, BotLines.Word(e.Kind), e.Kind))
            .ToList();
        return new StatusWaitingList(rows, ordered.Count - rows.Count);
    }

    /// Han, CJK symbols and punctuation, kana, Hangul or full-width forms.
    public static bool HasCjk(string text) => text.Any(c =>
        c is >= '\u3000' and <= '\u303F' or >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF');
}

/// One phrase of a card's note and the numbers in it to emphasise. Lead is
/// a label its parts stand under, each on a line of its own (Kimi Code's
/// month: "Month:" over each app's share).
public sealed record StatusPhrase(string Text, IReadOnlyList<string> Numbers)
{
    public string? Lead { get; init; }

    /// The phrase on one line, its lead first.
    public string Line => Lead is not { } lead ? Text : lead.EndsWith('：') ? lead + Text : $"{lead} {Text}";
}

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

    /// Sessions of this agent that wait on the user.
    public StatusWaitingList Waiting { get; init; } = StatusWaitingList.Empty;
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

    /// The note under a card, the agent's warning first. Grok's card carries
    /// either app's warning (Cursor works under it).
    private static IReadOnlyList<StatusPhrase> CardNotes(DisplayProvider provider)
    {
        var parts = NoteParts(provider).ToList();
        parts.InsertRange(0, AgentReports.Warnings(provider).Select(warning => Phrase(warning)));
        return parts;
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
            DisplayProvider.Claude => new[] { Meter(usage.FiveHour, "5h"), Meter(usage.Weekly, "Week") },
            DisplayProvider.Codex when usage.SecondaryMissing => new[] { Meter(usage.FiveHour, "Week") },
            DisplayProvider.Codex => new[] { Meter(usage.FiveHour, "5h"), Meter(usage.Weekly, "Week") },
            DisplayProvider.Antigravity => AntigravityMeters(usage, AntigravityUsageStore.Shared.Snapshot, AntigravityUsageStore.Shared.StatusCaption, Meter),
            DisplayProvider.Cursor => new[] { Meter(usage.FiveHour, "30d") },
            DisplayProvider.Grok => GrokMeters(Meter),
            DisplayProvider.Kimi => KimiMeters(Meter),
            // No quota source: the card shows phases and tokens only.
            DisplayProvider.DeepSeek => Array.Empty<StatusMeter>(),
            _ => new[] { Meter(usage.FiveHour, "Week") },
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

    /// Grok's weekly window, which is Cursor's too on one X Subscription;
    /// billed apart, Cursor's own cycle follows under its name. Each app's
    /// line shows only once that app has been here.
    private static IReadOnlyList<StatusMeter> GrokMeters(Func<WindowUsage, string, StatusMeter> meter)
    {
        var visibility = ProviderVisibilityStore.Shared;
        var meters = new List<StatusMeter>();
        if (visibility.IsDetected(DisplayProvider.Grok)) meters.Add(meter(ProviderUsage.For(DisplayProvider.Grok).FiveHour, "Week"));
        if (CursorHasOwnLine())
        {
            meters.Add(meter(ProviderUsage.For(DisplayProvider.Cursor).FiveHour, ProviderIdentity.DisplayName(DisplayProvider.Cursor)));
        }
        return meters;
    }

    private static bool CursorHasOwnLine() =>
        ProviderVisibilityStore.Shared.IsDetected(DisplayProvider.Cursor) && !SharedSubscription.GrokWithCursor();

    /// Gemini's 5-hour and longer windows, then one meter per further Gemini
    /// group under Google's own name for it (Gemini 4 Argon). Claude and GPT's
    /// pool stays out. A single-window snapshot keeps today's one meter. Each
    /// meter is labelled by its window: 5 hours, a day, else the week.
    internal static IReadOnlyList<StatusMeter> AntigravityMeters(
        AppUsage usage, AntigravityQuotaSnapshot? snapshot, string? caption, Func<WindowUsage, string, StatusMeter> meter)
    {
        static string Label(double? period) => period switch
        {
            5 * 3600 => "5h",
            24 * 3600 => "24h",
            _ => "Week",
        };
        var meters = new List<StatusMeter>();
        if (snapshot is null || usage.SecondaryMissing)
        {
            // A 5-hour bucket is known by its id too, whatever its window says.
            meters.Add(meter(usage.FiveHour, snapshot?.GeminiPools.ShortWindow is not null ? "5h" : Label(usage.FiveHour.PeriodSeconds)));
        }
        else
        {
            meters.Add(meter(usage.FiveHour, "5h"));
            meters.Add(meter(usage.Weekly, Label(usage.Weekly.PeriodSeconds)));
        }
        foreach (var extra in snapshot?.GeminiPools.Extras ?? Array.Empty<AntigravityQuotaBucket>())
        {
            meters.Add(meter(ProviderUsage.AntigravityWindow(extra, caption), extra.GroupLabel) with { CardLabel = CardLabelOf(extra.GroupLabel) });
        }
        return meters;
    }

    /// A group's name for the hover card: its last word ("Gemini 4 Argon" is
    /// "Argon"), cut to the width of "Tokens" without an ellipsis.
    internal static string CardLabelOf(string group)
    {
        var word = group.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? group;
        return word.Length > 6 ? word[..6] : word;
    }

    /// Every window Kimi Code's plan reports: 5 hours, the week, the month.
    private static IReadOnlyList<StatusMeter> KimiMeters(Func<WindowUsage, string, StatusMeter> meter)
    {
        var store = KimiUsageStore.Shared;
        if (store.Snapshot is not { } snapshot)
        {
            return store.ErrorCaption is null
                ? Array.Empty<StatusMeter>()
                : new[] { meter(new WindowUsage(0, null, store.ErrorCaption), "Month") };
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
        Add(snapshot.Weekly, "Week", 7 * 86400);
        Add(snapshot.Month, "Month", null);
        return meters;
    }

    /// Today's and this month's tokens from the local logs, counted the way
    /// the Token counting setting says; Grok's include Cursor's.
    public static StatusTokens? Tokens(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Grok => TokensFrom(TokenTotals.Sum(CostStore.Shared.Totals(DisplayProvider.Grok), CostStore.Shared.Totals(DisplayProvider.Cursor)),
            TokenCountModeStore.Shared.Mode, DateTimeOffset.Now),
        _ => TokensFrom(CostStore.Shared.Totals(provider), TokenCountModeStore.Shared.Mode, DateTimeOffset.Now),
    };

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
        var now = DateTimeOffset.Now;
        var monthEnded = provider == DisplayProvider.Kimi && KimiUsageStore.Shared.Snapshot?.Month?.ResetAt is { } monthReset && monthReset <= now;
        parts.AddRange(QuotaAges(
            (ProviderUsage.For(provider), QuotaUpdatedAt(provider), monthEnded),
            provider == DisplayProvider.Grok && CursorHasOwnLine()
                ? (ProviderUsage.For(DisplayProvider.Cursor), QuotaUpdatedAt(DisplayProvider.Cursor))
                : null,
            now, TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds)));
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

    /// How old a card's quota numbers are: its agent's, then on Grok's card
    /// Cursor's own line, named for Cursor as its warning is.
    internal static IReadOnlyList<StatusPhrase> QuotaAges(
        (AppUsage Usage, DateTimeOffset? UpdatedAt, bool MonthEnded) own,
        (AppUsage Usage, DateTimeOffset? UpdatedAt)? cursorLine,
        DateTimeOffset now, TimeSpan interval)
    {
        var ages = new List<StatusPhrase>();
        if (QuotaAge(own.Usage, own.UpdatedAt, own.MonthEnded, now, interval) is { } age) ages.Add(age);
        if (cursorLine is { } line && QuotaAge(line.Usage, line.UpdatedAt, false, now, interval) is { } cursor)
        {
            ages.Add(cursor with { Text = $"{ProviderIdentity.DisplayName(DisplayProvider.Cursor)}: {cursor.Text}" });
        }
        return ages;
    }

    /// Explain the wait at a reset boundary before showing an age for old
    /// numbers; the prior window cannot describe the new balance.
    private static StatusPhrase? QuotaAge(AppUsage usage, DateTimeOffset? updatedAt, bool monthEnded, DateTimeOffset now, TimeSpan interval)
    {
        if (usage.FiveHour.At(now).AwaitingSync || usage.Weekly.At(now).AwaitingSync || monthEnded)
        {
            return Phrase(L10n.Tr("Awaiting sync"));
        }
        if (updatedAt is not { } at
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
        NoteParts(provider) is { Count: > 0 } parts ? string.Join(" · ", parts.Select(p => p.Line)) : null;

    private static IReadOnlyList<StatusPhrase> KimiNote()
    {
        if (KimiUsageStore.Shared.Snapshot is not { } snapshot) return Array.Empty<StatusPhrase>();
        var parts = new List<StatusPhrase>();
        if (snapshot.MonthCode is { } code && snapshot.Month is { } total && total.UsedPercent > 0)
        {
            var codeShare = Math.Min(code.UsedPercent, total.UsedPercent);
            var codePercent = Formatting.PercentInt(codeShare);
            var appPercent = Formatting.PercentInt(total.UsedPercent - codeShare);
            parts.Add(Phrase(L10n.TrFormat("Kimi Code {0}% · Kimi app {1}%", codePercent, appPercent),
                $"{codePercent}%", $"{appPercent}%") with { Lead = L10n.Tr("Month:") });
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
