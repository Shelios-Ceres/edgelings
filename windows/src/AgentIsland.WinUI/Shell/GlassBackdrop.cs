using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Foundation;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// Frosted glass for the hover card: Windows' desktop acrylic in its thin,
/// more see-through kind, tinted lightly and kept live even though the
/// card's window never takes activation (acrylic otherwise falls back to a
/// flat colour on inactive windows).
internal sealed class GlassBackdrop : SystemBackdrop, IDisposable
{
    private const string ExternalBackdropLink = "Microsoft.UI.Content.ContentExternalBackdropLink";
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private ICompositionSupportsSystemBackdrop? _retainedTarget;
    private IDisposable? _retainedClosable;
    private IntPtr _retainedIdentity;
    private bool _dark;
    private bool _disposed;

    public GlassBackdrop(bool dark) => _dark = dark;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        base.OnTargetConnected(target, root);
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
        KeepTarget(target);
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
        };
        _controller.AddSystemBackdropTarget(target);
        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        _controller.SetSystemBackdropConfiguration(_configuration);
        ApplyTheme();
    }

    public void UpdateTheme(bool dark)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dark == dark) return;
        _dark = dark;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (_configuration is not null) _configuration.Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (_controller is null) return;
        _controller.TintColor = _dark ? Color.FromArgb(255, 0x20, 0x20, 0x22) : Color.FromArgb(255, 0xFB, 0xFB, 0xFD);
        _controller.TintOpacity = _dark ? 0.28f : 0.15f;
        _controller.LuminosityOpacity = _dark ? 0.58f : 0.5f;
        _controller.FallbackColor = _dark ? Color.FromArgb(0xF0, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xF0, 0xF7, 0xF7, 0xF9);
    }

    /// A glass pane's edge is the backdrop's own: crisp only on whole
    /// physical pixels, since an offset between pixels (at 150% scale) smears
    /// it across two. An outline is never drawn: the backdrop stops a pixel
    /// short of one, so it floated around the pane as a separate frame.
    public static double Snap(double dip, double scale) => Math.Round(dip * scale) / scale;

    /// A character's colour washed through the frost, strongest in the top
    /// corner and fading out, so the pane reads as theirs while the glass
    /// stays see-through. Laid over the backdrop rather than tinting it: the
    /// acrylic's own tint costs transparency.
    public static Brush Wash(uint rgb, bool dark)
    {
        Color Tone(byte alpha) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Tone((byte)(dark ? 0x40 : 0x38)), Offset = 0 },
                new GradientStop { Color = Tone((byte)(dark ? 0x14 : 0x0E)), Offset = 1 },
            },
        };
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        VerifyAccess();
        Trace("disconnected", TargetName(target), Identity(target));
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
        base.OnTargetDisconnected(target);
    }

    private void KeepTarget(ICompositionSupportsSystemBackdrop target)
    {
        var name = TargetName(target);
        var identity = Identity(target);
        Trace("connected", name, identity);
        if (name != ExternalBackdropLink || identity == IntPtr.Zero) return;
        if (_retainedTarget is not null && identity == _retainedIdentity) return;
        IDisposable closable;
        try { closable = target.As<IDisposable>(); }
        catch (Exception error) when (error is InvalidCastException or COMException)
        {
            DebugTrace.Write($"Backdrop lease unavailable type={name} hresult=0x{error.HResult:X8} thread={Environment.CurrentManagedThreadId}");
            return;
        }
        // A disconnected link may be reused by this same element. Only a
        // different replacement, or the element's disposal, retires the lease.
        RetireTarget();
        _retainedTarget = target;
        _retainedClosable = closable;
        _retainedIdentity = identity;
    }

    private static string TargetName(ICompositionSupportsSystemBackdrop target) =>
        target is IInspectable inspectable
            ? inspectable.GetRuntimeClassName(noThrow: true) ?? target.GetType().FullName ?? "unknown"
            : target.GetType().FullName ?? "unknown";

    private static IntPtr Identity(ICompositionSupportsSystemBackdrop target)
    {
        if (!ComWrappersSupport.TryUnwrapObject(target, out var reference)) return IntPtr.Zero;
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        if (Marshal.QueryInterface(reference.ThisPtr, in unknown, out var identity) < 0) return IntPtr.Zero;
        // This is our temporary QI reference, not the target projection's ref.
        Marshal.Release(identity);
        GC.KeepAlive(target);
        return identity;
    }

    private void RetireTarget()
    {
        if (_retainedTarget is not { } target || _retainedClosable is not { } closable) return;
        Trace("retire", ExternalBackdropLink, _retainedIdentity);
        closable.Dispose();
        GC.KeepAlive(target);
        _retainedTarget = null;
        _retainedClosable = null;
        _retainedIdentity = IntPtr.Zero;
    }

    private void Trace(string action, string name, IntPtr identity) =>
        DebugTrace.Write($"Backdrop {action} type={name} identity=0x{identity.ToInt64():X} thread={Environment.CurrentManagedThreadId} retained={(_retainedTarget is null ? 0 : 1)}");

    private void VerifyAccess()
    {
        if (!_queue.HasThreadAccess) throw new InvalidOperationException("Glass backdrop lifetime must be managed on its UI thread.");
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
        RetireTarget();
        _disposed = true;
    }
}
