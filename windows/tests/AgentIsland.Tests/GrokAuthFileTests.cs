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
        try
        {
            RefreshDestinations(Path.Combine(directory, "requests.json"));
            MatchingLoginIsWritten(Path.Combine(directory, "match.json"));
            ChangedLoginIsLeftAlone(Path.Combine(directory, "changed.json"));
            HeldFileIsWrittenOnceReleased(Path.Combine(directory, "held.json"));
            Console.WriteLine("GrokAuthFileTests GREEN");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
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
}
