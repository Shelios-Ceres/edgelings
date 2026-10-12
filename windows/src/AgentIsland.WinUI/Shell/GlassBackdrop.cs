using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
            TintOpacity = _dark ? 0.32f : 0.22f,
            LuminosityOpacity = _dark ? 0.62f : 0.55f,
            FallbackColor = _dark ? Color.FromArgb(0xF0, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xF0, 0xF7, 0xF7, 0xF9),
        };
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        });
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(target);
    }
}
