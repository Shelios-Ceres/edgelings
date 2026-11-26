using System.Net;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The Grok card's side of the read-only quota: what each outcome shows, and
/// when a change to auth.json sends the next request. A store of its own per
/// test, its login in a temporary folder, the network a counter.
public static class GrokUsageStoreTests
{
    private const string Scope = "https://auth.x.ai::client-1";
    private const string Billing = """{"config":{"creditUsagePercent":10}}""";

    public static void RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentisland-grokstore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previousFile = Preferences.FileOverride;
        try
        {
            Preferences.FileOverride = Path.Combine(directory, "settings.json");
            Preferences.Forget();
            Step("the card says to open grok on an expired or refused token, and keeps its numbers", CaptionsAndNumbers);
            Step("a change to auth.json while waiting sends one request; the same file again sends none", () => ChangeSendsOnce(directory));
            Step("a change held back by the ten-second floor is found again, not swallowed", () => FloorDoesNotSwallow(directory));
            Step("a change is not taken as seen while the card is hidden or grok is not detected", () => HiddenAndUndetected(directory));
            Step("nothing is waited on after a good read, so a changed file sends nothing by itself", () => GoodReadWaitsOnNothing(directory));
            Step("after a restart the refused token is not sent, and the card still asks to open grok", () => RestartStaysQuiet(directory));
            Step("a file deleted and made again is read at the next tick, even though detection last saw it gone", () => DeletedAndMadeAgain(directory));
            Step("a file with no login in it is not fetched again and again", () => EmptyFileIsNotPolled(directory));
            Console.WriteLine("GrokUsageStoreTests GREEN");
        }
        finally
        {
            GrokUsageFetcher.ResetStateForTests();
            Preferences.FileOverride = previousFile;
            Preferences.Forget();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static void Step(string name, Action test)
    {
        GrokUsageFetcher.ResetStateForTests();
        Preferences.Remove("GrokUsageStore.lastSnapshot.v1");
        test();
        Console.WriteLine($"PASS grok store: {name}");
    }

    private sealed class Rig
    {
        public readonly string Home;
        public readonly string AuthFile;
        public readonly GrokUsageStore Store;
        public int Requests;
        public int Fetches;
        public bool Shown = true;
        public bool Detected = true;
        public Func<string, HttpStatusCode> Status = _ => HttpStatusCode.OK;

        public Rig(string root, string name)
        {
            Home = Path.Combine(root, name);
            Directory.CreateDirectory(Home);
            AuthFile = Path.Combine(Home, "auth.json");
            Store = Make();
        }

        public GrokUsageStore Make()
        {
            var client = new HttpClient(new Handler(request =>
            {
                Interlocked.Increment(ref Requests);
                var status = Status(request.Headers.Authorization!.Parameter!);
                return status == HttpStatusCode.OK
                    ? new(status) { Content = new StringContent(Billing) }
                    : new(status);
            }));
            return new GrokUsageStore
            {
                AuthPath = () => AuthFile,
                IsShown = () => Shown,
                IsDetected = () => Detected,
                Redetect = () => Detected = File.Exists(AuthFile),
                Fetcher = (path, ct) =>
                {
                    Interlocked.Increment(ref Fetches);
                    return GrokUsageFetcher.Fetch(path, client, ct);
                },
            };
        }

        public void WriteLogin(string access, int stampSeconds, TimeSpan? life = null)
        {
            File.WriteAllText(AuthFile, $$"""
                { "{{Scope}}": { "key": "{{access}}", "expires_at": {{DateTimeOffset.Now.Add(life ?? TimeSpan.FromHours(3)).ToUnixTimeMilliseconds()}}, "auth_mode": "oidc", "email": "a@example.test" } }
                """);
            File.SetLastWriteTimeUtc(AuthFile, new DateTime(2026, 11, 26, 3, 0, 0, DateTimeKind.Utc).AddSeconds(stampSeconds));
        }

        /// Waits for the fetch the last kick started.
        public void Settle(GrokUsageStore? store = null) => (store ?? Store).LastRun?.GetAwaiter().GetResult();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }

    private static readonly GrokBillingSnapshot Numbers = new(40, DateTimeOffset.Now.AddDays(3), null, null, null, null);

    private static void CaptionsAndNumbers()
    {
        var rig = new Rig(Path.GetTempPath(), $"agentisland-grokstore-c-{Guid.NewGuid():N}");
        try
        {
            var store = rig.Store;
            store.Apply(new GrokUsageFetcher.Outcome.Success(Numbers));
            Expect(store.ErrorCaption is null && store.Snapshot is not null, "a good read shows numbers and no caption");
            store.Apply(new GrokUsageFetcher.Outcome.SyncRequired("1:1"));
            Expect(store.ErrorCaption, L10n.Tr("Open grok to sync"));
            Expect(store.Snapshot is not null, "the numbers stay under it");
            store.Apply(new GrokUsageFetcher.Outcome.Success(Numbers));
            Expect(store.ErrorCaption is null, "and the next good read clears it");
            store.Apply(new GrokUsageFetcher.Outcome.NotInstalled());
            Expect(store.Snapshot is null && store.ErrorCaption is null, "no file shows nothing");
        }
        finally
        {
            try { Directory.Delete(rig.Home, recursive: true); } catch { }
        }
    }

    private static void ChangeSendsOnce(string root)
    {
        var rig = new Rig(root, "once");
        rig.Status = _ => HttpStatusCode.Unauthorized;
        rig.WriteLogin("old", 10);
        rig.Store.KickRefresh();
        rig.Settle();
        Expect(rig.Store.ErrorCaption, L10n.Tr("Open grok to sync"));
        var sent = rig.Requests;
        Expect(sent > 0, "refused once");

        rig.Store.NoticeCredentialChange();
        Expect(rig.Requests, sent);

        rig.Status = _ => HttpStatusCode.OK;
        rig.WriteLogin("new", 20);
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.NoticeCredentialChange();
        rig.Settle();
        Expect(rig.Requests > sent, "the changed file was read");
        Expect(rig.Store.ErrorCaption is null && rig.Store.Snapshot is not null, "and the card has numbers again");
        var after = rig.Requests;
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.NoticeCredentialChange();
        Expect(rig.Requests, after);
    }

    private static void FloorDoesNotSwallow(string root)
    {
        var rig = new Rig(root, "floor");
        rig.Status = _ => HttpStatusCode.Unauthorized;
        rig.WriteLogin("old", 10);
        rig.Store.KickRefresh();
        rig.Settle();
        var sent = rig.Requests;

        // Within ten seconds of that attempt a second change is held back by the floor...
        rig.WriteLogin("newer", 20);
        rig.Store.NoticeCredentialChange();
        Expect(rig.Requests, sent);
        // ...and is still a change on the next tick, once the floor has passed.
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.NoticeCredentialChange();
        rig.Settle();
        Expect(rig.Requests > sent, "the held-back change was found again");
    }

    private static void HiddenAndUndetected(string root)
    {
        var rig = new Rig(root, "hidden");
        rig.Status = _ => HttpStatusCode.Unauthorized;
        rig.WriteLogin("old", 10);
        rig.Store.KickRefresh();
        rig.Settle();
        var sent = rig.Requests;
        rig.WriteLogin("new", 20);

        rig.Shown = false;
        rig.Store.NoticeCredentialChange();
        Expect(rig.Requests, sent);
        rig.Shown = true;
        rig.Detected = false;
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.NoticeCredentialChange();
        Expect(rig.Requests, sent);
        rig.Detected = true;
        rig.Store.NoticeCredentialChange();
        rig.Settle();
        Expect(rig.Requests > sent, "once shown and detected the same change is read");
    }

    private static void GoodReadWaitsOnNothing(string root)
    {
        var rig = new Rig(root, "good");
        rig.WriteLogin("live", 10);
        rig.Store.KickRefresh();
        rig.Settle();
        var sent = rig.Requests;
        Expect(sent > 0 && rig.Store.ErrorCaption is null, "read");
        rig.WriteLogin("live-again", 20);
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.NoticeCredentialChange();
        Expect(rig.Requests, sent);
    }

    private static void RestartStaysQuiet(string root)
    {
        var rig = new Rig(root, "restart");
        rig.Status = _ => HttpStatusCode.Unauthorized;
        rig.WriteLogin("old", 10);
        rig.Store.KickRefresh();
        rig.Settle();
        var sent = rig.Requests;

        GrokUsageFetcher.ForgetMemoryForTests();
        var restarted = rig.Make();
        restarted.KickRefresh();
        rig.Settle(restarted);
        Expect(rig.Requests, sent);
        Expect(restarted.ErrorCaption, L10n.Tr("Open grok to sync"));

        // The first look after a restart learns nothing it must act on: the same file, no request.
        restarted.ForgetAttemptFloorForTests();
        restarted.NoticeCredentialChange();
        Expect(rig.Requests, sent);

        rig.Status = _ => HttpStatusCode.OK;
        rig.WriteLogin("new", 20);
        restarted.ForgetAttemptFloorForTests();
        restarted.NoticeCredentialChange();
        rig.Settle(restarted);
        Expect(rig.Requests > sent && restarted.ErrorCaption is null, "a changed file after a restart is read");
    }

    private static void DeletedAndMadeAgain(string root)
    {
        var rig = new Rig(root, "again");
        rig.WriteLogin("live", 10);
        rig.Store.KickRefresh();
        rig.Settle();
        Expect(rig.Store.Snapshot is not null, "read");

        File.Delete(rig.AuthFile);
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.KickRefresh();
        rig.Settle();
        Expect(rig.Store.Snapshot is null, "no file, no numbers");
        // The poll's detection sees it gone; the file then comes back before the next poll.
        rig.Detected = false;
        var sent = rig.Requests;

        rig.WriteLogin("live", 10);
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.NoticeCredentialChange();
        rig.Settle();
        Expect(rig.Detected, "Grok was looked for again");
        Expect(rig.Requests > sent && rig.Store.Snapshot is not null, "the file made again is read at once");
    }

    private static void EmptyFileIsNotPolled(string root)
    {
        var rig = new Rig(root, "empty");
        File.WriteAllText(rig.AuthFile, "{}");
        rig.Store.KickRefresh();
        rig.Settle();
        Expect(rig.Fetches, 1);
        Expect(rig.Store.Snapshot is null && rig.Store.ErrorCaption is null, "nothing to show");
        for (var tick = 0; tick < 5; tick++)
        {
            rig.Store.ForgetAttemptFloorForTests();
            rig.Store.NoticeCredentialChange();
            rig.Settle();
        }
        Expect(rig.Fetches, 1);
        Expect(rig.Requests, 0);

        rig.WriteLogin("live", 10);
        rig.Store.ForgetAttemptFloorForTests();
        rig.Store.NoticeCredentialChange();
        rig.Settle();
        Expect(rig.Fetches, 2);
        Expect(rig.Store.Snapshot is not null, "a login arriving is read");
    }
}
