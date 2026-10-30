using System.Reflection;
using System.Runtime.InteropServices;

namespace AgentIsland.Bridge;

/// Finds the bundled SQLite. Core is a library inside someone else's process
/// (the Mac app), so dyld's own search never looks in the app's Frameworks
/// folder: the host says where the native libraries are, and SQLite's
/// provider is pointed there.
internal static class NativeResolver
{
    private static string? _folder;
    private static bool _registered;

    public static void Use(string folder)
    {
        _folder = folder;
        if (_registered) return;
        _registered = true;
        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "e_sqlite3" || _folder is null) return IntPtr.Zero;
        var file = Path.Combine(_folder, "libe_sqlite3.dylib");
        return File.Exists(file) && NativeLibrary.TryLoad(file, out var handle) ? handle : IntPtr.Zero;
    }
}
