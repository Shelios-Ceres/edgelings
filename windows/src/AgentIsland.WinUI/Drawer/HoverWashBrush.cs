using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentIsland.WinUI.Drawer;

internal sealed class HoverWashBrush : XamlCompositionBrushBase, IDisposable
{
    private readonly Compositor _compositor;
    private readonly CompositionLinearGradientBrush _gradient;
    private readonly CompositionColorGradientStop _start;
    private readonly CompositionColorGradientStop _end;
    private Color _startColor;
    private Color _endColor;

    public HoverWashBrush(Compositor compositor)
    {
        _compositor = compositor;
        _gradient = compositor.CreateLinearGradientBrush();
        _gradient.StartPoint = new(0, 0);
        _gradient.EndPoint = new(1, 1);
        _start = compositor.CreateColorGradientStop(0, default);
        _end = compositor.CreateColorGradientStop(1, default);
        _gradient.ColorStops.Add(_start);
        _gradient.ColorStops.Add(_end);
    }

    protected override void OnConnected() => CompositionBrush = _gradient;

    // Owned by one persistent card. Keep the two color stops across popup
    // closes so a reconnect cannot replace an in-flight color transition.
    protected override void OnDisconnected() => CompositionBrush = null;

    public void Set(uint? swatch, bool dark, TimeSpan duration, CompositionEasingFunction easing)
    {
        var rgb = swatch ?? 0;
        Color Tone(byte alpha) => Color.FromArgb(swatch is null ? (byte)0 : alpha,
            (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var start = Tone((byte)(dark ? 0x40 : 0x38));
        var end = Tone((byte)(dark ? 0x14 : 0x0E));
        if (start == _startColor && end == _endColor) return;
        _startColor = start;
        _endColor = end;
        FallbackColor = start;
        if (duration == TimeSpan.Zero) { Settle(); return; }
        Animate(_start, start, duration, easing);
        Animate(_end, end, duration, easing);
    }

    private void Animate(CompositionColorGradientStop stop, Color color, TimeSpan duration, CompositionEasingFunction easing)
    {
        using var animation = _compositor.CreateColorKeyFrameAnimation();
        animation.Duration = duration;
        animation.InterpolationColorSpace = CompositionColorSpace.Rgb;
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, color, easing);
        stop.StartAnimation("Color", animation);
    }

    public void Settle()
    {
        _start.StopAnimation("Color");
        _end.StopAnimation("Color");
        _start.Color = _startColor;
        _end.Color = _endColor;
    }

    public void Dispose()
    {
        CompositionBrush = null;
        _gradient.Dispose();
        _start.Dispose();
        _end.Dispose();
    }
}
