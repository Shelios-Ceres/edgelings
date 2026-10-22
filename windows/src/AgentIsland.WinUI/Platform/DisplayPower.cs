using System.Runtime.InteropServices;

namespace AgentIsland.WinUI.Platform;

/// Whether the displays are on, as Windows reports them: off once the power
/// plan blanks them, on again at the first input. Windows calls back on a
/// thread of its own, first with the current state when this registers.
internal sealed class DisplayPower : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PowerSettingChange = 0x8013;
    private static Guid _consoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    // Held so the collector never frees the delegate Windows calls.
    private readonly Callback _callback;
    private IntPtr _registration;
    private volatile bool _on = true;

    public DisplayPower()
    {
        _callback = OnChange;
        var recipient = new SubscribeParameters { Callback = _callback };
        if (PowerSettingRegisterNotification(ref _consoleDisplayState, DeviceNotifyCallback, ref recipient, out _registration) != 0)
        {
            _registration = IntPtr.Zero;
        }
    }

    /// Off only once Windows said so; dimmed still counts as on.
    public bool On => _on;

    private uint OnChange(IntPtr context, uint type, IntPtr setting)
    {
        // POWERBROADCAST_SETTING: the setting's GUID, the data's length, then
        // the state itself: 0 off, 1 on, 2 dimmed.
        if (type == PowerSettingChange && setting != IntPtr.Zero) _on = Marshal.ReadInt32(setting, 20) != 0;
        return 0;
    }

    public void Dispose()
    {
        if (_registration == IntPtr.Zero) return;
        PowerSettingUnregisterNotification(_registration);
        _registration = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public Callback Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref SubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registration);
}
