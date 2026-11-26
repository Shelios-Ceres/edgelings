using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Antigravity's Gemini pools: the five-bucket reply a Google AI Pro account
/// returns, how its main group fills the 5-hour and weekly slots, the extra
/// Gemini 4 Argon meter, and the rule that only the main group's pools put
/// the bot to sleep. Claude and GPT's pool stays out of every view.
public static class AntigravityQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private const string FiveBucketReply = """
        {"response":{"groups":[
          {"displayName":"Gemini Models","buckets":[
            {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.98,"resetTime":"2026-10-25T11:36:10Z"},
            {"bucketId":"gemini-5h","window":"5h","remainingFraction":1,"resetTime":"2026-10-21T06:10:54Z"}]},
          {"displayName":"Claude and GPT models","buckets":[
            {"bucketId":"3p-weekly","window":"weekly","remainingFraction":1,"resetTime":"2026-10-28T01:10:54Z"},
            {"bucketId":"3p-5h","window":"5h","remainingFraction":1,"resetTime":"2026-10-21T06:10:54Z"}]},
          {"displayName":"Gemini 4 Argon","buckets":[
            {"bucketId":"gemini-argon-wtu-weekly","window":"weekly","remainingFraction":0.25,"resetTime":"2026-10-25T11:35:55Z"}]}
        ],"description":"Within each group, models share a weekly limit."}}
        """;

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("the parser keeps all five buckets of a Pro account", TestParsesFiveBuckets),
            ("only this process's own hub file gives its token", TestHubToken),
            ("the hub file's token comes before the command line", TestCsrfTokenPrefersHub),
            ("a server without GetUnleashData is found and authenticated from its hub file", TestDiscoverWithoutUnleashData),
            ("the diagnostic tells hub files, discovery and the quota call apart, and never shows a token", TestDiagnosticLines),
            ("the single-process Windows Job Object blocks grandchild spawns", TestSingleProcessJob),
            ("with no language server the last snapshot stays, with its sync time and no warning", TestNoServerKeepsSnapshot),
            ("with no language server and no snapshot the card gives a neutral hint, not a sign-in prompt", TestNoServerNoSnapshotShowsNeutralHint),
            ("a snapshot older than eight days is still restored after a restart", TestOldSnapshotSurvivesRestart),
            ("a language server answering 401 keeps the snapshot under a quota HTTP 401 caption", TestServer401KeepsSnapshotUnderCaption),
            ("a cancelled token ends a quota call still waiting on the server", TestCancelEndsPendingQuotaCall),
            ("refresh cadence keeps the 120s background floor and 1-minute manual floor", TestRefreshCadence),
            ("the main group fills the 5-hour and weekly slots", TestSlots),
            ("a snapshot with no 5-hour bucket keeps one weekly window", TestSingleWindow),
            ("a snapshot with no Gemini bucket falls back to its first", TestNoGemini),
            ("the card shows 5h, Week and the Argon pool, never Claude or GPT", TestMeters),
            ("a single window keeps one meter, and no snapshot shows a dash", TestMeterFallbacks),
            ("each meter is labelled by its own window", TestMeterLabels),
            ("only the main group's pools put the bot to sleep", TestSleepRule),
            ("a snapshot saved before this change still restores", TestOldSnapshotRoundTrip),
            ("a failed refresh keeps every pool under its caption", TestKeptPools),
            ("a spent bucket whose zero fraction was left out stays, at 100%", TestOmittedZeroFraction),
            ("only Antigravity's own language server is asked, never Windsurf's or Codeium's", TestLanguageServerOwner),
            ("a port is traced to the process listening on it", TestPortOwner),
            ("a port with several different listeners has no owner, and an exact 127.0.0.1 bind wins", TestPortOwnerAmbiguity),
            ("a process's CSRF token is read once, and looked for again only after a while", TestCsrfTokenIsRemembered),
            ("a quoted or equals-separated CSRF token is still read from the command line", TestParseCsrfToken),
            ("a CSRF retry surfaces its real failure instead of reporting 401", TestCsrfRetrySurfacesRealReply),
            ("discovery skips a 401-only port when another Antigravity port authenticates", TestDiscoverPrefersAuthenticatedPort),
            ("a helper that hangs is stopped at its timeout", TestHelperTimeout),
        };
        // This machine's own hub files must never reach a test.
        var realHub = AntigravityLanguageServer.HubFiles;
        AntigravityLanguageServer.HubFiles = Array.Empty<string>;
        try
        {
            foreach (var (name, test) in tests)
            {
                test();
                Console.WriteLine($"PASS antigravity quota: {name}");
            }
        }
        finally
        {
            AntigravityLanguageServer.HubFiles = realHub;
        }
        Console.WriteLine("AntigravityQuotaTests GREEN");
    }

    private static AntigravityQuotaSnapshot Pro(double argonUsed = 0.75, double weeklyUsed = 0.02, double fiveHourUsed = 0)
    {
        var parsed = AntigravityQuotaParser.ParseQuotaSummary(Encoding.UTF8.GetBytes(FiveBucketReply));
        var buckets = parsed!.Value.Buckets
            .Select(b => b.BucketId switch
            {
                "gemini-argon-wtu-weekly" => b with { UsedPercent = argonUsed },
                "gemini-weekly" => b with { UsedPercent = weeklyUsed },
                "gemini-5h" => b with { UsedPercent = fiveHourUsed },
                _ => b,
            })
            .ToList();
        return new AntigravityQuotaSnapshot(buckets, "g1-pro-tier", "Google AI Pro");
    }

    private static IReadOnlyList<StatusMeter> Meters(AntigravityQuotaSnapshot? snapshot, string? caption = null)
    {
        var usage = ProviderUsage.AntigravityFrom(snapshot, caption, null);
        return StatusCards.AntigravityMeters(usage, snapshot, caption,
            (window, label) => StatusCards.MeterFor(window, label, showsRemaining: false, Now));
    }

    private static void TestParsesFiveBuckets()
    {
        var parsed = AntigravityQuotaParser.ParseQuotaSummary(Encoding.UTF8.GetBytes(FiveBucketReply));
        Expect(parsed is not null, "the reply parses");
        var (buckets, note) = parsed!.Value;
        Expect(buckets.Count, 5);
        Expect(buckets.Select(b => b.BucketId).SequenceEqual(
            new[] { "gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h", "gemini-argon-wtu-weekly" }),
            "the buckets keep Google's order");
        Expect(Math.Round(buckets[0].UsedPercent, 2), 0.02);
        Expect(buckets[4].UsedPercent, 0.75);
        Expect(buckets[4].GroupLabel, "Gemini 4 Argon");
        Expect(buckets[1].PeriodSeconds, 5.0 * 3600);
        Expect(note is not null, "Google's note comes along");
    }

    private sealed class HubFolder : IDisposable
    {
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("limen-hub-");
        public List<string> Files { get; } = new();

        public string Add(object content, DateTime writtenUtc)
        {
            var path = Path.Combine(_folder.FullName, $"hub_{Files.Count}.json");
            File.WriteAllText(path, content as string ?? JsonSerializer.Serialize(content));
            File.SetLastWriteTimeUtc(path, writtenUtc);
            Files.Add(path);
            return path;
        }

        public void Dispose() => _folder.Delete(recursive: true);
    }

    private static void TestHubToken()
    {
        var prevHub = AntigravityLanguageServer.HubFiles;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        try
        {
            using var hub = new HubFolder();
            AntigravityLanguageServer.HubFiles = () => hub.Files;
            AntigravityLanguageServer.ListListeningPorts = pid => pid == 41852 ? new List<int> { 55845, 55846 } : new List<int> { 60000 };
            var started = new DateTime(2026, 11, 25, 0, 28, 6, DateTimeKind.Utc);

            // A reused pid's file from days ago, another process's file, and garbage come first.
            hub.Add(new { pid = 41852, httpsPort = 55845, httpPort = 55846, csrfToken = "old" }, started.AddDays(-3));
            hub.Add(new { pid = 7, httpsPort = 60000, httpPort = 60001, csrfToken = "other" }, started.AddSeconds(1));
            hub.Add("not json", started.AddSeconds(1));
            Expect(AntigravityLanguageServer.HubToken(41852, started) is null, "no file of this process yet");

            hub.Add(new { pid = 41852, httpsPort = 1, httpPort = 2, csrfToken = "wrong-ports" }, started.AddSeconds(1));
            Expect(AntigravityLanguageServer.HubToken(41852, started) is null, "a file naming ports the process does not hold is not its own");
            hub.Add(new { pid = 99, httpsPort = 55845, httpPort = 55846, csrfToken = "wrong-pid" }, started.AddSeconds(1));
            Expect(AntigravityLanguageServer.HubToken(41852, started) is null, "a fresh file naming its ports but another pid is not its own");

            // Written a fraction of a second after the process started, ms dropped on one side.
            hub.Add(new { pid = 41852, httpsPort = 55845, httpPort = 55846, lsVersion = "2.35.1", csrfToken = "tok-hub" }, started.AddMilliseconds(-500));
            Expect(AntigravityLanguageServer.HubToken(41852, started), "tok-hub");
            Expect(AntigravityLanguageServer.HubToken(41852, null) is null, "an unknown start time proves nothing");

            // Rewritten by the same server later: the newest file wins.
            hub.Add(new { pid = 41852, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-newer" }, started.AddMinutes(5));
            Expect(AntigravityLanguageServer.HubToken(41852, started), "tok-newer");
        }
        finally
        {
            AntigravityLanguageServer.HubFiles = prevHub;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
        }
    }

    private static void TestCsrfTokenPrefersHub()
    {
        var prevHub = AntigravityLanguageServer.HubFiles;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        var prevStarted = AntigravityLanguageServer.StartTime;
        var reads = 0;
        try
        {
            using var hub = new HubFolder();
            var pid = Environment.ProcessId;
            var started = DateTime.UtcNow.AddMinutes(-1);
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.HubFiles = () => hub.Files;
            AntigravityLanguageServer.StartTime = _ => started;
            AntigravityLanguageServer.ListListeningPorts = _ => new List<int> { 55845 };
            AntigravityLanguageServer.ReadCommandLine = _ => { reads++; return null; };

            // The server has not written its file yet: the command line is read once.
            Expect(AntigravityLanguageServer.CsrfToken(pid) is null, "nothing yet");
            Expect(reads, 1);
            // The file appears: it is found on the next ask, without waiting out the command line's pause.
            hub.Add(new { pid, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-hub" }, DateTime.UtcNow);
            Expect(AntigravityLanguageServer.CsrfToken(pid), "tok-hub");
            Expect(AntigravityLanguageServer.CsrfToken(pid), "tok-hub");
            Expect(reads, 1);
        }
        finally
        {
            AntigravityLanguageServer.HubFiles = prevHub;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    // The desktop's 2.35 server answers GetUnleashData with 404.
    private static void TestDiscoverWithoutUnleashData()
    {
        var prevCall = AntigravityLanguageServer.CallRpc;
        var prevPids = AntigravityLanguageServer.ListAntigravityProcessIds;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevOwner = AntigravityLanguageServer.ResolvePortOwner;
        var prevStarted = AntigravityLanguageServer.StartTime;
        var prevHub = AntigravityLanguageServer.HubFiles;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        try
        {
            using var hub = new HubFolder();
            var started = DateTime.UtcNow.AddMinutes(-1);
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.StartTime = _ => started;
            AntigravityLanguageServer.HubFiles = () => hub.Files;
            AntigravityLanguageServer.ReadCommandLine = _ => "language_server.exe --read_host_bridge_token_from_stdin";
            AntigravityLanguageServer.ListAntigravityProcessIds = () => new List<int> { 201 };
            AntigravityLanguageServer.ListListeningPorts = _ => new List<int> { 55845, 55846 };
            AntigravityLanguageServer.ResolvePortOwner = port => port is 55845 or 55846 ? 201 : null;
            hub.Add(new { pid = 201, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-hub" }, DateTime.UtcNow);
            var asked = new List<string>();
            AntigravityLanguageServer.CallRpc = (method, port, _, token, _) =>
            {
                asked.Add($"{method}@{port}:{token ?? "-"}");
                return Task.FromResult<AntigravityLanguageServer.Reply?>(port != 55845
                    ? null
                    : method == "GetUnleashData" ? new(404, Array.Empty<byte>())
                    : token == "tok-hub" ? new(200, Array.Empty<byte>())
                    : new(401, Array.Empty<byte>()));
            };
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            Expect(asked.Contains("GetUserStatus@55845:tok-hub"), $"the probe authenticates with the hub token, asked {string.Join(", ", asked)}");

            // A server that has neither method is no RPC port.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.CallRpc = (_, _, _, _, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(new(404, Array.Empty<byte>()));
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult() is null, "404 on both probes is not a port");

            // An older server without GetUserStatus is still found through GetUnleashData,
            // and so is one whose GetUserStatus is slow (null) or failing (500).
            foreach (var first in new AntigravityLanguageServer.Reply?[] { new(404, Array.Empty<byte>()), null, new(500, Array.Empty<byte>()) })
            {
                AntigravityLanguageServer.ForgetCachedPort();
                AntigravityLanguageServer.CallRpc = (method, port, _, _, _) => Task.FromResult(
                    port != 55845 ? null
                    : method == "GetUnleashData" ? new AntigravityLanguageServer.Reply(200, Array.Empty<byte>())
                    : first);
                Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            }

            // A token the server refuses is dropped: the hub file rewritten with a new one is read.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            var accepted = "tok-rotated";
            AntigravityLanguageServer.CallRpc = (_, port, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port == 55845 && token == accepted ? new(200, Array.Empty<byte>()) : new(401, Array.Empty<byte>()));
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            hub.Add(new { pid = 201, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-rotated" }, DateTime.UtcNow.AddSeconds(5));
            AntigravityLanguageServer.ForgetCachedPort();
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            Expect(AntigravityLanguageServer.CsrfToken(201), "tok-rotated");
        }
        finally
        {
            AntigravityLanguageServer.CallRpc = prevCall;
            AntigravityLanguageServer.ListAntigravityProcessIds = prevPids;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ResolvePortOwner = prevOwner;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.HubFiles = prevHub;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static void TestDiagnosticLines()
    {
        var prevCall = AntigravityLanguageServer.CallRpc;
        var prevPids = AntigravityLanguageServer.ListAntigravityProcessIds;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevOwner = AntigravityLanguageServer.ResolvePortOwner;
        var prevStarted = AntigravityLanguageServer.StartTime;
        var prevHub = AntigravityLanguageServer.HubFiles;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        var prevName = AntigravityDiagnostics.ProcessName;
        try
        {
            using var hub = new HubFolder();
            var started = DateTime.UtcNow.AddMinutes(-1);
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.HubFiles = () => hub.Files;
            AntigravityLanguageServer.ReadCommandLine = _ => null;
            AntigravityLanguageServer.StartTime = pid => pid == 201 ? started : null;
            AntigravityLanguageServer.ListAntigravityProcessIds = () => new List<int> { 201 };
            AntigravityLanguageServer.ListListeningPorts = pid => pid == 201 ? new List<int> { 55845, 55846 } : new List<int>();
            AntigravityLanguageServer.ResolvePortOwner = port => port is 55845 or 55846 ? 201 : null;
            AntigravityDiagnostics.ProcessName = pid => pid == 201 ? "agy" : null;

            // Nothing written yet: no files, and the probe finds no answering port.
            AntigravityLanguageServer.CallRpc = (_, _, _, _, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(null);
            var empty = AntigravityDiagnostics.Describe();
            Expect(empty[0].Contains("0 found, 0 match"), "no hub files: " + empty[0]);
            Expect(empty.Any(l => l.Contains("no port found, probe Dead")), "a silent server is Dead: " + string.Join(" | ", empty));
            Expect(!empty.Any(l => l.Contains("RetrieveUserQuotaSummary")), "no port, no quota call");

            // One file that counts, and four that must not: a stale one, a dead pid, a port the process does not hold, garbage.
            hub.Add(new { pid = 201, httpsPort = 55845, httpPort = 55846, csrfToken = "secret-live" }, started.AddSeconds(1));
            hub.Add(new { pid = 201, httpsPort = 55845, httpPort = 55846, csrfToken = "secret-stale" }, started.AddDays(-3));
            hub.Add(new { pid = 7, httpsPort = 55845, httpPort = 55846, csrfToken = "secret-dead" }, started.AddSeconds(1));
            hub.Add(new { pid = 201, httpsPort = 1, httpPort = 2, csrfToken = "secret-ports" }, started.AddSeconds(1));
            hub.Add("not json", started.AddSeconds(1));
            Expect(AntigravityLanguageServer.HubCensus(), (5, 1, 1));

            // The server wants the token; with it, the quota arrives in five buckets.
            AntigravityLanguageServer.CallRpc = (method, port, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port != 55845 ? null
                : token != "secret-live" ? new(401, Array.Empty<byte>())
                : method == "RetrieveUserQuotaSummary" ? new(200, Encoding.UTF8.GetBytes(FiveBucketReply))
                : new(200, Array.Empty<byte>()));
            var lines = AntigravityDiagnostics.Describe();
            var text = string.Join("\n", lines);
            Expect(lines[0].Contains("5 found, 1 match") && lines[0].Contains("1 of those carry a token"), lines[0]);
            Expect(text.Contains("1 Antigravity process(es), 2 listening port(s)"), text);
            Expect(text.Contains("a port owned by agy, probe Ready, hub token for that process yes"), text);
            Expect(text.Contains("RetrieveUserQuotaSummary HTTP 200, 5 bucket(s)"), text);
            Expect(!text.Contains("secret"), "no token reaches the report");

            // A token the server refuses leaves the 401 and no buckets.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.CallRpc = (_, port, _, _, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port == 55845 ? new(401, Array.Empty<byte>()) : null);
            var refused = string.Join("\n", AntigravityDiagnostics.Describe());
            Expect(refused.Contains("probe Unauthorized"), refused);
            Expect(refused.Contains("RetrieveUserQuotaSummary HTTP 401, no buckets"), refused);

            // Another product's server is named, not hidden.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityDiagnostics.ProcessName = _ => "language_server_macos_arm";
            Expect(string.Join("\n", AntigravityDiagnostics.Describe()).Contains("a port owned by language_server,"), "language_server is told apart");
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityDiagnostics.ProcessName = _ => "antigravity";
            Expect(string.Join("\n", AntigravityDiagnostics.Describe()).Contains("a port owned by antigravity,"), "the desktop app is told apart");
        }
        finally
        {
            AntigravityLanguageServer.CallRpc = prevCall;
            AntigravityLanguageServer.ListAntigravityProcessIds = prevPids;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ResolvePortOwner = prevOwner;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.HubFiles = prevHub;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityDiagnostics.ProcessName = prevName;
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static IDisposable GeminiHomeWithAntigravity()
    {
        var root = Path.Combine(AntigravityCredentials.GeminiHome, "antigravity");
        var created = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        return new Cleanup(() => { if (created) Directory.Delete(root, recursive: true); });
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // Neither the app nor agy is running: Limen starts neither, so nothing is
    // read, and the card keeps its last numbers with their time. It is not a
    // sign-in problem and must not look like one.
    private static void TestNoServerKeepsSnapshot()
    {
        using var home = GeminiHomeWithAntigravity();
        var store = AntigravityUsageStore.Shared;
        try
        {
            AntigravityLanguageServer.ForgetCachedPort();
            var outcome = AntigravityUsageFetcher.Fetch().GetAwaiter().GetResult();
            Expect(outcome is AntigravityUsageFetcher.Outcome.NotRunning, "no server is NotRunning, not a failure");

            var saved = Pro(argonUsed: 0.5);
            store.Apply(new AntigravityUsageFetcher.Outcome.Success(saved, "me@example.com"));
            var syncedAt = store.LastUpdated;
            Expect(syncedAt is not null, "the good reading is stamped");

            store.Apply(outcome);
            Expect(ReferenceEquals(store.Snapshot, saved), "the snapshot stays");
            Expect(store.LastUpdated == syncedAt, "with the time of the reading it shows");
            Expect(store.AccountEmail, "me@example.com");
            Expect(store.StatusCaption is null, "and no warning or sign-in line");
            Expect(AgentReports.Warning(DisplayProvider.Antigravity) is null, "the character carries no warning");
            Expect(Meters(store.Snapshot, store.StatusCaption).All(m => m.Value != "—"), "every meter keeps its number");
        }
        finally
        {
            store.ResetForTests();
            AntigravityLanguageServer.ForgetCachedPort();
        }
    }

    // Nothing was ever read: the card says how to get a reading, in words
    // that are not about signing in, and the numbers show a dash.
    private static void TestNoServerNoSnapshotShowsNeutralHint()
    {
        var store = AntigravityUsageStore.Shared;
        try
        {
            store.ResetForTests();
            store.Apply(new AntigravityUsageFetcher.Outcome.NotRunning());
            Expect(store.Snapshot is null, "no snapshot appears");
            Expect(store.StatusCaption, L10n.Tr("Open Antigravity or agy to sync"));
            Expect(!L10n.Tr("Open Antigravity or agy to sync").Contains("sign", StringComparison.OrdinalIgnoreCase)
                && !L10n.Tr("Open Antigravity or agy to sync").Contains("登录"), "and it never talks of signing in");
            Expect(AgentReports.Warning(DisplayProvider.Antigravity) is { Length: > 0 }, "the hint reaches the card");
            Expect(Meters(null, store.StatusCaption).All(m => m.Value == "—"), "every meter shows a dash");
            var language = L10n.Current;
            try
            {
                L10n.Current = L10n.Language.SimplifiedChinese;
                var zh = L10n.Tr("Open Antigravity or agy to sync");
                Expect(zh != "Open Antigravity or agy to sync" && !zh.Contains("登录"), "the Chinese line exists and never talks of signing in");
            }
            finally { L10n.Current = language; }
        }
        finally { store.ResetForTests(); }
    }

    // A week of an app left closed must not blank the card: the snapshot
    // is kept at any age, with the time it was read.
    private static void TestOldSnapshotSurvivesRestart()
    {
        using var home = GeminiHomeWithAntigravity();
        var readAt = DateTimeOffset.Now.AddDays(-30);
        try
        {
            Preferences.Set(AntigravityUsageStore.CacheKey,
                new AntigravityUsageStore.CachedSnapshot(Pro(), readAt, "me@example.com"));
            var restarted = new AntigravityUsageStore();
            Expect(restarted.Snapshot is not null, "a 30-day-old snapshot is restored");
            Expect(restarted.LastUpdated == readAt, "with the time it was read");
            Expect(restarted.AccountEmail, "me@example.com");
            Expect(restarted.StatusCaption is null, "and no warning");
        }
        finally { Preferences.Remove(AntigravityUsageStore.CacheKey); }
    }

    // The server is there but turns Limen away. That is a real fault worth
    // saying, so the caption stays and the numbers stay under it.
    private static void TestServer401KeepsSnapshotUnderCaption()
    {
        using var home = GeminiHomeWithAntigravity();
        var store = AntigravityUsageStore.Shared;
        var prevCall = AntigravityLanguageServer.CallRpc;
        var prevPids = AntigravityLanguageServer.ListAntigravityProcessIds;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevOwner = AntigravityLanguageServer.ResolvePortOwner;
        try
        {
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.ListAntigravityProcessIds = () => new List<int> { 101 };
            AntigravityLanguageServer.ListListeningPorts = _ => new List<int> { 51001 };
            AntigravityLanguageServer.ResolvePortOwner = _ => null;
            AntigravityLanguageServer.CallRpc = (_, _, _, _, _) =>
                Task.FromResult<AntigravityLanguageServer.Reply?>(new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));

            var outcome = AntigravityUsageFetcher.Fetch().GetAwaiter().GetResult();
            Expect(outcome is AntigravityUsageFetcher.Outcome.Failed { Message: "quota HTTP 401" }, "a refusing server is a failure with its status");

            var saved = Pro();
            store.Apply(new AntigravityUsageFetcher.Outcome.Success(saved, null));
            store.Apply(outcome);
            Expect(ReferenceEquals(store.Snapshot, saved), "the snapshot stays");
            Expect(store.StatusCaption, "quota HTTP 401");
            Expect(Meters(store.Snapshot, store.StatusCaption).All(m => m.Value != "—"), "the numbers stay under the caption");
        }
        finally
        {
            AntigravityLanguageServer.CallRpc = prevCall;
            AntigravityLanguageServer.ListAntigravityProcessIds = prevPids;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ResolvePortOwner = prevOwner;
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            store.ResetForTests();
        }
    }

    // Hiding Antigravity cancels the poll's token: a call still waiting on
    // the server ends at once instead of running beside the next poll.
    private static void TestCancelEndsPendingQuotaCall()
    {
        var prevCall = AntigravityLanguageServer.CallRpc;
        try
        {
            AntigravityLanguageServer.CallRpc = (_, _, _, _, _) =>
                new TaskCompletionSource<AntigravityLanguageServer.Reply?>().Task;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var cancelled = false;
            try { AntigravityUsageFetcher.CallWithCsrfRetry("RetrieveUserQuotaSummary", 50001, "{}", cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Expect(cancelled, "the call ends as cancelled");
            Expect(clock.Elapsed < TimeSpan.FromSeconds(3), $"and long before the server answers, took {clock.Elapsed}");
        }
        finally { AntigravityLanguageServer.CallRpc = prevCall; }
    }

    private static void TestSingleProcessJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        var single = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c echo ok",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Expect(WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5)), "ok");
        var folder = Directory.CreateTempSubdirectory("limen-job-");
        try
        {
            single.WorkingDirectory = folder.FullName;
            single.Arguments = "/d /c echo started>marker";
            Expect(WindowsJobProcess.Run(0, single, TimeSpan.FromSeconds(5), null) is null,
                "invalid job prevents creation");
            Expect(WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5), _ => false) is null,
                "hidden launch is denied");
            Expect(!File.Exists(Path.Combine(folder.FullName, "marker")), "main thread never ran");
            File.WriteAllText(Path.Combine(folder.FullName, "child.cmd"), "@echo child>child");
            single.Arguments = "/d /c echo started>parent & cmd.exe /d /c child.cmd";
            WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5));
            Expect(File.Exists(Path.Combine(folder.FullName, "parent")) && !File.Exists(Path.Combine(folder.FullName, "child")),
                "parent entered its job and the child produced no marker");
            single.Environment["ZZ_LIMEN_SENTINEL"] = "isolated-value";
            single.Arguments = "/d /c echo %ZZ_LIMEN_SENTINEL%";
            Expect(WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5)), "isolated-value");
        }
        finally { folder.Delete(true); }

        var wait = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 12 127.0.0.1");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Expect(WindowsJobProcess.RunInSingleProcessJob(wait, TimeSpan.FromMilliseconds(150)) is null, "timeout stops the job");
        Expect(clock.Elapsed < TimeSpan.FromSeconds(5), "pipe cleanup respects timeout");

        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
        {
            var sleeper = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            clock.Restart();
            Expect(WindowsJobProcess.RunInSingleProcessJob(sleeper, TimeSpan.FromSeconds(60), null, cancel.Token) is null, "cancel stops the job");
            Expect(clock.Elapsed < TimeSpan.FromSeconds(5), "cancel ends the wait long before the timeout");
        }

        var spawnChild = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c cmd.exe /c echo SHOULD_NOT_RUN",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Expect(WindowsJobProcess.RunInSingleProcessJob(spawnChild, TimeSpan.FromSeconds(5)) is null,
            "ActiveProcessLimit=1 must block the child's own CreateProcess calls at kernel level");
    }

    private static void TestRefreshCadence()
    {
        Expect(AntigravityUsageStore.EffectiveFloor(manual: false), TimeSpan.FromSeconds(120));
        Expect(AntigravityUsageStore.EffectiveFloor(manual: true), TimeSpan.FromMinutes(1));
    }

    private static void TestSlots()
    {
        var pools = Pro().GeminiPools;
        Expect(pools.ShortWindow?.BucketId, "gemini-5h");
        Expect(pools.LongWindow?.BucketId, "gemini-weekly");
        Expect(pools.Extras.Count, 1);
        Expect(pools.Extras[0].BucketId, "gemini-argon-wtu-weekly");

        var usage = ProviderUsage.AntigravityFrom(Pro(), null, "Google AI Pro");
        Expect(usage.FiveHour.PeriodSeconds, 5.0 * 3600);
        Expect(usage.Weekly.PeriodSeconds, (double?)AntigravityQuotaBucket.WeekSeconds);
        Expect(Math.Round(usage.Weekly.UsedPercent, 2), 0.02);
        Expect(!usage.SecondaryMissing, "two windows are not a single-window provider");
        Expect(usage.Plan, "google ai pro");
    }

    private static void TestLanguageServerOwner()
    {
        const string server = "language_server_windows_x64";
        static bool Is(string name, string? path, string? parent) => AntigravityLanguageServer.IsAntigravityProcess(name, path, parent);
        Expect(Is("agy", null, "pwsh") && Is("Antigravity", null, "explorer") && Is("antigravity-cli", null, null), "the CLI and the desktop app");
        Expect(Is(server, @"C:\Users\u\AppData\Local\Programs\Antigravity\resources\app\extensions\antigravity\bin\language_server_windows_x64.exe", "Antigravity"),
            "the desktop app's own server");
        Expect(Is(server, @"D:\Tools\AG\bin\language_server_windows_x64.exe", "Antigravity"), "one Antigravity started, wherever it is installed");
        Expect(Is(server, @"C:\Users\u\AppData\Local\Programs\Antigravity\resources\bin\language_server.exe", null), "one under an Antigravity folder");
        Expect(!Is(server, @"C:\Users\u\AppData\Local\Programs\Windsurf\resources\app\extensions\windsurf\bin\language_server_windows_x64.exe", "Windsurf"),
            "Windsurf's server");
        Expect(!Is(server, @"C:\Users\u\.vscode\extensions\codeium.codeium-1.40.0\dist\abc\language_server_windows_x64.exe", "Code"),
            "Codeium's server in VS Code");
        Expect(!Is(server, null, null), "a server whose path and parent can't be read");
        Expect(!Is("language_server_antigravity", @"C:\Tools\codeium\language_server_antigravity.exe", "Code"), "the file name alone says nothing");
        Expect(!Is("Windsurf", null, null), "another editor");
        var self = AntigravityLanguageServer.Processes().FirstOrDefault(p => p.Id == Environment.ProcessId);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        Expect(self.Name == current.ProcessName && self.ParentId > 0, $"the process list names this very process, got '{self.Name}'");
    }

    private static void TestPortOwner()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            Expect(AntigravityLanguageServer.PortOwner(port), (int?)Environment.ProcessId);
            Expect(AntigravityLanguageServer.ListeningPorts(Environment.ProcessId).Contains(port), "and listed under it");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void TestPortOwnerAmbiguity()
    {
        var rows = AntigravityLanguageServer.ParseLsofRows("p101\nn*:5000\nn127.0.0.1:5001\np202\nn*:5000\nn127.0.0.1:5002\np303\nn*:5002\nn[::1]:5003\n");
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5000), null);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5001), (int?)101);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5002), (int?)202);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5003), null);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 6000), null);
        Expect(AntigravityLanguageServer.OwnerOf(new[] { (7, 5000, false), (7, 5000, false) }, 5000), (int?)7);
        Expect(AntigravityLanguageServer.ParseLsof("p101\nn127.0.0.1:5001\n").Count, 1);
    }

    private static void TestCsrfTokenIsRemembered()
    {
        var previous = AntigravityLanguageServer.ReadCommandLine;
        var reads = 0;
        try
        {
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.ReadCommandLine = _ =>
            {
                reads++;
                return "language_server.exe --csrf_token abc-123 --extension_server_port 5000";
            };
            var pid = Environment.ProcessId;
            Expect(AntigravityLanguageServer.CsrfToken(pid), "abc-123");
            Expect(AntigravityLanguageServer.CsrfToken(pid), "abc-123");
            Expect(reads, 1);

            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.ReadCommandLine = _ =>
            {
                reads++;
                return null;
            };
            reads = 0;
            Expect(AntigravityLanguageServer.CsrfToken(pid) is null, "a read that fails finds no token");
            Expect(AntigravityLanguageServer.CsrfToken(pid) is null, "and is not repeated on the next fetch");
            Expect(reads, 1);
        }
        finally
        {
            AntigravityLanguageServer.ReadCommandLine = previous;
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static void TestParseCsrfToken()
    {
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token abc-123 --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token=abc-123 --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token \"abc-123\" --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token=\"abc-123\" --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe \"--csrf_token=abc-123\" --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token 'abc-123'"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token='abc-123'"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token \"\" --port 1") is null, "empty quotes carry no token");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --port 1") is null, "missing flag returns null");
    }

    private static void TestCsrfRetrySurfacesRealReply()
    {
        var prevCall = AntigravityLanguageServer.CallRpc;
        var prevOwner = AntigravityLanguageServer.ResolvePortOwner;
        var prevStarted = AntigravityLanguageServer.StartTime;
        AntigravityLanguageServer.StartTime = _ => Now.UtcDateTime;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        try
        {
            AntigravityLanguageServer.ForgetCsrfTokens();
            var pid = Environment.ProcessId;
            AntigravityLanguageServer.ResolvePortOwner = _ => pid;
            AntigravityLanguageServer.ReadCommandLine = _ => "language_server.exe --csrf_token tok-1";

            // Authenticated retry succeeds.
            AntigravityLanguageServer.CallRpc = (_, _, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                token == "tok-1"
                    ? new AntigravityLanguageServer.Reply(200, Array.Empty<byte>())
                    : new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));
            var ok = AntigravityUsageFetcher.CallWithCsrfRetry("RetrieveUserQuotaSummary", 50001, "{}").GetAwaiter().GetResult();
            Expect(ok?.Status, 200);

            // Authenticated retry returns 503: must surface 503, not the initial 401.
            AntigravityLanguageServer.CallRpc = (_, _, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                token == "tok-1"
                    ? new AntigravityLanguageServer.Reply(503, Array.Empty<byte>())
                    : new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));
            var backendErr = AntigravityUsageFetcher.CallWithCsrfRetry("RetrieveUserQuotaSummary", 50001, "{}").GetAwaiter().GetResult();
            Expect(backendErr?.Status, 503);

            // Authenticated retry times out (null): must surface null, not the initial 401.
            AntigravityLanguageServer.CallRpc = (_, _, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                token == "tok-1"
                    ? null
                    : new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));
            var timedOut = AntigravityUsageFetcher.CallWithCsrfRetry("RetrieveUserQuotaSummary", 50001, "{}").GetAwaiter().GetResult();
            Expect(timedOut is null, "a timed-out retry returns null instead of 401");

            // No token available: stays 401.
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.ReadCommandLine = _ => "language_server.exe --port 50001";
            AntigravityLanguageServer.CallRpc = (_, _, _, _, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));
            var noToken = AntigravityUsageFetcher.CallWithCsrfRetry("RetrieveUserQuotaSummary", 50001, "{}").GetAwaiter().GetResult();
            Expect(noToken?.Status, 401);
        }
        finally
        {
            AntigravityLanguageServer.CallRpc = prevCall;
            AntigravityLanguageServer.ResolvePortOwner = prevOwner;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static void TestDiscoverPrefersAuthenticatedPort()
    {
        var prevCall = AntigravityLanguageServer.CallRpc;
        var prevPids = AntigravityLanguageServer.ListAntigravityProcessIds;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevOwner = AntigravityLanguageServer.ResolvePortOwner;
        var prevStarted = AntigravityLanguageServer.StartTime;
        AntigravityLanguageServer.StartTime = _ => Now.UtcDateTime;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        try
        {
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();

            // Port 51001 rejects everything with 401 (no token); port 51002 authenticates with its own token.
            AntigravityLanguageServer.ListAntigravityProcessIds = () => new List<int> { 101, 102 };
            AntigravityLanguageServer.ListListeningPorts = pid => pid == 101 ? new List<int> { 51001 } : new List<int> { 51002 };
            AntigravityLanguageServer.ResolvePortOwner = port => port == 51001 ? 101 : port == 51002 ? 102 : null;
            AntigravityLanguageServer.ReadCommandLine = pid => pid == 102 ? "language_server.exe --csrf_token \"good-tok\"" : "Antigravity.exe";
            AntigravityLanguageServer.CallRpc = (_, port, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port == 51002 && token == "good-tok"
                    ? new AntigravityLanguageServer.Reply(200, Array.Empty<byte>())
                    : new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));

            var discovered = AntigravityLanguageServer.Discover().GetAwaiter().GetResult();
            Expect(discovered, (int?)51002);

            // When only a 401 port exists, Discover returns it as a fallback without pinning it,
            // so a later ready port is picked up immediately.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            var cliStarted = false;
            AntigravityLanguageServer.ListAntigravityProcessIds = () => cliStarted ? new List<int> { 101, 103 } : new List<int> { 101 };
            AntigravityLanguageServer.ListListeningPorts = pid => pid == 101 ? new List<int> { 51001 } : new List<int> { 51003 };
            AntigravityLanguageServer.ResolvePortOwner = port => port == 51001 ? 101 : port == 51003 ? 103 : null;
            AntigravityLanguageServer.CallRpc = (_, port, _, _, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port == 51003
                    ? new AntigravityLanguageServer.Reply(200, Array.Empty<byte>())
                    : new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));

            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)51001);
            cliStarted = true;
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)51003);
        }
        finally
        {
            AntigravityLanguageServer.CallRpc = prevCall;
            AntigravityLanguageServer.ListAntigravityProcessIds = prevPids;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ResolvePortOwner = prevOwner;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static void TestHelperTimeout()
    {
        var quick = AntigravityLanguageServer.Output(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo hello" : "-c \"echo hello\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, TimeSpan.FromSeconds(10));
        Expect(quick, "hello");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hung = AntigravityLanguageServer.Output(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c ping -n 30 127.0.0.1" : "-c \"sleep 30\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, TimeSpan.FromMilliseconds(300));
        Expect(hung is null, "a helper still running at its timeout gives nothing");
        Expect(clock.Elapsed < TimeSpan.FromSeconds(5), $"and is not waited out, took {clock.Elapsed.TotalSeconds:F1}s");
    }

    private static void TestOmittedZeroFraction()
    {
        const string spent = """
            {"response":{"groups":[
              {"displayName":"Gemini Models","buckets":[
                {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.4,"resetTime":"2026-10-25T11:36:10Z"},
                {"bucketId":"gemini-5h","window":"5h","resetTime":"2026-10-20T14:10:54Z"},
                {"bucketId":"gemini-unknown","window":"weekly"},
                {"bucketId":"gemini-garbled","window":"weekly","remainingFraction":"lots","resetTime":"2026-10-25T11:36:10Z"}]}
            ]}}
            """;
        var buckets = AntigravityQuotaParser.ParseQuotaSummary(Encoding.UTF8.GetBytes(spent))!.Value.Buckets;
        Expect(buckets.Select(b => b.BucketId).SequenceEqual(new[] { "gemini-weekly", "gemini-5h" }),
            "only the rows that say nothing, or something unreadable, are dropped: " + string.Join(", ", buckets.Select(b => b.BucketId)));
        Expect(buckets[1].UsedPercent, 1.0);
        var snapshot = new AntigravityQuotaSnapshot(buckets);
        Expect(snapshot.GeminiPools.ShortWindow?.BucketId, "gemini-5h");
        Expect(ProviderUsage.AntigravityFrom(snapshot, null, null).IsExhausted(Now), "the spent 5-hour pool puts the bot to sleep");
        Expect(Meters(snapshot)[0].Value, "100%");
    }

    private static void TestSingleWindow()
    {
        var weeklyOnly = new AntigravityQuotaSnapshot(new[]
        {
            new AntigravityQuotaBucket("gemini-weekly", "Gemini Models", "weekly", 0.3, Now.AddDays(3)),
            new AntigravityQuotaBucket("3p-weekly", "Claude and GPT models", "weekly", 0.9, Now.AddDays(3)),
        });
        var usage = ProviderUsage.AntigravityFrom(weeklyOnly, null, null);
        Expect(usage.FiveHour.UsedPercent, 0.3);
        Expect(usage.FiveHour.PeriodSeconds, (double?)AntigravityQuotaBucket.WeekSeconds);
        Expect(usage.SecondaryMissing, "the second slot stays the no-data sentinel");
        Expect(weeklyOnly.GeminiPools.Extras.Count, 0);
    }

    private static void TestNoGemini()
    {
        var other = new AntigravityQuotaSnapshot(new[]
        {
            new AntigravityQuotaBucket("pool-a", "Pool A", "weekly", 0.4),
            new AntigravityQuotaBucket("pool-b", "Pool B", "weekly", 0.8),
        });
        var usage = ProviderUsage.AntigravityFrom(other, null, null);
        Expect(usage.FiveHour.UsedPercent, 0.4);
        Expect(usage.SecondaryMissing, "a lone fallback pool is single-window");
        Expect(other.GeminiPools.Extras.Count, 0);

        var empty = ProviderUsage.AntigravityFrom(new AntigravityQuotaSnapshot(), null, null);
        Expect(empty.FiveHour.Error, "no data");
    }

    private static void TestMeters()
    {
        var meters = Meters(Pro());
        Expect(meters.Select(m => m.Label).SequenceEqual(new[] { "5h", "Week", "Gemini 4 Argon" }),
            "labels: " + string.Join(", ", meters.Select(m => m.Label)));
        Expect(meters[0].Value, "0%");
        Expect(meters[1].Value, "2%");
        Expect(meters[2].Value, "75%");
        Expect(meters.Select(m => m.CardLabel).SequenceEqual(new[] { "5h", "Week", "Argon" }), "the hover card says Argon");
        Expect(meters[2].Label, "Gemini 4 Argon");
        Expect(StatusCards.CardLabelOf("Gemini 4 Carbon"), "Carbon");
        Expect(StatusCards.CardLabelOf("Gemini 4 Neon"), "Neon");
        Expect(StatusCards.CardLabelOf("Gemini 4 Hydrogen"), "Hydrog");
        Expect(StatusCards.CardLabelOf("Gemini Ultra Premium"), "Premiu");
        Expect(meters.All(m => m.Resets is not null || m.Value != "—"), "every pool is read");
        Expect(!meters.Any(m => m.Label.Contains("Claude") || m.Label.Contains("GPT")), "Claude and GPT stay out");
    }

    private static void TestMeterFallbacks()
    {
        var weeklyOnly = new AntigravityQuotaSnapshot(new[]
        {
            new AntigravityQuotaBucket("gemini-weekly", "Gemini Models", "weekly", 0.3, Now.AddDays(3)),
        });
        var single = Meters(weeklyOnly);
        Expect(single.Count, 1);
        Expect(single[0].Label, "Week");

        var cold = Meters(null);
        Expect(cold.Count, 1);
        Expect(cold[0].Label, "Week");
        Expect(cold[0].Value, "—");
    }

    /// A daily bucket is "24h", never "Week"; a 5-hour bucket known only by
    /// its id is still "5h".
    private static void TestMeterLabels()
    {
        var dailyOnly = new AntigravityQuotaSnapshot(new[]
        {
            new AntigravityQuotaBucket("gemini-daily", "Gemini Models", "daily", 0.4, Now.AddHours(9)),
        });
        Expect(Meters(dailyOnly).Single().Label, "24h");
        var fiveHourById = new AntigravityQuotaSnapshot(new[]
        {
            new AntigravityQuotaBucket("gemini-5h", "Gemini Models", null, 0.1, Now.AddHours(2)),
        });
        Expect(Meters(fiveHourById).Single().Label, "5h");
        var fiveHourAndDaily = new AntigravityQuotaSnapshot(new[]
        {
            new AntigravityQuotaBucket("gemini-5h", "Gemini Models", "5h", 0.1, Now.AddHours(2)),
            new AntigravityQuotaBucket("gemini-daily", "Gemini Models", "1d", 0.4, Now.AddHours(9)),
        });
        Expect(string.Join(",", Meters(fiveHourAndDaily).Select(m => m.Label)), "5h,24h");
    }

    private static void TestSleepRule()
    {
        // Argon alone out: the other Gemini models still work.
        Expect(!ProviderUsage.AntigravityFrom(Pro(argonUsed: 1), null, null).IsExhausted(Now),
            "Argon running out leaves the bot awake");
        Expect(Meters(Pro(argonUsed: 1))[2].Value == "100%", "its meter still says so");
        // The main group's 5-hour or weekly pool out: asleep.
        Expect(ProviderUsage.AntigravityFrom(Pro(fiveHourUsed: 1), null, null).IsExhausted(Now),
            "the 5-hour pool out puts it to sleep");
        Expect(ProviderUsage.AntigravityFrom(Pro(weeklyUsed: 1), null, null).IsExhausted(Now),
            "the weekly pool out puts it to sleep");
        Expect(!ProviderUsage.AntigravityFrom(Pro(), null, null).IsExhausted(Now), "healthy pools leave it awake");
        // A used-up window that has since reset no longer sleeps.
        var past = new AntigravityQuotaSnapshot(new[]
        {
            new AntigravityQuotaBucket("gemini-weekly", "Gemini Models", "weekly", 1, Now.AddDays(-1)),
            new AntigravityQuotaBucket("gemini-5h", "Gemini Models", "5h", 1, Now.AddHours(-1)),
        });
        Expect(!ProviderUsage.AntigravityFrom(past, null, null).IsExhausted(Now), "a reset window waits for sync instead");
    }

    private static void TestOldSnapshotRoundTrip()
    {
        // The v1 cache holds the buckets and tier only; the pools are derived.
        const string saved = """
            {"Snapshot":{"Buckets":[
              {"BucketId":"gemini-weekly","GroupLabel":"Gemini Models","Window":"weekly","UsedPercent":0.016,"ResetAt":"2026-10-25T11:36:10+00:00"},
              {"BucketId":"gemini-5h","GroupLabel":"Gemini Models","Window":"5h","UsedPercent":0,"ResetAt":"2026-10-21T06:10:54+00:00"},
              {"BucketId":"gemini-argon-wtu-weekly","GroupLabel":"Gemini 4 Argon","Window":"weekly","UsedPercent":0.75,"ResetAt":"2026-10-25T11:35:55+00:00"}],
             "TierId":"g1-pro-tier","TierLabel":"Google AI Pro","Note":"n"},
             "UpdatedAt":"2026-10-20T21:10:56-04:00","Email":"a@b.c"}
            """;
        var cached = JsonSerializer.Deserialize<AntigravityUsageStore.CachedSnapshot>(saved)!;
        Expect(cached.Snapshot!.GeminiPools.Extras.Count, 1);
        Expect(cached.Snapshot.GeminiPools.ShortWindow?.BucketId, "gemini-5h");

        var again = JsonSerializer.Serialize(cached);
        Expect(!again.Contains("GeminiPools"), "the derived pools are not written");
        Expect(JsonSerializer.Deserialize<AntigravityUsageStore.CachedSnapshot>(again)!.Snapshot!.Buckets.Count, 3);
    }

    private static void TestKeptPools()
    {
        var meters = Meters(Pro(), caption: "Antigravity is not running");
        Expect(meters.Count, 3);
        Expect(meters[0].Value, "0%");
        Expect(meters[2].Value, "75%");
        var usage = ProviderUsage.AntigravityFrom(Pro(), "Antigravity is not running", null);
        Expect(usage.FiveHour.Kept && usage.Weekly.Kept, "both windows keep their numbers");
    }
}
