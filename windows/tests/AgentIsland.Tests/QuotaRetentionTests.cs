using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Model;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// A character's numbers outlive a dropped network, a closed agent and a
/// restart: kept quota shows until its observed window ends, says how old it
/// is, and token counts come back from the last scan.
public static class QuotaRetentionTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Local));

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a failed refresh keeps the last numbers on the card", TestFailedRefreshKeepsNumbers),
            ("a window that never loaded still shows a dash", TestNeverLoadedShowsDash),
            ("startup and its first failed refresh never invent a zero reading", TestColdStartFailure),
            ("observed zero without a reset survives cache and failures", TestObservedZeroRoundTrip),
            ("legacy cache restores numbers but not an empty placeholder", TestLegacyCache),
            ("provider windows distinguish an explicit zero from missing data", TestParsedZero),
            ("kept numbers survive repeated failures, even at 0%", TestKeptThroughRepeatedFailures),
            ("a single-window quota keeps its label through a failure", TestSingleWindowKeepsLabel),
            ("a window that never held numbers does not become kept", TestErroredWindowStaysEmpty),
            ("a window that reset since it was read waits for sync", TestResetWindowWaitsForSync),
            ("a reset window without a known period waits for sync", TestResetWithoutPeriod),
            ("the exact reset boundary is unknown until a new observation", TestResetBoundary),
            ("kept or old numbers say how old they are", TestStaleness),
            ("a reset window is no longer out of quota", TestResetIsNotRateLimited),
            ("token counts from an earlier day or month show a dash", TestTokenDates),
            ("token counts with no scan yet say so", TestTokensNotScanned),
            ("saved token totals survive the round trip to disk", TestTotalsRoundTrip),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("QuotaRetentionTests GREEN");
    }

    private static AppUsage Good(double fiveHour, double weekly) => new(
        new WindowUsage(fiveHour, Now.AddHours(2), null, 5 * 3600),
        new WindowUsage(weekly, Now.AddDays(3), null, 7 * 86400),
        "max");

    private static void TestFailedRefreshKeepsNumbers()
    {
        var kept = UsageCachePolicy.Merged(Good(0.42, 0.18), AppUsage.ErrorPair("network timeout"));
        Expect(kept.FiveHour.Kept && kept.Weekly.Kept, "both windows are marked kept");
        Expect(kept.FiveHour.Error == "network timeout", "the failure still explains itself");
        var meter = StatusCards.MeterFor(kept.FiveHour, "5h", showsRemaining: false, Now);
        Expect(meter.Value == "42%", $"the card keeps showing 42%, got {meter.Value}");
        Expect(meter.Resets is not null, "the countdown survives too");
        Expect(kept.Plan == "max", "the plan badge is kept");
    }

    private static void TestNeverLoadedShowsDash()
    {
        var cold = UsageCachePolicy.Merged(AppUsage.ErrorPair("no credentials"), AppUsage.ErrorPair("network timeout"));
        var meter = StatusCards.MeterFor(cold.FiveHour, "5h", showsRemaining: false, Now);
        Expect(meter.Value == "—" && meter.Shown is null, $"nothing to keep shows a dash, got {meter.Value}");
    }

    private static void TestColdStartFailure()
    {
        var cold = AppUsage.Empty;
        Expect(!cold.SecondaryMissing, "unknown startup must not masquerade as a single healthy window");
        Expect(UsageCachePolicy.CacheableCopy(cold) is null, "unknown startup must not be saved as observed zero");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var window in new[] { cold.FiveHour, cold.Weekly })
            {
                Expect(!window.Kept, "no number was ever observed, so there is nothing to keep");
                foreach (var remaining in new[] { false, true })
                {
                    var meter = StatusCards.MeterFor(window, "quota", remaining, Now);
                    Expect(meter.Value == "—" && meter.Shown is null && meter.Used is null,
                        "startup and failures must show neither 0% used nor 100% left");
                }
            }
            cold = UsageCachePolicy.Merged(cold, AppUsage.ErrorPair("network timeout"));
        }
        Expect(cold.FiveHour.Error == "network timeout", "the failed first refresh keeps its explanation");
        Expect(UsageCachePolicy.SuccessfulUpdateAt(cold, null, Now) is null, "failure is not a successful sync");
    }

    private static void TestObservedZeroRoundTrip()
    {
        var zero = new WindowUsage(0, null, null) { Observed = true };
        var original = new AppUsage(zero, WindowUsage.Unknown, "pro");
        var cache = UsageCachePolicy.SnapshotForSave(AppUsage.Empty, original, null, Now);
        Expect(cache is not null, "an explicit zero with no reset is cacheable");
        var serialized = JsonSerializer.Serialize(cache);
        var saved = JsonSerializer.Deserialize<UsageCacheSnapshot>(serialized)!;
        var restored = UsageCachePolicy.RestoredSnapshot(saved, Now.AddMinutes(1), UsageCachePolicy.KeepFor)!;
        var usage = restored.Codex;
        Expect(usage.FiveHour.Observed && usage.SecondaryMissing, "observation and single-window shape survive JSON and restore");
        for (var i = 0; i < 3; i++) usage = UsageCachePolicy.Merged(usage, AppUsage.ErrorPair("network timeout"));
        Expect(usage.FiveHour.Kept && usage.SecondaryMissing, "actual zero survives repeated failures without adding a second window");
        Expect(StatusCards.MeterFor(usage.FiveHour, "week", false, Now).Value == "0%", "observed zero stays a displayed zero");
        Expect(StatusCards.MeterFor(usage.FiveHour, "week", true, Now).Shown == 1, "observed zero legitimately means 100% left");
        Expect(UsageCachePolicy.CacheableCopy(usage) is null, "failed refresh must not renew zero's successful timestamp");
    }

    private static void TestLegacyCache()
    {
        const string json = """
            {"FiveHour":{"UsedPercent":0.4,"ResetAt":null,"Error":null},
             "Weekly":{"UsedPercent":0,"ResetAt":null,"Error":"no data"},"Plan":"pro"}
            """;
        var legacy = JsonSerializer.Deserialize<AppUsage>(json)!;
        Expect(!legacy.FiveHour.Observed, "old JSON has no observation marker");
        var copy = UsageCachePolicy.CacheableCopy(legacy);
        Expect(copy is not null && copy.FiveHour.UsedPercent == 0.4 && copy.SecondaryMissing,
            "old value-bearing cache remains usable");
        var placeholder = legacy with
        {
            FiveHour = new WindowUsage(0, null, null),
            Weekly = new WindowUsage(0, null, null),
        };
        Expect(UsageCachePolicy.CacheableCopy(placeholder) is null, "ambiguous old all-zero placeholders do not become observations");
        var zeroWithReset = placeholder with { FiveHour = placeholder.FiveHour with { ResetAt = Now.AddHours(1) } };
        Expect(UsageCachePolicy.CacheableCopy(zeroWithReset) is not null, "old real zero with a reset remains usable");
    }

    private static void TestParsedZero()
    {
        using var codex = JsonDocument.Parse("{\"used_percent\":0}");
        using var claude = JsonDocument.Parse("{\"utilization\":0}");
        using var missing = JsonDocument.Parse("{}");
        var codexZero = UsageFetcher.ParseCodexWindow(codex.RootElement);
        var claudeZero = UsageFetcher.ParseClaudeWindow(claude.RootElement, 5 * 3600);
        Expect(codexZero.Observed && !codexZero.HasError && claudeZero.Observed && !claudeZero.HasError,
            "both providers' explicit zero payloads count as observations without resets");
        Expect(UsageCachePolicy.CacheableCopy(new AppUsage(claudeZero, codexZero)) is not null,
            "an entirely zero successful response can be saved");
        Expect(!UsageFetcher.ParseCodexWindow(missing.RootElement).Observed
            && UsageFetcher.ParseCodexWindow(missing.RootElement).HasError
            && UsageFetcher.ParseClaudeWindow(missing.RootElement, 5 * 3600).HasError,
            "missing percentage fields cannot be fabricated as a successful zero");
    }

    private static void TestKeptThroughRepeatedFailures()
    {
        var usage = Good(0, 0.25);
        for (var i = 0; i < 3; i++) usage = UsageCachePolicy.Merged(usage, AppUsage.ErrorPair("http 503"));
        Expect(usage.FiveHour.Kept && usage.FiveHour.UsedPercent == 0, "a kept 0% window stays kept");
        Expect(StatusCards.MeterFor(usage.FiveHour, "5h", false, Now).Value == "0%", "it still shows 0%");
        Expect(StatusCards.MeterFor(usage.Weekly, "week", false, Now).Value == "25%", "the weekly window still shows 25%");
    }

    private static void TestSingleWindowKeepsLabel()
    {
        var single = new AppUsage(new WindowUsage(0.3, Now.AddDays(2), null, 7 * 86400), WindowUsage.Unknown, "plus");
        Expect(single.SecondaryMissing, "the fixture is single-window");
        var kept = UsageCachePolicy.Merged(single, AppUsage.ErrorPair("network timeout"));
        Expect(kept.SecondaryMissing, "a failure keeps the single-window shape, so the number stays under its label");
        Expect(kept.FiveHour.Kept && kept.FiveHour.UsedPercent == 0.3, "the weekly number is kept");
    }

    private static void TestErroredWindowStaysEmpty()
    {
        var partial = new AppUsage(new WindowUsage(0.5, Now.AddHours(1), null), new WindowUsage(0, null, "parse error"));
        var kept = UsageCachePolicy.Merged(partial, AppUsage.ErrorPair("network timeout"));
        Expect(kept.FiveHour.Kept, "the window with numbers is kept");
        Expect(!kept.Weekly.Kept, "a window that only ever failed is not dressed up as kept");
        Expect(StatusCards.MeterFor(kept.Weekly, "week", false, Now).Value == "—", "it keeps its dash");
    }

    private static void TestResetWindowWaitsForSync()
    {
        var expired = new WindowUsage(0.95, Now.AddHours(-1), "network timeout", 5 * 3600) { Kept = true };
        var current = expired.At(Now);
        Expect(current.AwaitingSync, "the prior window does not observe the new balance");
        Expect(current.ResetAt is null, "no future reset may be fabricated from a period");
        var meter = StatusCards.MeterFor(expired, "5h", showsRemaining: true, Now);
        Expect(meter.Value == "—" && meter.Shown is null && meter.Used is null && meter.Resets is not null,
            "the meter waits for sync without an invented percentage or bar");
        Expect(expired.UsedPercent == 0.95 && expired.ResetAt == Now.AddHours(-1), "display projection preserves the source observation");
        var longGone = new WindowUsage(0.8, Now.AddHours(-12), null, 5 * 3600).At(Now);
        Expect(longGone.AwaitingSync && longGone.ResetAt is null, "several elapsed periods do not invent the next one");
    }

    private static void TestResetWithoutPeriod()
    {
        var current = new WindowUsage(0.6, Now.AddDays(-1), null).At(Now);
        Expect(current.AwaitingSync && current.ResetAt is null, "unknown after reset, with no guessed countdown");
        var live = new WindowUsage(0.6, Now.AddDays(1), null);
        Expect(live.At(Now) == live, "a window before its reset is untouched");
    }

    private static void TestResetBoundary()
    {
        var source = new WindowUsage(0.9, Now, null, 7 * 86400) { Observed = true };
        Expect(StatusCards.MeterFor(source, "week", false, Now.AddTicks(-1)).Value == "90%",
            "the observation is still usable immediately before reset");
        foreach (var remaining in new[] { false, true })
        {
            var meter = StatusCards.MeterFor(source, "week", remaining, Now);
            Expect(meter.Value == "—" && meter.Shown is null && meter.Resets is not null,
                "even a healthy recent snapshot becomes unknown exactly at reset");
        }
        var projected = source.At(Now);
        Expect(projected.At(Now.AddHours(1)).AwaitingSync, "waiting state is stable across repeated rendering");
        Expect(UsageCachePolicy.CacheableCopy(new AppUsage(projected, WindowUsage.Unknown)) is null,
            "a display projection cannot be saved as a fresh observation");
        var fresh = new WindowUsage(0.2, Now.AddDays(7), null, 7 * 86400) { Observed = true };
        var merged = UsageCachePolicy.Merged(new AppUsage(source, WindowUsage.Unknown), new AppUsage(fresh, WindowUsage.Unknown));
        Expect(StatusCards.MeterFor(merged.FiveHour, "week", false, Now).Value == "20%",
            "a fresh provider response replaces the expired reading and resumes numbers");
        Expect(merged.SecondaryMissing, "the single-window provider keeps its shape through reset and recovery");
    }

    private static void TestStaleness()
    {
        var interval = TimeSpan.FromMinutes(5);
        Expect(StatusCards.QuotaIsStale(kept: true, Now.AddMinutes(-1), Now, interval), "a failed refresh is never live");
        Expect(!StatusCards.QuotaIsStale(kept: false, Now.AddMinutes(-3), Now, interval), "a recent success is live");
        Expect(StatusCards.QuotaIsStale(kept: false, Now.AddMinutes(-20), Now, interval), "numbers two rounds old are not live");
        Expect(Formatting.RelativeAgo(TimeSpan.FromDays(3), chinese: true) == "3 天前", "old numbers read in days");
        Expect(Formatting.RelativeAgo(TimeSpan.FromHours(30), chinese: false) == "30h ago", "a day and a bit still reads in hours");
    }

    private static void TestResetIsNotRateLimited()
    {
        var full = new AppUsage(new WindowUsage(0.2, null, null), new WindowUsage(1, DateTimeOffset.Now.AddDays(-1), null, 7 * 86400));
        Expect(ActivityMonitor.UsageAttentionState(full) is null, "a used-up window that has reset no longer reads as out of quota");
        var stillFull = full with { Weekly = full.Weekly with { ResetAt = DateTimeOffset.Now.AddDays(1) } };
        Expect(ActivityMonitor.UsageAttentionState(stillFull) == ActivityState.RateLimited, "before its reset it still does");
    }

    private static void TestTokenDates()
    {
        var totals = new TokenTotals(Now.AddHours(-2), 278_000_000, 21_000_000, 2_300_000_000, 180_000_000);
        var same = StatusCards.TokensFrom(totals, TokenCountMode.All, Now);
        Expect(same.TodayNumber == "278M" && same.MonthNumber == "2.3B", $"same day shows both, got {same.TodayNumber} / {same.MonthNumber}");
        var billable = StatusCards.TokensFrom(totals, TokenCountMode.Billable, Now);
        Expect(billable.TodayNumber == "21M", $"the counting setting still applies, got {billable.TodayNumber}");
        var yesterday = StatusCards.TokensFrom(totals with { AsOf = Now.AddDays(-1) }, TokenCountMode.All, Now);
        Expect(yesterday.TodayNumber == "—" && yesterday.MonthNumber == "2.3B", "yesterday's count is not today's, the month still holds");
        var lastMonth = StatusCards.TokensFrom(totals with { AsOf = Now.AddDays(-20) }, TokenCountMode.All, Now);
        Expect(lastMonth.TodayNumber == "—" && lastMonth.MonthNumber == "—", "last month's count says nothing about this one");
    }

    private static void TestTokensNotScanned()
    {
        var none = StatusCards.TokensFrom(null, TokenCountMode.All, Now);
        Expect(none.MonthNumber == "" && none.Month == "—", "no saved or live count still says it has not scanned");
    }

    private static void TestTotalsRoundTrip()
    {
        var saved = new Dictionary<string, TokenTotals>
        {
            ["Claude"] = new(Now, 278_000_000, 21_000_000, 2_300_000_000, 180_000_000),
        };
        var restored = JsonSerializer.Deserialize<Dictionary<string, TokenTotals>>(JsonSerializer.SerializeToElement(saved));
        Expect(restored is not null && restored["Claude"] == saved["Claude"], "the saved figures read back unchanged");
    }
}
