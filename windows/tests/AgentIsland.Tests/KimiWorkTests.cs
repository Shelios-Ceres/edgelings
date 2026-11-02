using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using static AgentIsland.Tests.Check;
using static AgentIsland.Core.AgentPresences;

namespace AgentIsland.Tests;

/// Kimi's desktop app (Kimi Work) and the approval wait. Kimi Work runs on an
/// embedded Kimi Code, so its conversations are Kimi sessions in a second
/// tree (conv-<id> folders); every test builds synthetic wire files in temp
/// folders and never touches a real one.
public static class KimiWorkTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("the Kimi Work tree is under the desktop app's data folder", WorkRoot),
            ("a Kimi Work conversation is a Kimi session: working while its turn is open, done after", WorkingThenDone),
            ("an approval left open is Approving with the stock event, and resolving it ends the wait", ApprovalOpenThenResolved),
            ("a question kind is Asking", QuestionIsAsking),
            ("a request behind a closed turn is not a wait", ClosedTurnDropsRequest),
            ("an approval in a Kimi Code CLI session reads the same way", CliApproval),
            ("the desktop app's helper sessions (titles, automations) are not conversations", HelpersSkipped),
            ("a request's display (the command and the folder) is never read or kept", DisplayNeverRead),
            ("tokens of both trees count once, and a copied history counts once", TokensCountOnce),
            ("the Kimi desktop app counts for Kimi's presence by its install folder", DesktopPresence),
            ("the kimi CLI's path hint and the desktop app's both match their own", PathHints),
            ("a desktop-hosted session is idled with its app, a CLI session with its CLI", SettleFollowsHost),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS kimi work: " + name);
        }
        Console.WriteLine("KimiWorkTests GREEN");
    }

    private static string Wd(string name = "wd_test_" ) => name + Guid.NewGuid().ToString("N")[..8];

    /// Runs `body` with a fresh KIMI_CODE_HOME and a clean Kimi Work tree.
    private static void Isolated(Action<string> body)
    {
        var home = Path.Combine(Path.GetTempPath(), "agentisland-kimiwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var before = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
        var work = IslandPaths.KimiWorkSessionsRoot;
        var workExisted = Directory.Exists(work);
        var seen = workExisted ? Directory.GetDirectories(work).ToHashSet() : new HashSet<string>();
        try
        {
            body(home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", before);
            try { Directory.Delete(home, recursive: true); } catch { }
            if (Directory.Exists(work))
            {
                foreach (var dir in Directory.GetDirectories(work).Where(d => !seen.Contains(d)))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
    }

    private static string Conversation(string id, DateTimeOffset modified, params string[] lines) =>
        Write(Path.Combine(IslandPaths.KimiWorkSessionsRoot, Wd(), "conv-" + id), "conv-" + id, modified, lines);

    private static string CliSession(string home, string id, DateTimeOffset modified, params string[] lines) =>
        Write(Path.Combine(home, "sessions", Wd(), "session_" + id), "session_" + id, modified, lines);

    private static string Write(string dir, string name, DateTimeOffset modified, string[] lines)
    {
        var agent = Path.Combine(dir, "agents", "main");
        Directory.CreateDirectory(agent);
        // Kimi Work's state.json names the folder workDir; Kimi Code's calls it cwd.
        File.WriteAllText(Path.Combine(dir, "state.json"), name.StartsWith("conv-", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new { id = name, workDir = @"D:\work\app" })
            : JsonSerializer.Serialize(new { id = name, cwd = @"D:\work\app" }));
        var wire = Path.Combine(agent, "wire.jsonl");
        File.WriteAllLines(wire, lines);
        File.SetLastWriteTimeUtc(wire, modified.UtcDateTime);
        return wire;
    }

    private static string Line(string type, DateTimeOffset at, object? extra = null)
    {
        var map = new Dictionary<string, object?> { ["type"] = type, ["time"] = at.ToUnixTimeMilliseconds() };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties()) map[property.Name] = property.GetValue(extra);
        }
        return JsonSerializer.Serialize(map);
    }

    private static string Request(DateTimeOffset at, string id, string kind = "approval") =>
        Line("interaction.request", at, new
        {
            id,
            kind,
            toolCallId = "call-" + id,
            request = new
            {
                toolName = "Bash",
                action = "run",
                display = new { command = "SECRET-COMMAND --token hunter2", cwd = @"D:\secret-folder" },
            },
        });

    private static string Resolved(DateTimeOffset at, string id) =>
        Line("interaction.resolved", at, new { id, response = new { decision = "approved" } });

    private static List<ScannedSession> Kimi(DateTimeOffset now) =>
        SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>());

    private static void WorkRoot()
    {
        var expected = Path.Combine(IslandPaths.RoamingAppData, "kimi-desktop", "daimon-share", "daimon", "runtime", "kimi-code", "home", "sessions");
        Expect(IslandPaths.KimiWorkSessionsRoot, expected);
    }

    private static void WorkingThenDone() => Isolated(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var wire = Conversation(id, now, Line("agent.turn.started", now.AddSeconds(-5)), Line("llm.request", now.AddSeconds(-4)));
        var working = Kimi(now).Single(s => s.SessionId == id);
        Expect(working.Tool, TriggerTool.Kimi);
        Expect(working.Status, ActivityState.Working);
        Expect(working.PendingPrompt is null, "no wait");
        // The turn ends: the conversation is done, not working.
        File.AppendAllLines(wire, new[] { Line("agent.turn.ended", now.AddSeconds(-1)) });
        File.SetLastWriteTimeUtc(wire, now.UtcDateTime);
        var done = Kimi(now).Single(s => s.SessionId == id);
        Expect(done.Status != ActivityState.Working, "a finished conversation is not working");
        Expect(done.TurnEnded, "and its turn ended");
    });

    private static void ApprovalOpenThenResolved() => Isolated(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var wire = Conversation(id, now,
            Line("agent.turn.started", now.AddSeconds(-9)), Line("llm.request", now.AddSeconds(-8)),
            Request(now.AddSeconds(-5), "i1"));
        var waiting = Kimi(now).Single(s => s.SessionId == id);
        Expect(waiting.Status, ActivityState.Working);
        Expect(waiting.PendingPrompt is { Phase: AgentPhase.Approving }, "Approving");
        // The stock approval event is raised for a wait, as for Claude's and Grok's.
        var detector = new SessionEventDetector((_, _, _) => null, () => Array.Empty<SkippedTask>());
        detector.Observe(new List<ScannedSession>(), now);
        var observed = detector.Observe(new List<ScannedSession> { waiting }, now);
        Expect(observed.Waiting.Any(e => e.Kind == SessionEventKind.Approval), "it waits on the card's list");
        Expect(observed.Fresh.Any(e => e.Kind == SessionEventKind.Approval), "a stock approval event");
        // The user decides: the wait ends and the turn goes on.
        File.AppendAllLines(wire, new[] { Resolved(now.AddSeconds(-2), "i1"), Line("llm.request", now.AddSeconds(-1)) });
        File.SetLastWriteTimeUtc(wire, now.UtcDateTime);
        var resumed = Kimi(now).Single(s => s.SessionId == id);
        Expect(resumed.Status, ActivityState.Working);
        Expect(resumed.PendingPrompt is null, "resolved ends the wait");
        // A second request on another id waits again.
        File.AppendAllLines(wire, new[] { Request(now, "i2") });
        File.SetLastWriteTimeUtc(wire, now.UtcDateTime);
        Expect(Kimi(now).Single(s => s.SessionId == id).PendingPrompt is { Phase: AgentPhase.Approving }, "the next request waits again");
    });

    private static void QuestionIsAsking() => Isolated(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        Conversation(id, now, Line("agent.turn.started", now.AddSeconds(-4)), Request(now.AddSeconds(-2), "q1", kind: "question"));
        Expect(Kimi(now).Single(s => s.SessionId == id).PendingPrompt is { Phase: AgentPhase.Asking }, "Asking");
    });

    private static void ClosedTurnDropsRequest() => Isolated(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        // The turn was cancelled with the request open: nothing waits.
        Conversation(id, now,
            Line("agent.turn.started", now.AddSeconds(-9)), Request(now.AddSeconds(-7), "i1"), Line("prompt.aborted", now.AddSeconds(-5)));
        var session = Kimi(now).Single(s => s.SessionId == id);
        Expect(session.PendingPrompt is null, "no wait behind a closed turn");
        Expect(KimiInteractions.Pending(new[] { Request(now, "x"), Line("turn.ended", now.AddSeconds(1)) }) is null, "the closing line stops the search");
        Expect(KimiInteractions.Pending(new[] { Line("turn.ended", now), Request(now.AddSeconds(1), "y") }) is { Id: "y" }, "a request after the close is the next turn's");
    });

    private static void HelpersSkipped() => Isolated(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var open = new[] { Line("agent.turn.started", now.AddSeconds(-3)) };
        Conversation(id, now, open);
        var wd = Wd();
        foreach (var helper in new[] { "ctitle-" + id, "automation-" + id })
        {
            Write(Path.Combine(IslandPaths.KimiWorkSessionsRoot, wd, helper), helper, now, open);
        }
        var scanned = Kimi(now).Where(s => s.DesktopHosted).ToList();
        Expect(string.Join(",", scanned.Select(s => s.SessionId)), id);
        // The conversation's folder comes from workDir.
        Expect(scanned.Single().Cwd, @"D:\work\app");
    });

    private static void CliApproval() => Isolated(home =>
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var wire = CliSession(home, id, now, Line("agent.turn.started", now.AddSeconds(-6)), Request(now.AddSeconds(-3), "c1"));
        var waiting = Kimi(now).Single(s => s.SessionId == id);
        Expect(waiting.PendingPrompt is { Phase: AgentPhase.Approving }, "the CLI waits on an approval too");
        File.AppendAllLines(wire, new[] { Resolved(now.AddSeconds(-1), "c1") });
        File.SetLastWriteTimeUtc(wire, now.UtcDateTime);
        Expect(Kimi(now).Single(s => s.SessionId == id).PendingPrompt is null, "and the wait ends when it is resolved");
    });

    private static void DisplayNeverRead() => Isolated(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        Conversation(id, now, Line("agent.turn.started", now.AddSeconds(-4)), Request(now.AddSeconds(-2), "i1"));
        var session = Kimi(now).Single(s => s.SessionId == id);
        var text = session.ToString() + JsonSerializer.Serialize(session.PendingPrompt);
        Expect(!text.Contains("SECRET", StringComparison.Ordinal) && !text.Contains("hunter2", StringComparison.Ordinal), "nothing of the display is kept");
        Expect(session.PendingPrompt?.Detail is null, "the wait carries no text");
        // The parser touches only type, kind, id and time: a display that is not even an object changes nothing.
        var odd = "{\"type\":\"interaction.request\",\"id\":\"z\",\"kind\":\"approval\",\"time\":" + now.ToUnixTimeMilliseconds()
            + ",\"request\":{\"toolName\":\"Bash\",\"display\":[1,{\"a\":2},\"SECRET\"]}}";
        Expect(KimiInteractions.Pending(new[] { odd }) is { Kind: "approval", Id: "z" }, "an odd display is not looked at");
    });

    private static void TokensCountOnce() => Isolated(home =>
    {
        var now = DateTimeOffset.UtcNow;
        string Usage(int seconds) => Line("usage.record", now.AddSeconds(-seconds), new
        {
            model = "kimi-code/k3",
            agentId = "main",
            usageScope = "turn-1",
            usage = new { inputOther = 100, output = 50, inputCacheRead = 0, inputCacheCreation = 0 },
        });
        Conversation(Guid.NewGuid().ToString("N"), now, Usage(30));
        // The same record in a Kimi Code session of the other tree (a copied history) counts once.
        CliSession(home, Guid.NewGuid().ToString("N"), now, Usage(30));
        // A different record counts on top.
        Conversation(Guid.NewGuid().ToString("N"), now, Usage(20));
        var events = KimiLogReader.Scan(30).Where(e => e.Model == "kimi-code/k3").ToList();
        Expect(events.Count, 2);
        Expect(events.Sum(e => e.InputTokens), 200L);
    });

    private static readonly Running Explorer = new(1, 0, "explorer");

    private static void DesktopPresence()
    {
        var desktop = new Running(70, 1, "Kimi", @"C:\Users\me\AppData\Local\Programs\kimi-desktop\Kimi.exe");
        var open = Of(new[] { Explorer, desktop }, new[] { new AppWindow(70, false) });
        Expect(open[TriggerTool.Kimi], AgentPresence.Open);
        var minimized = Of(new[] { Explorer, desktop }, new[] { new AppWindow(70, true) });
        Expect(minimized[TriggerTool.Kimi], AgentPresence.Minimized);
        // Its helper processes (renderers, GPU) carry the same name and show no window of their own.
        var helper = new Running(71, 70, "Kimi", desktop.Path);
        Expect(Of(new[] { Explorer, desktop, helper }, new[] { new AppWindow(70, false) })[TriggerTool.Kimi], AgentPresence.Open);
        // It is not Kimi Code: the CLI's sessions are still gone while only the desktop app runs.
        Expect(AgentProcesses.Gone(new[] { ("Kimi", (string?)desktop.Path) }).Contains(TriggerTool.Kimi), "Kimi Code is gone");
        // But Kimi Work's sessions follow the desktop app.
        Expect(!AgentProcesses.DesktopGone(new[] { ("Kimi", (string?)desktop.Path) }).Contains(TriggerTool.Kimi), "the desktop app is up");
        Expect(AgentProcesses.DesktopGone(Array.Empty<(string, string?)>()).Contains(TriggerTool.Kimi), "the desktop app is gone");
        Expect(AgentProcesses.DesktopGone(new[] { ("Kimi", (string?)@"C:\Program Files\Kimi\Kimi.exe") }).Contains(TriggerTool.Kimi), "another Kimi.exe is not it");
        Expect(!AgentProcesses.DesktopGone(new[] { ("Kimi", (string?)null) }).Contains(TriggerTool.Kimi), "an unreadable path counts as alive");
    }

    private static void SettleFollowsHost()
    {
        var now = DateTimeOffset.UtcNow;
        ScannedSession Make(string id, bool hosted) => new(TriggerTool.Kimi, id, @"D:\work", id, now, ActivityState.Working, null, "k", SessionLaunchTarget.Cli) { DesktopHosted = hosted };
        var sessions = new List<ScannedSession> { Make("cli", false), Make("work", true) };
        var kimi = new HashSet<TriggerTool> { TriggerTool.Kimi };
        var none = new HashSet<TriggerTool>();
        // Only the desktop app runs: Kimi Code's CLI session is abandoned, Kimi Work's is not.
        var onlyDesktop = ActivityMonitor.Settle(sessions, kimi, false, none);
        Expect(onlyDesktop.Single(s => s.SessionId == "cli").Status, ActivityState.Idle);
        Expect(onlyDesktop.Single(s => s.SessionId == "work").Status, ActivityState.Working);
        // Only the CLI runs: the reverse.
        var onlyCli = ActivityMonitor.Settle(sessions, none, false, kimi);
        Expect(onlyCli.Single(s => s.SessionId == "cli").Status, ActivityState.Working);
        Expect(onlyCli.Single(s => s.SessionId == "work").Status, ActivityState.Idle);
    }

    private static void PathHints()
    {
        var cli = new Running(80, 1, "kimi", @"C:\Users\me\.kimi-code\bin\kimi.exe");
        Expect(Of(new[] { Explorer, cli }, new[] { new AppWindow(80, false) })[TriggerTool.Kimi], AgentPresence.Open);
        var elsewhere = new Running(81, 1, "Kimi", @"C:\Program Files\Kimi\Kimi.exe");
        Expect(Of(new[] { Explorer, elsewhere }, new[] { new AppWindow(81, false) })[TriggerTool.Kimi], AgentPresence.Closed);
    }
}
