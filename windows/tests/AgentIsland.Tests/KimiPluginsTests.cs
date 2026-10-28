using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Kimi Code's plugins: the data folder, the memory worker's background
/// runs and the mailbox. Each test runs against a temp KIMI_CODE_HOME.
public static class KimiPluginsTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("kimi home: KIMI_CODE_HOME wins, ~/.kimi-code otherwise", KimiHomeVariable),
            ("memory runs: the scan skips a session whose cwd is the memories folder", MemoryRunSkipped),
            ("memory runs: a project named memories elsewhere is a real session", MemoriesNamedProject),
            ("memory runs: their tokens list as one row, priced by the real model, totals unchanged", MemoryCostSplit),
            ("memory runs: the row is named in the shown language", MemoryRowName),
            ("mailbox: mail present at launch is listed but silent", MailAtLaunch),
            ("mailbox: files appearing speak once, more files once more, the same files never again", MailSpeaksOncePerBatch),
            ("mailbox: files moved to read leave the list", MailReadLeavesList),
            ("mailbox: a count above one shows on both styles' cards", MailCountOnCard),
            ("mailbox: a session the scan does not list is ignored", MailForUnlistedSession),
            ("mailbox: only names are looked at, and nothing under mailbox changes", MailNeverReadOrTouched),
            ("mailbox: a mail bubble never merges into a summary", MailNeverMerges),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS kimi plugins: " + name);
        }
        Console.WriteLine("KimiPluginsTests GREEN");
    }

    /// Runs `body` with KIMI_CODE_HOME pointing at a fresh temp folder.
    private static void WithHome(Action<string> body)
    {
        var home = Path.Combine(Path.GetTempPath(), "agentisland-kimi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var before = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
        try
        {
            body(home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", before);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    private static void KimiHomeVariable()
    {
        var before = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", null);
            Expect(IslandPaths.KimiHome, Path.Combine(IslandPaths.Home, ".kimi-code"));
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", "  ");
            Expect(IslandPaths.KimiHome, Path.Combine(IslandPaths.Home, ".kimi-code"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", before);
        }
        WithHome(home =>
        {
            Expect(IslandPaths.KimiHome, home);
            Expect(IslandPaths.KimiSessionsRoot, Path.Combine(home, "sessions"));
        });
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private const string ProviderConfig = """
        [models."deepseek/deepseek-flash"]
        provider = "deepseek"
        model = "deepseek-flash"
        """;

    /// A Kimi session folder under the temp home with a state.json and a
    /// wire.jsonl of the given lines; the log is stamped `modified`.
    private static string Session(string home, string workdir, string id, string cwd, DateTimeOffset modified, params string[] lines)
    {
        var dir = Path.Combine(home, "sessions", workdir, "session_" + id);
        var agent = Path.Combine(dir, "agents", "main");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(new { id = "session_" + id, cwd }));
        var wire = Path.Combine(agent, "wire.jsonl");
        File.WriteAllLines(wire, lines);
        File.SetLastWriteTimeUtc(wire, modified.UtcDateTime);
        return wire;
    }

    private static string Usage(string model, long input, long output, int secondsAgo = 60) =>
        JsonSerializer.Serialize(new
        {
            type = "usage.record",
            time = Now.AddSeconds(-secondsAgo).ToUnixTimeMilliseconds(),
            model,
            usage = new { inputOther = input, output, inputCacheRead = 0, inputCacheCreation = 0 },
        });

    private static string Turn(string type, DateTimeOffset at) =>
        JsonSerializer.Serialize(new { type, time = at.ToUnixTimeMilliseconds() });

    private static void MemoryRunSkipped() => WithHome(home =>
    {
        var now = DateTimeOffset.UtcNow;
        var memories = Path.Combine(home, "memories");
        var open = new[] { Turn("agent.turn.started", now.AddSeconds(-3)) };
        Session(home, "wd_memories_a", "run1", memories, now, open);
        Session(home, "wd_memories_a", "run2", memories + Path.DirectorySeparatorChar, now, open);
        Session(home, "wd_memories_a", "run3", memories.ToUpperInvariant().Replace('\\', '/'), now, open);
        Session(home, "wd_work_b", "real", @"D:\work\app", now, open);
        var scanned = SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>());
        Expect(string.Join(",", scanned.Select(s => s.SessionId)), "real");
    });

    private static void MemoriesNamedProject() => WithHome(home =>
    {
        var now = DateTimeOffset.UtcNow;
        var open = new[] { Turn("agent.turn.started", now.AddSeconds(-3)) };
        // The workdir folder carries the same name Kimi gives the plugin's runs;
        // the cwd is what tells them apart.
        Session(home, "wd_memories_66683c140241", "mine", @"D:\projects\memories", now, open);
        Session(home, "wd_memories_66683c140241", "inside", Path.Combine(home, "memories", "sub"), now, open);
        Session(home, "wd_memories_66683c140241", "run", Path.Combine(home, "memories"), now, open);
        var scanned = SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>());
        Expect(string.Join(",", scanned.Select(s => s.SessionId).Order()), "inside,mine");
    });

    private static void MemoryCostSplit() => WithHome(home =>
    {
        var memories = Path.Combine(home, "memories");
        var run = Session(home, "wd_memories_a", "run", memories, Now,
            Usage("deepseek/deepseek-flash", 1_000_000, 200_000), Usage("deepseek/deepseek-flash", 500_000, 100_000, 30));
        var work = Session(home, "wd_work_b", "real", @"D:\work\app", Now,
            Usage("deepseek/deepseek-flash", 2_000_000, 300_000), Usage("kimi-code/k3", 1_000, 500));
        var providers = KimiLogReader.ProviderModels(ProviderConfig);
        var events = KimiLogReader.ParseFile(run).Concat(KimiLogReader.ParseFile(work)).ToList();
        var split = KimiLogReader.PriceByProvider(events, providers);
        // The same tokens with no tagging, as before the change.
        var plain = KimiLogReader.PriceByProvider(
            events.Select(e => e.Model.Contains('|') ? e with { Model = e.Model[(e.Model.IndexOf('|') + 1)..] } : e).ToList(), providers);

        Expect(split.Count(e => e.Model == KimiLogReader.MemoryModel), 2);
        Expect(split.Where(e => e.Model == KimiLogReader.MemoryModel).All(e => e.PricedAs == "deepseek-flash"), "priced by the model that ran them");
        Expect(split.Where(e => e.Model == KimiLogReader.MemoryModel).Sum(e => e.Dollars) > 0, "and they cost something");
        var at = Now.AddMinutes(5);
        var splitSummary = CostSummarizer.Summarize(split, at);
        var plainSummary = CostSummarizer.Summarize(plain, at);
        Expect(splitSummary.RecentModels.Sum(m => m.Tokens), plainSummary.RecentModels.Sum(m => m.Tokens));
        Expect(Math.Abs(splitSummary.RecentModels.Sum(m => m.Dollars) - plainSummary.RecentModels.Sum(m => m.Dollars)) < 1e-9, "dollars unchanged");
        Expect(splitSummary.TodayModels.Sum(m => m.Tokens), plainSummary.TodayModels.Sum(m => m.Tokens));
        var row = splitSummary.RecentModels.Single(m => m.Model == KimiLogReader.MemoryModel);
        Expect(row.Tokens, 1_800_000L);
        var flash = splitSummary.RecentModels.Single(m => m.Model == "deepseek/deepseek-flash");
        Expect(flash.Tokens, 2_300_000L);
        Expect(splitSummary.UnknownModels.Count, 0);
        // The row is not K2: Kimi Code's list keeps it.
        Expect(KimiLogReader.IsK2(KimiLogReader.MemoryModel, null), false);
    });

    private static void MemoryRowName()
    {
        var language = L10n.Current;
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            var summary = ProviderCostSummary.Empty;
            Expect(summary.NameOf(KimiLogReader.MemoryModel), "记忆（后台）");
            using (L10n.English())
            {
                Expect(summary.NameOf(KimiLogReader.MemoryModel), "Memory (background)");
            }
        }
        finally
        {
            L10n.Current = language;
        }
    }

    private static SessionEventDetector Detector() =>
        new((_, _, _) => null, () => Array.Empty<SkippedTask>());

    private static string Inbox(string home, string id) =>
        Path.Combine(home, "mailbox", "inbox", "session_" + id);

    private static void Mail(string home, string id, params string[] names)
    {
        var inbox = Inbox(home, id);
        Directory.CreateDirectory(inbox);
        foreach (var name in names) File.WriteAllText(Path.Combine(inbox, name), "{ not a message we read");
    }

    private static SessionObservation Observe(SessionEventDetector detector)
    {
        var now = DateTimeOffset.UtcNow;
        return detector.Observe(SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>()), now);
    }

    private static SessionEvent[] MailOf(IEnumerable<SessionEvent> events) =>
        events.Where(e => e.Kind == SessionEventKind.Mail).ToArray();

    private static void Listed(string home, string id) =>
        Session(home, "wd_work_b", id, @"D:\work\app", DateTimeOffset.UtcNow, Turn("agent.turn.started", DateTimeOffset.UtcNow));

    private static void MailAtLaunch() => WithHome(home =>
    {
        Listed(home, "s1");
        Mail(home, "s1", "a.json");
        var detector = Detector();
        var first = Observe(detector);
        Expect(MailOf(first.Waiting).Length, 1);
        Expect(MailOf(first.Fresh).Length, 0);
        // Later scans of the same file stay quiet.
        Expect(MailOf(Observe(detector).Fresh).Length, 0);
    });

    private static void MailSpeaksOncePerBatch() => WithHome(home =>
    {
        Listed(home, "s1");
        var detector = Detector();
        Observe(detector);

        Mail(home, "s1", "a.json", "b.json");
        var arrived = Observe(detector);
        var fresh = MailOf(arrived.Fresh);
        Expect(fresh.Length, 1);
        Expect(fresh[0].Count, 2);
        Expect(fresh[0].Tool, TriggerTool.Kimi);
        var lines = new HashSet<string>();
        using (L10n.English())
        {
            for (var seed = 0; seed < 40; seed++) lines.Add(BotLines.For(fresh[0], new Random(seed)) ?? "");
        }
        Expect(string.Join("|", lines.Order()), "A message came in.|You've got mail.");
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            var zh = new HashSet<string>();
            for (var seed = 0; seed < 40; seed++) zh.Add(BotLines.For(fresh[0], new Random(seed)) ?? "");
            Expect(string.Join("|", zh.Order()), "来消息了。|有新邮件。");
        }
        finally
        {
            L10n.Current = L10n.Language.Auto;
        }

        Expect(MailOf(Observe(detector).Fresh).Length, 0);

        Mail(home, "s1", "c.json");
        var more = Observe(detector);
        Expect(MailOf(more.Fresh).Length, 1);
        Expect(MailOf(more.Fresh)[0].Count, 3);
        Expect(MailOf(more.Fresh)[0].Key != fresh[0].Key, "a new batch is a new event");
        Expect(MailOf(more.Waiting).Length, 1);
        Expect(MailOf(Observe(detector).Fresh).Length, 0);

        // A name that came and went does not speak when it returns.
        var inbox = Inbox(home, "s1");
        Directory.CreateDirectory(Path.Combine(inbox, "read"));
        File.Move(Path.Combine(inbox, "a.json"), Path.Combine(inbox, "read", "a.json"));
        Expect(MailOf(Observe(detector).Fresh).Length, 0);
        File.Move(Path.Combine(inbox, "read", "a.json"), Path.Combine(inbox, "a.json"));
        Expect(MailOf(Observe(detector).Fresh).Length, 0);
    });

    private static void MailReadLeavesList() => WithHome(home =>
    {
        Listed(home, "s1");
        var detector = Detector();
        Observe(detector);
        Mail(home, "s1", "a.json");
        Expect(MailOf(Observe(detector).Waiting).Length, 1);
        var inbox = Inbox(home, "s1");
        Directory.CreateDirectory(Path.Combine(inbox, "read"));
        File.Move(Path.Combine(inbox, "a.json"), Path.Combine(inbox, "read", "a.json"));
        var after = Observe(detector);
        Expect(MailOf(after.Waiting).Length, 0);
        Expect(StatusWaitingList.Of(after.Waiting).Rows.Count, 0);
    });

    private static void MailCountOnCard() => WithHome(home =>
    {
        Listed(home, "s1");
        Mail(home, "s1", "a.json");
        var detector = Detector();
        Observe(detector);
        var one = Observe(detector).Waiting;
        Mail(home, "s1", "b.json", "c.json");
        var three = Observe(detector).Waiting;
        using (L10n.English())
        {
            Expect(StatusWaitingList.Of(one).Rows.Single().Word, "Mail");
            Expect(StatusWaitingList.Of(three).Rows.Single().Word, "Mail 3");
            // Classic keeps unread mail on its list however long it has sat there.
            var later = DateTimeOffset.UtcNow.AddHours(3);
            Expect(StatusWaitingList.ForClassic(three, later).Rows.Single().Word, "Mail 3");
        }
        var language = L10n.Current;
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            Expect(StatusWaitingList.Of(three).Rows.Single().Word, "邮件 3");
        }
        finally
        {
            L10n.Current = language;
        }
    });

    private static void MailForUnlistedSession() => WithHome(home =>
    {
        // Idle for two days: not listed. A memory run: not listed. And a mailbox with no session at all.
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        Session(home, "wd_work_b", "old", @"D:\work\app", old, Turn("agent.turn.started", old));
        Session(home, "wd_memories_a", "run", Path.Combine(home, "memories"), DateTimeOffset.UtcNow, Turn("agent.turn.started", DateTimeOffset.UtcNow));
        var detector = Detector();
        Observe(detector);
        Mail(home, "old", "a.json");
        Mail(home, "run", "a.json");
        Mail(home, "nobody", "a.json");
        var seen = Observe(detector);
        Expect(MailOf(seen.Waiting).Length, 0);
        Expect(MailOf(seen.Fresh).Length, 0);
    });

    private static void MailNeverReadOrTouched() => WithHome(home =>
    {
        Listed(home, "s1");
        Mail(home, "s1", "a.json", "notes.txt");
        Directory.CreateDirectory(Path.Combine(Inbox(home, "s1"), "read"));
        File.WriteAllText(Path.Combine(Inbox(home, "s1"), "read", "old.json"), "{}");
        string Snapshot() => string.Join(";", Directory.EnumerateFileSystemEntries(Path.Combine(home, "mailbox"), "*", SearchOption.AllDirectories)
            .Order().Select(path => path + "@" + File.GetLastWriteTimeUtc(path).Ticks));
        var before = Snapshot();
        // A file held open exclusively is still only listed.
        using (new FileStream(Path.Combine(Inbox(home, "s1"), "a.json"), FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var detector = Detector();
            Observe(detector);
            Expect(MailOf(Observe(detector).Waiting).Single().Count, 1);
        }
        Expect(Snapshot(), before);
    });

    private static void MailNeverMerges()
    {
        SessionEvent Event(string key, SessionEventKind kind) =>
            new(TriggerTool.Kimi, key, key, kind, key, DateTimeOffset.UtcNow);
        foreach (var rules in new[] { SpeechRules.Pixel, SpeechRules.Classic })
        {
            var queue = new SpeechQueue { Rules = rules };
            queue.Add(Event("m1", SessionEventKind.Mail));
            queue.Add(Event("m2", SessionEventKind.Mail));
            queue.Add(Event("m3", SessionEventKind.Mail));
            var speech = queue.Next(100);
            Expect(speech is { IsSummary: false } && speech.First.Kind == SessionEventKind.Mail, "mail speaks alone");
        }
        var mixed = new SpeechQueue { Rules = SpeechRules.Classic };
        mixed.Add(Event("m", SessionEventKind.Mail));
        mixed.Add(Event("d1", SessionEventKind.Done));
        mixed.Add(Event("d2", SessionEventKind.Done));
        var said = mixed.Next(100)!;
        Expect(said.Events.Count, 2);
        Expect(said.Events.All(e => e.Kind == SessionEventKind.Done), "the summary counts only sessions that need the user");
    }
}
