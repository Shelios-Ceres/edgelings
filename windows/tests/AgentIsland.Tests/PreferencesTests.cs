using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Preferences against a temporary settings file; the real one is never touched.
public static class PreferencesTests
{
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

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
