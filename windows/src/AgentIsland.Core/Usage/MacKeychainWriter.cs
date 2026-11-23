using System.Runtime.InteropServices;
using System.Text;

namespace AgentIsland.Usage;

/// Updates an existing generic-password item without putting its data in argv.
/// The optional search list confines the integration test to its disposable keychain.
internal static class MacKeychainWriter
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal static bool Update(string service, string account, string json, string? keychain)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        var owned = new Stack<nint>();
        nint Own(nint value)
        {
            if (value == 0) throw new OutOfMemoryException();
            owned.Push(value);
            return value;
        }
        nint Text(string value) => Own(CFStringCreateWithCString(0, value, 0x08000100));
        try
        {
            var keyCallbacks = CoreFoundationSymbol("kCFTypeDictionaryKeyCallBacks");
            var valueCallbacks = CoreFoundationSymbol("kCFTypeDictionaryValueCallBacks");
            var query = Own(CFDictionaryCreateMutable(0, 0, keyCallbacks, valueCallbacks));
            CFDictionarySetValue(query, Constant("kSecClass"), Constant("kSecClassGenericPassword"));
            CFDictionarySetValue(query, Constant("kSecAttrService"), Text(service));
            CFDictionarySetValue(query, Constant("kSecAttrAccount"), Text(account));
            CFDictionarySetValue(query, Constant("kSecUseAuthenticationUI"), Constant("kSecUseAuthenticationUIFail"));
            if (keychain is not null)
            {
                if (SecKeychainOpen(keychain, out var file) != 0) return false;
                Own(file);
                var files = Own(CFArrayCreate(0, ref file, 1, CoreFoundationSymbol("kCFTypeArrayCallBacks")));
                CFDictionarySetValue(query, Constant("kSecMatchSearchList"), files);
            }
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                var data = Own(CFDataCreate(0, bytes, bytes.Length));
                var update = Own(CFDictionaryCreateMutable(0, 0, keyCallbacks, valueCallbacks));
                CFDictionarySetValue(update, Constant("kSecValueData"), data);
                return SecItemUpdate(query, update) == 0;
            }
            finally { Array.Clear(bytes); }
        }
        finally
        {
            while (owned.TryPop(out var item)) CFRelease(item);
        }
    }

    private static readonly Lazy<nint> Library = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<nint> CoreFoundationLibrary = new(() => NativeLibrary.Load(CoreFoundation));
    private static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Library.Value, name));
    private static nint CoreFoundationSymbol(string name) => NativeLibrary.GetExport(CoreFoundationLibrary.Value, name);

    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
    [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFArrayCreate(nint allocator, ref nint values, nint count, nint callbacks);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint item);
    [DllImport(Security)] private static extern int SecKeychainOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint keychain);
    [DllImport(Security)] private static extern int SecItemUpdate(nint query, nint attributes);
}
