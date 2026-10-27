using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Pins the opt-in Grok permission-prompt hook: installing adds one hook file
/// of ours and a script of ours and nothing else, uninstalling removes only
/// those, a file of that name that isn't ours is left alone, and a prompt
/// marker counts only while it is newer than its session's transcript.
public static class GrokHooksTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("install adds our hook file and script, and only those", InstallAddsOurFiles),
            ("the hook file asks for nothing Grok does not read", HandlerUsesOnlyReadFields),
            ("the script reads ids and a type, never text", ScriptReadsNoText),
            ("install is idempotent and uninstall removes only ours", UninstallRemovesOnlyOurs),
            ("a file of our name that is not ours is left alone", ConflictIsLeftAlone),
            ("a marker newer than the transcript is a pending approval", PendingWhenNewer),
            ("a transcript write after the marker retires it", RetiredByWrite),
            ("stale, future and other-typed markers are ignored", InvalidMarkersIgnored),
            ("a corrupt unrelated marker does not hide approvals", CorruptMarkerIsIsolated),
            ("the installed script turns Grok's event into a marker without its text", ScriptWritesMarker),
            ("the scan reads a pending marker as the session's approval", ScanReadsApproval),
            ("a completed turn is not reopened by a late marker", ClosedTurnStaysClosed),
            ("a marker wakes the file watcher, another folder's json does not", MarkerWakesWatcher),
            ("sessions, auth and hooks all read one Grok home", OneGrokHome),
            ("switches flipped at once never clash, and the last one wins", TogglesTakeTurns),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS grok hooks: {name}");
        }
        Console.WriteLine("GrokHooksTests GREEN");
    }

    /// A throwaway hooks folder, script folder and signal folder; real time,
    /// since the rule compares against the transcript's own write time.
    private static void With(Action<string, string, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "agentisland-grok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (hooks, scripts, signals) = (Path.Combine(root, "hooks"), Path.Combine(root, "scripts"), Path.Combine(root, "signals"));
        Directory.CreateDirectory(signals);
        var before = (GrokHooks.HooksDirectory, GrokHooks.ScriptDirectory, GrokHooks.SignalDirectory);
        (GrokHooks.HooksDirectory, GrokHooks.ScriptDirectory, GrokHooks.SignalDirectory) = (hooks, scripts, signals);
        try
        {
            test(root, hooks, signals);
        }
        finally
        {
            (GrokHooks.HooksDirectory, GrokHooks.ScriptDirectory, GrokHooks.SignalDirectory) = before;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void InstallAddsOurFiles() => With((root, hooks, _) =>
    {
        Expect(!GrokHooks.IsInstalled(), "nothing is installed at first");
        Expect(GrokHooks.SetInstalled(true) is null, "install succeeds");
        Expect(GrokHooks.IsInstalled(), "installed");
        Expect(Directory.GetFiles(hooks).Length == 1 && File.Exists(Path.Combine(hooks, GrokHooks.HookFileName)), "exactly one file in the hooks folder");
        Expect(File.Exists(Path.Combine(root, "scripts", "grok-hook.ps1")), "the script is ours, outside ~/.grok");
        using var doc = JsonDocument.Parse(File.ReadAllText(GrokHooks.HookPath));
        var group = doc.RootElement.GetProperty("hooks").GetProperty("Notification")[0];
        Expect(group.GetProperty("matcher").GetString() == "permission_prompt", "only permission prompts");
        Expect(group.GetProperty("hooks")[0].GetProperty("command").GetString()!.Contains("grok-hook.ps1", StringComparison.Ordinal), "the command runs our script");
    });

    private static void HandlerUsesOnlyReadFields() => With((_, _, _) =>
    {
        // Grok reads type, command, url, timeout and env; async and shell are reported as load errors.
        using var doc = JsonDocument.Parse(GrokHooks.HookJson());
        var handler = doc.RootElement.GetProperty("hooks").GetProperty("Notification")[0].GetProperty("hooks")[0];
        var names = handler.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Expect(names.SequenceEqual(new[] { "command", "timeout", "type" }), $"handler fields were {string.Join(",", names)}");
    });

    private static void ScriptReadsNoText()
    {
        var script = GrokHooks.Script;
        foreach (var text in new[] { "message", "toolInput", "tool_input", "prompt", "command" })
            Expect(!script.Contains(text, StringComparison.OrdinalIgnoreCase), $"the script must not read '{text}'");
        Expect(script.Contains("sessionId") && script.Contains("notificationType"), "it reads the session and the type");
    }

    private static void UninstallRemovesOnlyOurs() => With((root, hooks, _) =>
    {
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "other.json"), "{}");
        Expect(GrokHooks.SetInstalled(true) is null && GrokHooks.SetInstalled(true) is null, "installing twice is fine");
        Expect(GrokHooks.SetInstalled(false) is null, "uninstall succeeds");
        Expect(!GrokHooks.IsInstalled(), "gone");
        Expect(File.Exists(Path.Combine(hooks, "other.json")), "another hook file is untouched");
        Expect(!File.Exists(Path.Combine(root, "scripts", "grok-hook.ps1")), "our script is gone");
        File.Delete(Path.Combine(hooks, "other.json"));
        GrokHooks.SetInstalled(true);
        GrokHooks.SetInstalled(false);
        Expect(!Directory.Exists(hooks), "a hooks folder we created is removed once empty");
    });

    private static void ConflictIsLeftAlone() => With((_, hooks, _) =>
    {
        Directory.CreateDirectory(hooks);
        var foreign = "{\"hooks\":{}}";
        File.WriteAllText(GrokHooks.HookPath, foreign);
        Expect(GrokHooks.Conflicts() && !GrokHooks.IsInstalled(), "it is a conflict, not our install");
        Expect(GrokHooks.SetInstalled(true) is { Length: > 0 }, "install says why it did nothing");
        Expect(GrokHooks.SetInstalled(false) is { Length: > 0 }, "uninstall leaves it too");
        Expect(File.ReadAllText(GrokHooks.HookPath) == foreign, "the file is untouched");
    });

    private static void ScriptWritesMarker() => With((root, _, signals) =>
    {
        GrokHooks.SetInstalled(true);
        var script = Path.Combine(root, "scripts", "grok-hook.ps1");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["AGENTISLAND_GROK_SIGNALS"] = signals;
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.WriteLine("{\"hookEventName\":\"notification\",\"sessionId\":\"01a1-abc\",\"session_id\":\"01a1-abc\",\"notificationType\":\"permission_prompt\",\"message\":\"SECRET TEXT\",\"cwd\":\"D:/x\"}");
        process.StandardInput.Close();
        Expect(process.WaitForExit(30_000), "the script finishes");
        Expect(process.ExitCode == 0, $"the script exits 0, got {process.ExitCode}");
        var text = File.ReadAllText(Path.Combine(signals, "01a1-abc.json"));
        using var doc = JsonDocument.Parse(text);
        Expect(doc.RootElement.GetProperty("type").GetString() == "permission_prompt" && doc.RootElement.GetProperty("session_id").GetString() == "01a1-abc", "the marker holds the session and the type");
        Expect(!text.Contains("SECRET", StringComparison.Ordinal), "never the text");
    });

    private static string Transcript(string folder, DateTimeOffset at, string update = "tool_call")
    {
        var path = Path.Combine(folder, "updates.jsonl");
        var line = new JsonObject
        {
            ["timestamp"] = at.ToUnixTimeSeconds(),
            ["params"] = new JsonObject { ["update"] = new JsonObject { ["sessionUpdate"] = update, ["toolCallId"] = "t1" } },
        };
        File.WriteAllText(path, line.ToJsonString() + "\n");
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
        return path;
    }

    private static void Marker(string signals, string session, string type, DateTimeOffset at) =>
        File.WriteAllText(Path.Combine(signals, session + ".json"),
            new JsonObject { ["session_id"] = session, ["type"] = type, ["at"] = at.ToUnixTimeMilliseconds() }.ToJsonString());

    private static void PendingWhenNewer() => With((root, _, signals) =>
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = Transcript(root, now.AddSeconds(-40));
        Marker(signals, "s1", "permission_prompt", now.AddSeconds(-5));
        var pending = GrokHooks.PendingFor(new[] { ("s1", transcript) }, now);
        Expect(pending.TryGetValue("s1", out var prompt) && prompt.Type == "permission_prompt", "a marker newer than the transcript is open");
    });

    private static void RetiredByWrite() => With((root, _, signals) =>
    {
        var now = DateTimeOffset.UtcNow;
        Marker(signals, "s1", "permission_prompt", now.AddSeconds(-30));
        var transcript = Transcript(root, now.AddSeconds(-2));
        Expect(GrokHooks.PendingFor(new[] { ("s1", transcript) }, now).Count == 0, "answering writes the transcript, which retires the marker");
    });

    private static void InvalidMarkersIgnored() => With((root, _, signals) =>
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = Transcript(root, now.AddHours(-3));
        foreach (var (type, at) in new[] { ("permission_prompt", now.AddHours(-2)), ("permission_prompt", now.AddMinutes(1)), ("idle_prompt", now.AddSeconds(-5)) })
        {
            Marker(signals, "s1", type, at);
            Expect(GrokHooks.PendingFor(new[] { ("s1", transcript) }, now).Count == 0, $"{type} at {at:u} must be ignored");
        }
    });

    private static void CorruptMarkerIsIsolated() => With((root, _, signals) =>
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = Transcript(root, now.AddSeconds(-40));
        Marker(signals, "s1", "permission_prompt", now.AddSeconds(-5));
        File.WriteAllText(Path.Combine(signals, "s2.json"), "{");
        var other = Path.Combine(root, "other.jsonl");
        File.WriteAllText(other, "{}\n");
        var pending = GrokHooks.PendingFor(new[] { ("s2", other), ("s1", transcript) }, now);
        Expect(pending.ContainsKey("s1") && !pending.ContainsKey("s2"), "the corrupt marker hides nothing");
    });

    private static ScannedSession Scan(string transcript, DateTimeOffset now, string session)
    {
        SessionScanner.ClearTurnCache();
        var state = SessionScanner.SessionState(transcript, now, new Dictionary<string, DateTimeOffset>(), null, SessionTurnState.Grok);
        return new(TriggerTool.Grok, session, "", session, state.Modified, state.Status, transcript, state.TurnKey, SessionLaunchTarget.Cli);
    }

    /// A marker kicks a scan the moment it lands, as a transcript line does,
    /// so an approval shows without waiting for the poll.
    private static void MarkerWakesWatcher() => With((root, _, signals) =>
    {
        Expect(TranscriptEventStream.IsRelevant(Path.Combine(signals, "s1.json")), "a marker wakes the scan");
        Expect(!TranscriptEventStream.IsRelevant(Path.Combine(root, "state.json")), "another folder's json does not");
        Expect(TranscriptEventStream.IsRelevant(Path.Combine(root, "local_1234.json")), "a Claude Desktop session still does");
        Expect(TranscriptEventStream.IsRelevant(Path.Combine(root, "updates.jsonl")), "a transcript still does");
    });

    private static void TogglesTakeTurns() => With((root, hooks, _) =>
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            if (GrokHooks.SetInstalled(i % 2 == 0) is { } error) errors.Add(error);
        });
        Expect(errors.IsEmpty, "no change failed: " + string.Join(" | ", errors.Distinct()));
        Expect(GrokHooks.SetInstalled(true) is null && GrokHooks.IsInstalled(), "the last install wins");
        Expect(Directory.GetFiles(hooks, "*.tmp").Length == 0 && Directory.GetFiles(Path.Combine(root, "scripts"), "*.tmp").Length == 0,
            "no temp copy is left behind");
        Expect(GrokHooks.SetInstalled(false) is null && !GrokHooks.IsInstalled(), "the last uninstall wins");
    });

    /// GROK_HOME as the CLI reads it, ~ expanded, with every reader under it.
    private static void OneGrokHome()
    {
        var home = IslandPaths.Home;
        Expect(IslandPaths.GrokHomeFrom(null), Path.Combine(home, ".grok"));
        Expect(IslandPaths.GrokHomeFrom("  "), Path.Combine(home, ".grok"));
        Expect(IslandPaths.GrokHomeFrom(@" D:\grok "), @"D:\grok");
        Expect(IslandPaths.GrokHomeFrom("~"), home);
        Expect(IslandPaths.GrokHomeFrom("~/grok"), Path.Combine(home, "grok"));
        Expect(IslandPaths.GrokHomeFrom(@"~\grok"), Path.Combine(home, "grok"));
        var grokHome = IslandPaths.GrokHome;
        Expect(IslandPaths.GrokSessionsRoot, Path.Combine(grokHome, "sessions"));
        Expect(AgentIsland.Usage.GrokAuthFile.DefaultPath, Path.Combine(grokHome, "auth.json"));
        var before = GrokHooks.HooksDirectory;
        GrokHooks.HooksDirectory = "";
        try
        {
            Expect(GrokHooks.HookPath, Path.Combine(grokHome, "hooks", GrokHooks.HookFileName));
        }
        finally
        {
            GrokHooks.HooksDirectory = before;
        }
    }

    private static void ScanReadsApproval() => With((root, _, signals) =>
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = Transcript(root, now.AddSeconds(-40));
        Marker(signals, "s1", "permission_prompt", now.AddSeconds(-5));
        var prepared = ActivityMonitor.PrepareSessions(new[] { Scan(transcript, now, "s1") }, now)[0];
        Expect(prepared.Status == ActivityState.Working && prepared.PendingPrompt?.Phase == AgentPhase.Approving, "the session reads as waiting for approval");
        // The per-session path then says it, once.
        var detector = new SessionEventDetector((_, _, _) => null, () => Array.Empty<SkippedTask>());
        detector.Observe(new[] { prepared with { PendingPrompt = null } }, now.AddSeconds(-6));
        var seen = detector.Observe(new[] { prepared }, now);
        Expect(seen.Fresh.Count == 1 && seen.Fresh[0].Kind == SessionEventKind.Approval && seen.Fresh[0].Tool == TriggerTool.Grok, "Grok's bot says it needs approval");
    });

    private static void ClosedTurnStaysClosed() => With((root, _, signals) =>
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = Transcript(root, now.AddSeconds(-40), "turn_completed");
        Marker(signals, "s1", "permission_prompt", now.AddSeconds(-5));
        var scanned = Scan(transcript, now, "s1");
        var prepared = ActivityMonitor.PrepareSessions(new[] { scanned }, now)[0];
        Expect(prepared.PendingPrompt is null && prepared.Status == scanned.Status, "a finished turn is not reopened");
    });
}
