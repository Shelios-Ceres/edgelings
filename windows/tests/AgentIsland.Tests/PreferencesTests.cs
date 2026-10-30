using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Preferences against a temporary settings file; the real one is never touched.
public static class PreferencesTests
{
    private static void RoundTrips()
    {
        void Check<T>(T value, Func<T?, bool> same, string name)
        {
            Preferences.Set("AgentIsland.test.roundtrip", value);
            Preferences.Forget();
            Expect(same(Preferences.Get<T>("AgentIsland.test.roundtrip")), "round trip of " + name);
        }
        var when = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        Check("text", v => v == "text", "string");
        Check(true, v => v, "bool");
        Check<bool?>(false, v => v == false, "bool?");
        Check(42, v => v == 42, "int");
        Check<int?>(7, v => v == 7, "int?");
        Check(0.25, v => v == 0.25, "double");
        Check<double?>(0.5, v => v == 0.5, "double?");
        Check(new List<string> { "a", "b" }, v => v is { Count: 2 } && v[1] == "b", "List<string>");
        Check(new Dictionary<string, TokenTotals> { ["claude"] = new(when, 1, 2, 3, 4) },
            v => v is { Count: 1 } && v["claude"].Month == 3, "Dictionary<string, TokenTotals>");
        Check(new UsageCacheSnapshot(), v => v is not null, "UsageCacheSnapshot");
        Check(new CursorUsageStore.Cached(), v => v is not null, "CursorUsageStore.Cached");
        Check(new AntigravityUsageStore.CachedSnapshot(null, when, "me@example.com"), v => v?.Email == "me@example.com" && v.UpdatedAt == when, "AntigravityUsageStore.CachedSnapshot");
        Check(new GrokCachedSnapshot { UpdatedAt = when }, v => v?.UpdatedAt == when, "GrokCachedSnapshot");
        Check(new KimiCachedSnapshot { UpdatedAt = when }, v => v?.UpdatedAt == when, "KimiCachedSnapshot");
        Preferences.Remove("AgentIsland.test.roundtrip");
    }

    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentisland-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logged = new List<(string Context, string Message)>();
        var previousSink = CoreLog.Sink;
        var previousFile = Preferences.FileOverride;
        try
        {
            CoreLog.Sink = (error, context) => logged.Add((context, error.Message));
            CoreLog.Reset();

            Preferences.FileOverride = Path.Combine(root, "good", "settings.json");
            Preferences.Forget();
            Preferences.Set("AgentIsland.test.key", 7);
            Expect(Preferences.Get<int>("AgentIsland.test.key") == 7, "a saved value reads back");
            Expect(File.Exists(Preferences.FileOverride) && Directory.GetFiles(Path.GetDirectoryName(Preferences.FileOverride)!).Length == 1,
                "the settings file is written and no temp copy is left");
            Expect(logged.Count == 0, "a good save logs nothing");
            Console.WriteLine("PASS preferences: a save writes the file and logs nothing");

            // Every stored type goes through the source-generated context (AOT-safe), so each must be listed.
            RoundTrips();
            Console.WriteLine("PASS preferences: every stored type round-trips through the generated JSON");

            // A directory where the file belongs makes the final move fail.
            var blocked = Path.Combine(root, "blocked", "settings.json");
            Directory.CreateDirectory(blocked);
            Preferences.FileOverride = blocked;
            Preferences.Forget();
            Preferences.Set("AgentIsland.test.key", 1);
            Preferences.Set("AgentIsland.test.key", 2);
            Expect(logged.Count == 1 && logged[0].Context == "Settings save",
                $"a save that fails is logged once, not once per attempt: {logged.Count}");
            Expect(Preferences.Get<int>("AgentIsland.test.key") == 2, "the value stays in memory for this run");
            Expect(Directory.GetFileSystemEntries(Path.GetDirectoryName(blocked)!).Length == 1, "the failed save leaves no temp copy");
            Console.WriteLine("PASS preferences: a save that fails is logged once and leaves nothing behind");

            // A crash mid-save can leave the file zeroed: its bytes are kept aside, not overwritten.
            var corrupt = Path.Combine(root, "corrupt", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
            File.WriteAllBytes(corrupt, new byte[] { 0, 0, 0, 0 });
            Preferences.FileOverride = corrupt;
            Preferences.Forget();
            logged.Clear();
            Expect(Preferences.Get<int>("AgentIsland.test.key") == 0, "an unreadable file reads as defaults");
            var aside = Directory.GetFiles(Path.GetDirectoryName(corrupt)!, "settings.json.corrupt-*");
            Expect(aside.Length == 1 && File.ReadAllBytes(aside[0]).SequenceEqual(new byte[] { 0, 0, 0, 0 }), "the damaged file is kept aside as it was");
            Expect(logged.Count == 1 && logged[0].Context.StartsWith("Settings unreadable", StringComparison.Ordinal), "and logged");
            Preferences.Set("AgentIsland.test.key", 3);
            Expect(Preferences.Get<int>("AgentIsland.test.key") == 3 && File.Exists(aside[0]), "the next save writes a fresh file beside it");
            Console.WriteLine("PASS preferences: a corrupt file is moved aside and logged, not overwritten");
            Console.WriteLine("PreferencesTests GREEN");
        }
        finally
        {
            Preferences.FileOverride = previousFile;
            Preferences.Forget();
            CoreLog.Sink = previousSink;
            CoreLog.Reset();
            Directory.Delete(root, recursive: true);
        }
    }
}
