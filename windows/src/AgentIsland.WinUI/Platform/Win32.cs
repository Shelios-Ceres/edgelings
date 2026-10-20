using System.Runtime.InteropServices;
using System.Text;

namespace AgentIsland.WinUI.Platform;

internal static class Win32
{
    public const int GwlExStyle = -20;
    public const long WsExTopmost = 0x8;
    public const long WsExToolWindow = 0x80;
    public const long WsExNoActivate = 0x08000000;
    public const uint SwpNoSize = 0x1;
    public const uint SwpNoMove = 0x2;
    public const uint SwpNoActivate = 0x10;
    public const uint MonitorDefaultToNearest = 2;
    public static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    public struct MinMaxInfo
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    public static bool TryGetMonitorAreas(IntPtr monitor, out RECT bounds, out RECT work)
    {
        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        var ok = GetMonitorInfo(monitor, ref info);
        bounds = info.Monitor;
        work = info.Work;
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NcCalcSizeParams
    {
        public RECT Rect0;
        public RECT Rect1;
        public RECT Rect2;
        public IntPtr WindowPos;
    }

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);

    /// DWMWA_EXTENDED_FRAME_BOUNDS: the window's visible bounds, without the
    /// invisible resize border Windows adds.
    public static int DwmGetExtendedFrameBounds(IntPtr hwnd, out RECT bounds) => DwmGetWindowAttribute(hwnd, 9, out bounds, Marshal.SizeOf<RECT>());

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr data);

    public static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(128);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// This process's GDI (0) or USER (1) object count.
    public static uint GuiObjects(uint kind) => GetGuiResources(GetCurrentProcess(), kind);

    private static bool _noArrangedQuery;

    /// Whether Windows' snapping arranged the window. User32 exports
    /// IsWindowArranged only from Windows 10 build 20348 (so on every
    /// Windows 11); on older builds, which the app still runs on, a window
    /// never reads as arranged.
    public static bool IsArranged(IntPtr hwnd)
    {
        if (_noArrangedQuery) return false;
        try
        {
            return IsWindowArranged(hwnd);
        }
        catch (EntryPointNotFoundException)
        {
            _noArrangedQuery = true;
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowArranged(IntPtr hwnd);

    public static bool TryGetWorkArea(IntPtr monitor, out RECT work)
    {
        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        var ok = GetMonitorInfo(monitor, ref info);
        work = info.Work;
        return ok;
    }

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    /// The primary display's DPI, where the notification area lives.
    public static uint PrimaryDpi() =>
        GetDpiForMonitor(MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */), 0, out var dpi, out _) == 0 ? dpi : 96;

    public const int SmCxIcon = 11;
    public const int SmCxSmIcon = 49;
    public const uint WmSetIcon = 0x80;
    public const int IconSmall = 0;
    public const int IconBig = 1;

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    /// One entry of an .ico file at exactly this size (IMAGE_ICON,
    /// LR_LOADFROMFILE); zero if the file can't be read.
    public static IntPtr LoadIconFile(string path, int size) => LoadImage(IntPtr.Zero, path, 1, size, size, 0x10);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    /// The size the taskbar draws button icons at, in DIP: 24, or 16 with
    /// Windows 11's smaller taskbar buttons, whose taskbar is 32 DIP thick
    /// instead of 48.
    public static int TaskbarIconDip()
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out var rect)) return 24;
        var dpi = GetDpiForWindow(bar);
        var thickness = Math.Min(rect.Right - rect.Left, rect.Bottom - rect.Top) * 96.0 / (dpi == 0 ? 96 : dpi);
        return thickness < 40 ? 16 : 24;
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    /// The system owns the region once it is set; do not delete it after.
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmBlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;
        public IntPtr BlurRegion;
        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blur);

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    public delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    public static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, IntPtr id);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint color);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// Running on battery right now (unknown counts as plugged in).
    public static bool OnBattery() => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    public sealed record Screen(string Device, RECT Bounds, RECT Work, double Scale, bool Primary);

    /// Every monitor with its work area (physical pixels) and scale factor.
    public static List<Screen> Screens()
    {
        var screens = new List<Screen>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
                screens.Add(new Screen(info.Device, info.Monitor, info.Work, scale, (info.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return screens;
    }

    /// A foreground window covering its whole monitor (games, video,
    /// presentations) — the shell's own desktop and taskbar excluded.
    public static bool IsFullscreen(IntPtr foreground, IntPtr monitor)
    {
        if (foreground == IntPtr.Zero) return false;
        var name = new StringBuilder(64);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (MonitorFromWindow(foreground, MonitorDefaultToNearest) != monitor) return false;
        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(foreground, out var rect)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    private static IntPtr _systemQueue;

    /// Windows.UI.Composition needs a Windows.System dispatcher queue on the
    /// thread; WinUI threads only carry the Microsoft.UI one.
    public static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || _systemQueue != IntPtr.Zero) return;
        var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        CreateDispatcherQueueController(options, out _systemQueue);
    }
}

/// A fully transparent system backdrop: the window shows only what its
/// content draws. Three parts: a clear composition brush behind the XAML
/// content; blur-behind with an off-screen region, so DWM composes the
/// window with per-pixel alpha (and no blur); and the redirection surface
/// erased to black, which is fully transparent under that alpha — the
/// default white erase would add white over the desktop.
internal sealed class TransparentBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
{
    private const uint WmEraseBackground = 0x14;
    private const uint WmDwmCompositionChanged = 0x31E;

    private Windows.UI.Composition.Compositor? _compositor;
    private Win32.SubclassProc? _subclass;
    private IntPtr _hwnd;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, Microsoft.UI.Xaml.XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        Win32.EnsureSystemDispatcherQueue();
        _compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        ConfigureDwm(_hwnd);
        _subclass ??= Subclass;
        Win32.SetWindowSubclass(_hwnd, _subclass, 1, IntPtr.Zero);
        var dc = Win32.GetDC(_hwnd);
        Clear(_hwnd, dc);
        Win32.ReleaseDC(_hwnd, dc);
    }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
        if (_subclass is not null && _hwnd != IntPtr.Zero) Win32.RemoveWindowSubclass(_hwnd, _subclass, 1);
    }

    /// WinUI calls this on a window's backdrop with a null target, once at
    /// startup and again on every light/dark switch, and the base rejects
    /// that with an ArgumentException. A clear backdrop has no material for
    /// the default configuration to steer, so the base is never needed.
    protected override void OnDefaultSystemBackdropConfigurationChanged(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
    }

    private IntPtr Subclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (message == WmEraseBackground)
        {
            Clear(hwnd, wParam);
            return 1;
        }
        if (message == WmDwmCompositionChanged) ConfigureDwm(hwnd);
        return Win32.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static void ConfigureDwm(IntPtr hwnd)
    {
        var margins = new Win32.Margins();
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var region = Win32.CreateRectRgn(-2, -2, -1, -1);
        var blur = new Win32.DwmBlurBehind { Flags = 0x1 | 0x2, Enable = true, BlurRegion = region };
        Win32.DwmEnableBlurBehindWindow(hwnd, ref blur);
        Win32.DeleteObject(region);
    }

    private static void Clear(IntPtr hwnd, IntPtr dc)
    {
        Win32.GetClientRect(hwnd, out var rect);
        var black = Win32.CreateSolidBrush(0);
        Win32.FillRect(dc, ref rect, black);
        Win32.DeleteObject(black);
    }
}
