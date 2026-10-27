using System.Text.Json.Nodes;
using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Pins the opt-in Claude Code permission-prompt hook: installing merges
/// into settings.json without touching anything else and only once,
/// uninstalling removes only our group, a prompt marker counts only while it
/// is newer than the transcript's last write, and an open prompt survives
/// transcript silence without overriding a turn's end or a closed agent.
public static class ClaudeHooksTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("install keeps other settings and is idempotent", InstallMerges),
            ("uninstall removes only our group", UninstallKeepsOthers),
            ("uninstall of the last group drops the empty hooks", UninstallCleansUp),
            ("a marker newer than the transcript is a pending approval", PendingWhenNewer),
            ("a transcript write after the marker retires it", RetiredByWrite),
            ("an idle prompt is not an approval", IgnoresIdlePrompt),
            ("the classifier reads the pending approval", ClassifierApproving),
            ("an open prompt outranks network trouble", ApprovingOutranksNetworkTrouble),
            ("a prompt outranks a newer working sibling", PromptWinsSelection),
            ("a prompt survives the scanner's idle age", PromptSurvivesSilence),
            ("transcript progress retires the selected prompt", ProgressRetiresSelection),
            ("a delayed marker cannot reopen a completed or interrupted turn", ClosedTurnsStayClosed),
            ("process exit retires a pending prompt", ProcessExitWins),
            ("future and expired markers are ignored", InvalidTimesIgnored),
            ("a corrupt unrelated marker does not hide approvals", CorruptMarkerIsIsolated),
            ("a locked unrelated marker does not hide approvals", LockedMarkerIsIsolated),
            ("switches flipped at once never clash, and the last one wins", TogglesTakeTurns),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS claude hooks: {name}");
        }
        Console.WriteLine("ClaudeHooksTests GREEN");
    }

    private static void InstallMerges()
    {
        var settings = JsonNode.Parse("""{"theme":"dark","hooks":{"Stop":[{"hooks":[{"type":"command","command":"say done"}]}]}}""")!.AsObject();
        Expect(ClaudeHooks.Install(settings), true);
        Expect(ClaudeHooks.Install(settings), false);
        Expect(settings["theme"]!.GetValue<string>(), "dark");
        Expect(settings["hooks"]!["Stop"]!.AsArray().Count, 1);
        var group = settings["hooks"]!["Notification"]!.AsArray().Single()!;
        Expect(group["matcher"]!.GetValue<string>(), "permission_prompt|elicitation_dialog");
        Expect(group["hooks"]![0]!["async"]!.GetValue<bool>(), true);
        Expect(ClaudeHooks.Contains(settings), true);
    }

    private static void UninstallKeepsOthers()
    {
        var settings = JsonNode.Parse("""{"hooks":{"Notification":[{"matcher":"idle_prompt","hooks":[{"type":"command","command":"beep"}]}]}}""")!.AsObject();
        ClaudeHooks.Install(settings);
        Expect(ClaudeHooks.Uninstall(settings), true);
        var groups = settings["hooks"]!["Notification"]!.AsArray();
        Expect(groups.Count, 1);
        Expect(groups[0]!["matcher"]!.GetValue<string>(), "idle_prompt");
        Expect(ClaudeHooks.Uninstall(settings), false);
    }

    private static void UninstallCleansUp()
    {
        var settings = JsonNode.Parse("""{"theme":"light"}""")!.AsObject();
        ClaudeHooks.Install(settings);
        ClaudeHooks.Uninstall(settings);
        Expect(settings.ContainsKey("hooks"), false);
        Expect(settings.Count, 1);
    }

    private static void PendingWhenNewer() => WithFiles((transcript, now) =>
    {
        Marker(transcript, "permission_prompt", now.AddSeconds(-5));
        File.SetLastWriteTimeUtc(transcript, now.AddSeconds(-20).UtcDateTime);
        Expect(ClaudeHooks.Pending(transcript, now)?.Type, "permission_prompt");
    });

    private static void RetiredByWrite() => WithFiles((transcript, now) =>
    {
        Marker(transcript, "permission_prompt", now.AddSeconds(-30));
        File.SetLastWriteTimeUtc(transcript, now.AddSeconds(-2).UtcDateTime);
        Expect(ClaudeHooks.Pending(transcript, now), null);
    });

    private static void IgnoresIdlePrompt() => WithFiles((transcript, now) =>
    {
        Marker(transcript, "idle_prompt", now.AddSeconds(-5));
        File.SetLastWriteTimeUtc(transcript, now.AddSeconds(-20).UtcDateTime);
        Expect(ClaudeHooks.Pending(transcript, now), null);
    });

    private static void ClassifierApproving() => WithFiles((transcript, now) =>
    {
        Marker(transcript, "permission_prompt", now.AddSeconds(-5));
        File.SetLastWriteTimeUtc(transcript, now.AddSeconds(-20).UtcDateTime);
        Expect(AgentPhaseClassifier.Classify(TriggerTool.Claude, transcript, now)?.Phase, AgentPhase.Approving);
    });

    private static void ApprovingOutranksNetworkTrouble()
    {
        var reading = new PhaseReading(AgentPhase.Approving, null, DateTimeOffset.Now);
        Expect(AgentPhases.Combine(ActivityState.NetworkTrouble, reading, quotaExhausted: false), AgentPhase.Approving);
        Expect(AgentPhases.Combine(ActivityState.NetworkTrouble, null, quotaExhausted: false), AgentPhase.NetworkTrouble);
        Expect(AgentPhases.Combine(ActivityState.Working, reading, quotaExhausted: true), AgentPhase.Approving);
        Expect(AgentPhases.Combine(ActivityState.RateLimited, reading, quotaExhausted: true), AgentPhase.Approving);
        Expect(AgentPhases.Combine(ActivityState.AuthRequired, reading, quotaExhausted: false), AgentPhase.AuthRequired);
    }

    private static void PromptWinsSelection() => WithFiles((transcript, now) =>
    {
        OpenTurn(transcript, now.AddSeconds(-30));
        Marker(transcript, ClaudeHooks.PermissionPrompt, now.AddSeconds(-20));
        var sessions = ActivityMonitor.PrepareSessions(new[]
        {
            Scan(transcript, now),
            new ScannedSession(TriggerTool.Claude, "sibling", "", "sibling", now.AddSeconds(-1), ActivityState.Working,
                null, null, SessionLaunchTarget.Cli),
        }, now);
        var selected = ActivityMonitor.BestSession(sessions, TriggerTool.Claude);
        Expect(selected.Thread?.TranscriptPath, transcript);
        Expect(selected.Thread?.PendingPrompt?.Phase, AgentPhase.Approving);
    });

    private static void PromptSurvivesSilence() => WithFiles((transcript, now) =>
    {
        OpenTurn(transcript, now.AddMinutes(-40));
        Marker(transcript, ClaudeHooks.ElicitationDialog, now.AddMinutes(-39));
        var scanned = Scan(transcript, now);
        Expect(scanned.Status, ActivityState.Idle);
        var sessions = ActivityMonitor.PrepareSessions(new[] { scanned }, now);
        sessions = ActivityMonitor.Settle(sessions, new HashSet<TriggerTool>(), offline: true);
        var selected = ActivityMonitor.BestSession(sessions, TriggerTool.Claude);
        Expect(selected.Thread?.PendingPrompt?.Phase, AgentPhase.Asking);
        Expect(AgentPhases.Combine(selected.State, selected.Thread?.PendingPrompt, false), AgentPhase.Asking);
    });

    private static void ProgressRetiresSelection() => WithFiles((transcript, now) =>
    {
        OpenTurn(transcript, now.AddSeconds(-30));
        Marker(transcript, ClaudeHooks.PermissionPrompt, now.AddSeconds(-20));
        Expect(ActivityMonitor.PrepareSessions(new[] { Scan(transcript, now) }, now)[0].PendingPrompt?.Phase, AgentPhase.Approving);
        OpenTurn(transcript, now.AddSeconds(-1));
        Expect(ActivityMonitor.PrepareSessions(new[] { Scan(transcript, now) }, now)[0].PendingPrompt, null);
    });

    private static void ClosedTurnsStayClosed() => WithFiles((transcript, now) =>
    {
        foreach (var closed in new[]
        {
            """{"type":"assistant","message":{"stop_reason":"end_turn","content":[]}}""",
            """{"type":"user","message":{"content":"[Request interrupted by user]"}}""",
            """{"type":"assistant","isApiErrorMessage":true,"message":{"stop_reason":"stop_sequence","content":[{"type":"text","text":"API Error: 401 unauthorized"}]}}""",
        })
        {
            var record = JsonNode.Parse(closed)!;
            record["timestamp"] = now.AddSeconds(-20).ToString("O");
            File.WriteAllText(transcript, record.ToJsonString() + "\n");
            File.SetLastWriteTimeUtc(transcript, now.AddSeconds(-20).UtcDateTime);
            SessionScanner.ClearTurnCache();
            Marker(transcript, ClaudeHooks.PermissionPrompt, now.AddSeconds(-10));
            var scanned = Scan(transcript, now);
            var prepared = ActivityMonitor.PrepareSessions(new[] { scanned }, now)[0];
            Expect(prepared.Status, scanned.Status);
            Expect(prepared.PendingPrompt, null);
            Expect(AgentPhaseClassifier.Classify(TriggerTool.Claude, transcript, now)?.Phase == AgentPhase.Approving, false);
        }
    });

    private static void ProcessExitWins() => WithFiles((transcript, now) =>
    {
        OpenTurn(transcript, now.AddSeconds(-30));
        Marker(transcript, ClaudeHooks.PermissionPrompt, now.AddSeconds(-20));
        var sessions = ActivityMonitor.PrepareSessions(new[] { Scan(transcript, now) }, now);
        sessions = ActivityMonitor.Settle(sessions, new HashSet<TriggerTool> { TriggerTool.Claude }, offline: true);
        Expect(sessions[0].Status, ActivityState.Idle);
        Expect(sessions[0].PendingPrompt, null);
        Expect(ActivityMonitor.BestSession(sessions, TriggerTool.Claude).Thread, null);
    });

    private static void InvalidTimesIgnored() => WithFiles((transcript, now) =>
    {
        OpenTurn(transcript, now.AddHours(-3));
        foreach (var at in new[] { now.AddHours(-2), now.AddMinutes(1) })
        {
            Marker(transcript, ClaudeHooks.PermissionPrompt, at);
            Expect(ClaudeHooks.Pending(transcript, now), null);
            Expect(ActivityMonitor.PrepareSessions(new[] { Scan(transcript, now) }, now)[0].PendingPrompt, null);
        }
    });

    private static void CorruptMarkerIsIsolated() => WithFiles((transcript, now) =>
    {
        OpenTurn(transcript, now.AddSeconds(-30));
        Marker(transcript, ClaudeHooks.PermissionPrompt, now.AddSeconds(-20));
        File.WriteAllText(Path.Combine(ClaudeHooks.SignalDirectory, "unrelated.json"), "{");
        Expect(ClaudeHooks.Pending(transcript, now)?.Type, ClaudeHooks.PermissionPrompt);
    });

    private static void LockedMarkerIsIsolated() => WithFiles((transcript, now) =>
    {
        OpenTurn(transcript, now.AddSeconds(-30));
        Marker(transcript, ClaudeHooks.PermissionPrompt, now.AddSeconds(-20));
        using var locked = new FileStream(Path.Combine(ClaudeHooks.SignalDirectory, "unrelated.json"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Expect(ClaudeHooks.Pending(transcript, now)?.Type, ClaudeHooks.PermissionPrompt);
    });

    private static void TogglesTakeTurns() => WithSettings(settings =>
    {
        File.WriteAllText(settings, """{"theme":"dark"}""");
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            if (ClaudeHooks.SetInstalled(i % 2 == 0) is { } error) errors.Add(error);
        });
        Expect(errors.IsEmpty, "no change failed: " + string.Join(" | ", errors.Distinct()));
        Expect(Directory.GetFiles(Path.GetDirectoryName(settings)!, "*.tmp").Length, 0);
        Expect(ClaudeHooks.SetInstalled(true), null);
        Expect(ClaudeHooks.SetInstalled(false), null);
        Expect(ClaudeHooks.IsInstalled(), false);
        Expect(JsonNode.Parse(File.ReadAllText(settings))!["theme"]!.GetValue<string>(), "dark");
    });

    /// A throwaway settings.json; the real one is never touched.
    private static void WithSettings(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "agentisland-claude-settings-" + Guid.NewGuid().ToString("N"));
        var previous = ClaudeHooks.SettingsFileOverride;
        Directory.CreateDirectory(root);
        ClaudeHooks.SettingsFileOverride = Path.Combine(root, ".claude", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudeHooks.SettingsFileOverride)!);
            test(ClaudeHooks.SettingsFileOverride);
        }
        finally
        {
            ClaudeHooks.SettingsFileOverride = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void OpenTurn(string transcript, DateTimeOffset at)
    {
        var line = new JsonObject
        {
            ["type"] = "assistant", ["timestamp"] = at.ToString("O"),
            ["message"] = new JsonObject
            {
                ["stop_reason"] = "tool_use", ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "tool_use", ["id"] = "tool1", ["name"] = "Bash" },
                },
            },
        };
        File.WriteAllText(transcript, line.ToJsonString() + "\n");
        File.SetLastWriteTimeUtc(transcript, at.UtcDateTime);
    }

    private static ScannedSession Scan(string transcript, DateTimeOffset now)
    {
        var state = SessionScanner.SessionState(transcript, now, new Dictionary<string, DateTimeOffset>(), null, SessionTurnState.Claude);
        return new(TriggerTool.Claude, "approval", "", "approval", state.Modified, state.Status, transcript, state.TurnKey, SessionLaunchTarget.Cli);
    }

    /// A throwaway transcript and signal directory; real time, since the
    /// rule compares against the file's own write time.
    private static void WithFiles(Action<string, DateTimeOffset> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "agentisland-hooks-" + Guid.NewGuid().ToString("N"));
        var previous = ClaudeHooks.SignalDirectory;
        Directory.CreateDirectory(root);
        ClaudeHooks.SignalDirectory = Path.Combine(root, "signals");
        Directory.CreateDirectory(ClaudeHooks.SignalDirectory);
        try
        {
            var transcript = Path.Combine(root, "session.jsonl");
            OpenTurn(transcript, DateTimeOffset.UtcNow.AddMinutes(-1));
            test(transcript, DateTimeOffset.UtcNow);
        }
        finally
        {
            ClaudeHooks.SignalDirectory = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Marker(string transcript, string type, DateTimeOffset at) =>
        File.WriteAllText(Path.Combine(ClaudeHooks.SignalDirectory, "s1.json"), new JsonObject
        {
            ["session_id"] = "s1",
            ["transcript_path"] = transcript.Replace('\\', '/'),
            ["type"] = type,
            ["at"] = at.ToUnixTimeMilliseconds(),
        }.ToJsonString());
}
