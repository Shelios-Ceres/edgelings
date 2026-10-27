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
            return Task.FromResult<ClaudeCredentials.RefreshedTokens?>(new ClaudeCredentials.RefreshedTokens("B", "R2", 1));
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
            return Task.FromResult<ClaudeCredentials.RefreshedTokens?>(new ClaudeCredentials.RefreshedTokens("C", "R3", 1));
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
            return Task.FromResult<ClaudeCredentials.RefreshedTokens?>(null);
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
        ClaudeCredentials.RefreshToken = _ => Task.FromResult<ClaudeCredentials.RefreshedTokens?>(null);
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
            return Task.FromResult<ClaudeCredentials.RefreshedTokens?>(new ClaudeCredentials.RefreshedTokens("B", "R2", 1));
        };
        var result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        await released!;
        Expect(result is ClaudeCredentials.Resolution.Usage, "the refreshed token's usage is returned, not a re-login");
        Expect(Read() == ("B", "R2"), "the rotated pair is on disk, or Claude Code would sign out");
    }
}
