using System.Text;
using System.Text.Json;
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
            ("a process's CSRF token is read once, and looked for again only after a while", TestCsrfTokenIsRemembered),
            ("a helper that hangs is stopped at its timeout", TestHelperTimeout),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS antigravity quota: {name}");
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

    private static void TestHelperTimeout()
    {
        var quick = AntigravityLanguageServer.Output(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo hello",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, TimeSpan.FromSeconds(10));
        Expect(quick, "hello");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hung = AntigravityLanguageServer.Output(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1",
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
