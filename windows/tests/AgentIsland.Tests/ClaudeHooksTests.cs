using System.IO;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Pins the opt-in Claude Code permission-prompt hook: installing merges
/// into settings.json without touching anything else and only once,
/// uninstalling removes only our group, a prompt marker counts only while it
/// is newer than the transcript's last write, and an open prompt survives
/// the scan's stall verdict.
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
            File.WriteAllText(transcript, "{}\n");
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

    private static void Expect<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}, got {actual}");
    }
}
