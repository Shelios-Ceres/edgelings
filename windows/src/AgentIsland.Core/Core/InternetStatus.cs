using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentIsland.Core;

/// The computer's own verdict on internet access. On Windows that is the
/// network list manager behind the taskbar's "No internet" globe; elsewhere
/// the network interfaces' state. A drop counts only once it has lasted
/// OfflineAfter, so a blip never flags anyone, and a check that fails assumes
/// the computer is online.
internal static class InternetStatus
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(15);
    private static readonly object Gate = new();
    private static DateTimeOffset? _offlineSince;

    public static bool IsOffline(DateTimeOffset now)
    {
        var online = IsConnected();
        lock (Gate)
        {
            if (online)
            {
                _offlineSince = null;
                return false;
            }
            _offlineSince ??= now;
            return now - _offlineSince.Value >= OfflineAfter;
        }
    }

    private static bool IsConnected()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            return !OperatingSystem.IsWindows() || WindowsManagerConnected();
        }
        catch
        {
            return true;
        }
    }

    // The network list manager is a COM class reached by reflection. That is
    // neither trim- nor AOT-safe, but this runs only on Windows, where the app
    // is neither trimmed nor compiled ahead of time; the macOS bridge never
    // reaches it (the OperatingSystem check above).
    [SupportedOSPlatform("windows")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Windows only; the Windows app is not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2077", Justification = "Windows only; the Windows app is not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Windows only; the Windows app is not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "Windows only; the Windows app is not trimmed.")]
    private static bool WindowsManagerConnected()
    {
        var type = ManagerType.Value;
        if (type is null) return true;
        var manager = Activator.CreateInstance(type);
        try
        {
            return type.InvokeMember("IsConnectedToInternet", BindingFlags.GetProperty, null, manager, null) is not false;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager);
        }
    }

    [SupportedOSPlatform("windows")]
    private static readonly Lazy<Type?> ManagerType = new(() => Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
}
