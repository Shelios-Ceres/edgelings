using AgentIsland.Localization;

namespace AgentIsland.Cost;

public sealed record DailyTokenBucket(DateTimeOffset DayStart, long Tokens, long BillableTokens, double Dollars);

public sealed record ModelSpend(string Model, long Tokens, long BillableTokens, double Dollars)
{
    /// Tokens 拆成四类：输入（不含缓存）、输出、写入缓存、读取缓存。
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long CacheReadTokens { get; init; }

    /// 没有价格的调用的 token 数（价格表查不到，日志也没自带金额）。
    /// 这些调用不进 Dollars，也就不进任何合计金额。
    public long UnpricedTokens { get; init; }

    /// 全部调用都没有价格：界面写“未定价”，不写 $0。
    public bool Unpriced => Tokens > 0 && UnpricedTokens >= Tokens;

    /// 一部分调用没有价格：Dollars 只是有价格那部分的金额。
    public bool PartlyUnpriced => UnpricedTokens > 0 && UnpricedTokens < Tokens;

    /// 几行加成一行（一个 agent 一个时段的合计），Model 为空。
    public static ModelSpend Total(IEnumerable<ModelSpend> models)
    {
        long tokens = 0, billable = 0, input = 0, output = 0, creation = 0, read = 0, unpriced = 0;
        double dollars = 0;
        foreach (var model in models)
        {
            tokens += model.Tokens;
            billable += model.BillableTokens;
            dollars += model.Dollars;
            input += model.InputTokens;
            output += model.OutputTokens;
            creation += model.CacheCreationTokens;
            read += model.CacheReadTokens;
            unpriced += model.UnpricedTokens;
        }
        return new ModelSpend("", tokens, billable, dollars)
        {
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = creation,
            CacheReadTokens = read,
            UnpricedTokens = unpriced,
        };
    }

    /// 合计金额：只加有价格的部分；没有一行带价格时为 null（不是 0）。
    public static double? PricedDollars(IEnumerable<ModelSpend> models)
    {
        var list = models.ToList();
        return list.Any(model => model.Tokens > model.UnpricedTokens) ? list.Sum(model => model.Dollars) : null;
    }

    /// Largest first by the page's token mode: billable (input + output) or
    /// wire (all tokens). The summary's own lists stay sorted by billable.
    public static List<ModelSpend> Ranked(IEnumerable<ModelSpend> models, bool billable) =>
        models.OrderByDescending(model => billable ? model.BillableTokens : model.Tokens).ToList();
}

/// One provider's rollups for the cost page and the overview grid.
public sealed record ProviderCostSummary(
    double TodayDollars,
    long TodayTokens,
    long TodayBillableTokens,
    double MonthDollars,
    long MonthTokens,
    long MonthBillableTokens,
    IReadOnlyList<double> TodayCumulativeDollars,     // 24 hourly points
    IReadOnlyList<double> MonthCumulativeDollars,     // one per day of month
    IReadOnlyList<ModelSpend> RecentModels,           // rolling 5h, billable desc
    IReadOnlyList<ModelSpend> WeeklyModels,           // rolling 7d, billable desc
    IReadOnlyList<ModelSpend> MonthModels,            // calendar month, billable desc
    IReadOnlyList<DailyTokenBucket> DailyHistory,     // Jan 1 -> today
    IReadOnlyList<string> UnknownModels)
{
    /// The cost window's seven calendar days, including today. The rolling
    /// WeeklyModels above remains unchanged for existing report consumers.
    public ReportSlice LastSevenDays { get; init; } = ReportSlice.Empty;
    public DateTimeOffset? AsOf { get; init; }

    /// Today's tokens by local hour, 24 points, for a chart with no spend to
    /// show: everything that crossed the wire, and the billable part.
    public IReadOnlyList<long> TodayHourlyTokens { get; init; } = new long[24];
    public IReadOnlyList<long> TodayHourlyBillableTokens { get; init; } = new long[24];

    /// Every model used today, billable desc. RecentModels covers the last
    /// five hours only, while today's spend covers the whole day.
    public IReadOnlyList<ModelSpend> TodayModels { get; init; } = Array.Empty<ModelSpend>();

    /// What the agent itself calls its models, by model id (Kimi Code names
    /// its plan's aliases); an id it doesn't name reads as itself.
    public IReadOnlyDictionary<string, string> ModelNames { get; init; } = new Dictionary<string, string>();

    /// Antigravity's unnamed step reads in the caller's language: the
    /// summary is built on a scan thread, the name shown on the UI's.
    public string NameOf(string model) =>
        model == AntigravityLogReader.UnknownModel ? L10n.Tr("Model not named")
        : model == KimiLogReader.MemoryModel ? L10n.Tr("Memory (background)")
        : ModelNames.TryGetValue(model, out var name) ? name : model;

    public static ProviderCostSummary Empty { get; } = new(
        0, 0, 0, 0, 0, 0,
        new double[24], Array.Empty<double>(),
        Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
        Array.Empty<DailyTokenBucket>(), Array.Empty<string>());
}

/// One report period's aggregation: per-day buckets, total dollars, and
/// per-model rows over an arbitrary half-open [start, end) interval. The
/// report pager feeds this from its own full-year reader scan (per-file
/// memoization makes the rescan cheap), fully independent of the fixed
/// windows Summarize computes for the live panel.
public sealed record ReportSlice(
    IReadOnlyList<DailyTokenBucket> DailyTokens,   // zero-filled, oldest first
    double Dollars,
    IReadOnlyList<ModelSpend> ByModel)
{
    public static ReportSlice Empty { get; } = new(
        Array.Empty<DailyTokenBucket>(), 0, Array.Empty<ModelSpend>());
}

/// Single-pass aggregation, calendar-local like the macOS CostSummary.
public static class CostSummarizer
{
    /// Aggregate events over [start, end) with the same accounting rules as
    /// Summarize: wire vs billable split, canonical model grouping,
    /// self-reported cost override.
    public static ReportSlice Slice(IReadOnlyList<TokenEvent> events, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var startDay = TimeZoneInfo.ConvertTime(start, zone).Date;
        var lastDay = TimeZoneInfo.ConvertTime(end, zone).AddTicks(-1).Date;
        var dayCount = Math.Max(1, (int)(lastDay - startDay).TotalDays + 1);

        var tokenBuckets = new long[dayCount];
        var billableBuckets = new long[dayCount];
        var dollarBuckets = new double[dayCount];
        double dollars = 0;
        var byModel = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);

        foreach (var tokenEvent in events)
        {
            var local = TimeZoneInfo.ConvertTime(tokenEvent.Timestamp, zone);
            if (local < start || local >= end) continue;
            var dayOffset = (int)(local.Date - startDay).TotalDays;
            if (dayOffset >= 0 && dayOffset < dayCount)
            {
                tokenBuckets[dayOffset] += tokenEvent.WireTokens;
                billableBuckets[dayOffset] += tokenEvent.BillableTokens;
                dollarBuckets[dayOffset] += tokenEvent.Dollars;
            }
            dollars += tokenEvent.Dollars;
            Accumulate(byModel, Pricing.CanonicalModelName(tokenEvent.Model), tokenEvent, tokenEvent.Dollars, Pricing.HasPrice(tokenEvent));
        }

        var daily = new List<DailyTokenBucket>(dayCount);
        for (var i = 0; i < dayCount; i++)
        {
            var date = startDay.AddDays(i);
            var day = new DateTimeOffset(date, zone.GetUtcOffset(date));
            daily.Add(new DailyTokenBucket(day, tokenBuckets[i], billableBuckets[i], dollarBuckets[i]));
        }

        return new ReportSlice(
            daily,
            dollars,
            ToSpendList(byModel));
    }

    public static int YearHistoryDays(DateTimeOffset now)
    {
        var jan1 = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
        return Math.Max(7, (int)(now.Date - jan1.Date).TotalDays + 1);
    }

    public static DateTimeOffset LastSevenDaysStart(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var date = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(-6);
        return new DateTimeOffset(date, zone.GetUtcOffset(date));
    }

    public static ProviderCostSummary Summarize(IReadOnlyList<TokenEvent> events, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        // Today, the month and the year are told by the event's local date. A
        // midnight built with today's offset is an hour off across a
        // daylight-saving change: the first hour of November would fall out
        // of November, and February's last hour into March.
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var fiveHoursAgo = now.AddHours(-5);
        var sevenDaysAgo = now.AddDays(-7);

        double todayDollars = 0, monthDollars = 0;
        long todayTokens = 0, todayBillable = 0, monthTokens = 0, monthBillable = 0;
        var hourly = new double[24];
        var hourlyTokens = new long[24];
        var hourlyBillable = new long[24];
        var daily = new double[DateTime.DaysInMonth(today.Year, today.Month)];
        var recent = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var todayModels = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var weekly = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var month = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<DateTime, (long Tokens, long Billable, double Dollars)>();
        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tokenEvent in events)
        {
            var local = TimeZoneInfo.ConvertTime(tokenEvent.Timestamp, zone);
            if (local > now || local.Year != today.Year) continue;
            var dollars = tokenEvent.Dollars;
            var priced = Pricing.HasPrice(tokenEvent);
            var model = Pricing.CanonicalModelName(tokenEvent.Model);
            if (!Pricing.IsKnown(tokenEvent.PricedAs ?? model)) unknown.Add(model);

            if (local.Date == today.Date)
            {
                todayDollars += dollars;
                todayTokens += tokenEvent.WireTokens;
                todayBillable += tokenEvent.BillableTokens;
                var hour = Math.Clamp(local.Hour, 0, 23);
                hourly[hour] += dollars;
                hourlyTokens[hour] += tokenEvent.WireTokens;
                hourlyBillable[hour] += tokenEvent.BillableTokens;
                Accumulate(todayModels, model, tokenEvent, dollars, priced);
            }
            if (local.Month == today.Month)
            {
                monthDollars += dollars;
                monthTokens += tokenEvent.WireTokens;
                monthBillable += tokenEvent.BillableTokens;
                daily[Math.Clamp(local.Day - 1, 0, daily.Length - 1)] += dollars;
                Accumulate(month, model, tokenEvent, dollars, priced);
            }
            if (local >= fiveHoursAgo) Accumulate(recent, model, tokenEvent, dollars, priced);
            if (local >= sevenDaysAgo) Accumulate(weekly, model, tokenEvent, dollars, priced);

            var day = local.Date;
            history.TryGetValue(day, out var bucket);
            history[day] = (bucket.Tokens + tokenEvent.WireTokens,
                bucket.Billable + tokenEvent.BillableTokens,
                bucket.Dollars + dollars);
        }

        // Cumulative series for the sparklines.
        var todaySeries = new double[24];
        double running = 0;
        var currentHour = today.Hour;
        for (var h = 0; h < 24; h++)
        {
            running += hourly[h];
            todaySeries[h] = h <= currentHour ? running : running;
        }
        var monthSeries = new double[today.Day];
        running = 0;
        for (var d = 0; d < today.Day; d++)
        {
            running += daily[d];
            monthSeries[d] = running;
        }

        var dailyHistory = history
            .OrderBy(kv => kv.Key)
            .Select(kv => new DailyTokenBucket(
                new DateTimeOffset(kv.Key, zone.GetUtcOffset(kv.Key)), kv.Value.Tokens, kv.Value.Billable, kv.Value.Dollars))
            .ToList();

        return new ProviderCostSummary(
            todayDollars, todayTokens, todayBillable,
            monthDollars, monthTokens, monthBillable,
            todaySeries, monthSeries,
            ToSpendList(recent), ToSpendList(weekly), ToSpendList(month),
            dailyHistory, unknown.ToList())
        {
            AsOf = now,
            LastSevenDays = Slice(events, LastSevenDaysStart(now, zone), now.AddTicks(1), zone),
            TodayHourlyTokens = hourlyTokens,
            TodayHourlyBillableTokens = hourlyBillable,
            TodayModels = ToSpendList(todayModels),
        };
    }

    /// 一个模型的累计，按引用改。
    private sealed class Tally
    {
        public long Tokens, Billable, Input, Output, CacheCreation, CacheRead, Unpriced;
        public double Dollars;
    }

    private static void Accumulate(
        Dictionary<string, Tally> sink,
        string model,
        TokenEvent tokenEvent,
        double dollars,
        bool priced)
    {
        if (!sink.TryGetValue(model, out var entry)) sink[model] = entry = new Tally();
        entry.Tokens += tokenEvent.WireTokens;
        entry.Billable += tokenEvent.BillableTokens;
        entry.Dollars += dollars;
        entry.Input += tokenEvent.InputTokens;
        entry.Output += tokenEvent.OutputTokens;
        entry.CacheCreation += tokenEvent.CacheCreationTokens;
        entry.CacheRead += tokenEvent.CacheReadTokens;
        if (!priced) entry.Unpriced += tokenEvent.WireTokens;
    }

    private static List<ModelSpend> ToSpendList(Dictionary<string, Tally> sink) =>
        sink.Select(kv => new ModelSpend(kv.Key, kv.Value.Tokens, kv.Value.Billable, kv.Value.Dollars)
            {
                InputTokens = kv.Value.Input,
                OutputTokens = kv.Value.Output,
                CacheCreationTokens = kv.Value.CacheCreation,
                CacheReadTokens = kv.Value.CacheRead,
                UnpricedTokens = kv.Value.Unpriced,
            })
            .OrderByDescending(spend => spend.BillableTokens)
            .ToList();
}
