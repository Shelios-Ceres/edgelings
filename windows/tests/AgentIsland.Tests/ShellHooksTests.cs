using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The Mac app's `sh` hooks (ClaudeHooks.UseShell, GrokHooks.UseShell): what
/// they install, and, on a Mac where plutil exists, that the installed
/// command turns the agent's event into a marker without its text. Nothing
/// here touches the real ~/.claude or ~/.grok: every folder is a temp one.
public static class ShellHooksTests
{
    public static void RunAll()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP shell hooks: they are the Mac app's");
            return;
        }
        var tests = new (string Name, Action Test)[]
        {
            ("Claude's hook is an sh command with no shell field, found and removed by its marker", ClaudeInstallShape),
            ("Claude's installed command writes a marker without the prompt's text", ClaudeCommandWritesMarker),
            ("Grok's hook is a sh script of ours, and uninstall removes only ours", GrokInstallShape),
            ("Grok's installed script writes a marker without the prompt's text", GrokScriptWritesMarker),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS shell hooks: {name}");
        }
        Console.WriteLine("ShellHooksTests GREEN");
    }

    private static bool HasPlutil => File.Exists("/usr/bin/plutil");

    private static void ClaudeInstallShape() => WithClaude((settings, signals) =>
    {
        var root = new JsonObject();
        Expect(ClaudeHooks.Install(root), true);
        var handler = root["hooks"]!["Notification"]![0]!["hooks"]![0]!;
        Expect(handler["shell"] is null, "no powershell field");
        Expect(handler["command"]!.GetValue<string>().Contains("Edgelings/claude-hooks", StringComparison.Ordinal), "the marker is in the command");
        Expect(handler["command"]!.GetValue<string>().Contains(signals, StringComparison.Ordinal), "the command writes to the signal folder");
        Expect(ClaudeHooks.Contains(root), true);
        Expect(ClaudeHooks.Uninstall(root), true);
        Expect(root["hooks"] is null, "nothing of ours is left");
    });

    private static void ClaudeCommandWritesMarker() => WithClaude((settings, signals) =>
    {
        if (!HasPlutil) return;
        var command = ClaudeHooks.ShellCommand(signals);
        var transcript = "/tmp/a \"quoted\" \\ path/session.jsonl";
        var input = new JsonObject
        {
            ["session_id"] = "abc-123",
            ["transcript_path"] = transcript,
            ["notification_type"] = "permission_prompt",
            ["message"] = "TOP-SECRET prompt text",
        }.ToJsonString();
        Run("/bin/sh", new[] { "-c", command }, input);
        var file = Path.Combine(signals, "abc-123.json");
        Expect(File.Exists(file), "the marker was written");
        var text = File.ReadAllText(file);
        Expect(!text.Contains("TOP-SECRET", StringComparison.Ordinal), "the prompt's text is never kept");
        using var doc = JsonDocument.Parse(text);
        Expect(doc.RootElement.GetProperty("type").GetString(), "permission_prompt");
        Expect(doc.RootElement.GetProperty("transcript_path").GetString(), transcript);
        Expect(doc.RootElement.GetProperty("at").GetInt64() > 1_700_000_000_000, "the time is in milliseconds");
    });

    private static void GrokInstallShape() => WithGrok((hooks, scripts, signals) =>
    {
        Expect(GrokHooks.SetInstalled(true), null);
        Expect(File.Exists(Path.Combine(scripts, "grok-hook.sh")), "the script is ours, outside ~/.grok");
        Expect(File.ReadAllText(Path.Combine(scripts, "grok-hook.sh")).StartsWith("#!/bin/sh", StringComparison.Ordinal), "it is an sh script");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(hooks, GrokHooks.HookFileName)));
        var command = doc.RootElement.GetProperty("hooks").GetProperty("Notification")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Expect(command.StartsWith("sh ", StringComparison.Ordinal) && command.Contains("grok-hook.sh", StringComparison.Ordinal), "the command runs our script with sh");
        Expect(GrokHooks.IsInstalled(), true);
        Expect(GrokHooks.SetInstalled(false), null);
        Expect(!File.Exists(Path.Combine(scripts, "grok-hook.sh")), "our script is gone");
        Expect(!GrokHooks.IsInstalled(), "uninstalled");
    });

    private static void GrokScriptWritesMarker() => WithGrok((hooks, scripts, signals) =>
    {
        if (!HasPlutil) return;
        Expect(GrokHooks.SetInstalled(true), null);
        var input = new JsonObject
        {
            ["sessionId"] = "grok-1",
            ["notificationType"] = "permission_prompt",
            ["message"] = "TOP-SECRET prompt text",
        }.ToJsonString();
        Run("/bin/sh", new[] { Path.Combine(scripts, "grok-hook.sh") }, input, ("EDGELINGS_GROK_SIGNALS", signals));
        var file = Path.Combine(signals, "grok-1.json");
        Expect(File.Exists(file), "the marker was written");
        var text = File.ReadAllText(file);
        Expect(!text.Contains("TOP-SECRET", StringComparison.Ordinal), "the prompt's text is never kept");
        using var doc = JsonDocument.Parse(text);
        Expect(doc.RootElement.GetProperty("type").GetString(), "permission_prompt");
    });

    private static void WithClaude(Action<string, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "edgelings-shellhook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var before = (ClaudeHooks.UseShell, ClaudeHooks.SettingsFileOverride, ClaudeHooks.SignalDirectory);
        ClaudeHooks.UseShell = true;
        ClaudeHooks.SettingsFileOverride = Path.Combine(root, "settings.json");
        ClaudeHooks.SignalDirectory = Path.Combine(root, "Library", "Application Support", "Edgelings", "claude-hooks");
        try
        {
            test(ClaudeHooks.SettingsFileOverride, ClaudeHooks.SignalDirectory);
        }
        finally
        {
            (ClaudeHooks.UseShell, ClaudeHooks.SettingsFileOverride, ClaudeHooks.SignalDirectory) = before;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WithGrok(Action<string, string, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "edgelings-shellgrok-" + Guid.NewGuid().ToString("N"));
        var (hooks, scripts, signals) = (Path.Combine(root, "hooks"), Path.Combine(root, "scripts"), Path.Combine(root, "signals"));
        Directory.CreateDirectory(signals);
        var before = (GrokHooks.UseShell, GrokHooks.HooksDirectory, GrokHooks.ScriptDirectory, GrokHooks.SignalDirectory);
        GrokHooks.UseShell = true;
        (GrokHooks.HooksDirectory, GrokHooks.ScriptDirectory, GrokHooks.SignalDirectory) = (hooks, scripts, signals);
        try
        {
            test(hooks, scripts, signals);
        }
        finally
        {
            (GrokHooks.UseShell, GrokHooks.HooksDirectory, GrokHooks.ScriptDirectory, GrokHooks.SignalDirectory) = before;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Run(string file, string[] arguments, string input, (string Name, string Value)? environment = null)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is { } variable) info.Environment[variable.Name] = variable.Value;
        using var process = Process.Start(info)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        Expect(process.WaitForExit(15000), "the hook finished");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
    }
}
