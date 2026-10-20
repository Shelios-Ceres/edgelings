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
        try
        {
            SessionTurnStateTests.RunAll();
            CodexStallTests.RunAll();
            ClaudeStallTests.RunAll();
            RunningTasksTests.RunAll();
            EvidenceTests.RunAll();
            PersonalityTests.RunAll();
            AgentPhaseTests.RunAll();
            KimiDeepSeekTests.RunAll();
            AntigravityCursorTests.RunAll();
            SubagentFilterTests.RunAll();
            GrokTurnStateTests.RunAll();
            GrokStallTests.RunAll();
            GrokPhaseTests.RunAll();
            KimiUsageTests.RunAll();
            ClaudeHooksTests.RunAll();
            CodexReplayGuardTests.RunAll();
            UsageCachePolicyTests.RunAll();
            UsageFreshnessTests.RunAll();
            CostPeriodTests.RunAll();
            TokenDisplayTextTests.RunAll();
            TokenAccountingTests.RunAll();
            OtherTokenAccountingTests.RunAll();
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
            LookCatalogTests.RunAll();
            ClaudeCredentialsTests.RunAll();
            ActivityMonitorScanTests.RunAll();
            Console.WriteLine("ALL GREEN");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"RED: {error.Message}");
            return 1;
        }
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
                $"target={session.LaunchTarget}");
        }
        return 0;
    }
}
