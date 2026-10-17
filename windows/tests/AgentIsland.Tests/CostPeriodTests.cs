using AgentIsland.Core;
using AgentIsland.Cost;

namespace AgentIsland.Tests;

public static class CostPeriodTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("calendar week excludes the rolling-only oldest day", ExcludesRollingOnlyDay),
            ("calendar week totals, daily bars and models share boundaries", SharedBoundaries),
            ("midnight still produces seven calendar-day buckets", Midnight),
            ("early January includes the previous year's calendar days", CrossYear),
            ("report slices remain half-open and daily dollars sum to the total", HalfOpenSlice),
            ("today's tokens fall into their local hours", HourlyTokens),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS cost periods: {name}");
        }
        Console.WriteLine("CostPeriodTests GREEN");
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour = 0)
    {
        var date = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
    }

    private static TokenEvent Event(DateTimeOffset at, double dollars, string model = "model-a") =>
        new(TriggerTool.Codex, at, model, 100, 20, 30, 40, dollars);

    private static void ExcludesRollingOnlyDay()
    {
        var now = Local(2026, 10, 12, 12);
        var summary = CostSummarizer.Summarize(new[] { Event(Local(2026, 10, 5, 13), 10) }, now);
        Expect(summary.WeeklyModels.Sum(m => m.Dollars) == 10, "existing rolling weekly report semantics must stay unchanged");
        Expect(summary.LastSevenDays.Dollars == 0, "the cost page must exclude the eighth calendar day from its total");
        Expect(summary.LastSevenDays.DailyTokens.Count == 7 && summary.LastSevenDays.DailyTokens.Sum(d => d.Dollars) == 0,
            "the same empty seven days must appear in the chart");
    }

    private static void SharedBoundaries()
    {
        var now = Local(2026, 10, 12, 12);
        var start = CostSummarizer.LastSevenDaysStart(now);
        var period = CostSummarizer.Summarize(new[]
        {
            Event(start.AddTicks(-1), 100), Event(start, 2), Event(now, 3, "model-b"), Event(now.AddTicks(1), 200),
        }, now).LastSevenDays;
        Expect(period.Dollars == 5 && period.ByModel.Sum(m => m.Dollars) == 5 && period.DailyTokens.Sum(d => d.Dollars) == 5,
            "start and now are included; older and future events must stay out of every total");
        Expect(period.DailyTokens.Sum(d => d.Tokens) == 380 && period.ByModel.Sum(m => m.Tokens) == 380,
            "all-token totals and model details must agree");
        Expect(period.DailyTokens.Sum(d => d.BillableTokens) == 300 && period.ByModel.Sum(m => m.BillableTokens) == 300,
            "new-input/output totals and model details must agree, including cache writes");
    }

    private static void Midnight()
    {
        var now = Local(2026, 10, 12);
        var period = CostSummarizer.Summarize(new[] { Event(now, 4) }, now).LastSevenDays;
        Expect(period.DailyTokens.Count == 7 && period.DailyTokens[^1].DayStart.Date == now.Date,
            "today's midnight event needs a seventh bucket even within the first second");
        Expect(period.DailyTokens[^1].Dollars == 4, "the midnight event must appear on today's bar");
    }

    private static void CrossYear()
    {
        var now = Local(2027, 1, 3, 12);
        var period = CostSummarizer.Summarize(new[] { Event(Local(2026, 12, 28), 7) }, now).LastSevenDays;
        Expect(period.Dollars == 7 && period.DailyTokens[0].DayStart.Date == new DateTime(2026, 12, 28),
            "the calendar week must cross New Year without truncating its first days");
        Expect(CostSummarizer.YearHistoryDays(now) >= 7, "the scan lookback must supply enough previous-year events");
    }

    private static void HalfOpenSlice()
    {
        var start = Local(2026, 11, 1);
        var end = Local(2026, 11, 3);
        var slice = CostSummarizer.Slice(new[] { Event(start, 2), Event(end.AddTicks(-1), 3), Event(end, 100) }, start, end);
        Expect(slice.Dollars == 5 && slice.DailyTokens.Sum(d => d.Dollars) == 5,
            "daily dollar buckets and total must both respect the exclusive end");
        Expect(slice.DailyTokens.Count == 2, "the exclusive ending day must not get a bucket");
    }

    private static void HourlyTokens()
    {
        var now = Local(2026, 10, 12, 12);
        var summary = CostSummarizer.Summarize(new[]
        {
            Event(Local(2026, 10, 11, 23), 1), Event(Local(2026, 10, 12, 1), 0),
            Event(Local(2026, 10, 12, 4), 0), Event(Local(2026, 10, 12, 4), 0),
        }, now);
        Expect(summary.TodayHourlyTokens.Count == 24 && summary.TodayHourlyTokens.Sum() == summary.TodayTokens,
            "every token of today must sit in one of its 24 hours");
        Expect(summary.TodayHourlyTokens[1] == 190 && summary.TodayHourlyTokens[4] == 380 && summary.TodayHourlyTokens[23] == 0,
            "an hour must hold its own events only, and yesterday's must stay out");
        Expect(summary.TodayHourlyBillableTokens[4] == 300 && summary.TodayHourlyBillableTokens.Sum() == summary.TodayBillableTokens,
            "the billable hours must add up to the billable total");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
