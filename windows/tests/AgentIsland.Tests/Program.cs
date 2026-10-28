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
        try
        {
            SettingsIsolation();
            SessionTurnStateTests.RunAll();
            CodexStallTests.RunAll();
            ClaudeStallTests.RunAll();
            RunningTasksTests.RunAll();
            EvidenceTests.RunAll();
            PersonalityTests.RunAll();
            AgentPhaseTests.RunAll();
            KimiDeepSeekTests.RunAll();
            AntigravityCursorTests.RunAll();
            AntigravityQuotaTests.RunAll();
            SubagentFilterTests.RunAll();
            GrokTurnStateTests.RunAll();
            GrokStallTests.RunAll();
            GrokPhaseTests.RunAll();
            KimiUsageTests.RunAll();
            KimiPluginsTests.RunAll();
            ClaudeHooksTests.RunAll();
            CodexReplayGuardTests.RunAll();
            CodexSwitchRefreshTests.RunAll();
            UsageRetryTests.RunAll();
            CodexAutoSwitchReadingTests.RunAll();
            CodexArchiveTests.RunAll();
            CursorReplyClockTests.RunAll();
            UsageCachePolicyTests.RunAll();
            UsageFreshnessTests.RunAll();
            CostPeriodTests.RunAll();
            TokenDisplayTextTests.RunAll();
            TokenAccountingTests.RunAll();
            OtherTokenAccountingTests.RunAll();
            PricingTests.RunAll();
            AntigravityCostTests.RunAll();
            CursorDatabaseTests.RunAll();
            CursorQuotaStateTests.RunAll();
            CodexAccountSwitcherTests.RunAll();
            DrawerPresentationTests.RunAll();
            QuotaRetentionTests.RunAll();
            PixelThemeTests.RunAll();
            ClassicThemeTests.RunAll();
            SharedSubscriptionTests.RunAll();
            DrawerPlacementTests.RunAll();
            SecurityGuardTests.RunAll();
            FormattingTests.RunAll();
            LaunchAtLoginTests.RunAll();
            ScannerCwdTests.RunAll();
            AgentPresenceTests.RunAll();
            SessionEventTests.RunAll();
            GrokHooksTests.RunAll();
            GrokAuthFileTests.RunAll();
            LookCatalogTests.RunAll();
            CharacterHostTests.RunAll();
            ClaudeCredentialsTests.RunAll();
            ActivityMonitorScanTests.RunAll();
            LogParseCacheTests.RunAll();
            PreferencesTests.RunAll();
            CrashLogTests.RunAll();
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
            try { Directory.Delete(settings, recursive: true); } catch { }
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
