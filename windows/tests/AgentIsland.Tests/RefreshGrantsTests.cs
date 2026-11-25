using System.Net;
using System.Text.Json.Nodes;
using System.Net.Sockets;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// How long a refresh grant is held back, which failures count as "never sent",
/// and what the diagnostic log may contain. Synthetic tokens, a fake clock and a
/// captured log: no file or network is touched.
public static class RefreshGrantsTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("a grant being sent is not sent twice", InFlightIsNotRepeated),
            ("a grant that never settled is released after ten minutes", InFlightHealsItself),
            ("an ambiguous grant is retried after 10, 30 and 60 minutes, then every hour", AmbiguousHoldsThenRetries),
            ("a late caller cannot settle or mark a grant that has been begun again", StaleHandleDoesNothing),
            ("a settled grant can be begun again", SettledReleases),
            ("a failure while connecting counts as never sent; a lost reply or a timeout does not", ConnectionStageIsNotAmbiguous),
            ("a connect that times out is a connection error around a timeout, and a caller's cancel is not", ConnectTimeoutIsBeforeSend),
            ("a slow address does not use up the budget of the ones after it, and the whole connect is capped", ConnectBudgetIsPerAddress),
            ("a reset after the request was read is not a connection error", ResetAfterRequestIsAmbiguous),
            ("a replace checks the file before it writes, and leaves no temp or backup copy behind", AtomicReplaceChecksAndCleansUp),
            ("a file whose other fields changed before the swap is built again from its latest content", AtomicReplaceMergesOtherFields),
            ("a file whose other fields changed during the swap is put back and built again", AtomicReplaceMergesAfterTheSwap),
            ("a displaced login that cannot be put back is kept, not deleted", AtomicReplaceKeepsALoginItCannotRestore),
            ("a swap that failed half way is checked against the displaced file too", AtomicReplaceChecksAfterRecovering),
            ("a login written between the check and the swap is put back", AtomicReplacePutsBackALateLogin),
            ("a replace that fails half way keeps whichever copy it can", AtomicReplaceRecovers),
            ("leftover copies are only ever deleted once old, with the file gone or not", LeftoversAreCleanedUp),
            ("a half-failed swap is put back only when the copies are what it left and hold the expected logins", RestoreInterruptedChecksWhatIsThere),
            ("the log rolls past 64 KB and writes a line when trouble recovers", LogRollsAndRecovers),
            ("the log writes a line only when a provider's reason changes, and tolerates a failing sink", LogDeduplicates),
        };
        var previousClock = RefreshGrants.Clock;
        var previousLog = CredentialRefreshLog.Write;
        defaultWrite = previousLog;
        try
        {
            foreach (var (name, test) in tests)
            {
                CredentialRefreshLog.ResetForTests();
                test();
                Console.WriteLine($"PASS refresh grants: {name}");
            }
            Console.WriteLine("RefreshGrantsTests GREEN");
        }
        finally
        {
            RefreshGrants.Clock = previousClock;
            CredentialRefreshLog.Write = previousLog;
            CredentialRefreshLog.ResetForTests();
        }
    }

    private static Func<string, bool> defaultWrite = _ => true;

    private static Exception Thrown(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new Exception("expected an exception");
    }

    private static DateTimeOffset Start => new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static void InFlightIsNotRepeated()
    {
        var now = Start;
        RefreshGrants.Clock = () => now;
        CredentialRefreshLog.Write = _ => true;
        var grants = new RefreshGrants("test");
        Expect(grants.Begin("token-1") is not null, "the first begin is allowed");
        Expect(grants.Begin("token-1") is null, "a second begin while it is in flight is not");
        Expect(grants.Begin("token-2") is not null, "another token is independent");
    }

    private static void InFlightHealsItself()
    {
        var now = Start;
        RefreshGrants.Clock = () => now;
        CredentialRefreshLog.Write = _ => true;
        var grants = new RefreshGrants("test");
        grants.Begin("token-1");
        now = Start.AddMinutes(9).AddSeconds(59);
        Expect(grants.Begin("token-1") is null, "still held just before ten minutes");
        now = Start.AddMinutes(10);
        Expect(grants.Begin("token-1") is not null, "released after ten minutes");
        Expect(grants.Begin("token-1") is null, "and in flight again");
    }

    private static void AmbiguousHoldsThenRetries()
    {
        var now = Start;
        RefreshGrants.Clock = () => now;
        var lines = new List<string>();
        CredentialRefreshLog.Write = line => { lines.Add(line); return true; };
        var grants = new RefreshGrants("test");
        var grant = grants.Begin("token-1")!;
        grants.Ambiguous(grant, "timeout");
        foreach (var (hold, retry) in new[] { (10, 1), (30, 2), (60, 3) })
        {
            now = now.AddMinutes(hold).AddSeconds(-1);
            Expect(grants.Begin("token-1") is null, $"not replayed before {hold} minutes");
            Expect(!grants.GaveUp("token-1"), "still hoping");
            now = now.AddSeconds(1);
            grant = grants.Begin("token-1")!;
            Expect(grant is not null, $"replayed at {hold} minutes (retry {retry})");
            Expect(grants.Begin("token-1") is null, "and not again while that attempt is in flight");
            grants.Ambiguous(grant!, "HTTP 503");
        }
        Expect(grants.Begin("token-1") is null && grants.GaveUp("token-1"), "after the third retry it is reported as given up");
        now = now.AddMinutes(60).AddSeconds(-1);
        Expect(grants.Begin("token-1") is null, "and tried again only an hour later");
        now = now.AddSeconds(1);
        Expect(grants.GaveUp("token-1") && grants.Begin("token-1") is not null, "every hour, with the report kept until it works");
        Expect(lines.Any(line => line.EndsWith("test: retries used up, trying hourly from now on")), "using the retries up is logged");
    }

    private static void StaleHandleDoesNothing()
    {
        var now = Start;
        RefreshGrants.Clock = () => now;
        CredentialRefreshLog.Write = _ => true;
        var grants = new RefreshGrants("test");
        var first = grants.Begin("token-1")!;
        now = Start.AddMinutes(10);
        var second = grants.Begin("token-1")!;
        Expect(second is not null, "the backstop released it");
        grants.Settled(first);
        Expect(grants.Begin("token-1") is null, "the first caller's late settle did not free the second's grant");
        grants.Ambiguous(first, "timeout");
        Expect(!grants.GaveUp("token-1"), "nor did its late mark count");
        grants.Settled(second!);
        Expect(grants.Begin("token-1") is not null, "the current holder can settle");
    }

    private static void SettledReleases()
    {
        RefreshGrants.Clock = () => Start;
        CredentialRefreshLog.Write = _ => true;
        var grants = new RefreshGrants("test");
        var id = grants.Begin("token-1")!;
        grants.Settled(id);
        Expect(grants.Begin("token-1") is not null, "a settled grant is free");
    }

    private static void ConnectionStageIsNotAmbiguous()
    {
        Expect(RefreshGrants.BeforeSend(new HttpRequestException(HttpRequestError.ConnectionError, "fixture")), "a connection that could not be made");
        Expect(RefreshGrants.BeforeSend(new HttpRequestException(HttpRequestError.NameResolutionError, "fixture")), "a name that did not resolve");
        Expect(RefreshGrants.BeforeSend(new HttpRequestException(HttpRequestError.SecureConnectionError, "fixture")), "a TLS handshake that failed");
        Expect(RefreshGrants.BeforeSend(new HttpRequestException(HttpRequestError.Unknown, "fixture", new SocketException((int)SocketError.ConnectionRefused))),
            "a refused connection");
        Expect(!RefreshGrants.BeforeSend(new HttpRequestException(HttpRequestError.ResponseEnded, "fixture")), "a reply cut short may follow a rotation");
        Expect(!RefreshGrants.BeforeSend(new HttpRequestException(HttpRequestError.Unknown, "fixture", new IOException("reset"))),
            "a reset mid-request may follow a rotation");
        Expect(!RefreshGrants.BeforeSend(new TaskCanceledException("fixture", new TimeoutException())), "a timeout may follow a rotation");
    }

    private static readonly IPAddress Blackhole = IPAddress.Parse("192.0.2.1");
    private static Task<IPAddress[]> Resolve(string host, CancellationToken ct, params IPAddress[] addresses) => Task.FromResult(addresses);

    private static void ConnectTimeoutIsBeforeSend()
    {
        var endpoint = new DnsEndPoint("blackhole.test", 443);
        var error = Thrown(() => Http.ConnectWithTimeout(endpoint, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5),
            (host, ct) => Resolve(host, ct, Blackhole), async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct), default).AsTask().GetAwaiter().GetResult());
        Expect(error is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError, InnerException: TimeoutException }, $"got {error}");
        Expect(RefreshGrants.BeforeSend(error), "so it counts as never sent");
        using var cancel = new CancellationTokenSource(50);
        var cancelled = Thrown(() => Http.ConnectWithTimeout(endpoint, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1),
            (host, ct) => Resolve(host, ct, Blackhole), async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct), cancel.Token).AsTask().GetAwaiter().GetResult());
        Expect(cancelled is OperationCanceledException, "the caller's own cancellation stays a cancellation");
    }

    private static void ConnectBudgetIsPerAddress()
    {
        var endpoint = new DnsEndPoint("dual.test", 443);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var tried = new List<IPAddress>();
            var stream = Http.ConnectWithTimeout(endpoint, TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(10),
                (host, ct) => Resolve(host, ct, Blackhole, IPAddress.Parse("192.0.2.2")),
                (socket, address, ct) =>
                {
                    tried.Add(address.Address);
                    return address.Address.Equals(Blackhole)
                        ? new ValueTask(Task.Delay(Timeout.Infinite, ct))
                        : socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                }, default).AsTask().GetAwaiter().GetResult();
            stream.Dispose();
            Expect(tried.Count == 2 && tried[0].Equals(Blackhole), "the first address is tried first and given up on, the next gets its turn");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var error = Thrown(() => Http.ConnectWithTimeout(endpoint, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(150),
                (host, ct) => Resolve(host, ct, Enumerable.Range(1, 20).Select(n => IPAddress.Parse($"192.0.2.{n}")).ToArray()),
                (_, _, ct) => new ValueTask(Task.Delay(Timeout.Infinite, ct)), default).AsTask().GetAwaiter().GetResult());
            Expect(error is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError, InnerException: TimeoutException }, $"got {error}");
            Expect(clock.Elapsed < TimeSpan.FromSeconds(2), $"the total cap ends it, took {clock.Elapsed}");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void ResetAfterRequestIsAmbiguous()
    {
        // A loopback server that reads the request and then resets the connection.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var served = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync();
                var buffer = new byte[4096];
                _ = await peer.GetStream().ReadAsync(buffer);
                await Task.Delay(50);
                peer.LingerState = new LingerOption(true, 0);
            });
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = (context, ct) => Http.ConnectWithTimeout(context.DnsEndPoint, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
                    (host, token) => Resolve(host, token, IPAddress.Loopback), (socket, address, token) => socket.ConnectAsync(address, token), ct),
            });
            var error = Thrown(() => client.PostAsync($"http://127.0.0.1:{port}/", new StringContent("grant")).GetAwaiter().GetResult());
            served.Wait();
            Expect(error is HttpRequestException, $"got {error}");
            Expect(!RefreshGrants.BeforeSend(error), "a reply cut short after the request left may follow a rotation");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static AtomicReplace.Plan PlanFor(string expected) => new(
        root => root["login"] is JsonObject login && (string?)login["token"] == expected,
        root => ((JsonObject)root["login"]!)["token"] = "new",
        new System.Text.Json.JsonSerializerOptions());

    private static string Creds(string token, string extra = "") =>
        new JsonObject { ["login"] = new JsonObject { ["token"] = token }, ["extra"] = extra }.ToJsonString();

    private static string TokenIn(string path) => (string)JsonNode.Parse(File.ReadAllText(path))!["login"]!["token"]!;

    private static void InTempDirectory(Action<string, string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentisland-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = AtomicReplace.ReplaceFile;
        try
        {
            body(directory, Path.Combine(directory, "creds.json"));
        }
        finally
        {
            AtomicReplace.ReplaceFile = previous;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AtomicReplaceChecksAndCleansUp()
    {
        InTempDirectory((directory, path) =>
        {
            File.WriteAllText(path, Creds("old", "kept"));
            var written = AtomicReplace.Run(path, PlanFor("old"));
            Expect(written.Result == AtomicReplace.Result.Written && TokenIn(path) == "new", "a matching file is replaced");
            Expect((string)JsonNode.Parse(File.ReadAllText(path))!["extra"]! == "kept", "and the rest of it is kept");
            Expect(Directory.GetFiles(directory).Length == 1, "with no temp or backup copy left");
            var changed = AtomicReplace.Run(path, PlanFor("old"));
            Expect(changed.Result == AtomicReplace.Result.Superseded && TokenIn(path) == "new", "a file that holds another login is left alone");
            Expect(Directory.GetFiles(directory).Length == 1, "and nothing is left behind");
            File.Delete(path);
            var gone = AtomicReplace.Run(path, PlanFor("old"));
            Expect(gone.Result == AtomicReplace.Result.Gone && !File.Exists(path), "a removed file is not recreated");
            File.WriteAllText(path, "{\"half\":");
            var unreadable = AtomicReplace.Run(path, PlanFor("old"));
            Expect(unreadable.Result == AtomicReplace.Result.Failed && File.ReadAllText(path) == "{\"half\":", "a file that never parses is not written over");
            Expect(Directory.GetFiles(directory).Length == 1, "and leaves nothing behind either");
        });
    }

    private static void AtomicReplaceMergesOtherFields()
    {
        InTempDirectory((directory, path) =>
        {
            File.WriteAllText(path, Creds("old"));
            var calls = 0;
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                // Another program saves something else in the file, the login unchanged, then the swap fails once.
                if (++calls == 1)
                {
                    File.WriteAllText(path, Creds("old", "added-meanwhile"));
                    throw new IOException("fixture");
                }
                File.Replace(source, target, backup);
            };
            var result = AtomicReplace.Run(path, PlanFor("old"));
            Expect(result.Result == AtomicReplace.Result.Written && TokenIn(path) == "new", "the new token lands");
            Expect((string)JsonNode.Parse(File.ReadAllText(path))!["extra"]! == "added-meanwhile", "on top of what the other program saved");
            Expect(Directory.GetFiles(directory).Length == 1, "with nothing left behind");
        });
    }

    private static void AtomicReplaceMergesAfterTheSwap()
    {
        InTempDirectory((directory, path) =>
        {
            File.WriteAllText(path, Creds("old"));
            var calls = 0;
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                // The login is the same but another program saved something else, in the gap before the swap.
                if (++calls == 1) File.WriteAllText(path, Creds("old", "saved-in-the-gap"));
                File.Replace(source, target, backup);
            };
            var result = AtomicReplace.Run(path, PlanFor("old"));
            Expect(calls == 2, $"the swap is done again from the new content, got {calls}");
            Expect(result.Result == AtomicReplace.Result.Written && TokenIn(path) == "new", "the new token lands");
            Expect((string)JsonNode.Parse(File.ReadAllText(path))!["extra"]! == "saved-in-the-gap", "without losing what the other program saved");
            Expect(Directory.GetFiles(directory).Length == 1, "and nothing is left behind");
        });
    }

    private static void AtomicReplaceKeepsALoginItCannotRestore()
    {
        InTempDirectory((directory, path) =>
        {
            File.WriteAllText(path, Creds("old"));
            FileStream? holder = null;
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                // Signed in again in the gap; then something holds the new file so the displaced one cannot go back.
                File.WriteAllText(path, Creds("someone-else"));
                File.Replace(source, target, backup);
                holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            };
            AtomicReplace.Result result;
            try
            {
                result = AtomicReplace.Run(path, PlanFor("old")).Result;
            }
            finally
            {
                holder?.Dispose();
            }
            Expect(result == AtomicReplace.Result.Failed, $"the swap is not reported as settled, got {result}");
            var backups = Directory.GetFiles(directory, "*.bak-*");
            Expect(backups.Length == 1 && JsonNode.Parse(File.ReadAllText(backups[0]))!["login"]!["token"]!.ToString() == "someone-else",
                "the other login is still there, in the backup");
        });
    }

    private static void AtomicReplaceChecksAfterRecovering()
    {
        InTempDirectory((directory, path) =>
        {
            File.WriteAllText(path, Creds("old"));
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                // Signed in again just before a swap that then fails half way.
                File.WriteAllText(path, Creds("someone-else"));
                File.Move(target, backup);
                throw new IOException("fixture");
            };
            var result = AtomicReplace.Run(path, PlanFor("old"));
            Expect(result.Result == AtomicReplace.Result.Superseded, $"the recovered file is not trusted blindly, got {result.Result}");
            Expect(TokenIn(path) == "someone-else", "the displaced login is put back");
            Expect(Directory.GetFiles(directory).Length == 1, "and nothing is left behind");
        });
    }

    private static void AtomicReplacePutsBackALateLogin()
    {
        InTempDirectory((directory, path) =>
        {
            File.WriteAllText(path, Creds("old"));
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                // Signed in again after the check, before the swap.
                File.WriteAllText(path, Creds("someone-else"));
                File.Replace(source, target, backup);
            };
            var result = AtomicReplace.Run(path, PlanFor("old"));
            Expect(result.Result == AtomicReplace.Result.Superseded, "it is not reported as written");
            Expect(TokenIn(path) == "someone-else", "the login that was displaced is put back");
            Expect(Directory.GetFiles(directory).Length == 1, "with nothing left behind");
        });
    }

    private static void AtomicReplaceRecovers()
    {
        InTempDirectory((directory, path) =>
        {
            File.WriteAllText(path, Creds("old"));
            // ERROR_UNABLE_TO_MOVE_REPLACEMENT: the old file is already at the backup name.
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                File.Move(target, backup);
                throw new IOException("fixture");
            };
            var result = AtomicReplace.Run(path, PlanFor("old"));
            Expect(result.Result == AtomicReplace.Result.Written && TokenIn(path) == "new", "the new copy is put in place, not read as a sign-out");
            Expect(Directory.GetFiles(directory).Length == 1, "and the backup is cleared");

            File.WriteAllText(path, Creds("old"));
            AtomicReplace.ReplaceFile = (source, target, backup) =>
            {
                File.Move(target, backup);
                Directory.CreateDirectory(target);
                throw new IOException("fixture");
            };
            var failed = AtomicReplace.Run(path, PlanFor("old"));
            Expect(failed.Result == AtomicReplace.Result.Failed, "both moves failing is a failure");
            Directory.Delete(path);
            var left = Directory.GetFiles(directory).Select(file => Path.GetFileName(file)).ToArray();
            Expect(left.Length == 2 && left.Any(name => name.Contains(".tmp-")) && left.Any(name => name.Contains(".bak-")),
                $"neither copy is deleted, got {string.Join(",", left)}");
        });
    }

    private static void LeftoversAreCleanedUp()
    {
        InTempDirectory((directory, path) =>
        {
            var hex = () => Guid.NewGuid().ToString("N");
            var old = DateTime.UtcNow.AddHours(-1);
            string Copy(string name, DateTime written, string text = "x")
            {
                var file = Path.Combine(directory, name);
                File.WriteAllText(file, text);
                File.SetLastWriteTimeUtc(file, written);
                return file;
            }
            File.WriteAllText(path, Creds("live"));
            var oldTmp = Copy("creds.json.tmp-" + hex(), old);
            var oldBak = Copy("creds.json.bak-" + hex(), old);
            var freshTmp = Copy("creds.json.tmp-" + hex(), DateTime.UtcNow);
            var other = Copy("creds.json.tmp-notahexname", old);
            var unrelated = Copy("other.json.bak-" + hex(), old);
            AtomicReplace.CleanUpLeftovers(path);
            Expect(!File.Exists(oldTmp) && !File.Exists(oldBak), "old copies are removed");
            Expect(File.Exists(freshTmp) && File.Exists(other) && File.Exists(unrelated) && File.Exists(path), "a recent copy, a stranger's file and the credentials are not");
            Expect(TokenIn(path) == "live", "and the file is as it was");

            // A sign-out is not undone by what lies next to the file.
            File.Delete(freshTmp);
            File.Delete(path);
            var lone = Copy("creds.json.bak-" + hex(), old, Creds("someone-elses"));
            AtomicReplace.CleanUpLeftovers(path);
            Expect(!File.Exists(path) && !File.Exists(lone), "a backup alone is deleted, the file stays gone");
            var loneTmp = Copy("creds.json.tmp-" + hex(), old, Creds("revived"));
            AtomicReplace.CleanUpLeftovers(path);
            Expect(!File.Exists(path) && !File.Exists(loneTmp), "a temp copy alone is deleted, the file stays gone");
            var apartTmp = Copy("creds.json.tmp-" + hex(), old, Creds("revived"));
            var apartBak = Copy("creds.json.bak-" + hex(), old.AddMinutes(-30), Creds("spent"));
            AtomicReplace.CleanUpLeftovers(path);
            Expect(!File.Exists(path) && !File.Exists(apartTmp) && !File.Exists(apartBak), "the two far apart in time are both deleted");

            // Even the scene of a swap cut short: at start-up nothing says what the copies hold.
            var sceneBak = Copy("creds.json.bak-" + hex(), old, Creds("spent"));
            var sceneTmp = Copy("creds.json.tmp-" + hex(), old.AddSeconds(3), Creds("fresh"));
            AtomicReplace.CleanUpLeftovers(path);
            Expect(!File.Exists(path) && !File.Exists(sceneBak) && !File.Exists(sceneTmp), "a temp and a backup stamped together are deleted too, the file stays gone");
        });
    }

    private static void RestoreInterruptedChecksWhatIsThere()
    {
        InTempDirectory((directory, path) =>
        {
            AtomicReplace.ResetForTests();
            Func<JsonObject, bool> isNew = root => (string?)root["login"]?["token"] == "new";
            Func<JsonObject, bool> isOld = root => (string?)root["login"]?["token"] == "old";
            void HalfFail()
            {
                AtomicReplace.ReplaceFile = (source, target, backup) =>
                {
                    File.Move(target, backup);
                    Directory.CreateDirectory(target);
                    throw new IOException("fixture");
                };
                File.WriteAllText(path, Creds("old"));
                Expect(AtomicReplace.Run(path, PlanFor("old")).Result == AtomicReplace.Result.Failed, "a swap that cannot be finished");
                Directory.Delete(path);
            }
            int Copies() => Directory.GetFiles(directory, "*.bak-*").Length + Directory.GetFiles(directory, "*.tmp-*").Length;

            HalfFail();
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.Restored && TokenIn(path) == "new", "the new copy it left is put back");
            Expect(Directory.GetFiles(directory).Length == 1, "and the backup is not left behind");

            File.Delete(path);
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.None, "nothing recorded, nothing put back: a removed file is a sign-out");

            File.WriteAllText(path, Creds("old"));
            HalfFail();
            var backup = Directory.GetFiles(directory, "*.bak-*").Single();
            File.WriteAllText(backup, Creds("someone-else"));
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.None && !File.Exists(path), "a backup that is not the login being replaced is not trusted");
            Expect(Copies() == 0, "and the copies are deleted, the record forgotten");

            // A backup lying there that this process never recorded is not the file.
            File.WriteAllText(Path.Combine(directory, "creds.json.bak-" + Guid.NewGuid().ToString("N")), Creds("old"));
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.None && !File.Exists(path), "a stray backup does not undo a sign-out");

            // A backup that cannot be read right now is a wait: the record stays for the next try.
            File.Delete(Directory.GetFiles(directory, "*.bak-*").Single());
            File.WriteAllText(path, Creds("old"));
            HalfFail();
            backup = Directory.GetFiles(directory, "*.bak-*").Single();
            using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.Unsure, "unreadable: not yet known");
            }
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.Restored && TokenIn(path) == "new", "and the next try puts it back");

            // The file is back: the old scene is forgotten, and a later removal is a sign-out.
            File.Delete(path);
            File.WriteAllText(path, Creds("old"));
            HalfFail();
            File.WriteAllText(path, Creds("someone-else"));
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.None && Copies() == 0, "a file that is there clears what a cut-short swap left");
            File.Delete(path);
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.None && !File.Exists(path), "and removing it afterwards stays removed");

            // A new run on a file that is there forgets it too.
            File.WriteAllText(path, Creds("old"));
            HalfFail();
            File.WriteAllText(path, Creds("old"));
            AtomicReplace.ReplaceFile = (source, target, backup2) => File.Replace(source, target, backup2);
            AtomicReplace.Run(path, PlanFor("old"));
            Expect(Copies() == 0, "a new write to the same file clears the old scene");
            File.Delete(path);
            Expect(AtomicReplace.RestoreInterrupted(path, isNew, isOld) == AtomicReplace.Restoration.None, "nothing to put back afterwards");
            AtomicReplace.ResetForTests();
        });
    }

    private static void LogRollsAndRecovers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentisland-log-" + Guid.NewGuid().ToString("N"));
        var previousCache = AgentIsland.Core.IslandPaths.CacheDirOverride;
        try
        {
            AgentIsland.Core.IslandPaths.CacheDirOverride = directory;
            CredentialRefreshLog.Write = defaultWrite;
            CredentialRefreshLog.ResetForTests();
            for (var i = 0; i < 800; i++) CredentialRefreshLog.Note("claude", $"reason {i} " + new string('x', 90));
            Expect(File.Exists(CredentialRefreshLog.LogPath + ".old"), "past 64 KB the log moves aside");
            Expect(new FileInfo(CredentialRefreshLog.LogPath).Length < 64 * 1024 + 4096, "and the live file starts again");
        }
        finally
        {
            AgentIsland.Core.IslandPaths.CacheDirOverride = previousCache;
            try { Directory.Delete(directory, recursive: true); } catch { }
        }

        var lines = new List<string>();
        CredentialRefreshLog.Write = line => { lines.Add(line); return true; };
        CredentialRefreshLog.ResetForTests();
        CredentialRefreshLog.Recovered("claude");
        Expect(lines.Count, 0);
        CredentialRefreshLog.Note("claude", "ambiguous: timeout");
        CredentialRefreshLog.Note("claude", "ambiguous: timeout");
        CredentialRefreshLog.Recovered("claude");
        CredentialRefreshLog.Recovered("claude");
        CredentialRefreshLog.Note("claude", "ambiguous: timeout");
        Expect(lines.Count == 3 && lines[1].EndsWith("claude: recovered") && lines[2].EndsWith("claude: ambiguous: timeout"),
            $"the same trouble after a recovery is written again, got {string.Join(" | ", lines)}");
    }

    private static void LogDeduplicates()
    {
        var lines = new List<string>();
        var busy = true;
        CredentialRefreshLog.Write = line =>
        {
            if (busy) return false;
            lines.Add(line);
            return true;
        };
        CredentialRefreshLog.Note("claude", "ambiguous: timeout");
        busy = false;
        CredentialRefreshLog.Note("claude", "ambiguous: timeout");
        CredentialRefreshLog.Note("claude", "ambiguous: timeout");
        CredentialRefreshLog.Note("grok", "ambiguous: timeout");
        CredentialRefreshLog.Note("claude", "write-back failed, holding rotated tokens in memory");
        Expect(lines.Count == 3, $"a line lost to a busy file is tried again, then each change is written once, got {lines.Count}");
        CredentialRefreshLog.Write = _ => throw new InvalidOperationException("fixture");
        CredentialRefreshLog.Note("claude", "another reason");
    }
}
