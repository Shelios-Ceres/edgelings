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
        Console.WriteLine("LaunchAtLoginTests GREEN");
    }

    /// The first byte is the switch; the rest is when it was flipped.
    private static byte[] State(byte flag) => new byte[] { flag, 0, 0, 0, 0xD5, 0x92, 0xF6, 0xD5, 0x9D, 0x3B, 0xDD, 0x01 };
}
