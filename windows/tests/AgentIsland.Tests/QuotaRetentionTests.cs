using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// A character's numbers outlive a dropped network, a closed agent and a
/// restart: kept quota still shows (refilled once its reset passes), says how
/// old it is, and token counts come back from the last scan.
public static class QuotaRetentionTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Local));

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a failed refresh keeps the last numbers on the card", TestFailedRefreshKeepsNumbers),
            ("a window that never loaded still shows a dash", TestNeverLoadedShowsDash),
            ("kept numbers survive repeated failures, even at 0%", TestKeptThroughRepeatedFailures),
            ("a single-window quota keeps its label through a failure", TestSingleWindowKeepsLabel),
            ("a window that never held numbers does not become kept", TestErroredWindowStaysEmpty),
            ("a window that reset since it was read shows as refilled", TestResetWindowRefills),
            ("a refilled window without a known period has no countdown", TestResetWithoutPeriod),
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

    private static void TestResetWindowRefills()
    {
        var expired = new WindowUsage(0.95, Now.AddHours(-1), "network timeout", 5 * 3600) { Kept = true };
        var current = expired.At(Now);
        Expect(current.UsedPercent == 0, "a window past its reset has refilled");
        Expect(current.ResetAt == Now.AddHours(4), $"the next reset is one period on, got {current.ResetAt}");
        var meter = StatusCards.MeterFor(expired, "5h", showsRemaining: true, Now);
        Expect(meter.Value.Contains("100"), $"remaining mode shows it full again, got {meter.Value}");
        var longGone = new WindowUsage(0.8, Now.AddHours(-12), null, 5 * 3600).At(Now);
        Expect(longGone.ResetAt == Now.AddHours(3), $"several periods later the reset still lands in the future, got {longGone.ResetAt}");
    }

    private static void TestResetWithoutPeriod()
    {
        var current = new WindowUsage(0.6, Now.AddDays(-1), null).At(Now);
        Expect(current.UsedPercent == 0 && current.ResetAt is null, "refilled, with no guessed countdown");
        var live = new WindowUsage(0.6, Now.AddDays(1), null);
        Expect(live.At(Now) == live, "a window before its reset is untouched");
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

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
