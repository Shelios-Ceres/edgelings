using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

public static class UsageFreshnessTests
{
    private static readonly DateTimeOffset Old = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Old.AddHours(2);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("nonzero cached quotas retain the fetch failure explanation", NonzeroQuotaWarning),
            ("one provider succeeding does not renew its failed peer", MixedRefresh),
            ("cold-start and repeated failures never claim a successful sync", FailedRefresh),
            ("a healthy single-window or zero-percent response counts as a sync", HealthyResponses),
            ("a partial window failure does not advance successful sync time", PartialFailure),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS usage freshness: {name}");
        }
        Console.WriteLine("UsageFreshnessTests GREEN");
    }

    private static AppUsage Healthy() => new(new WindowUsage(0.45, Now.AddHours(1), null), new WindowUsage(0.65, Now.AddDays(2), null));

    private static void NonzeroQuotaWarning()
    {
        var cached = Healthy();
        var stale = cached with
        {
            FiveHour = cached.FiveHour with { Error = "network timeout" },
            Weekly = cached.Weekly with { Error = "network timeout" },
        };
        Expect(AgentReports.WindowError(stale) == "network timeout", "45% / 65% cached values must not suppress the failure");
        Expect(AgentReports.WindowError(cached) is null, "recovery must clear the warning");
        Expect(AgentReports.WindowError(new AppUsage(cached.FiveHour, WindowUsage.Unknown)) is null,
            "an absent secondary window is not a failure");
    }

    private static void MixedRefresh()
    {
        var claude = UsageCachePolicy.SuccessfulUpdateAt(AppUsage.ErrorPair("http 503"), Old, Now);
        var codex = UsageCachePolicy.SuccessfulUpdateAt(Healthy(), Old, Now);
        Expect(claude == Old && codex == Now, "only the provider with a healthy response may get a new successful timestamp");
    }

    private static void FailedRefresh()
    {
        var failure = AppUsage.ErrorPair("network timeout");
        Expect(UsageCachePolicy.SuccessfulUpdateAt(failure, null, Now) is null, "first failure is not a first sync");
        var stamp = UsageCachePolicy.SuccessfulUpdateAt(failure, Old, Now);
        stamp = UsageCachePolicy.SuccessfulUpdateAt(failure, stamp, Now.AddMinutes(5));
        Expect(stamp == Old, "repeated failures must keep the last successful time");
    }

    private static void HealthyResponses()
    {
        var single = new AppUsage(new WindowUsage(0.4, Now.AddDays(3), null, 604800), WindowUsage.Unknown);
        Expect(UsageCachePolicy.SuccessfulUpdateAt(single, Old, Now) == Now, "Codex's single weekly window is healthy");
        var zero = new AppUsage(new WindowUsage(0, Now.AddHours(5), null), new WindowUsage(0, Now.AddDays(7), null));
        Expect(UsageCachePolicy.SuccessfulUpdateAt(zero, null, Now) == Now, "a real 0% response is a successful sync");
        var noReset = new AppUsage(new WindowUsage(0, null, null), new WindowUsage(0, null, null));
        Expect(UsageCachePolicy.SuccessfulUpdateAt(noReset, null, Now) == Now,
            "a healthy zero-usage response need not report a reset timestamp");
    }

    private static void PartialFailure()
    {
        var partial = Healthy() with { Weekly = new WindowUsage(0.65, Now.AddDays(2), "http 500") };
        Expect(UsageCachePolicy.SuccessfulUpdateAt(partial, Old, Now) == Old, "a partial failure is not a complete successful sync");
        Expect(AgentReports.WindowError(partial) == "http 500", "a nonzero failed secondary window still explains its error");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
