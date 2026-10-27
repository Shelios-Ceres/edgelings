using System.Text.Json.Nodes;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// ClaudeCredentials.ResolveUsage against a temporary credentials file, a
/// scripted probe and a scripted refresh call: no network, and the real
/// credentials file is never read or written.
public static class ClaudeCredentialsTests
{
    private static readonly ClaudeCredentials.ProbeOutcome Ok = new ClaudeCredentials.ProbeOutcome.Success(AppUsage.Empty);
    private static readonly ClaudeCredentials.ProbeOutcome Expired = new ClaudeCredentials.ProbeOutcome.Unauthorized();

    public static void RunAll()
    {
        var tests = new (string Name, Func<Task> Test)[]
        {
            ("a valid stored token is used without refreshing", ValidToken),
            ("an expired token is refreshed with the pair on disk and the new pair is saved", RefreshesExpiredToken),
            ("a pair the CLI rotated meanwhile is tried before any refresh", PicksUpRotatedPair),
            ("a rotated pair that is also expired refreshes with the newest refresh token", RotatedPairStillExpired),
            ("a rotated pair missing a scope asks for a new sign-in without refreshing", RotatedPairNeedsScope),
            ("a rotated pair that is rate limited reads as rate limited, not as a lost sign-in", RotatedPairRateLimited),
            ("a refresh the CLI beat by a moment is followed by one look at the file", RefreshLosesTheRace),
            ("a failed refresh reads as a lost sign-in", RefreshFails),
            ("a rotated pair is saved once another program lets go of the file", HeldFileIsWrittenOnceReleased),
            ("a file caught mid-rewrite is read again and everything else in it is kept", MidRewriteIsReadAgain),
            ("a file that cannot be parsed is never replaced with a bare skeleton", UnreadableFileIsLeftAlone),
            ("a refresh for a login replaced meanwhile leaves the new login alone", ReplacedLoginIsLeftAlone),
            ("a file the CLI deleted meanwhile is written fresh", MissingFileIsSeeded),
            ("a refresh the network stopped is not a lost sign-in", RefreshUnavailable),
            ("only a refused grant reads as a lost sign-in", RefreshReplies),
        };
        var directory = Path.Combine(Path.GetTempPath(), "agentisland-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        var previousFile = ClaudeCredentials.CredentialsFileOverride;
        var previousRefresh = ClaudeCredentials.RefreshToken;
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
            ClaudeCredentials.CredentialsFileOverride = Path.Combine(directory, ".credentials.json");
            foreach (var (name, test) in tests)
            {
                ClaudeCredentials.RefreshToken = _ => throw new Exception("refreshed when the test did not expect it");
                test().GetAwaiter().GetResult();
                Console.WriteLine($"PASS claude credentials: {name}");
            }
            Console.WriteLine("ClaudeCredentialsTests GREEN");
        }
        finally
        {
            ClaudeCredentials.CredentialsFileOverride = previousFile;
            ClaudeCredentials.RefreshToken = previousRefresh;
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", envToken);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CredentialsPath => ClaudeCredentials.CredentialsFileOverride!;

    private static Task<ClaudeCredentials.RefreshResult> Refreshed(string access, string refresh) =>
        Task.FromResult<ClaudeCredentials.RefreshResult>(
            new ClaudeCredentials.RefreshResult.Refreshed(new ClaudeCredentials.RefreshedTokens(access, refresh, 1)));

    private static Task<ClaudeCredentials.RefreshResult> Rejected() =>
        Task.FromResult<ClaudeCredentials.RefreshResult>(new ClaudeCredentials.RefreshResult.Rejected());

    /// What Claude Code keeps in the file: the pair, plus fields this app must leave alone.
    private static void Write(string access, string refresh) => File.WriteAllText(CredentialsPath, new JsonObject
    {
        ["claudeAiOauth"] = new JsonObject
        {
            ["accessToken"] = access,
            ["refreshToken"] = refresh,
            ["subscriptionType"] = "max",
            ["scopes"] = new JsonArray("user:inference", "user:profile"),
        },
        ["otherKey"] = "kept",
    }.ToJsonString());

    private static (string Access, string Refresh) Read()
    {
        var oauth = JsonNode.Parse(File.ReadAllText(CredentialsPath))!["claudeAiOauth"]!;
        return ((string)oauth["accessToken"]!, (string)oauth["refreshToken"]!);
    }

    private static async Task ValidToken()
    {
        Write("A", "R1");
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(Ok);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage, "the stored token's usage is returned");
        Expect(string.Join(",", seen) == "A", $"only the stored token was probed, got {string.Join(",", seen)}");
    }

    private static async Task RefreshesExpiredToken()
    {
        Write("A", "R1");
        var refreshedWith = new List<string>();
        ClaudeCredentials.RefreshToken = refresh =>
        {
            refreshedWith.Add(refresh);
            return Refreshed("B", "R2");
        };
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(token == "B" ? Ok : Expired);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage, "the refreshed token's usage is returned");
        Expect(string.Join(",", refreshedWith) == "R1", $"refreshed with the stored refresh token, got {string.Join(",", refreshedWith)}");
        Expect(string.Join(",", seen) == "A,B", $"probed the stored then the refreshed token, got {string.Join(",", seen)}");
        Expect(Read() == ("B", "R2"), "the rotated pair is saved, or Claude Code would sign out");
        var saved = JsonNode.Parse(File.ReadAllText(CredentialsPath))!;
        Expect((string?)saved["otherKey"] == "kept" && (string?)saved["claudeAiOauth"]!["subscriptionType"] == "max",
            "everything else in the file is kept");
    }

    private static async Task PicksUpRotatedPair()
    {
        Write("A", "R1");
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            if (token == "A")
            {
                // Claude Code refreshed while this probe was out.
                Write("B", "R2");
                return Task.FromResult(Expired);
            }
            return Task.FromResult(Ok);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage, "the newer pair's usage is returned");
        Expect(string.Join(",", seen) == "A,B", $"the newer access token was probed next, got {string.Join(",", seen)}");
        Expect(Read() == ("B", "R2"), "the pair on disk is left as Claude Code wrote it");
    }

    private static async Task RotatedPairStillExpired()
    {
        Write("A", "R1");
        var refreshedWith = new List<string>();
        ClaudeCredentials.RefreshToken = refresh =>
        {
            refreshedWith.Add(refresh);
            return Refreshed("C", "R3");
        };
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            if (token == "A") Write("B", "R2");
            return Task.FromResult(token == "C" ? Ok : Expired);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage, "the refreshed token's usage is returned");
        Expect(string.Join(",", refreshedWith) == "R2", $"refreshed with the newest refresh token, got {string.Join(",", refreshedWith)}");
        Expect(string.Join(",", seen) == "A,B,C", $"probed A, the newer B, then the refreshed C, got {string.Join(",", seen)}");
        Expect(Read() == ("C", "R3"), "the newest rotated pair is saved");
    }

    private static async Task RotatedPairNeedsScope()
    {
        Write("A", "R1");
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            if (token == "A")
            {
                Write("B", "R2");
                return Task.FromResult(Expired);
            }
            return Task.FromResult<ClaudeCredentials.ProbeOutcome>(new ClaudeCredentials.ProbeOutcome.ScopeInsufficient());
        });
        Expect(result is ClaudeCredentials.Resolution.ReauthRequired, "a missing scope asks for a new sign-in");
    }

    private static async Task RotatedPairRateLimited()
    {
        Write("A", "R1");
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            if (token == "A")
            {
                Write("B", "R2");
                return Task.FromResult(Expired);
            }
            return Task.FromResult<ClaudeCredentials.ProbeOutcome>(new ClaudeCredentials.ProbeOutcome.RateLimited());
        });
        Expect(result is ClaudeCredentials.Resolution.Failed { Message: "rate limited" }, "rate limiting is reported as such");
    }

    private static async Task RefreshLosesTheRace()
    {
        Write("A", "R1");
        // Claude Code refreshes between our read of the file and our refresh call.
        ClaudeCredentials.RefreshToken = _ =>
        {
            Write("B", "R2");
            return Rejected();
        };
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(token == "B" ? Ok : Expired);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage, "the pair Claude Code wrote is used");
        Expect(string.Join(",", seen) == "A,B", $"one more probe, with the new token, got {string.Join(",", seen)}");
        Expect(Read() == ("B", "R2"), "the pair on disk is left as Claude Code wrote it");
    }

    private static async Task RefreshFails()
    {
        Write("A", "R1");
        ClaudeCredentials.RefreshToken = _ => Rejected();
        var result = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(result is ClaudeCredentials.Resolution.Failed { Message: ClaudeCredentials.TokenRefreshFailedMessage },
            "a refresh that fails reads as a lost sign-in");
    }

    private static async Task HeldFileIsWrittenOnceReleased()
    {
        Write("A", "R1");
        Task? released = null;
        ClaudeCredentials.RefreshToken = _ =>
        {
            // An antivirus scan opens the file just as the rotated pair is written.
            released = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(150));
            return Refreshed("B", "R2");
        };
        var result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        await released!;
        Expect(result is ClaudeCredentials.Resolution.Usage, "the refreshed token's usage is returned, not a re-login");
        Expect(Read() == ("B", "R2"), "the rotated pair is on disk, or Claude Code would sign out");
    }

    private static async Task MidRewriteIsReadAgain()
    {
        Write("A", "R1");
        var whole = File.ReadAllText(CredentialsPath);
        Task? finished = null;
        ClaudeCredentials.RefreshToken = _ =>
        {
            // Claude Code rewrites the file in place (to save an MCP login, say):
            // it is empty for a moment, then whole again.
            File.WriteAllText(CredentialsPath, "");
            finished = Task.Run(async () =>
            {
                await Task.Delay(60);
                File.WriteAllText(CredentialsPath, whole);
            });
            return Refreshed("B", "R2");
        };
        var result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        await finished!;
        Expect(result is ClaudeCredentials.Resolution.Usage, "the refreshed token's usage is returned");
        Expect(Read() == ("B", "R2"), "the rotated pair is saved");
        var saved = JsonNode.Parse(File.ReadAllText(CredentialsPath))!;
        Expect((string?)saved["otherKey"] == "kept" && (string?)saved["claudeAiOauth"]!["subscriptionType"] == "max",
            "the file read whole is the one updated, so nothing else is lost");
    }

    private static async Task UnreadableFileIsLeftAlone()
    {
        Write("A", "R1");
        ClaudeCredentials.RefreshToken = _ =>
        {
            File.WriteAllText(CredentialsPath, "{\"claudeAiOauth\":{\"accessToken\":\"A\",");
            return Refreshed("B", "R2");
        };
        var result = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(File.ReadAllText(CredentialsPath) == "{\"claudeAiOauth\":{\"accessToken\":\"A\",",
            "a file that never parses is left as it is, not rewritten as a skeleton");
        Expect(result is ClaudeCredentials.Resolution.ReauthRequired, "the lost pair asks for a new sign-in");
    }

    private static async Task ReplacedLoginIsLeftAlone()
    {
        Write("A", "R1");
        ClaudeCredentials.RefreshToken = _ =>
        {
            // The user ran `claude /login` as another account while the refresh was out.
            Write("C", "R9");
            return Refreshed("B", "R2");
        };
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(token == "A" ? Expired : Ok);
        });
        Expect(Read() == ("C", "R9"), "the new login is not overwritten with the old account's refreshed pair");
        Expect(result is ClaudeCredentials.Resolution.Usage, "the new login's usage is returned");
        Expect(string.Join(",", seen) == "A,C", $"the login now in the file is probed, got {string.Join(",", seen)}");
    }

    private static async Task MissingFileIsSeeded()
    {
        Write("A", "R1");
        ClaudeCredentials.RefreshToken = _ =>
        {
            File.Delete(CredentialsPath);
            return Refreshed("B", "R2");
        };
        var result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        Expect(result is ClaudeCredentials.Resolution.Usage, "the refreshed token's usage is returned");
        Expect(Read() == ("B", "R2"), "the rotated pair is written to a fresh file");
    }

    private static async Task RefreshUnavailable()
    {
        Write("A", "R1");
        ClaudeCredentials.RefreshToken = _ =>
            Task.FromResult<ClaudeCredentials.RefreshResult>(new ClaudeCredentials.RefreshResult.Unavailable("network drop"));
        var result = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(result is ClaudeCredentials.Resolution.Failed { Message: "network drop" },
            $"a timeout at the token endpoint reads as one, got {result}");
        Expect(!ClaudeCredentials.IsAuthRecoverableError(((ClaudeCredentials.Resolution.Failed)result).Message),
            "and offers no sign-in button");
        Expect(Read() == ("A", "R1"), "the file is untouched");
    }

    private static Task RefreshReplies()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        static byte[] Body(string json) => System.Text.Encoding.UTF8.GetBytes(json);
        var tokens = Body("""{"access_token":"B","refresh_token":"R2","expires_in":3600}""");
        foreach (var status in new[] { 400, 401, 403 })
        {
            Expect(ClaudeCredentials.RefreshResultFor(status, Body("""{"error":"invalid_grant"}"""), now)
                is ClaudeCredentials.RefreshResult.Rejected, $"{status} is a refused grant");
        }
        foreach (var status in new[] { 429, 500, 503 })
        {
            Expect(ClaudeCredentials.RefreshResultFor(status, Body("{}"), now)
                is ClaudeCredentials.RefreshResult.Unavailable { Message: var message } && message == $"HTTP {status}",
                $"{status} is not a lost sign-in");
        }
        Expect(ClaudeCredentials.RefreshResultFor(200, Body("<html>sign in to the hotel wifi</html>"), now)
            is ClaudeCredentials.RefreshResult.Unavailable, "a captive portal's page is not a lost sign-in");
        Expect(ClaudeCredentials.RefreshResultFor(200, tokens, now)
            is ClaudeCredentials.RefreshResult.Refreshed { Tokens: { AccessToken: "B", RefreshToken: "R2" } refreshed }
            && refreshed.ExpiresAtMs == now.AddSeconds(3600).ToUnixTimeMilliseconds(), "a token reply is read");
        return Task.CompletedTask;
    }
}
