using AgentIsland.Cost;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Tests;

public static class TokenDisplayTextTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("mode labels explicitly distinguish cache inclusion", ModeLabels),
            ("a pre-midnight snapshot keeps its date after midnight", SnapshotDate),
            ("seven-day and month captions cross month and year boundaries", PeriodRanges),
            ("zone captions use the snapshot's daylight-saving offset", DaylightSaving),
            ("date labels follow the supplied computer zone, not UTC", LocalDate),
            ("an unscanned summary has no asserted date", EmptySnapshot),
        };
        var previousLanguage = L10n.Current;
        try
        {
            L10n.Current = L10n.Language.English;
            foreach (var (name, test) in tests)
            {
                test();
                Console.WriteLine($"PASS token display: {name}");
            }
            Console.WriteLine("TokenDisplayTextTests GREEN");
        }
        finally
        {
            L10n.Current = previousLanguage;
        }
    }

    private static void ModeLabels()
    {
        Expect(TokenDisplayText.ModeLabel(TokenCountMode.All), "All tokens (including cache)");
        Expect(TokenDisplayText.ModeLabel(TokenCountMode.Billable), "Input + output (excluding cache reads)");
    }

    private static void SnapshotDate()
    {
        var started = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 10, 13, 3, 59, 30, TimeSpan.Zero), NewYork);
        var morning = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 10, 12, 13, 0, 0, TimeSpan.Zero), NewYork);
        var summary = CostSummarizer.Summarize(new[]
        {
            new TokenEvent(TriggerTool.Codex, morning, "synthetic", 100, 20, 30, 40),
        }, started);
        Expect(summary.AsOf, (DateTimeOffset?)started);
        Expect(summary.TodayTokens, 190L);
        Expect(summary.TodayBillableTokens, 150L);
        // Opening the same snapshot after midnight must not relabel its
        // October 12 counts as October 13 counts.
        Expect(TokenDisplayText.PeriodRange(UsageDisplayPeriod.Today, summary.AsOf.Value, NewYork), "2026-10-12");
    }

    private static void PeriodRanges()
    {
        var asOf = new DateTimeOffset(2027, 1, 3, 17, 0, 0, TimeSpan.Zero);
        Expect(TokenDisplayText.PeriodRange(UsageDisplayPeriod.Week, asOf, NewYork), "2026-12-28 – 2027-01-03");
        Expect(TokenDisplayText.PeriodRange(UsageDisplayPeriod.Month, asOf, NewYork), "2027-01-01 – 2027-01-03");
    }

    private static void DaylightSaving()
    {
        var summer = new DateTimeOffset(2026, 10, 13, 5, 0, 0, TimeSpan.Zero);
        var winter = new DateTimeOffset(2026, 12, 13, 5, 0, 0, TimeSpan.Zero);
        Expect(TokenDisplayText.TimeZoneLabel(summer, NewYork).EndsWith("(UTC−04:00)", StringComparison.Ordinal), true);
        Expect(TokenDisplayText.TimeZoneLabel(winter, NewYork).EndsWith("(UTC−05:00)", StringComparison.Ordinal), true);
    }

    private static void LocalDate()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test +05:45", TimeSpan.FromMinutes(345), "Test +05:45", "Test +05:45");
        var at = new DateTimeOffset(2026, 12, 31, 18, 30, 0, TimeSpan.Zero);
        Expect(TokenDisplayText.PeriodRange(UsageDisplayPeriod.Month, at, zone), "2027-01-01");
        Expect(TokenDisplayText.TimeZoneLabel(at, zone), "Test +05:45 (UTC+05:45)");
    }

    private static void EmptySnapshot() => Expect(ProviderCostSummary.Empty.AsOf, null);

    private static void Expect<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}, got {actual}");
    }
}
