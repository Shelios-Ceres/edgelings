using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// A Grok token refresh answers the login whose refresh token it sent. If the
/// user logged in again while it was in flight, the answer must not overwrite
/// the newer login. Synthetic tokens only.
public static class GrokAuthFileTests
{
    private const string Scope = "https://auth.x.ai::client-1";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private const string Response = """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""";

    public static void RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previousClock = RefreshGrants.Clock;
        var previousLog = CredentialRefreshLog.Write;
        var previousBackoff = CredentialWriteBacks.Backoff;
        try
        {
            // A held pair is retried only by the tests that look at the retries.
            CredentialWriteBacks.Backoff = Array.Empty<TimeSpan>();
            RefreshGrants.Clock = () => Clock;
            CredentialRefreshLog.Write = line => { lock (LogLines) LogLines.Add(line); return true; };
            GrokUsageFetcher.ResetStateForTests();
            RefreshDestinations(Path.Combine(directory, "requests.json"));
            MatchingLoginIsWritten(Path.Combine(directory, "match.json"));
            ChangedLoginIsLeftAlone(Path.Combine(directory, "changed.json"));
            HeldFileIsWrittenOnceReleased(Path.Combine(directory, "held.json"));
            Step("a file caught mid-rewrite is read again", () => MidRewriteIsReadAgain(Path.Combine(directory, "rewrite.json")));
            Step("a held-back refresh reports a temporary failure, not a lost sign-in", () => HeldBackIsNotReauth(Path.Combine(directory, "heldback.json")));
            Step("an ambiguous refresh is replayed once after ten minutes", () => AmbiguousExpires(Path.Combine(directory, "expiry.json")));
            Step("a rotated pair the file would not take is used from memory and written on a later poll", () => WriteBackFailsThenLands(Path.Combine(directory, "pending.json")));
            Step("a pending write-back is dropped when the file has changed", () => PendingDroppedWhenFileChanged(Path.Combine(directory, "changed2.json")));
            Step("a pending write-back is dropped, and the file not recreated, when it was removed", () => PendingDroppedWhenFileRemoved(Path.Combine(directory, "removed.json")));
            Step("a reply for a file that is gone is not written back", () => RemovedFileIsNotRecreated(Path.Combine(directory, "gone.json")));
            Step("the refresh log holds no token", LogHoldsNoToken);
            Step("a refresh that timed out reads as a temporary failure; a refused grant as a lost sign-in", TransientIsNotReauth);
            Step("a refresh is not sent for a login the file has moved on from during discovery", DiscoveryRacesFileChange);
            Step("an ambiguous refresh is retried after 10, 30 and 60 minutes, reported as given up, and tried again every hour", GivesUp);
            Step("a pair the file would not take is written again on its own back-off", BackgroundRetryWrites);
            Step("when the back-off runs out the quota card says the sign-in was not saved, until it is", ExhaustedBackoffIsVisible);
            Step("a pair still held is written once more on a normal exit", FlushOnExit);
            Step("a file with no login drops the pending pair, a file that cannot be read keeps it", NoLoginDropsUnreadableKeeps);
            Step("a login made while a write waits to retry is not overwritten", NewLoginDuringRetryWins);
            Step("the pending pair keeps only what a write needs", PendingKeepsNoIdToken);
            Step("a swap that failed half way is recovered on the next poll and the pending pair is not lost", HalfFailedSwapIsRecovered);
            Step("a refresh whose connects all run out is never sent, not ambiguous, however many addresses there are", BlackholedAddressesAreNotAmbiguous);
            Step("a sign-out is not undone, and its login not refreshed, by a leftover backup while a pair is pending", SignOutIsNotUndoneByALeftoverBackup);
            Step("a scene left by a cut-short swap is forgotten once the file is back, so a later sign-out stays", SignOutAfterAnOldCutShortSwap);
            Console.WriteLine("GrokAuthFileTests GREEN");
        }
        finally
        {
            RefreshGrants.Clock = previousClock;
            CredentialRefreshLog.Write = previousLog;
            CredentialWriteBacks.Backoff = previousBackoff;
            GrokUsageFetcher.ResetStateForTests();
            CredentialRefreshLog.ResetForTests();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static DateTimeOffset Clock = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<string> LogLines = new();

    /// The log as it stands: a retry loop may be adding to it from its own thread.
    private static string[] Logged()
    {
        lock (LogLines) return LogLines.ToArray();
    }

    private static void Step(string name, Action test)
    {
        GrokUsageFetcher.ResetStateForTests();
        CredentialRefreshLog.ResetForTests();
        lock (LogLines) LogLines.Clear();
        Clock = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        test();
        Console.WriteLine($"PASS grok credentials: {name}");
    }

    private static string Auth(string access, string refresh, string email) => $$"""
        {
          "{{Scope}}": {
            "key": "{{access}}",
            "refresh_token": "{{refresh}}",
            "expires_at": 1000,
            "auth_mode": "oidc",
            "email": "{{email}}",
            "extra": "kept"
          }
        }
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }

    private static void RefreshDestinations(string path)
    {
        int attempted = 0;
        File.WriteAllText(path, Auth("old", "ambiguous-grok", "a@example.test"));
        using var denied = new HttpClient(new Handler(_ => new(System.Net.HttpStatusCode.Unauthorized)));
        using var ambiguous = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return new(System.Net.HttpStatusCode.NotFound);
            attempted++;
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "fixture lost response");
        }));
        for (var poll = 0; poll < 2; poll++) GrokUsageFetcher.Fetch(path, denied, ambiguous, default).GetAwaiter().GetResult();
        Expect(attempted, 1);
        File.WriteAllText(path, Auth("other", "fresh-grok", "a@example.test"));
        GrokUsageFetcher.Fetch(path, denied, ambiguous, default).GetAwaiter().GetResult();
        Expect(attempted, 2);

        foreach (var (issuer, discovered, posts, discoveries) in new[]
        {
            ("https://auth.x.ai", "https://auth.x.ai/token", 1, 1),
            ("https://auth.x.ai", "https://other.x.ai/token", 0, 1),
            ("https://auth.x.ai", "http://auth.x.ai/token", 0, 1),
            ("https://auth.x.ai", "https://auth.x.ai:444/token", 0, 1),
            ("https://untrusted.invalid", "https://untrusted.invalid/token", 0, 0),
        })
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(Auth("old", "fake", "a@example.test"))!;
            doc[Scope]!["oidc_issuer"] = issuer;
            File.WriteAllText(path, doc.ToJsonString());
            int sent = 0, lookups = 0;
            using var billing = new HttpClient(new Handler(_ => new(System.Net.HttpStatusCode.Unauthorized)));
            using var oauth = new HttpClient(new Handler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    sent++;
                    return new(System.Net.HttpStatusCode.TemporaryRedirect);
                }
                lookups++;
                return new(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { token_endpoint = discovered })) };
            }));
            _ = GrokUsageFetcher.Fetch(path, billing, oauth, default).GetAwaiter().GetResult();
            Expect(sent, posts);
            Expect(lookups, discoveries);
        }
    }

    private static void MatchingLoginIsWritten(string path)
    {
        File.WriteAllText(path, Auth("old-access", "old-refresh", "a@example.test"));
        var entry = GrokAuthFile.ApplyRefreshResponse(Response, path, Scope, "old-refresh", Now);
        Expect(entry is not null, "a matching file is written");
        Expect(entry!.AccessToken, "new-access");
        Expect(entry.RefreshToken, "new-refresh");
        Expect(entry.ExpiresAt, Now.AddSeconds(3600));
        var saved = GrokAuthFile.LoadEntry(path)!;
        Expect(saved.AccessToken, "new-access");
        Expect(saved.RefreshToken, "new-refresh");
        Expect(saved.Email, "a@example.test");
        Expect(File.ReadAllText(path).Contains("\"extra\": \"kept\""), "fields the app does not know survive");
        Console.WriteLine("PASS a refresh for the login still in the file is written back");
    }

    private static void ChangedLoginIsLeftAlone(string path)
    {
        // The user logged in again (another account, same scope) while the
        // request for "old-refresh" was in flight.
        File.WriteAllText(path, Auth("fresh-access", "fresh-refresh", "b@example.test"));
        var before = File.ReadAllBytes(path);
        var entry = GrokAuthFile.ApplyRefreshResponse(Response, path, Scope, "old-refresh", Now);
        Expect(File.ReadAllBytes(path).SequenceEqual(before), "the file is left byte for byte as it was");
        Expect(entry is not null, "the current entry is returned");
        Expect(entry!.AccessToken, "fresh-access");
        Expect(entry.RefreshToken, "fresh-refresh");
        Expect(entry.Email, "b@example.test");
        Console.WriteLine("PASS a refresh for a login that has since changed leaves the file alone and returns the current entry");
    }

    private static void HeldFileIsWrittenOnceReleased(string path)
    {
        File.WriteAllText(path, Auth("old-access", "old-refresh", "a@example.test"));
        var released = HeldFile.For(path, TimeSpan.FromMilliseconds(150));
        var entry = GrokAuthFile.ApplyRefreshResponse(Response, path, Scope, "old-refresh", Now);
        released.Wait();
        Expect(entry?.RefreshToken == "new-refresh", "the rotated token is written once the other program lets go");
        Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "new-refresh", "and it is on disk");
        Console.WriteLine("PASS a refresh waits out a program holding auth.json open instead of dropping the rotated token");
    }

    /// Billing says 401 to every token but `good`; the oauth endpoint answers a refresh with `reply`.
    private static (HttpClient Billing, HttpClient Oauth, List<string> Tokens, List<string> Posted) Clients(
        Func<string, bool> good, Func<HttpResponseMessage> reply)
    {
        var tokens = new List<string>();
        var posted = new List<string>();
        var billing = new HttpClient(new Handler(request =>
        {
            var token = request.Headers.Authorization!.Parameter!;
            if (request.RequestUri!.Query.Contains("credits")) tokens.Add(token);
            return good(token)
                ? new(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"config":{"creditUsagePercent":10}}""") }
                : new(System.Net.HttpStatusCode.Unauthorized);
        }));
        var oauth = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return new(System.Net.HttpStatusCode.NotFound);
            posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return reply();
        }));
        return (billing, oauth, tokens, posted);
    }

    private static HttpResponseMessage Tokens(string json = Response) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };

    private static GrokUsageFetcher.Outcome Fetch(string path, HttpClient billing, HttpClient oauth) =>
        GrokUsageFetcher.Fetch(path, billing, oauth, default).GetAwaiter().GetResult();

    private static void MidRewriteIsReadAgain(string path)
    {
        File.WriteAllText(path, Auth("access", "refresh", "a@example.test"));
        var whole = File.ReadAllText(path);
        File.WriteAllText(path, "");
        var finished = Task.Run(async () =>
        {
            await Task.Delay(60);
            File.WriteAllText(path, whole);
        });
        var entry = GrokAuthFile.LoadEntry(path);
        finished.Wait();
        Expect(entry?.AccessToken == "access", "the file read whole is the one used, not a missing login");
        File.WriteAllText(path, "{\"half\":");
        Expect(GrokAuthFile.LoadEntry(path) is null, "a file that never parses reads as no entry");
    }

    private static void HeldBackIsNotReauth(string path)
    {
        File.WriteAllText(path, Auth("old", "held-back-grok", "a@example.test"));
        var (billing, oauth, _, posted) = Clients(_ => false, () => throw new HttpRequestException(HttpRequestError.ResponseEnded, "fixture lost reply"));
        Fetch(path, billing, oauth);
        var second = Fetch(path, billing, oauth);
        Expect(posted.Count, 1);
        Expect(second is GrokUsageFetcher.Outcome.Failed { Message: var message } && message.Length > 0 && message != "parse error",
            $"a refresh that is held back is a temporary failure, got {second}");
        Expect(second is not GrokUsageFetcher.Outcome.ReauthRequired, "and not a lost sign-in");
        Expect(Logged().Any(line => line.Contains("grok: ambiguous: connection lost")), "the ambiguous reason is logged");
    }

    private static void AmbiguousExpires(string path)
    {
        File.WriteAllText(path, Auth("old", "expiry-grok", "a@example.test"));
        var (billing, oauth, _, posted) = Clients(_ => false, () => new(System.Net.HttpStatusCode.ServiceUnavailable));
        Fetch(path, billing, oauth);
        Clock = Clock.AddMinutes(9);
        Fetch(path, billing, oauth);
        Expect(posted.Count, 1);
        Clock = Clock.AddMinutes(1);
        Fetch(path, billing, oauth);
        Expect(posted.Count, 2);
        Fetch(path, billing, oauth);
        Expect(posted.Count, 2);
        Expect(Logged().Any(line => line.Contains("grok: ambiguous: HTTP 503")), "the status is logged as the reason");
    }

    private static void WriteBackFailsThenLands(string path)
    {
        File.WriteAllText(path, Auth("old", "old-refresh", "a@example.test"));
        var (billing, oauth, tokens, posted) = Clients(token => token == "new-access", () => Tokens());
        var held = HeldFile.For(path, TimeSpan.FromMilliseconds(1200));
        var first = Fetch(path, billing, oauth);
        Expect(first is GrokUsageFetcher.Outcome.Success, "the quota is fetched with the new token");
        Expect(tokens.LastOrDefault() == "new-access", $"the new access token fetches the quota, got {tokens.LastOrDefault()}");
        Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "old-refresh", "the file could not be written yet");
        Expect(GrokUsageFetcher.HasPendingWriteBack(path), "the new pair is held");
        held.Wait();
        tokens.Clear();
        Fetch(path, billing, oauth);
        Expect(posted.Count, 1);
        Expect(tokens.LastOrDefault() == "new-access", "the next poll uses the new token without refreshing");
        Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "new-refresh", "the held pair is written once the file is free");
        Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "and nothing is pending any more");
    }

    private static void PendingDroppedWhenFileChanged(string path)
    {
        File.WriteAllText(path, Auth("old", "old-refresh", "a@example.test"));
        var (billing, oauth, tokens, _) = Clients(_ => true, () => Tokens());
        var held = HeldFile.For(path, TimeSpan.FromMilliseconds(1000));
        Fetch(path, billing, oauth);
        Expect(GrokUsageFetcher.HasPendingWriteBack(path), "the pair is held");
        held.Wait();
        // `grok login` again, as someone else.
        File.WriteAllText(path, Auth("fresh-access", "fresh-refresh", "b@example.test").Replace("\"expires_at\": 1000", "\"expires_at\": 99999999999999"));
        tokens.Clear();
        Fetch(path, billing, oauth);
        Expect(tokens.LastOrDefault() == "fresh-access", "the login in the file is the one used");
        Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "fresh-refresh", "the held pair is not written over it");
        Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "and it is dropped");
        Expect(Logged().Any(line => line.Contains("grok: pending write-back dropped, file changed")), "the drop is logged");
    }

    private static void PendingDroppedWhenFileRemoved(string path)
    {
        File.WriteAllText(path, Auth("old", "old-refresh", "a@example.test"));
        var (billing, oauth, tokens, _) = Clients(_ => true, () => Tokens());
        var held = HeldFile.For(path, TimeSpan.FromMilliseconds(1000));
        Fetch(path, billing, oauth);
        Expect(GrokUsageFetcher.HasPendingWriteBack(path), "the pair is held");
        held.Wait();
        File.Delete(path);
        tokens.Clear();
        var outcome = Fetch(path, billing, oauth);
        Expect(outcome is GrokUsageFetcher.Outcome.NotInstalled, "a signed-out login is not revived from memory");
        Expect(tokens.Count, 0);
        Expect(!File.Exists(path), "the file is not recreated");
        Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "and nothing stays pending");
    }

    private static void RemovedFileIsNotRecreated(string path)
    {
        var status = GrokAuthFile.ApplyRefresh(Response, path, Scope, "old-refresh", Clock).Status;
        Expect(status == GrokAuthFile.ApplyStatus.LoggedOut && !File.Exists(path), "no file, no login, nothing created");
        File.WriteAllText(path, Auth("old", "old-refresh", "a@example.test"));
        Expect(GrokAuthFile.ApplyRefresh("<html>portal</html>", path, Scope, "old-refresh", Clock).Status == GrokAuthFile.ApplyStatus.BadResponse,
            "a reply that is not a token reply is told apart");
        Expect(GrokAuthFile.ApplyRefresh(Response, path, Scope, "old-refresh", Clock).Status == GrokAuthFile.ApplyStatus.Written, "a matching file is written");
        Expect(GrokAuthFile.LoadEntry(path)?.AccessToken == "new-access", "and holds the reply");
    }

    private static void LogHoldsNoToken()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-log-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, Auth("SECRET-ACCESS-OLD", "SECRET-REFRESH-OLD", "secret-person@example.test"));
            var (billing, oauth, _, _) = Clients(_ => true, () => Tokens(
                """{"access_token":"SECRET-ACCESS-NEW","refresh_token":"SECRET-REFRESH-NEW","expires_in":3600}"""));
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(1000));
            Fetch(path, billing, oauth);
            held.Wait();
            Fetch(path, billing, oauth);
            var log = string.Join("\n", LogLines);
            Expect(log.Contains("grok: write-back failed") && log.Contains("grok: pending write-back saved"), $"the events are logged, got {log}");
            Expect(!log.Contains("SECRET") && !log.Contains("secret-person") && !System.Text.RegularExpressions.Regex.IsMatch(log, "[0-9A-F]{16}"),
                "no token, fingerprint or account is logged");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private const string NeverExpires = "\"expires_at\": 99999999999999";

    private static string FreshAuth(string access, string refresh, string email) =>
        Auth(access, refresh, email).Replace("\"expires_at\": 1000", NeverExpires);

    private static void WaitUntil(Func<bool> condition, string what)
    {
        for (var waited = 0; waited < 8000 && !condition(); waited += 25) Thread.Sleep(25);
        Expect(condition(), what);
    }

    private static void TransientIsNotReauth()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-t-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, Auth("old", "transient-grok", "a@example.test"));
            var (billing, oauth, _, _) = Clients(_ => false, () => throw new HttpRequestException(HttpRequestError.ResponseEnded, "fixture"));
            var timedOut = Fetch(path, billing, oauth);
            Expect(timedOut is GrokUsageFetcher.Outcome.Failed, $"a lost reply is not a lost sign-in, got {timedOut}");
            File.WriteAllText(path, Auth("old", "refused-grok", "a@example.test"));
            var (billing2, refusing, _, _) = Clients(_ => false, () => new(System.Net.HttpStatusCode.BadRequest));
            Expect(Fetch(path, billing2, refusing) is GrokUsageFetcher.Outcome.ReauthRequired, "a refused grant is");
            File.WriteAllText(path, Auth("old", "limited-grok", "a@example.test"));
            var (billing3, limiting, _, _) = Clients(_ => false, () => new(System.Net.HttpStatusCode.TooManyRequests));
            Expect(Fetch(path, billing3, limiting) is GrokUsageFetcher.Outcome.Failed, "a rate limit is not");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void DiscoveryRacesFileChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-r-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, Auth("old", "race-grok", "a@example.test"));
            int posts = 0;
            using var billing = new HttpClient(new Handler(_ => new(System.Net.HttpStatusCode.Unauthorized)));
            using var oauth = new HttpClient(new Handler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    // The grok CLI refreshes while the discovery request is out.
                    File.WriteAllText(path, FreshAuth("cli-access", "cli-refresh", "a@example.test"));
                    return new(System.Net.HttpStatusCode.NotFound);
                }
                posts++;
                return Tokens();
            }));
            Fetch(path, billing, oauth);
            Expect(posts, 0);
            Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "cli-refresh", "the CLI's login is left as it is");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void GivesUp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-g-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, Auth("old", "giveup-grok", "a@example.test"));
            var (billing, oauth, _, posted) = Clients(_ => false, () => new(System.Net.HttpStatusCode.ServiceUnavailable));
            Fetch(path, billing, oauth);
            foreach (var minutes in new[] { 10, 30, 60 })
            {
                var before = posted.Count;
                Clock = Clock.AddMinutes(minutes).AddSeconds(-1);
                Fetch(path, billing, oauth);
                Expect(posted.Count, before);
                Clock = Clock.AddSeconds(1);
                Fetch(path, billing, oauth);
                Expect(posted.Count, before + 1);
            }
            Clock = Clock.AddDays(1);
            Fetch(path, billing, oauth);
            Expect(posted.Count, 5);
            var outcome = Fetch(path, billing, oauth);
            Expect(posted.Count, 5);
            Expect(outcome is GrokUsageFetcher.Outcome.Failed { Message: var message }
                && message == AgentIsland.Localization.L10n.Tr("Couldn't renew the sign-in; check the network or sign in again"), $"got {outcome}");
            Expect(Logged().Any(line => line.Contains("retries used up")), "giving up is logged");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string HeldSetup(string name, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-{name}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, Auth("old", "old-refresh", "a@example.test"));
        return path;
    }

    private static void BackgroundRetryWrites()
    {
        var previous = CredentialWriteBacks.Backoff;
        CredentialWriteBacks.Backoff = Enumerable.Repeat(TimeSpan.FromMilliseconds(30), 100).ToArray();
        HeldSetup("bg", out var path);
        try
        {
            var (billing, oauth, _, _) = Clients(token => token == "new-access", () => Tokens());
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(600));
            Fetch(path, billing, oauth);
            Expect(GrokUsageFetcher.HasPendingWriteBack(path), "the pair is held");
            WaitUntil(() => !GrokUsageFetcher.HasPendingWriteBack(path), "the pair is written without another poll");
            held.Wait();
            Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "new-refresh", "and it is on disk");
        }
        finally
        {
            CredentialWriteBacks.Backoff = previous;
            try { File.Delete(path); } catch { }
        }
    }

    private static void ExhaustedBackoffIsVisible()
    {
        var previous = CredentialWriteBacks.Backoff;
        CredentialWriteBacks.Backoff = new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) };
        HeldSetup("ex", out var path);
        try
        {
            var (billing, oauth, _, _) = Clients(token => token == "new-access", () => Tokens());
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(3500));
            Expect(Fetch(path, billing, oauth) is GrokUsageFetcher.Outcome.Success, "while the retries last the quota is shown as usual");
            WaitUntil(() => Logged().Any(line => line.Contains("write-back retries exhausted")), "the back-off runs out");
            var shown = Fetch(path, billing, oauth);
            Expect(shown is GrokUsageFetcher.Outcome.Failed { Message: var message }
                && message == AgentIsland.Localization.L10n.Tr("Couldn't save the refreshed sign-in; Grok may need to sign in again"), $"got {shown}");
            held.Wait();
            Expect(Fetch(path, billing, oauth) is GrokUsageFetcher.Outcome.Success, "once the pair is written the message goes away");
            Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "new-refresh", "and the file holds it");
        }
        finally
        {
            CredentialWriteBacks.Backoff = previous;
            try { File.Delete(path); } catch { }
        }
    }

    private static void FlushOnExit()
    {
        HeldSetup("fl", out var path);
        try
        {
            var (billing, oauth, _, _) = Clients(token => token == "new-access", () => Tokens());
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(900));
            Fetch(path, billing, oauth);
            Expect(GrokUsageFetcher.HasPendingWriteBack(path), "the pair is held");
            held.Wait();
            CredentialWriteBacks.FlushAll();
            Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "new-refresh", "the exit wrote it");
            Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "and nothing is pending");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void NoLoginDropsUnreadableKeeps()
    {
        HeldSetup("nl", out var path);
        try
        {
            var (billing, oauth, tokens, _) = Clients(_ => true, () => Tokens());
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(900));
            Fetch(path, billing, oauth);
            held.Wait();
            File.WriteAllText(path, "{\"half\":");
            tokens.Clear();
            Fetch(path, billing, oauth);
            Expect(GrokUsageFetcher.HasPendingWriteBack(path) && tokens.LastOrDefault() == "new-access", "a half-written file is a wait, not a sign-out");
            File.WriteAllText(path, "{}");
            tokens.Clear();
            var outcome = Fetch(path, billing, oauth);
            Expect(outcome is GrokUsageFetcher.Outcome.NotInstalled && tokens.Count == 0, "a file with no login is signed out");
            Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "the pair is dropped");
            Expect(File.ReadAllText(path) == "{}", "and the file is not touched");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void NewLoginDuringRetryWins()
    {
        HeldSetup("nw", out var path);
        try
        {
            var (billing, oauth, _, _) = Clients(_ => true, () =>
            {
                // `grok login` again while the write waits between its tries.
                var login = Task.Run(async () =>
                {
                    await Task.Delay(1);
                    File.WriteAllText(path, FreshAuth("fresh-access", "fresh-refresh", "b@example.test"));
                });
                return Tokens();
            });
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(1500));
            Fetch(path, billing, oauth);
            held.Wait();
            Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "fresh-refresh", "the new login is not overwritten by the old session's pair");
            Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "nothing is pending");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void PendingKeepsNoIdToken()
    {
        var reply = GrokAuthFile.ParseReply("""{"access_token":"a","refresh_token":"r","expires_in":3600,"id_token":"SECRET-ID-TOKEN"}""");
        Expect(reply is { AccessToken: "a", RefreshToken: "r", ExpiresInSeconds: 3600 }, "the fields a write needs are read");
        Expect(typeof(GrokAuthFile.TokenReply).GetProperties().All(property => property.Name is "AccessToken" or "RefreshToken" or "ExpiresInSeconds"),
            "and nothing else is kept");
    }

    private static void HalfFailedSwapIsRecovered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-hf-{Guid.NewGuid():N}", "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var previous = AtomicReplace.ReplaceFile;
        try
        {
            File.WriteAllText(path, Auth("old", "old-refresh", "a@example.test"));
            var (billing, oauth, tokens, _) = Clients(token => token == "new-access" || token == "fresh-access", () => Tokens());
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                File.Move(target, backup);
                Directory.CreateDirectory(target);
                throw new IOException("fixture");
            };
            Fetch(path, billing, oauth);
            Expect(GrokUsageFetcher.HasPendingWriteBack(path) && tokens.LastOrDefault() == "new-access", "the pair is used from memory and held");
            Directory.Delete(path);
            AtomicReplace.ReplaceFile = previous;
            tokens.Clear();
            var outcome = Fetch(path, billing, oauth);
            Expect(outcome is GrokUsageFetcher.Outcome.Success, $"the next poll is not read as a sign-out, got {outcome}");
            Expect(GrokAuthFile.LoadEntry(path)?.RefreshToken == "new-refresh", "the new pair is in the file again");
            Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "and nothing is pending");
        }
        finally
        {
            AtomicReplace.ReplaceFile = previous;
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    private static void SignOutAfterAnOldCutShortSwap()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-cs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "auth.json");
        var previous = AtomicReplace.ReplaceFile;
        try
        {
            File.WriteAllText(path, Auth("old", "old-refresh", "a@example.test"));
            var (billing, oauth, tokens, posted) = Clients(_ => true, () => Tokens());
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                File.Move(target, backup);
                Directory.CreateDirectory(target);
                throw new IOException("fixture");
            };
            Fetch(path, billing, oauth);
            Expect(GrokUsageFetcher.HasPendingWriteBack(path), "the pair is held, the swap left its copies");
            Directory.Delete(path);
            AtomicReplace.ReplaceFile = previous;
            // The grok CLI writes a login of its own back, which ends the pending pair and the scene.
            File.WriteAllText(path, FreshAuth("cli-access", "cli-refresh", "a@example.test"));
            Fetch(path, billing, oauth);
            Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "the pair is dropped");
            Expect(!Directory.GetFiles(directory).Any(file => file.Contains(".tmp-") || file.Contains(".bak-")), "and the copies are deleted");
            // A plain write that fails again, and then the user signs out.
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(900));
            File.WriteAllText(path, Auth("cli-access", "cli-refresh", "a@example.test"));
            Fetch(path, billing, oauth);
            held.Wait();
            Expect(GrokUsageFetcher.HasPendingWriteBack(path), "a new pair is held");
            File.Delete(path);
            tokens.Clear();
            var before = posted.Count;
            var outcome = Fetch(path, billing, oauth);
            Expect(outcome is GrokUsageFetcher.Outcome.NotInstalled && !File.Exists(path), "the sign-out is not undone");
            Expect(tokens.Count == 0 && posted.Count == before, "and nothing was fetched or refreshed with a revived login");
        }
        finally
        {
            AtomicReplace.ReplaceFile = previous;
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static void SignOutIsNotUndoneByALeftoverBackup()
    {
        HeldSetup("so", out var path);
        try
        {
            var (billing, oauth, tokens, posted) = Clients(_ => true, () => Tokens());
            var held = HeldFile.For(path, TimeSpan.FromMilliseconds(900));
            Fetch(path, billing, oauth);
            held.Wait();
            Expect(GrokUsageFetcher.HasPendingWriteBack(path), "the pair is held");
            var stray = path + ".bak-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(stray, FreshAuth("someone-elses", "someone-elses-refresh", "b@example.test"));
            File.Delete(path);
            tokens.Clear();
            var before = posted.Count;
            var outcome = Fetch(path, billing, oauth);
            Expect(outcome is GrokUsageFetcher.Outcome.NotInstalled, $"a signed-out login is not revived, got {outcome}");
            Expect(!File.Exists(path) && tokens.Count == 0 && posted.Count == before, "nothing was refreshed or written for anyone else's login");
            Expect(!GrokUsageFetcher.HasPendingWriteBack(path), "and the pair is dropped");
            File.Delete(stray);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// Three addresses that never answer, through the real handler. With the old
    /// rule (a request's deadline is only its own margin, shorter than the connect
    /// round) the connect still running when discovery is cut off makes the grant
    /// wait for it and then run out of time; with the rule in use it never does.
    private static (int Attempts, bool Ambiguous) BlackholeRun(Func<TimeSpan, TimeSpan> rule)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-bh-{Guid.NewGuid():N}.json");
        var perAddress = Http.PerAddressTimeout;
        var total = Http.ConnectTimeout;
        var discovery = Http.DiscoveryMargin;
        var grant = Http.GrantMargin;
        var previousRule = Http.DeadlineRule;
        try
        {
            GrokUsageFetcher.ResetStateForTests();
            lock (LogLines) LogLines.Clear();
            File.WriteAllText(path, Auth("old", "blackhole-grok", "a@example.test"));
            Http.PerAddressTimeout = TimeSpan.FromMilliseconds(100);
            Http.ConnectTimeout = TimeSpan.FromMilliseconds(800);
            Http.DiscoveryMargin = TimeSpan.FromMilliseconds(200);
            Http.GrantMargin = TimeSpan.FromMilliseconds(380);
            Http.DeadlineRule = rule;
            var attempts = 0;
            using var billing = new HttpClient(new Handler(_ => new(System.Net.HttpStatusCode.Unauthorized)));
            using var oauth = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = (context, ct) => Http.ConnectWithTimeout(context.DnsEndPoint, Http.PerAddressTimeout, Http.ConnectTimeout,
                    (host, token) => Task.FromResult(new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" }.Select(System.Net.IPAddress.Parse).ToArray()),
                    (socket, address, token) =>
                    {
                        Interlocked.Increment(ref attempts);
                        return new ValueTask(Task.Delay(Timeout.Infinite, token));
                    }, ct),
            });
            Fetch(path, billing, oauth);
            return (attempts, Logged().Any(line => line.Contains("ambiguous")));
        }
        finally
        {
            Http.PerAddressTimeout = perAddress;
            Http.ConnectTimeout = total;
            Http.DiscoveryMargin = discovery;
            Http.GrantMargin = grant;
            Http.DeadlineRule = previousRule;
            try { File.Delete(path); } catch { }
        }
    }

    private static void BlackholedAddressesAreNotAmbiguous()
    {
        var current = Http.DeadlineRule;
        var (attempts, ambiguous) = BlackholeRun(current);
        Expect(!ambiguous, "with the rule in use the grant is never held as ambiguous");
        Expect(attempts >= 6, $"the discovery and the grant each went through all three addresses, got {attempts}");
        var again = BlackholeRun(current);
        Expect(!again.Ambiguous, "and the same on the next poll");
        var old = BlackholeRun(margin => margin);
        Expect(old.Ambiguous, "the same run under the old rule does end ambiguous, so this test would catch it coming back");
    }
}
