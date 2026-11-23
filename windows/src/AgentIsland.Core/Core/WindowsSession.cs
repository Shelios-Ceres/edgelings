using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentIsland.Core;

[SupportedOSPlatform("windows")]
internal static class WindowsSession
{
    // WTSINFOEXW level 1 is an 8-byte aligned union (it contains LARGE_INTEGER).
    // Its first three DWORDs are session ID, connection state and lock flags.
    public static bool? IsLocked()
    {
        const int wtsSessionInfoEx = 25, currentSession = -1;
        if (!WTSQuerySessionInformationW(0, currentSession, wtsSessionInfoEx, out var data, out var size))
            return null;
        try
        {
            if (data == 0 || size < 20 || Marshal.ReadInt32(data) != 1) return null;
            return Marshal.ReadInt32(data, 16) switch { 0 => true, 1 => false, _ => null };
        }
        finally { WTSFreeMemory(data); }
    }

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern bool WTSQuerySessionInformationW(nint server, int session, int infoClass, out nint data, out int size);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint data);
}
