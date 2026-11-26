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
            ("a refused refresh token is not sent again, nor its access token asked about, until the store changes", RefusedTokenIsNotReplayed),
            ("a refused sign-in stays held while only other things in the file change, and is tried once the refresh token is new", RefusedTokenResumesOnChange),
            ("on a Mac the item's date, or a keychain that cannot be read for a moment, does not lift the hold; a new refresh token does", RefusedTokenOnAMacKeychain),
            ("with an environment token as well, only the stored token is left alone once it is refused", RefusedStoredTokenWithAnEnvironmentToken),
            ("a rate limit, a dropped connection or a server error is not a dead token and is asked again", UnavailableRefreshIsTriedAgain),
            ("a rotated pair is saved once another program lets go of the file", HeldFileIsWrittenOnceReleased),
            ("a file caught mid-rewrite is read again and everything else in it is kept", MidRewriteIsReadAgain),
            ("a file that cannot be parsed is never replaced with a bare skeleton", UnreadableFileIsLeftAlone),
            ("a rotated pair the file would not take is used from memory and written on a later poll", WriteBackFailsThenLands),
            ("a pending write-back refreshes with the new refresh token, never the one on disk", PendingRefreshUsesNewToken),
            ("a pending write-back is dropped when the file has changed", PendingDroppedWhenFileChanged),
            ("a pending write-back is dropped, and the file not recreated, when it was removed", PendingDroppedWhenFileRemoved),
            ("the refresh log carries reasons only, never a token", LogHoldsNoToken),
            ("a pair the file would not take is written again on its own back-off, without a poll", BackgroundRetryWrites),
            ("when the back-off runs out the quota card says the sign-in was not saved, until it is", ExhaustedBackoffIsVisible),
            ("a pair still held is written once more on a normal exit", FlushOnExit),
            ("a file with no login drops the pending pair, a file that cannot be read keeps it", NoLoginDropsUnreadableKeeps),
            ("a login made while a write waits to retry is not overwritten", NewLoginDuringRetryWins),
            ("tokens are masked when a record is printed", RecordsAreMasked),
            ("a store that read empty and reads fine a moment later keeps the pending pair and writes it", RereadFindsTheFile),
            ("on a Mac a keychain that cannot be asked is a wait, one that says no item is a sign-out", KeychainFailureIsNotSignOut),
            ("the gave-up message is one the sign-in button answers to", GaveUpOffersSignIn),
            ("a swap that failed half way is recovered on the next poll and the pending pair is not lost", HalfFailedSwapIsRecovered),
            ("a sign-out is not undone by a leftover backup while a pair is pending", SignOutIsNotUndoneByALeftoverBackup),
            ("a scene left by a cut-short swap is forgotten once the file is back, so a later sign-out stays", SignOutAfterAnOldCutShortSwap),
            ("a displaced file that cannot be read yet keeps the pending pair until it can", LockedBackupKeepsThePendingPair),
            ("a refresh for a login replaced meanwhile leaves the new login alone", ReplacedLoginIsLeftAlone),
            ("sign-out during refresh leaves the credential file absent", MissingFileStaysMissing),
            ("a refresh the network stopped is not a lost sign-in", RefreshUnavailable),
            ("an ambiguous rotation is not replayed before ten minutes, then once", AmbiguousRotation),
            ("only a refused grant reads as a lost sign-in", RefreshReplies),
            ("a refresh whose connects all run out is never sent, so not ambiguous; under the old deadline rule it would be", BlackholedAddressesAreNotAmbiguous),
        };
        var directory = Path.Combine(Path.GetTempPath(), "agentisland-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        var previousFile = ClaudeCredentials.CredentialsFileOverride;
        var previousRefresh = ClaudeCredentials.RefreshToken;
        var previousClock = RefreshGrants.Clock;
        var previousLog = CredentialRefreshLog.Write;
        var previousBackoff = CredentialWriteBacks.Backoff;
        try
        {
            // A held pair is retried only by the tests that look at the retries.
            CredentialWriteBacks.Backoff = Array.Empty<TimeSpan>();
            RefreshGrants.Clock = () => Now;
            CredentialRefreshLog.Write = line => { lock (LogLines) LogLines.Add(line); return true; };
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
            ClaudeCredentials.CredentialsFileOverride = Path.Combine(directory, ".credentials.json");
            foreach (var (name, test) in tests)
            {
                ClaudeCredentials.RefreshToken = _ => throw new Exception("refreshed when the test did not expect it");
                ClaudeCredentials.ResetStateForTests();
                CredentialRefreshLog.ResetForTests();
                lock (LogLines) LogLines.Clear();
                Now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
                test().GetAwaiter().GetResult();
                Console.WriteLine($"PASS claude credentials: {name}");
            }
            Console.WriteLine("ClaudeCredentialsTests GREEN");
        }
        finally
        {
            ClaudeCredentials.CredentialsFileOverride = previousFile;
            ClaudeCredentials.RefreshToken = previousRefresh;
            RefreshGrants.Clock = previousClock;
            CredentialRefreshLog.Write = previousLog;
            CredentialWriteBacks.Backoff = previousBackoff;
            ClaudeCredentials.ResetStateForTests();
            CredentialRefreshLog.ResetForTests();
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", envToken);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DateTimeOffset Now;
    private static readonly List<string> LogLines = new();

    /// The log as it stands: a retry loop may be adding to it from its own thread.
    private static string[] Logged()
    {
        lock (LogLines) return LogLines.ToArray();
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

    private static async Task RefusedTokenIsNotReplayed()
    {
        Write("A", "R1");
        var refreshes = 0;
        var probes = 0;
        ClaudeCredentials.RefreshToken = _ => { refreshes++; return Rejected(); };
        Task<ClaudeCredentials.ProbeOutcome> Probe(string _, string? __) { probes++; return Task.FromResult(Expired); }
        await ClaudeCredentials.ResolveUsage(Probe);
        Expect(refreshes == 1 && probes == 1, $"the first poll asks once and is refused, got {refreshes} refreshes and {probes} probes");
        for (var poll = 0; poll < 3; poll++)
        {
            var result = await ClaudeCredentials.ResolveUsage(Probe);
            Expect(result is ClaudeCredentials.Resolution.Failed { Message: ClaudeCredentials.TokenRefreshFailedMessage },
                "the card keeps asking for a new sign-in");
        }
        Expect(refreshes == 1 && probes == 1, $"later polls send nothing, got {refreshes} refreshes and {probes} probes");
        Expect(Logged().All(line => !line.Contains("R1")), "the log never holds the token");
    }

    private static async Task RefusedTokenResumesOnChange()
    {
        Write("A", "R1");
        var refreshes = 0;
        var probes = 0;
        ClaudeCredentials.RefreshToken = _ => { refreshes++; return Rejected(); };
        Task<ClaudeCredentials.ProbeOutcome> Probe(string token, string? _) { probes++; return Task.FromResult(token == "A2" ? Ok : Expired); }
        await ClaudeCredentials.ResolveUsage(Probe);
        await ClaudeCredentials.ResolveUsage(Probe);
        Expect(refreshes == 1 && probes == 1, "held while the store is as it was");

        // Claude Code keeps other things in the file (an MCP login) and its
        // modification time moves with them: the refresh token is the same.
        var root = JsonNode.Parse(File.ReadAllText(CredentialsPath))!.AsObject();
        root["mcpOAuth"] = new JsonObject { ["server"] = new JsonObject { ["accessToken"] = "mcp" } };
        File.WriteAllText(CredentialsPath, root.ToJsonString());
        File.SetLastWriteTimeUtc(CredentialsPath, File.GetLastWriteTimeUtc(CredentialsPath).AddMinutes(1));
        await ClaudeCredentials.ResolveUsage(Probe);
        Expect(refreshes == 1 && probes == 1, $"a file that changed around the same refresh token stays held, got {refreshes} refreshes and {probes} probes");

        // A new sign-in: new tokens in the file.
        Write("A2", "R2");
        var result = await ClaudeCredentials.ResolveUsage(Probe);
        Expect(result is ClaudeCredentials.Resolution.Usage, "a new refresh token is used at once");
        Expect(refreshes == 1, "without refreshing");
    }

    private static async Task RefusedTokenOnAMacKeychain()
    {
        var access = "A";
        var refresh = "R1";
        var version = 1;
        var unreadable = false;
        File.Delete(CredentialsPath);
        const string Service = "Claude Code-credentials";
        ClaudeCredentials.KeychainOverride = new ClaudeKeychain(
            (IReadOnlyList<string> arguments, out int exitCode) =>
            {
                exitCode = 0;
                if (unreadable) { exitCode = 1; return null; }
                if (arguments[0] != "find-generic-password" || !arguments.Contains(Service)) { exitCode = ClaudeKeychain.ItemNotFound; return null; }
                if (arguments.Contains("-w"))
                {
                    return new JsonObject { ["claudeAiOauth"] = new JsonObject
                    {
                        ["accessToken"] = access, ["refreshToken"] = refresh, ["subscriptionType"] = "max",
                    } }.ToJsonString() + "\n";
                }
                return "keychain: \"/fake\"\nattributes:\n    \"acct\"<blob>=\"me\"\n"
                    + $"    \"mdat\"<timedate>=0x00  \"2026103012{version:D4}Z\\000\"\n    \"svce\"<blob>=\"{Service}\"\n";
            }, Service, null);
        try
        {
            var refreshes = 0;
            var probes = 0;
            ClaudeCredentials.RefreshToken = _ => { refreshes++; return Rejected(); };
            Task<ClaudeCredentials.ProbeOutcome> Probe(string _, string? __) { probes++; return Task.FromResult(Expired); }
            var before = ClaudeCredentials.CredentialsModificationStamp();
            await ClaudeCredentials.ResolveUsage(Probe);
            Expect(refreshes == 1 && probes == 1, "the keychain's login was refused once");
            version++;
            Expect(ClaudeCredentials.CredentialsModificationStamp() != before, "the item's date moved");
            await ClaudeCredentials.ResolveUsage(Probe);
            Expect(refreshes == 1 && probes == 1, "a new item date around the same refresh token stays held");
            unreadable = true;
            await ClaudeCredentials.ResolveUsage(Probe);
            unreadable = false;
            version++;
            await ClaudeCredentials.ResolveUsage(Probe);
            Expect(refreshes == 1 && probes == 1, "a keychain that could not be read for a moment, and a date that moved back and forth, change nothing");
            refresh = "R2";
            access = "A2";
            await ClaudeCredentials.ResolveUsage(Probe);
            Expect(probes == 2, "a new refresh token in the keychain is tried at once");
        }
        finally
        {
            ClaudeCredentials.KeychainOverride = null;
        }
    }

    private static async Task RefusedStoredTokenWithAnEnvironmentToken()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "ENV");
        try
        {
            Write("A", "R1");
            var refreshes = 0;
            var seen = new List<string>();
            var envWorks = false;
            ClaudeCredentials.RefreshToken = _ => { refreshes++; return Rejected(); };
            Task<ClaudeCredentials.ProbeOutcome> Probe(string token, string? _)
            {
                seen.Add(token);
                return Task.FromResult(token == "ENV" && envWorks ? Ok : Expired);
            }
            var first = await ClaudeCredentials.ResolveUsage(Probe);
            Expect(first is ClaudeCredentials.Resolution.Failed { Message: ClaudeCredentials.TokenRefreshFailedMessage } && refreshes == 1,
                "both refused: the stored sign-in is lost");
            Expect(seen.SequenceEqual(new[] { "ENV", "A" }), "the first poll asked with both");
            seen.Clear();
            var second = await ClaudeCredentials.ResolveUsage(Probe);
            Expect(second is ClaudeCredentials.Resolution.Failed { Message: ClaudeCredentials.TokenRefreshFailedMessage }, "the card still asks to sign in");
            Expect(seen.SequenceEqual(new[] { "ENV" }) && refreshes == 1, "only the environment token is asked again; the dead stored one is left alone");
            envWorks = true;
            var third = await ClaudeCredentials.ResolveUsage(Probe);
            Expect(third is ClaudeCredentials.Resolution.Usage && refreshes == 1, "a working environment token still shows the quota");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        }
    }

    private static async Task UnavailableRefreshIsTriedAgain()
    {
        Write("A", "R1");
        var posts = 0;
        var reply = (ClaudeCredentials.RefreshResult)new ClaudeCredentials.RefreshResult.Unavailable("network drop");
        ClaudeCredentials.RefreshToken = _ => { posts++; return Task.FromResult(reply); };
        var body = System.Text.Encoding.UTF8.GetBytes("{}");
        // A rate limit and a connection that never left: nothing was spent, ask again at the next poll.
        foreach (var next in new[] { ClaudeCredentials.RefreshResultFor(429, body, Now), new ClaudeCredentials.RefreshResult.Unavailable("network drop") })
        {
            reply = next;
            posts = 0;
            for (var poll = 1; poll <= 3; poll++)
            {
                var result = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
                Expect(result is ClaudeCredentials.Resolution.Failed { Message: not ClaudeCredentials.TokenRefreshFailedMessage }, "not read as a lost sign-in");
                Expect(posts == poll, $"poll {poll} asked again, got {posts}");
            }
        }
        // A server error may have rotated the token: held for the ten minutes it is held anyway, then asked again, never dead.
        reply = ClaudeCredentials.RefreshResultFor(503, body, Now);
        posts = 0;
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(posts == 1, "held for the ambiguity window");
        Now = Now.AddMinutes(11);
        var later = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(posts == 2 && later is ClaudeCredentials.Resolution.Failed { Message: not ClaudeCredentials.TokenRefreshFailedMessage }, "asked again after it, and still not a lost sign-in");
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
        Write("A", "unwritable-fixture");
        ClaudeCredentials.RefreshToken = _ =>
        {
            File.WriteAllText(CredentialsPath, "{\"claudeAiOauth\":{\"accessToken\":\"A\",");
            return Refreshed("B", "R2");
        };
        var result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        Expect(File.ReadAllText(CredentialsPath) == "{\"claudeAiOauth\":{\"accessToken\":\"A\",",
            "a file that never parses is left as it is, not rewritten as a skeleton");
        Expect(result is ClaudeCredentials.Resolution.Usage, "the rotated pair is used from memory, not read as a lost sign-in");
        Expect(ClaudeCredentials.HasPendingWriteBack, "and kept to be written later");
    }

    private static async Task WriteBackFailsThenLands()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            // Claude Code or an antivirus scan keeps the file open longer than the write retries last.
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(1200));
            return Refreshed("B", "R2");
        };
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(token == "B" ? Ok : Expired);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage, "the new access token fetches the quota, not a re-login");
        Expect(string.Join(",", seen) == "A,B", $"probed the old then the new token, got {string.Join(",", seen)}");
        Expect(Read() == ("A", "R1"), "the file could not be written yet");
        Expect(ClaudeCredentials.HasPendingWriteBack, "the new pair is held");
        ClaudeCredentials.RefreshToken = _ => throw new Exception("refreshed again for nothing");
        await held;
        seen.Clear();
        result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(Ok);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage, "the next poll fetches the quota");
        Expect(string.Join(",", seen) == "B", $"the new token is probed first, got {string.Join(",", seen)}");
        Expect(Read() == ("B", "R2"), "the held pair is written once the file is free");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "and nothing is pending any more");
    }

    private static async Task PendingRefreshUsesNewToken()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        var refreshedWith = new List<string>();
        ClaudeCredentials.RefreshToken = refresh =>
        {
            refreshedWith.Add(refresh);
            if (refreshedWith.Count == 1)
            {
                held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(2500));
                return Refreshed("B", "R2");
            }
            return Refreshed("C", "R3");
        };
        var result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" || token == "C" ? Ok : Expired));
        Expect(result is ClaudeCredentials.Resolution.Usage, "first poll uses the new token");
        // The new access token has expired as well; the file is still held.
        result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "C" ? Ok : Expired));
        Expect(result is ClaudeCredentials.Resolution.Usage, "the second refresh works from memory");
        Expect(string.Join(",", refreshedWith) == "R1,R2", $"the second refresh used the rotated token, got {string.Join(",", refreshedWith)}");
        Expect(Read() == ("A", "R1"), "the file is still held");
        await held;
        ClaudeCredentials.RefreshToken = _ => throw new Exception("refreshed again for nothing");
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
        Expect(Read() == ("C", "R3"), "the newest pair lands over the token it replaced");
    }

    private static async Task PendingDroppedWhenFileChanged()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(1000));
            return Refreshed("B", "R2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        Expect(ClaudeCredentials.HasPendingWriteBack, "the pair is held");
        await held;
        // The user ran `claude /login` again.
        Write("N", "R9");
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(Ok);
        });
        Expect(result is ClaudeCredentials.Resolution.Usage && string.Join(",", seen) == "N", "the file's login is the one used");
        Expect(Read() == ("N", "R9"), "the held pair is not written over it");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "and it is dropped");
        Expect(Logged().Any(line => line.Contains("dropped, file changed")), "the drop is logged");
    }

    private static async Task PendingDroppedWhenFileRemoved()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(1000));
            return Refreshed("B", "R2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        Expect(ClaudeCredentials.HasPendingWriteBack, "the pair is held");
        await held;
        File.Delete(CredentialsPath);
        var result = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
        Expect(result is not ClaudeCredentials.Resolution.Usage, "a signed-out login is not revived from memory");
        Expect(!File.Exists(CredentialsPath), "the file is not recreated");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "and nothing stays pending");
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var waited = 0; waited < 8000 && !condition(); waited += 25) await Task.Delay(25);
        Expect(condition(), what);
    }

    private static async Task BackgroundRetryWrites()
    {
        var previous = CredentialWriteBacks.Backoff;
        CredentialWriteBacks.Backoff = Enumerable.Repeat(TimeSpan.FromMilliseconds(30), 100).ToArray();
        try
        {
            Write("A", "R1");
            var held = Task.CompletedTask;
            ClaudeCredentials.RefreshToken = _ =>
            {
                held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(600));
                return Refreshed("B", "R2");
            };
            await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
            Expect(ClaudeCredentials.HasPendingWriteBack, "the pair is held");
            await WaitUntil(() => !ClaudeCredentials.HasPendingWriteBack, "the pair is written without another poll");
            await held;
            Expect(Read() == ("B", "R2"), "and it is on disk");
        }
        finally
        {
            CredentialWriteBacks.Backoff = previous;
        }
    }

    private static async Task ExhaustedBackoffIsVisible()
    {
        var previous = CredentialWriteBacks.Backoff;
        CredentialWriteBacks.Backoff = new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) };
        try
        {
            Write("A", "R1");
            var held = Task.CompletedTask;
            ClaudeCredentials.RefreshToken = _ =>
            {
                held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(3500));
                return Refreshed("B", "R2");
            };
            var first = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
            Expect(first is ClaudeCredentials.Resolution.Usage, "while the retries last the quota is shown as usual");
            await WaitUntil(() => Logged().Any(line => line.Contains("write-back retries exhausted")), "the back-off runs out");
            var shown = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
            Expect(shown is ClaudeCredentials.Resolution.Failed { Message: var message } && message == AgentIsland.Localization.L10n.Tr(
                "Couldn't save the refreshed sign-in; Claude Code may need to sign in again"), $"got {shown}");
            Expect(!ClaudeCredentials.IsAuthRecoverableError(((ClaudeCredentials.Resolution.Failed)shown).Message), "it does not claim a lost sign-in");
            await held;
            var after = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
            Expect(after is ClaudeCredentials.Resolution.Usage, "once the pair is written the message goes away");
            Expect(Read() == ("B", "R2"), "and the file holds it");
        }
        finally
        {
            CredentialWriteBacks.Backoff = previous;
        }
    }

    private static async Task FlushOnExit()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(900));
            return Refreshed("B", "R2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        Expect(ClaudeCredentials.HasPendingWriteBack, "the pair is held");
        await held;
        CredentialWriteBacks.FlushAll();
        Expect(Read() == ("B", "R2"), "the exit wrote it");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "and nothing is pending");
    }

    private static async Task NoLoginDropsUnreadableKeeps()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(900));
            return Refreshed("B", "R2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        await held;
        File.WriteAllText(CredentialsPath, "{\"claudeAiOauth\":{\"accessToken\":");
        var kept = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        Expect(kept is ClaudeCredentials.Resolution.Usage && ClaudeCredentials.HasPendingWriteBack, "a half-written file is a wait, not a sign-out");
        File.WriteAllText(CredentialsPath, "{\"otherKey\":\"kept\"}");
        var dropped = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
        Expect(dropped is not ClaudeCredentials.Resolution.Usage, "a file with no login is signed out");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "the pair is dropped");
        Expect(File.ReadAllText(CredentialsPath) == "{\"otherKey\":\"kept\"}", "and the file is not touched");
    }

    private static async Task NewLoginDuringRetryWins()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(1500));
            // The user signs in again while the write waits between its tries.
            var login = Task.Run(async () =>
            {
                await Task.Delay(1);
                Write("N", "R9");
            });
            return Refreshed("B", "R2");
        };
        var seen = new List<string>();
        var result = await ClaudeCredentials.ResolveUsage((token, _) =>
        {
            seen.Add(token);
            return Task.FromResult(token == "A" ? Expired : Ok);
        });
        await held;
        Expect(Read() == ("N", "R9"), "the new login is not overwritten by the old session's pair");
        Expect(result is ClaudeCredentials.Resolution.Usage && seen.Last() == "N", $"the new login is the one used, got {string.Join(",", seen)}");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "nothing is pending");
    }

    private static async Task RereadFindsTheFile()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(900));
            return Refreshed("B", "R2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        await held;
        Expect(ClaudeCredentials.HasPendingWriteBack, "the pair is held");
        // The first read finds the file locked; it is free again by the second.
        var exclusive = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        ClaudeCredentials.BeforePendingReread = () => exclusive.Dispose();
        try
        {
            var result = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
            Expect(result is ClaudeCredentials.Resolution.Usage, "the quota is fetched");
        }
        finally
        {
            ClaudeCredentials.BeforePendingReread = null;
            exclusive.Dispose();
        }
        Expect(Read() == ("B", "R2"), "the held pair is written, not dropped as a sign-out");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "and nothing is pending");
    }

    private static async Task KeychainFailureIsNotSignOut()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(900));
            return Refreshed("B", "R2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        await held;
        File.Delete(CredentialsPath);
        var failing = true;
        ClaudeCredentials.KeychainOverride = new ClaudeKeychain(
            (IReadOnlyList<string> arguments, out int exitCode) =>
            {
                exitCode = failing ? 1 : ClaudeKeychain.ItemNotFound;
                return null;
            }, "Claude Code-credentials", null);
        try
        {
            var waiting = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
            Expect(waiting is ClaudeCredentials.Resolution.Usage && ClaudeCredentials.HasPendingWriteBack,
                "no credentials file and a keychain that errors: the pair is kept, not read as a sign-out");
            failing = false;
            var signedOut = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
            Expect(signedOut is not ClaudeCredentials.Resolution.Usage && !ClaudeCredentials.HasPendingWriteBack,
                "a keychain that says there is no item, and no file: signed out");
        }
        finally
        {
            ClaudeCredentials.KeychainOverride = null;
        }
    }

    private static async Task HalfFailedSwapIsRecovered()
    {
        Write("A", "R1");
        var previous = AtomicReplace.ReplaceFile;
        try
        {
            ClaudeCredentials.RefreshToken = _ => Refreshed("B", "R2");
            // The old file is moved aside, and neither move that follows can put anything in its place.
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                File.Move(target, backup);
                Directory.CreateDirectory(target);
                throw new IOException("fixture");
            };
            var first = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
            Expect(first is ClaudeCredentials.Resolution.Usage && ClaudeCredentials.HasPendingWriteBack, "the pair is used from memory and held");
            Expect(Directory.GetFiles(Path.GetDirectoryName(CredentialsPath)!, "*.bak-*").Length == 1, "the displaced file is kept");
            // Whatever was in the way is gone.
            Directory.Delete(CredentialsPath);
            AtomicReplace.ReplaceFile = previous;
            var second = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
            Expect(second is ClaudeCredentials.Resolution.Usage, $"the next poll is not read as a sign-out, got {second}");
            Expect(Read() == ("B", "R2"), "the new pair is in the file again");
            Expect(!ClaudeCredentials.HasPendingWriteBack, "and nothing is pending");
        }
        finally
        {
            AtomicReplace.ReplaceFile = previous;
            foreach (var left in Directory.GetFiles(Path.GetDirectoryName(CredentialsPath)!, ".credentials.json.*")) File.Delete(left);
            if (Directory.Exists(CredentialsPath)) Directory.Delete(CredentialsPath);
        }
    }

    private static async Task SignOutIsNotUndoneByALeftoverBackup()
    {
        Write("A", "R1");
        var held = Task.CompletedTask;
        ClaudeCredentials.RefreshToken = _ =>
        {
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(900));
            return Refreshed("B", "R2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        await held;
        Expect(ClaudeCredentials.HasPendingWriteBack, "the pair is held");
        // Another login's backup is lying next to the file, and then the user signs out.
        var stray = CredentialsPath + ".bak-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(stray, File.ReadAllText(CredentialsPath));
        var strayTmp = CredentialsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(strayTmp, File.ReadAllText(CredentialsPath));
        File.Delete(CredentialsPath);
        var result = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
        Expect(result is not ClaudeCredentials.Resolution.Usage, "a signed-out login is not revived");
        Expect(!File.Exists(CredentialsPath), "the file stays gone");
        Expect(!ClaudeCredentials.HasPendingWriteBack, "and the pair is dropped");
        File.Delete(stray);
        File.Delete(strayTmp);
    }

    private static async Task CutShortSwap(Func<Task> afterwards)
    {
        var previous = AtomicReplace.ReplaceFile;
        try
        {
            ClaudeCredentials.RefreshToken = _ => Refreshed("B", "R2");
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                File.Move(target, backup);
                Directory.CreateDirectory(target);
                throw new IOException("fixture");
            };
            await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
            Expect(ClaudeCredentials.HasPendingWriteBack, "the pair is held, the swap left its copies");
            Directory.Delete(CredentialsPath);
            AtomicReplace.ReplaceFile = previous;
            await afterwards();
        }
        finally
        {
            AtomicReplace.ReplaceFile = previous;
            if (Directory.Exists(CredentialsPath)) Directory.Delete(CredentialsPath);
            foreach (var left in Directory.GetFiles(Path.GetDirectoryName(CredentialsPath)!, ".credentials.json.*")) File.Delete(left);
        }
    }

    private static async Task SignOutAfterAnOldCutShortSwap()
    {
        Write("A", "R1");
        await CutShortSwap(async () =>
        {
            // Claude Code writes a login of its own back, which ends the pending pair and the scene.
            Write("X", "R9");
            var seen = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
            Expect(seen is ClaudeCredentials.Resolution.Usage && !ClaudeCredentials.HasPendingWriteBack, "the file's login is the one used");
            var directory = Path.GetDirectoryName(CredentialsPath)!;
            Expect(!Directory.GetFiles(directory).Any(file => file.Contains(".tmp-") || file.Contains(".bak-")), "and the copies are deleted");
            // A plain write that fails again, and then the user signs out.
            var held = Task.CompletedTask;
            ClaudeCredentials.RefreshToken = _ =>
            {
                held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(900));
                return Refreshed("C", "R3");
            };
            await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "C" ? Ok : Expired));
            await held;
            Expect(ClaudeCredentials.HasPendingWriteBack, "a new pair is held");
            File.Delete(CredentialsPath);
            var after = await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
            Expect(after is not ClaudeCredentials.Resolution.Usage && !File.Exists(CredentialsPath), "the sign-out is not undone");
        });
    }

    private static async Task LockedBackupKeepsThePendingPair()
    {
        Write("A", "R1");
        await CutShortSwap(async () =>
        {
            var backup = Directory.GetFiles(Path.GetDirectoryName(CredentialsPath)!, "*.bak-*").Single();
            Task<ClaudeCredentials.Resolution> locked;
            using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                locked = ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
                Expect(await locked is ClaudeCredentials.Resolution.Usage, "the pair is used from memory meanwhile");
                Expect(ClaudeCredentials.HasPendingWriteBack, "and kept, not dropped as a sign-out");
            }
            var next = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
            Expect(next is ClaudeCredentials.Resolution.Usage && Read() == ("B", "R2") && !ClaudeCredentials.HasPendingWriteBack, "once it can be read the pair is put back");
        });
    }

    private static Task GaveUpOffersSignIn()
    {
        Expect(ClaudeCredentials.IsAuthRecoverableError(ClaudeCredentials.GaveUpMessage), "in English");
        var language = AgentIsland.Localization.L10n.Current;
        try
        {
            AgentIsland.Localization.L10n.Current = AgentIsland.Localization.L10n.Language.SimplifiedChinese;
            var shown = AgentIsland.Localization.L10n.Tr(ClaudeCredentials.GaveUpMessage);
            Expect(shown != ClaudeCredentials.GaveUpMessage && ClaudeCredentials.IsAuthRecoverableError(shown), "and as shown in Chinese");
        }
        finally
        {
            AgentIsland.Localization.L10n.Current = language;
        }
        Expect(!ClaudeCredentials.IsAuthRecoverableError("rate limited"), "other messages are not");
        return Task.CompletedTask;
    }

    /// Three addresses that never answer, through the real connect logic. With a request
    /// deadline shorter than the connect round (the old rule: only the margin) the deadline
    /// cuts the request while the connect is still running, and the grant reads as maybe sent;
    /// with the rule in use the connect gives up first and it never left.
    private static async Task<bool> BlackholeRunAmbiguous(Func<TimeSpan, TimeSpan> rule)
    {
        var perAddress = Http.PerAddressTimeout;
        var total = Http.ConnectTimeout;
        var grant = Http.GrantMargin;
        var previousRule = Http.DeadlineRule;
        var previousClient = ClaudeCredentials.OAuth;
        try
        {
            Http.PerAddressTimeout = TimeSpan.FromMilliseconds(200);
            Http.ConnectTimeout = TimeSpan.FromMilliseconds(800);
            Http.GrantMargin = TimeSpan.FromMilliseconds(380);
            Http.DeadlineRule = rule;
            using var oauth = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = (context, ct) => Http.ConnectWithTimeout(context.DnsEndPoint, Http.PerAddressTimeout, Http.ConnectTimeout,
                    (host, token) => Task.FromResult(new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" }.Select(System.Net.IPAddress.Parse).ToArray()),
                    (socket, address, token) => new ValueTask(Task.Delay(Timeout.Infinite, token)), ct),
            });
            ClaudeCredentials.OAuth = oauth;
            var result = await ClaudeCredentials.RefreshClaudeToken("blackhole-token");
            return result is ClaudeCredentials.RefreshResult.Unavailable { Uncertain: true };
        }
        finally
        {
            Http.PerAddressTimeout = perAddress;
            Http.ConnectTimeout = total;
            Http.GrantMargin = grant;
            Http.DeadlineRule = previousRule;
            ClaudeCredentials.OAuth = previousClient;
        }
    }

    private static async Task BlackholedAddressesAreNotAmbiguous()
    {
        var current = Http.DeadlineRule;
        Expect(!await BlackholeRunAmbiguous(current), "with the rule in use the grant is never held as ambiguous");
        Expect(!await BlackholeRunAmbiguous(current), "and the same on the next try");
        Expect(await BlackholeRunAmbiguous(margin => margin), "the same run under the old rule does end ambiguous, so this test would catch it coming back");
    }

    private static Task RecordsAreMasked()
    {
        var tokens = new ClaudeCredentials.RefreshedTokens("SECRET-ACCESS", "SECRET-REFRESH", 1);
        Expect(!tokens.ToString().Contains("SECRET"), "RefreshedTokens");
        var entry = new GrokAuthEntry("scope", "SECRET-ACCESS", null, "oidc", "person@example.test");
        Expect(!entry.ToString().Contains("SECRET") && !entry.ToString().Contains("person"), "GrokAuthEntry");
        return Task.CompletedTask;
    }

    private static async Task LogHoldsNoToken()
    {
        Write("SECRET-ACCESS-1", "SECRET-REFRESH-1");
        var held = Task.CompletedTask;
        var step = 0;
        ClaudeCredentials.RefreshToken = _ =>
        {
            if (++step == 1) return Task.FromResult<ClaudeCredentials.RefreshResult>(
                new ClaudeCredentials.RefreshResult.Unavailable("network drop", true, "timeout"));
            held = HeldFile.For(CredentialsPath, TimeSpan.FromMilliseconds(1000));
            return Refreshed("SECRET-ACCESS-2", "SECRET-REFRESH-2");
        };
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token.StartsWith("SECRET-ACCESS-2") ? Ok : Expired));
        Now = Now.AddMinutes(11);
        await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token.StartsWith("SECRET-ACCESS-2") ? Ok : Expired));
        await held;
        Write("SECRET-ACCESS-3", "SECRET-REFRESH-3");
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Ok));
        var log = string.Join("\n", LogLines);
        Expect(log.Contains("claude: ambiguous: timeout"), "the ambiguous reason is logged");
        Expect(log.Contains("retry 1 of 3"), "the retry is logged");
        Expect(log.Contains("write-back failed"), "the failed write-back is logged");
        Expect(log.Contains("dropped, file changed"), "the drop is logged");
        Expect(!log.Contains("SECRET") && !System.Text.RegularExpressions.Regex.IsMatch(log, "[0-9A-F]{16}"),
            "no token and no fingerprint is logged");
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

    private static async Task MissingFileStaysMissing()
    {
        Write("A", "R1");
        ClaudeCredentials.RefreshToken = _ =>
        {
            File.Delete(CredentialsPath);
            return Refreshed("B", "R2");
        };
        var result = await ClaudeCredentials.ResolveUsage((token, _) => Task.FromResult(token == "B" ? Ok : Expired));
        Expect(result is not ClaudeCredentials.Resolution.Usage, "logout supersedes a late refresh");
        Expect(!File.Exists(CredentialsPath), "logout is preserved");
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

    private static async Task AmbiguousRotation()
    {
        Write("A", "ambiguous-fixture");
        int posts = 0;
        ClaudeCredentials.RefreshToken = _ =>
        {
            posts++;
            return Task.FromResult<ClaudeCredentials.RefreshResult>(new ClaudeCredentials.RefreshResult.Unavailable("network drop", true));
        };
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(posts, 1);
        Now = Now.AddMinutes(9);
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(posts, 1);
        Now = Now.AddMinutes(1);
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(posts, 2);
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(posts, 2);
        Write("B", "replacement-fixture");
        await ClaudeCredentials.ResolveUsage((_, _) => Task.FromResult(Expired));
        Expect(posts, 3);
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
