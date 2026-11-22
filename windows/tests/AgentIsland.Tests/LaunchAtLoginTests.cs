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

    /// An Edgelings or Agent Island Run value becomes a Limen one, in a throwaway key under HKCU.
    private static void LegacyEntryMoves()
    {
        var root = @"Software\AgentIslandTests\" + Guid.NewGuid().ToString("N");
        var run = root + @"\Run";
        var approved = root + @"\Approved";
        const string exe = @"C:\Limen\Limen.exe";
        var user = Microsoft.Win32.Registry.CurrentUser;
        try
        {
            // No old value: nothing is written.
            LaunchAtLogin.MoveLegacyEntry(exe, run, approved);
            Expect(user.OpenSubKey(run) is null, "an install with no old entry gets none");

            // Edgelings -> Limen, keeping Windows' switch.
            using (var key = user.CreateSubKey(run)) key.SetValue("Edgelings", "\"C:\\Old\\Edgelings.exe\"");
            using (var key = user.CreateSubKey(approved)) key.SetValue("Edgelings", State(0x03), Microsoft.Win32.RegistryValueKind.Binary);
            LaunchAtLogin.MoveLegacyEntry(exe, run, approved);
            using (var key = user.OpenSubKey(run))
            {
                Expect(key!.GetValue("Limen") as string, "\"" + exe + "\"");
                Expect(key.GetValue("Edgelings") is null, "the Edgelings value is gone");
            }
            using (var key = user.OpenSubKey(approved))
            {
                Expect(key!.GetValue("Limen") is byte[] { Length: > 0 } moved && moved[0] == 0x03, "Windows' own switch moved with it, still off");
                Expect(key.GetValue("Edgelings") is null, "the Edgelings switch is gone");
            }
            // Once: another call changes nothing.
            LaunchAtLogin.MoveLegacyEntry(@"C:\Elsewhere\Limen.exe", run, approved);
            using (var key = user.OpenSubKey(run)) Expect(key!.GetValue("Limen") as string, "\"" + exe + "\"");

            // Older Agent Island -> Limen works the same way.
            user.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using (var key = user.CreateSubKey(run)) key.SetValue("AgentIsland", "\"C:\\Old\\AgentIsland.WinUI.exe\"");
            LaunchAtLogin.MoveLegacyEntry(exe, run, approved);
            using (var key = user.OpenSubKey(run))
            {
                Expect(key!.GetValue("Limen") as string, "\"" + exe + "\"");
                Expect(key.GetValue("AgentIsland") is null, "the Agent Island value is gone");
            }
            Console.WriteLine("PASS Edgelings and Agent Island startup entries become Limen's, once, keeping Windows' switch");
        }
        finally
        {
            user.DeleteSubKeyTree(@"Software\AgentIslandTests", throwOnMissingSubKey: false);
        }
    }

    /// The first byte is the switch; the rest is when it was flipped.
    private static byte[] State(byte flag) => new byte[] { flag, 0, 0, 0, 0xD5, 0x92, 0xF6, 0xD5, 0x9D, 0x3B, 0xDD, 0x01 };
}
