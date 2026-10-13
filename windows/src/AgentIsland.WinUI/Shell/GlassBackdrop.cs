using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// Frosted glass for the hover card: Windows' desktop acrylic in its thin,
/// more see-through kind, tinted lightly and kept live even though the
/// card's window never takes activation (acrylic otherwise falls back to a
/// flat colour on inactive windows).
internal sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private readonly bool _dark;

    public GlassBackdrop(bool dark) => _dark = dark;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = _dark ? Color.FromArgb(255, 0x20, 0x20, 0x22) : Color.FromArgb(255, 0xFB, 0xFB, 0xFD),
            // Kept see-through so the wallpaper's colours read through the
            // frost; a denser pane looked like flat plastic, not glass.
            TintOpacity = _dark ? 0.28f : 0.15f,
            LuminosityOpacity = _dark ? 0.58f : 0.5f,
            FallbackColor = _dark ? Color.FromArgb(0xF0, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xF0, 0xF7, 0xF7, 0xF9),
        };
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        });
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
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(target);
    }
}
