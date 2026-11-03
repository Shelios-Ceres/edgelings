using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Console test runner in the style of the macOS repo's script-level suites:
/// hand-rolled assertions, PASS per case, GREEN per suite, exit 1 on failure.
/// `AgentIsland.Tests.exe scan` instead runs a live MonitoringScan against
/// this machine's real session files — a diagnostic, not a test.
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "scan")
        {
            return LiveScan();
        }
        if (args.Length > 0 && args[0] == "bigscan")
        {
            return BigScan(args.Length > 1 ? args[1] : "", args.Length > 2 && int.TryParse(args[2], out var rounds) ? rounds : 5);
        }
        if (args.Length > 0 && args[0] == "kimiwork")
        {
            return KimiWorkLive();
        }
        if (args.Length > 0 && args[0] == "costdump")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return CostDump.Run();
        }
        if (args.Length > 0 && args[0] == "mailwatch")
        {
            return MailWatch(args.Length > 1 && int.TryParse(args[1], out var seconds) ? seconds : 600);
        }
        if (args.Length > 0 && args[0] == "locate")
        {
            foreach (var name in new[] { "claude", "codex", "wt" })
            {
                Console.WriteLine($"{name} -> {Trigger.CLILocator.Locate(name) ?? "(null)"}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "scanbench")
        {
            // Time repeated full MonitoringScans against this machine's real
            // session files: scan #0 builds the turn-parse cache, #1+ hit it,
            // so the delta isolates the tail-read/parse cost from the fixed
            // file-enumeration + stat cost that every scan pays.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var now = DateTimeOffset.UtcNow;
            var lw = new Dictionary<string, DateTimeOffset>();
            for (var i = 0; i < 6; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var s = SessionScanner.MonitoringScan(now, lw);
                sw.Stop();
                Console.WriteLine($"scan #{i}: {sw.ElapsedMilliseconds} ms, {s.Count} sessions");
            }
            return 0;
        }
        // The store singletons read and write Preferences: every suite runs
        // against a settings file of its own, never the developer's real one.
        var settings = Path.Combine(Path.GetTempPath(), "agentisland-test-settings-" + Guid.NewGuid().ToString("N"));
        Preferences.FileOverride = Path.Combine(settings, "settings.json");
        Preferences.Forget();
        // Nor do they read or write any agent's or the app's real folders: the
        // whole home moves under a temp root, and the agents' own overrides
        // (which a suite sets for itself when it needs one) start cleared.
        var world = Path.Combine(Path.GetTempPath(), "edgelings-test-root-" + Guid.NewGuid().ToString("N"));
        IslandPaths.RootOverride = world;
        foreach (var variable in new[] { "CODEX_HOME", "GROK_HOME", "KIMI_CODE_HOME", "CLAUDE_CONFIG_DIR" })
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
        try
        {
            SettingsIsolation();
            // A suite that needs Windows is skipped by name elsewhere, with a visible line.
            // AGENTISLAND_TEST_KEEP_GOING=1 runs every suite and lists the red ones at the end.
            var keepGoing = Environment.GetEnvironmentVariable("AGENTISLAND_TEST_KEEP_GOING") == "1";
            var red = new List<string>();
            var suites = new (string Name, Action Run, string? WindowsOnly)[]
            {
                ("SessionTurnStateTests", SessionTurnStateTests.RunAll, null),
                ("CodexStallTests", CodexStallTests.RunAll, null),
                ("ClaudeStallTests", ClaudeStallTests.RunAll, null),
                ("RunningTasksTests", RunningTasksTests.RunAll, null),
                ("EvidenceTests", EvidenceTests.RunAll, null),
                ("PersonalityTests", PersonalityTests.RunAll, null),
                ("AgentPhaseTests", AgentPhaseTests.RunAll, null),
                ("KimiDeepSeekTests", KimiDeepSeekTests.RunAll, null),
                ("KimiWorkTests", KimiWorkTests.RunAll, null),
                ("AntigravityCursorTests", AntigravityCursorTests.RunAll, null),
                ("AntigravityQuotaTests", AntigravityQuotaTests.RunAll, null),
                ("SubagentFilterTests", SubagentFilterTests.RunAll, null),
                ("GrokTurnStateTests", GrokTurnStateTests.RunAll, null),
                ("GrokStallTests", GrokStallTests.RunAll, null),
                ("GrokPhaseTests", GrokPhaseTests.RunAll, null),
                ("KimiUsageTests", KimiUsageTests.RunAll, null),
                ("KimiPluginsTests", KimiPluginsTests.RunAll, null),
                ("ClaudeHooksTests", ClaudeHooksTests.RunAll, null),
                ("CodexReplayGuardTests", CodexReplayGuardTests.RunAll, null),
                ("CodexSwitchRefreshTests", CodexSwitchRefreshTests.RunAll, null),
                ("UsageRetryTests", UsageRetryTests.RunAll, null),
                ("CodexAutoSwitchReadingTests", CodexAutoSwitchReadingTests.RunAll, null),
                ("CodexArchiveTests", CodexArchiveTests.RunAll, null),
                ("CursorReplyClockTests", CursorReplyClockTests.RunAll, null),
                ("UsageCachePolicyTests", UsageCachePolicyTests.RunAll, null),
                ("UsageFreshnessTests", UsageFreshnessTests.RunAll, null),
                ("CostPeriodTests", CostPeriodTests.RunAll, null),
                ("TokenDisplayTextTests", TokenDisplayTextTests.RunAll, null),
                ("TokenAccountingTests", TokenAccountingTests.RunAll, null),
                ("OtherTokenAccountingTests", OtherTokenAccountingTests.RunAll, null),
                ("PricingTests", PricingTests.RunAll, null),
                ("AntigravityCostTests", AntigravityCostTests.RunAll, null),
                ("CursorDatabaseTests", CursorDatabaseTests.RunAll, null),
                ("CursorQuotaStateTests", CursorQuotaStateTests.RunAll, null),
                ("CodexAccountSwitcherTests", CodexAccountSwitcherTests.RunAll, "relies on Windows file locking"),
                ("DrawerPresentationTests", DrawerPresentationTests.RunAll, null),
                ("QuotaRetentionTests", QuotaRetentionTests.RunAll, null),
                ("PixelThemeTests", PixelThemeTests.RunAll, null),
                ("ClassicThemeTests", ClassicThemeTests.RunAll, null),
                ("SharedSubscriptionTests", SharedSubscriptionTests.RunAll, null),
                ("DrawerPlacementTests", DrawerPlacementTests.RunAll, null),
                ("SecurityGuardTests", SecurityGuardTests.RunAll, "starts cmd.exe"),
                ("FormattingTests", FormattingTests.RunAll, null),
                ("AppDataMigrationTests", AppDataMigrationTests.RunAll, null),
                ("IslandPathsTests", IslandPathsTests.RunAll, null),
                ("LaunchAtLoginTests", LaunchAtLoginTests.RunAll, "Registry Run key"),
                ("ScannerCwdTests", ScannerCwdTests.RunAll, null),
                ("AgentPresenceTests", AgentPresenceTests.RunAll, null),
                ("SessionEventTests", SessionEventTests.RunAll, null),
                ("GrokHooksTests", GrokHooksTests.RunAll, "starts powershell.exe"),
                ("ShellHooksTests", ShellHooksTests.RunAll, null),
                ("GrokAuthFileTests", GrokAuthFileTests.RunAll, null),
                ("LookCatalogTests", LookCatalogTests.RunAll, null),
                ("StateGroupsTests", StateGroupsTests.RunAll, null),
                ("PixelChineseTests", PixelChineseTests.RunAll, null),
                ("CharacterHostTests", CharacterHostTests.RunAll, null),
                ("ClaudeCredentialsTests", ClaudeCredentialsTests.RunAll, null),
                ("ClaudeKeychainTests", ClaudeKeychainTests.RunAll, null),
                ("ActivityMonitorScanTests", ActivityMonitorScanTests.RunAll, null),
                ("LogParseCacheTests", LogParseCacheTests.RunAll, null),
                ("PreferencesTests", PreferencesTests.RunAll, null),
                ("CrashLogTests", CrashLogTests.RunAll, null),
            };
            foreach (var (name, run, windowsOnly) in suites)
            {
                if (windowsOnly is not null && !OperatingSystem.IsWindows())
                {
                    Console.WriteLine($"SKIP {name}: needs Windows ({windowsOnly})");
                    continue;
                }
                try
                {
                    run();
                }
                catch (Exception error) when (keepGoing)
                {
                    Console.Error.WriteLine($"RED {name}: {error.Message}");
                    red.Add(name);
                }
            }
            if (red.Count > 0)
            {
                Console.Error.WriteLine($"RED suites: {string.Join(", ", red)}");
                return 1;
            }
            Console.WriteLine("ALL GREEN");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"RED: {error.Message}");
            return 1;
        }
        finally
        {
            Preferences.FileOverride = null;
            Preferences.Forget();
            IslandPaths.RootOverride = null;
            try { Directory.Delete(settings, recursive: true); } catch { }
            try { Directory.Delete(world, recursive: true); } catch { }
        }
    }

    private static void SettingsIsolation()
    {
        Preferences.Set("AgentIsland.tests.isolation", 1);
        var saved = File.Exists(Preferences.FileOverride) && File.ReadAllText(Preferences.FileOverride!).Contains("AgentIsland.tests.isolation");
        Preferences.Remove("AgentIsland.tests.isolation");
        Check.Expect(saved, "a preference written by a test lands in the run's own settings file");
        Console.WriteLine("PASS the suite reads and writes a temporary settings file, never the real one");
    }

    /// `mailwatch [seconds]`: the app's own mail path against this machine's
    /// real Kimi sessions, scanned every two seconds. Prints when a session's
    /// unread mail changes (counts and session ids only: no file is opened),
    /// and every time the detector says a fresh Mail event. A diagnostic.
    private static int MailWatch(int seconds)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var detector = new SessionEventDetector();
        var last = new Dictionary<string, int>();
        var end = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var lastWorking = new Dictionary<string, DateTimeOffset>();
        while (DateTimeOffset.UtcNow < end)
        {
            var now = DateTimeOffset.UtcNow;
            var sessions = SessionScanner.ScanKimi(now, lastWorking);
            var seen = detector.Observe(sessions, now);
            var counts = seen.Waiting.Where(e => e.Kind == SessionEventKind.Mail).ToDictionary(e => e.SessionId, e => e.Count);
            foreach (var (id, count) in counts)
            {
                if (!last.TryGetValue(id, out var was) || was != count) Console.WriteLine($"{DateTime.Now:HH:mm:ss} unread mail in {id[..8]}: {count}");
            }
            foreach (var id in last.Keys.Where(id => !counts.ContainsKey(id))) Console.WriteLine($"{DateTime.Now:HH:mm:ss} no unread mail in {id[..8]}");
            last = counts;
            foreach (var e in seen.Fresh.Where(e => e.Kind == SessionEventKind.Mail))
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} FRESH mail event for {e.SessionId[..8]}, count {e.Count}, key {e.Key[..Math.Min(14, e.Key.Length)]}...");
            }
            Thread.Sleep(2000);
        }
        return 0;
    }

    /// Kimi Work against this machine's real folders, as ids, counts and states
    /// only: no titles, folders, commands or message text.
    private static int KimiWorkLive()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = IslandPaths.KimiWorkSessionsRoot;
        Console.WriteLine($"Kimi Work sessions folder exists: {Directory.Exists(root)}");
        if (Directory.Exists(root))
        {
            var folders = SessionScanner.SafeEnumerateDirectories(root).SelectMany(SessionScanner.SafeEnumerateDirectories).ToList();
            Console.WriteLine($"conversation folders: {folders.Count} ({folders.Count(f => Path.GetFileName(f).StartsWith("conv-", StringComparison.Ordinal))} named conv-*)");
            var wires = SessionScanner.SafeEnumerateFiles(root, "wire.jsonl");
            Console.WriteLine($"wire.jsonl files: {wires.Count}, changed in the last 24 h: {wires.Count(w => DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(w) < TimeSpan.FromHours(24))}");
        }
        var now = DateTimeOffset.UtcNow;
        var scanned = SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>());
        var work = scanned.Where(s => s.DesktopHosted).ToList();
        Console.WriteLine($"Kimi sessions in the recent window: {scanned.Count} ({work.Count} Kimi Work, {scanned.Count - work.Count} Kimi Code)");
        foreach (var session in work.Take(20))
        {
            var age = now - session.Modified;
            Console.WriteLine($"  conv {session.SessionId[..Math.Min(8, session.SessionId.Length)]}  status {session.Status}  wait {(session.PendingPrompt is { } p ? p.Phase.ToString() : "-")}  turn ended {session.TurnEnded}  last write {age.TotalMinutes:0} min ago");
        }
        Console.WriteLine($"desktop app gone: {AgentProcesses.DesktopGone().Contains(TriggerTool.Kimi)}   Kimi Code processes gone: {AgentProcesses.Gone().Contains(TriggerTool.Kimi)}");
        Console.WriteLine($"Kimi presence: {AgentPresences.Now().GetValueOrDefault(TriggerTool.Kimi)}");
        var events = AgentIsland.Cost.KimiLogReader.Scan(30);
        var inWork = SessionScanner.SafeEnumerateFiles(root, "wire.jsonl").Select(AgentIsland.Cost.KimiLogReader.ParseFile).SelectMany(e => e).ToList();
        Console.WriteLine($"Kimi tokens, 30 days: {events.Count} events, {events.Sum(e => e.WireTokens)} tokens in all trees; Kimi Work's own: {inWork.Count} events, {inWork.Sum(e => e.WireTokens)} tokens, models {string.Join(", ", inWork.Select(e => e.Model).Distinct().OrderBy(m => m, StringComparer.Ordinal))}");
        var priced = inWork.GroupBy(e => e.Model).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Sum(e => e.WireTokens)} tok ${g.Sum(e => e.Dollars):0.00}{(g.Any(e => e.Dollars == 0) ? " (unpriced)" : "")}");
        Console.WriteLine($"Kimi Work by model: {string.Join("; ", priced)}");
        Console.WriteLine($"K2 models hidden from the model rows: {string.Join(", ", inWork.Select(e => e.Model).Distinct().Where(m => AgentIsland.Cost.KimiLogReader.IsK2(m, null)).OrderBy(m => m, StringComparer.Ordinal))}");
        return 0;
    }

    /// Times Core's scans against a synthetic home of real sizes (make-big-home.py) under a temp root:
    /// the session scan of every agent and the cost scans, a few rounds each, then CoreProfile's table.
    private static int BigScan(string root, int rounds)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        IslandPaths.RootOverride = root;
        IslandPaths.CacheDirOverride = Path.Combine(root, "cache");
        Console.WriteLine($"root {root}, {rounds} rounds");
        for (var i = 0; i < rounds; i++)
        {
            var sessions = SessionScanner.MonitoringScan(DateTimeOffset.UtcNow, new Dictionary<string, DateTimeOffset>());
            if (i == 0) Console.WriteLine($"sessions found: {sessions.Count}");
            CoreProfile.Time("process table", () => AgentProcesses.Gone());
            CoreProfile.Time("presence", () => AgentPresences.Now());
            CoreProfile.Time("cost scan claude", () => AgentIsland.Cost.ClaudeLogReader.Scan(30));
            CoreProfile.Time("cost scan codex", () => AgentIsland.Cost.CodexLogReader.Scan(30));
            CoreProfile.Time("cost scan antigravity", () => AgentIsland.Cost.AntigravityLogReader.Scan(30));
            CoreProfile.Time("cost scan grok", () => AgentIsland.Cost.GrokLogReader.Scan(30));
            try { CoreProfile.Time("cost scan cursor", () => AgentIsland.Cost.CursorLogReader.Scan(30)); } catch (Exception e) { Console.WriteLine("cursor: " + e.GetType().Name); }
            CoreProfile.Time("cost scan kimi", () => AgentIsland.Cost.KimiLogReader.Scan(30));
            CoreProfile.Time("cost scan deepseek", () => AgentIsland.Cost.DeepSeekLogReader.Scan(30));
        }
        foreach (var row in CoreProfile.Snapshot())
        {
            Console.WriteLine($"{row.Name,-26} {row.Count,3} runs  {row.TotalMs / Math.Max(1, row.Count),9:0.0} ms each  worst {row.WorstMs,8:0.0} ms");
        }
        return 0;
    }

    private static int LiveScan()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var now = DateTimeOffset.UtcNow;
        var sessions = SessionScanner.MonitoringScan(now, new Dictionary<string, DateTimeOffset>());
        Console.WriteLine($"{sessions.Count} session(s) discovered");
        Console.WriteLine($"no process running: {string.Join(", ", AgentProcesses.Gone())}");
        Console.WriteLine($"on screen: {string.Join(", ", AgentPresences.Now().Select(p => $"{p.Key} {p.Value}"))}");
        Console.WriteLine($"offline: {InternetStatus.IsOffline(now)}");
        foreach (var session in sessions.Take(15))
        {
            var age = now - session.Modified;
            Console.WriteLine(
                $"[{session.Tool,-6}] {session.Status,-12} age={age.TotalSeconds,8:F0}s  " +
                $"{session.Label} ({session.SessionId[..Math.Min(8, session.SessionId.Length)]}) " +
                $"target={session.LaunchTarget}" + (session.UnreadMail.Count > 0 ? $" unread-mail={session.UnreadMail.Count}" : ""));
        }
        return 0;
    }
}
