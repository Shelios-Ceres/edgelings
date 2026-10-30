using AgentIsland.Model;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Reading Windows' own startup switch (StartupApproved). Pure: the registry
/// is never touched here.
public static class LaunchAtLoginTests
{
    public static void RunAll()
    {
        Expect(LaunchAtLogin.IsApproved(null), "no StartupApproved value means the entry is on");
        Expect(LaunchAtLogin.IsApproved(Array.Empty<byte>()), "an empty value means the entry is on");
        Expect(LaunchAtLogin.IsApproved(State(0x02)), "02 is switched on");
        Expect(LaunchAtLogin.IsApproved(State(0x06)), "06 is switched on");
        Expect(!LaunchAtLogin.IsApproved(State(0x01)), "01 is switched off");
        Expect(!LaunchAtLogin.IsApproved(State(0x03)), "03 is switched off");
        Expect(!LaunchAtLogin.IsApproved(State(0x07)), "07 is switched off");
        Console.WriteLine("PASS launch at login reads Windows' startup switch");
        LegacyEntryMoves();
        Console.WriteLine("LaunchAtLoginTests GREEN");
    }

    /// The Agent Island Run value becomes an Edgelings one, in a throwaway key under HKCU.
    private static void LegacyEntryMoves()
    {
        var root = @"Software\AgentIslandTests\" + Guid.NewGuid().ToString("N");
        var run = root + @"\Run";
        var approved = root + @"\Approved";
        const string exe = @"C:\Edgelings\Edgelings.exe";
        var user = Microsoft.Win32.Registry.CurrentUser;
        try
        {
            // No old value: nothing is written.
            LaunchAtLogin.MoveLegacyEntry(exe, run, approved);
            Expect(user.OpenSubKey(run) is null, "an install with no old entry gets none");

            using (var key = user.CreateSubKey(run)) key.SetValue("AgentIsland", "\"C:\\Old\\AgentIsland.WinUI.exe\"");
            using (var key = user.CreateSubKey(approved)) key.SetValue("AgentIsland", State(0x03), Microsoft.Win32.RegistryValueKind.Binary);
            LaunchAtLogin.MoveLegacyEntry(exe, run, approved);
            using (var key = user.OpenSubKey(run))
            {
                Expect(key!.GetValue("Edgelings") as string, "\"" + exe + "\"");
                Expect(key.GetValue("AgentIsland") is null, "the old value is gone");
            }
            using (var key = user.OpenSubKey(approved))
            {
                Expect(key!.GetValue("Edgelings") is byte[] { Length: > 0 } moved && moved[0] == 0x03, "Windows' own switch moved with it, still off");
                Expect(key.GetValue("AgentIsland") is null, "the old switch is gone");
            }
            // Once: another call changes nothing.
            LaunchAtLogin.MoveLegacyEntry(@"C:\Elsewhere\Edgelings.exe", run, approved);
            using (var key = user.OpenSubKey(run)) Expect(key!.GetValue("Edgelings") as string, "\"" + exe + "\"");
            Console.WriteLine("PASS the Agent Island startup entry becomes Edgelings's, once, keeping Windows' switch");
        }
        finally
        {
            user.DeleteSubKeyTree(@"Software\AgentIslandTests", throwOnMissingSubKey: false);
        }
    }

    /// The first byte is the switch; the rest is when it was flipped.
    private static byte[] State(byte flag) => new byte[] { flag, 0, 0, 0, 0xD5, 0x92, 0xF6, 0xD5, 0x9D, 0x3B, 0xDD, 0x01 };
}
