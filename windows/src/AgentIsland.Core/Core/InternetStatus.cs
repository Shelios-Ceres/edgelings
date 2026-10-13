using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AgentIsland.Core;

/// Windows' own verdict on internet access: the network list manager behind
/// the taskbar's "No internet" globe. A drop counts only once it has lasted
/// OfflineAfter, so a blip never flags anyone, and a check that fails assumes
/// the computer is online.
internal static class InternetStatus
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(15);
    private static readonly Type? ManagerType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
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
            if (ManagerType is null) return true;
            var manager = Activator.CreateInstance(ManagerType);
            try
            {
                return ManagerType.InvokeMember("IsConnectedToInternet", BindingFlags.GetProperty, null, manager, null) is not false;
            }
            finally
            {
                if (manager is not null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager);
            }
        }
        catch
        {
            return true;
        }
    }
}
