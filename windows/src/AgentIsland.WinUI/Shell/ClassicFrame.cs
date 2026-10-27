using System.Runtime.InteropServices;
using AgentIsland.WinUI.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

/// Classic's own window frame. Windows only rounds a window's corners by
/// preset (8 DIP, 4 DIP or none), so under Classic a window draws its own
/// body, outline, shadow and caption buttons: the window is borderless and
/// transparent, Windows' frame drawing is off, and a margin around the body
/// holds the shadow. The margin is also the resize band, since a transparent
/// margin that passes clicks on to the window behind cannot be had without a
/// window region, which would clip the shadow (the fallback of D3 §5).
///
/// Moving, resizing, maximizing and the snap-layouts flyout go through
/// InputNonClientPointerSource regions, which make the content island answer
/// WM_NCHITTEST with the system's own codes.
internal sealed partial class ClassicFrame : IDisposable
{
    /// The body's corner radius, in DIP.
    public const double Radius = 16;
    public const double TitleHeight = 32;
    public const double ButtonWidth = 46;
    /// The transparent margin around a restored body, in DIP: left, top, right, bottom.
    public static readonly Thickness Room = new(12, 8, 12, 16);

    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000;
    private const long WsMaximizeBox = 0x00010000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsSysMenu = 0x00080000;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpFrameChanged = 0x20;
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int NcRenderingDisabled = 1;
    private const int NcRenderingUseStyle = 0;
    private const uint WmSize = 0x5;
    private const uint WmGetMinMaxInfo = 0x24;
    private const uint WmNcCalcSize = 0x83;
    private const uint WmNcActivate = 0x86;
    private const uint WmWindowPosChanged = 0x47;

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly Win32.SubclassProc _proc;
    private readonly InputNonClientPointerSource _nonClient;

    private readonly bool _visualOnly;
    private bool _square;
    private bool _maximized;
    private bool _active = true;
    private bool _disposed;

    /// Raised when the frame goes square (maximized or snapped) or round again.
    public event Action? ShapeChanged;

    /// visualOnly draws the frame's parts inside an ordinary window (the
    /// verification films show them over a known background); it takes none
    /// of the window's frame, regions or messages.
    public ClassicFrame(Window window, bool visualOnly = false)
    {
        _window = window;
        _visualOnly = visualOnly;
        _hwnd = WindowNative.GetWindowHandle(window);
        _nonClient =InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        _proc = Subclass;
        if (visualOnly) return;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = true;
        window.AppWindow.SetPresenter(presenter);
        // Sizing, minimizing and maximizing need the styles, though nothing of
        // the frame they would draw is left.
        var style = (long)Win32.GetWindowLongPtr(_hwnd, GwlStyle);
        Win32.SetWindowLongPtr(_hwnd, GwlStyle, (IntPtr)(style | WsThickFrame | WsMaximizeBox | WsMinimizeBox | WsSysMenu));
        var off = NcRenderingDisabled;
        Win32.DwmSetWindowAttribute(_hwnd, DwmwaNcRenderingPolicy, ref off, sizeof(int));
        var square = 1;
        Win32.DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref square, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE);
        Win32.DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));
        window.SystemBackdrop = new TransparentBackdrop();
        Win32.SetWindowSubclass(_hwnd, _proc, 2, IntPtr.Zero);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Win32.SwpNoMove | Win32.SwpNoSize | SwpNoZOrder | Win32.SwpNoActivate | SwpFrameChanged);
        window.Activated += OnActivated;
        _square = IsSquare();
    }

    /// The body is square (no margin, no shadow, no outline rounding) while
    /// the window is maximized or arranged by Windows' snapping.
    public bool Square => _square;

    public bool Active => _active;

    /// Whether the window is maximized (square and with no outline), as
    /// against snapped (square, outlined).
    private bool Maximized => _visualOnly ? _maximized : Win32.IsZoomed(_hwnd);

    /// For the verification films: the frame in a state it is not in.
    public void ShowAs(bool square, bool maximized, bool active)
    {
        (_square, _maximized, _active) = (square, maximized, active);
        Reshape();
    }

    /// Physical pixels per DIP.
    public double Scale => Win32.GetDpiForWindow(_hwnd) / 96.0;

    /// The transparent margin now, in DIP: none while square.
    public Thickness Margin => _square ? new Thickness(0) : Room;

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        _active = e.WindowActivationState != WindowActivationState.Deactivated;
        ShapeChanged?.Invoke();
    }

    private bool IsSquare() => Win32.IsZoomed(_hwnd) || Win32.IsArranged(_hwnd);

    private IntPtr Subclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        switch (message)
        {
            case WmNcCalcSize:
                // The client is the whole window; maximized, it is the work
                // area, not the window's rectangle, which Windows pushes out by
                // the frame it no longer draws.
                if (wParam != IntPtr.Zero && Win32.IsZoomed(hwnd))
                {
                    var calc = Marshal.PtrToStructure<Win32.NcCalcSizeParams>(lParam);
                    var monitor = Win32.MonitorFromWindow(hwnd, Win32.MonitorDefaultToNearest);
                    if (Win32.TryGetWorkArea(monitor, out var work))
                    {
                        calc.Rect0 = work;
                        Marshal.StructureToPtr(calc, lParam, false);
                    }
                }
                return IntPtr.Zero;
            case WmGetMinMaxInfo:
                // Maximized, the window is exactly the work area. With a thick
                // frame Windows would push its rectangle out past the work area
                // (over a taskbar, which may be transparent), and the part
                // beyond the client would show.
                var result = Win32.DefSubclassProc(hwnd, message, wParam, lParam);
                var info = Marshal.PtrToStructure<Win32.MinMaxInfo>(lParam);
                if (Win32.TryGetMonitorAreas(Win32.MonitorFromWindow(hwnd, Win32.MonitorDefaultToNearest), out var bounds, out var area))
                {
                    info.MaxPosition = new Win32.POINT { X = area.Left - bounds.Left, Y = area.Top - bounds.Top };
                    info.MaxSize = new Win32.POINT { X = area.Right - area.Left, Y = area.Bottom - area.Top };
                    Marshal.StructureToPtr(info, lParam, false);
                }
                return result;
            case WmNcActivate:
                // No frame to repaint: lParam -1 keeps Windows from drawing one.
                return Win32.DefSubclassProc(hwnd, message, wParam, new IntPtr(-1));
            case WmSize:
            case WmWindowPosChanged:
                var square = IsSquare();
                if (square != _square)
                {
                    _square = square;
                    ShapeChanged?.Invoke();
                }
                break;
        }
        return Win32.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// Where the frame's non-client regions are, for a body of the window's
    /// client size, in physical pixels from the client's top-left: the
    /// caption, the three buttons and (restored) the resize band around the
    /// body.
    public void SetRegions()
    {
        if (_disposed || _visualOnly) return;
        var scale = Scale;
        var size = _window.AppWindow.ClientSize;
        int W = size.Width, H = size.Height;
        var margin = Margin;
        int ml = (int)Math.Round(margin.Left * scale), mt = (int)Math.Round(margin.Top * scale);
        int mr = (int)Math.Round(margin.Right * scale), mb = (int)Math.Round(margin.Bottom * scale);
        int bar = (int)Math.Round(TitleHeight * scale), cell = (int)Math.Round(ButtonWidth * scale);
        int right = W - mr, top = mt;

        RectInt32 Rect(int x, int y, int w, int h) => new(x, y, Math.Max(0, w), Math.Max(0, h));
        _nonClient.SetRegionRects(NonClientRegionKind.Close, new[] { Rect(right - cell, top, cell, bar) });
        _nonClient.SetRegionRects(NonClientRegionKind.Maximize, new[] { Rect(right - 2 * cell, top, cell, bar) });
        _nonClient.SetRegionRects(NonClientRegionKind.Minimize, new[] { Rect(right - 3 * cell, top, cell, bar) });
        _nonClient.SetRegionRects(NonClientRegionKind.Caption, new[] { Rect(ml, top, right - 3 * cell - ml, bar) });

        if (_square)
        {
            foreach (var kind in Borders) _nonClient.SetRegionRects(kind, Array.Empty<RectInt32>());
            return;
        }
        // Full-length bands that overlap in the corners: there is no region kind
        // of their own for a corner, so whether Windows reads the overlap as a
        // diagonal is what the spike finds out.
        _nonClient.SetRegionRects(NonClientRegionKind.LeftBorder, new[] { Rect(0, 0, ml, H) });
        _nonClient.SetRegionRects(NonClientRegionKind.RightBorder, new[] { Rect(W - mr, 0, mr, H) });
        _nonClient.SetRegionRects(NonClientRegionKind.TopBorder, new[] { Rect(0, 0, W, mt) });
        _nonClient.SetRegionRects(NonClientRegionKind.BottomBorder, new[] { Rect(0, H - mb, W, mb) });
    }

    private static readonly NonClientRegionKind[] Borders =
    {
        NonClientRegionKind.LeftBorder, NonClientRegionKind.RightBorder, NonClientRegionKind.TopBorder, NonClientRegionKind.BottomBorder,
    };

    public InputNonClientPointerSource NonClient => _nonClient;

    /// Gives the window back to Windows' own frame: the presenter's border
    /// and title bar, its frame drawing, corners and edge.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_wired) UnwireButtons();
        if (_visualOnly) return;
        _window.Activated -= OnActivated;
        Win32.RemoveWindowSubclass(_hwnd, _proc, 2);
        foreach (var kind in Enum.GetValues<NonClientRegionKind>())
        {
            if (kind != NonClientRegionKind.Passthrough) _nonClient.ClearRegionRects(kind);
        }
        var policy = NcRenderingUseStyle;
        Win32.DwmSetWindowAttribute(_hwnd, DwmwaNcRenderingPolicy, ref policy, sizeof(int));
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(true, true);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Win32.SwpNoMove | Win32.SwpNoSize | SwpNoZOrder | Win32.SwpNoActivate | SwpFrameChanged);
    }
}
