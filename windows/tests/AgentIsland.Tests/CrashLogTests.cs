using AgentIsland.WinUI;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The app's crash log (WinUI Diagnostics.cs) against a temporary folder; the
/// real log in %APPDATA% is never touched. A drawer poll that throws every
/// half second once grew it by hundreds of MB a day.
public static class CrashLogTests
{
    public static void RunAll()
    {
        CrashLog.Reset();
        try
        {
            Expect(CrashLog.FirstTime(Thrown("bad state"), "Drawer poll"), "a failure is written the first time");
            Expect(!CrashLog.FirstTime(Thrown("bad state"), "Drawer poll"), "the same failure again is not");
            Expect(CrashLog.FirstTime(Thrown("bad state"), "XAML"), "the same failure from another source is");
            Expect(CrashLog.FirstTime(Thrown("other state"), "Drawer poll"), "another message is");
            Expect(CrashLog.FirstTime(Elsewhere("bad state"), "Drawer poll"), "the same message from another place is");
            Console.WriteLine("PASS crash log: a repeating failure is written once per launch");
        }
        finally
        {
            CrashLog.Reset();
        }

        var root = Path.Combine(Path.GetTempPath(), "agentisland-crashlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = Path.Combine(root, "crash.log");
            var old = Path.Combine(root, "crash.old.log");
            CrashLog.Append(log, "first\n");
            CrashLog.Append(log, "second\n");
            Expect(File.ReadAllText(log) == "first\nsecond\n" && !File.Exists(old), "a small log is appended to");
            File.WriteAllText(log, new string('x', (int)CrashLog.MostBytes));
            CrashLog.Append(log, "after\n");
            Expect(File.ReadAllText(log) == "after\n", "a full log starts again");
            Expect(new FileInfo(old).Length == CrashLog.MostBytes, "the full log moves to crash.old.log");
            File.WriteAllText(log, new string('y', (int)CrashLog.MostBytes));
            CrashLog.Append(log, "again\n");
            Expect(File.ReadAllText(old).StartsWith('y') && File.ReadAllText(log) == "again\n", "the next full log replaces the old one");
            Console.WriteLine("PASS crash log: past 1 MB the log moves to crash.old.log and starts again");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("CrashLogTests GREEN");
    }

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static Exception Elsewhere(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
