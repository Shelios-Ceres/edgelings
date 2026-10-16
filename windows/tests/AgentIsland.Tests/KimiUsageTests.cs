using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// Pins Kimi Code's plan-usage parsing: the local server's camelCase quota,
/// Kimi's own snake_case payload with string ratios and a fixed-point
/// booster balance, missing windows, and the loopback-only, alive-only rule
/// for Kimi Code's local server. Synthetic payloads mirroring the shapes
/// observed on 2026-10-11.
public static class KimiUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("local quota: windows, plan and no booster", LocalQuota),
            ("api payload: string ratios, absent weekly window, booster in cents", ApiPayload),
            ("api payload: a ratio above one clamps", ClampsRatio),
            ("server: a fresh loopback instance is used", FreshLoopback),
            ("server: a remote host is refused", RemoteRefused),
            ("server: a stale heartbeat is refused", StaleRefused),
            ("server: a bad port is refused", BadPortRefused),
            ("live: the desktop server's session page parses", LiveParse),
            ("live: an approval waits on the user", LiveApproval),
            ("live: a question asks the user", LiveQuestion),
            ("live: an active turn works while its log is quiet", LiveActive),
            ("live: an idle report never stops a session", LiveIdle),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS kimi usage: {name}");
        }
        Console.WriteLine("KimiUsageTests GREEN");
    }

    private static void LocalQuota()
    {
        using var doc = JsonDocument.Parse("""
            {"usages":{"limit5h":{"usedRatio":0.25,"resetAt":"2026-10-12T03:39:55Z"},
                       "monthTotal":{"usedRatio":0.9281,"resetAt":"2026-10-21T00:00:00Z"},
                       "monthCode":{"usedRatio":0.8637}},
             "extraUsage":null}
            """);
        var snapshot = KimiUsageFetcher.FromLocalQuota(doc.RootElement, "Max");
        Expect(snapshot.FiveHour?.UsedPercent, 0.25);
        Expect(snapshot.FiveHour?.ResetAt, new DateTimeOffset(2026, 10, 12, 3, 39, 55, TimeSpan.Zero));
        Expect(snapshot.Weekly, null);
        Expect(snapshot.Month?.UsedPercent, 0.9281);
        Expect(snapshot.MonthCode?.ResetAt, null);
        Expect(snapshot.Plan, "Max");
        Expect(snapshot.BoosterCents, null);
    }

    private static void ApiPayload()
    {
        using var doc = JsonDocument.Parse("""
            {"usages":{"limit_5h":{"used_ratio":"0.5","reset_time":"2026-10-11T15:00:00Z"},
                       "limit_month_total":{"used_ratio":0.4}},
             "boosterWallet":{"balance":{"type":"BOOSTER","amount":"5000000000","amountLeft":"1234000000"},
                              "monthlyUsed":{"priceInCents":0,"currency":"CNY"}}}
            """);
        var snapshot = KimiUsageFetcher.FromApiPayload(doc.RootElement, null);
        Expect(snapshot.FiveHour?.UsedPercent, 0.5);
        Expect(snapshot.Weekly, null);
        Expect(snapshot.Month?.UsedPercent, 0.4);
        Expect(snapshot.BoosterCents, 1234L);
        Expect(snapshot.BoosterCurrency, "CNY");
    }

    private static void ClampsRatio()
    {
        using var doc = JsonDocument.Parse("""{"usages":{"limit_7d":{"used_ratio":1.3}}}""");
        Expect(KimiUsageFetcher.FromApiPayload(doc.RootElement, null).Weekly?.UsedPercent, 1.0);
    }

    private static void FreshLoopback() =>
        Expect(Server("127.0.0.1", 52096, 30), "http://127.0.0.1:52096/api/v1");

    private static void RemoteRefused() => Expect(Server("10.0.0.5", 52096, 30), null);

    private static void StaleRefused() => Expect(Server("127.0.0.1", 52096, 600), null);

    private static void BadPortRefused() => Expect(Server("127.0.0.1", 70000, 30), null);

    private static string? Server(string host, int port, int heartbeatSecondsAgo)
    {
        var heartbeat = Now.AddSeconds(-heartbeatSecondsAgo).ToUnixTimeMilliseconds();
        using var doc = JsonDocument.Parse($$"""{"host":"{{host}}","port":{{port}},"heartbeat_at":{{heartbeat}}}""");
        return KimiUsageFetcher.ServerBaseUrl(doc.RootElement, Now);
    }

    /// The desktop server's GET /sessions page, as its schema defines it
    /// (Kimi Code 2.2, kap-server's sessionSchema).
    private static void LiveParse()
    {
        using var doc = JsonDocument.Parse("""
            {"code":0,"data":{"items":[
              {"id":"session_a","busy":true,"main_turn_active":true,"pending_interaction":"none"},
              {"id":"session_b","busy":false,"pending_interaction":"question"},
              {"id":"","busy":true}],"has_more":false}}
            """);
        var reports = KimiLiveSessions.Parse(doc.RootElement);
        Expect(reports.Count, 2);
        Expect(reports["session_a"], new KimiLiveSessions.Report(true, "none"));
        Expect(reports["session_b"], new KimiLiveSessions.Report(false, "question"));
        using var failed = JsonDocument.Parse("""{"code":40100,"msg":"unauthorized"}""");
        Expect(KimiLiveSessions.Parse(failed.RootElement).Count, 0);
    }

    private static void LiveApproval()
    {
        var session = KimiLiveSessions.Apply(Session(ActivityState.Working), new(true, "approval"));
        Expect(session.Status, ActivityState.Working);
        Expect(session.PendingPrompt?.Phase, AgentPhase.Approving);
    }

    private static void LiveQuestion() =>
        Expect(KimiLiveSessions.Apply(Session(ActivityState.Idle), new(false, "question")).PendingPrompt?.Phase, AgentPhase.Asking);

    private static void LiveActive() =>
        Expect(KimiLiveSessions.Apply(Session(ActivityState.Idle), new(true, "none")).Status, ActivityState.Working);

    /// CLI sessions are in the desktop app's list too, always idle there.
    private static void LiveIdle()
    {
        var session = KimiLiveSessions.Apply(Session(ActivityState.Working), new(false, "none"));
        Expect(session.Status, ActivityState.Working);
        Expect(session.PendingPrompt, null);
    }

    private static ScannedSession Session(ActivityState status) =>
        new(TriggerTool.Kimi, "a", "", "a", Now, status, null, null, SessionLaunchTarget.Cli);

    private static void Expect<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}, got {actual}");
    }
}
