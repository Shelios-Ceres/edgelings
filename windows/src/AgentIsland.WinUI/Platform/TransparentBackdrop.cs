
namespace AgentIsland.WinUI.Platform;

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
