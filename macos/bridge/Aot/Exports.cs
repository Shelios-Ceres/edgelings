using System.Runtime.InteropServices;
using AgentIsland.Bridge;

namespace AgentIsland.Bridge.Aot;

/// The C surface Swift links: ai_call takes a UTF-8 JSON request and returns a
/// UTF-8 JSON reply that the caller releases with ai_free.
public static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "ai_call")]
    public static byte* Call(byte* request)
    {
        var json = Marshal.PtrToStringUTF8((IntPtr)request) ?? "{}";
        return (byte*)Marshal.StringToCoTaskMemUTF8(BridgeApi.Handle(json));
    }

    [UnmanagedCallersOnly(EntryPoint = "ai_free")]
    public static void Free(byte* pointer) => Marshal.FreeCoTaskMem((IntPtr)pointer);
}
