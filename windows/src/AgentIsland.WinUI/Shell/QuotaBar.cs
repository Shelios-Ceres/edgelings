using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// A filled quota bar: a rounded track tinted with the agent's colour and a
/// solid fill for the share used (or left). A fill under one bar-height
/// still shows as a dot, so a small share never disappears into the track.
internal sealed class QuotaBar : Grid, IQuotaBar
{
    private readonly Border _fill;
    private double _ratio;

    public QuotaBar(double height)
    {
        Height = height;
        CornerRadius = new CornerRadius(height / 2);
        _fill = new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Children.Add(_fill);
        SizeChanged += (_, _) => Layout();
    }

    /// ratio null means the window failed to load: an empty, faded track.
    public void Set(double? ratio, Color color, bool warn, bool dark)
    {
        _ratio = Math.Clamp(ratio ?? 0, 0, 1);
        var ink = warn ? Fluent.Resource<SolidColorBrush>("SystemFillColorCriticalBrush", dark).Color : color;
        _fill.Background = new SolidColorBrush(ink);
        Background = new SolidColorBrush(Color.FromArgb((byte)(dark ? 0x40 : 0x2E), ink.R, ink.G, ink.B));
        Opacity = ratio is null ? 0.45 : 1;
        Layout();
    }

    private void Layout()
    {
        if (ActualWidth <= 0) return;
        _fill.Visibility = _ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        _fill.Width = Math.Max(Height, _ratio * ActualWidth);
    }
}

/// A quota bar in either look: the rounded Fluent one or the pixel blocks.
internal interface IQuotaBar
{
    /// ratio null means the window failed to load.
    void Set(double? ratio, Color color, bool warn, bool dark);
}
