using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

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
            ("local quota: a ratio above one clamps", ClampsRatio),
            ("no local server: unavailable, and the stored token is never used", NoServerNoRequest),
            ("server: a fresh loopback instance is used", FreshLoopback),
            ("server: a remote host is refused", RemoteRefused),
            ("server: a stale heartbeat is refused", StaleRefused),
            ("server: a bad port is refused", BadPortRefused),
            ("server: localhost is pinned to 127.0.0.1 and ::1 is refused", LocalhostPinned),
            ("server: the token goes only to the registered pid's port", TokenNeedsPortOwner),
            ("server: the local client uses no proxy and follows no redirect", LoopbackClientHardened),
            ("live: the desktop server's session page parses", LiveParse),
            ("live: an approval waits on the user", LiveApproval),
            ("live: a question asks the user", LiveQuestion),
            ("live: an active turn works while its log is quiet", LiveActive),
            ("live: an idle report never stops a session", LiveIdle),
            ("sign-in stamp: two servers' heartbeats don't read as a new sign-in", HeartbeatsKeepTheStamp),
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

    private static void ClampsRatio()
    {
        using var doc = JsonDocument.Parse("""{"usages":{"limit7d":{"usedRatio":1.3}}}""");
        Expect(KimiUsageFetcher.FromLocalQuota(doc.RootElement, null).Weekly?.UsedPercent, 1.0);
    }

    // 本机服务没开时，哪怕磁盘上有没过期的 access token，也不发任何请求、
    // 不进入“需要登录”：只留下旧快照。
    private static void NoServerNoRequest()
    {
        var home = Directory.CreateTempSubdirectory("limen-kimi-");
        var previousHome = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home.FullName);
            Directory.CreateDirectory(Path.Combine(home.FullName, "credentials"));
            var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            File.WriteAllText(Path.Combine(home.FullName, "credentials", "kimi-code.json"),
                $$"""{"access_token":"fake-token","expires_at":{{expires}}}""");
            var outcome = KimiUsageFetcher.Fetch().GetAwaiter().GetResult();
            // 旧代码在这里会拿 token 去连外网，得到 Failed 或 Success。
            Expect(outcome is KimiUsageFetcher.Outcome.ServerUnavailable, "no local server means unavailable, not failed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousHome);
            home.Delete(true);
        }
    }

    private static void FreshLoopback() =>
        Expect(Server("127.0.0.1", 52096, 30), "http://127.0.0.1:52096/api/v1");

    private static void RemoteRefused() => Expect(Server("10.0.0.5", 52096, 30), null);

    private static void StaleRefused() => Expect(Server("127.0.0.1", 52096, 600), null);

    private static void BadPortRefused() => Expect(Server("127.0.0.1", 70000, 30), null);

    private static void LocalhostPinned()
    {
        Expect(Server("localhost", 52096, 30), "http://127.0.0.1:52096/api/v1");
        Expect(Server("::1", 52096, 30), null);
    }

    private static void LoopbackClientHardened()
    {
        // The handler is private to HttpClient; a redirect to another host proves it is not followed.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(() =>
        {
            using var client = listener.AcceptTcpClient();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = stream.Read(buffer, 0, buffer.Length);
            var reply = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(reply, 0, reply.Length);
        });
        using var response = Http.LoopbackClient.GetAsync($"http://127.0.0.1:{port}/x").GetAwaiter().GetResult();
        served.GetAwaiter().GetResult();
        Expect((int)response.StatusCode, 302);
        using var handler = Http.LoopbackHandler();
        Expect(handler.UseProxy, false);
        Expect(handler.AllowAutoRedirect, false);
    }

    private static void TokenNeedsPortOwner()
    {
        var home = Directory.CreateTempSubdirectory("limen-kimi-");
        var previousHome = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        var previousOwner = AntigravityLanguageServer.ResolvePortOwner;
        try
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home.FullName);
            Directory.CreateDirectory(Path.Combine(home.FullName, "server", "instances"));
            File.WriteAllText(Path.Combine(home.FullName, "server.token"), "secret-token");
            var instance = Path.Combine(home.FullName, "server", "instances", "one.json");
            var heartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            void Register(string pid) => File.WriteAllText(instance,
                $$"""{{{pid}}"host":"127.0.0.1","port":52096,"heartbeat_at":{{heartbeat}}}""");
            var me = Environment.ProcessId;

            Register($"\"pid\":{me},");
            AntigravityLanguageServer.ResolvePortOwner = _ => me;
            Expect(KimiUsageFetcher.LocalServer()?.BaseUrl, "http://127.0.0.1:52096/api/v1");
            Expect(KimiUsageFetcher.LocalServer()?.Token, "secret-token");

            AntigravityLanguageServer.ResolvePortOwner = _ => me + 1;
            Expect(KimiUsageFetcher.LocalServer(), null);
            AntigravityLanguageServer.ResolvePortOwner = _ => null;
            Expect(KimiUsageFetcher.LocalServer(), null);

            Register("");
            AntigravityLanguageServer.ResolvePortOwner = _ => me;
            Expect(KimiUsageFetcher.LocalServer(), null);
        }
        finally
        {
            AntigravityLanguageServer.ResolvePortOwner = previousOwner;
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previousHome);
            home.Delete(true);
        }
    }

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

    private static void HeartbeatsKeepTheStamp()
    {
        var home = Path.Combine(Path.GetTempPath(), "agentisland-kimi-" + Guid.NewGuid().ToString("N"));
        var instances = Path.Combine(home, "server", "instances");
        Directory.CreateDirectory(instances);
        try
        {
            var a = Path.Combine(instances, "01JA.json");
            var b = Path.Combine(instances, "01JB.json");
            File.WriteAllText(a, "{}");
            File.WriteAllText(b, "{}");
            var t = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(a, t);
            File.SetLastWriteTimeUtc(b, t.AddSeconds(-7));
            var stamp = KimiUsageFetcher.SignInStamp(home);
            // B beats its heartbeat seven seconds later, and is now the newest.
            File.SetLastWriteTimeUtc(b, t.AddSeconds(8));
            Expect(KimiUsageFetcher.SignInStamp(home), stamp);
            File.WriteAllText(Path.Combine(instances, "01JC.json"), "{}");
            Expect(KimiUsageFetcher.SignInStamp(home) != stamp, "a server that opens is still a new sign-in");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
