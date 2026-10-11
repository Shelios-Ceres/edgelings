using System.Windows;
using System.Windows.Media;
using AgentIsland.Core;

namespace AgentIsland.UI.Drawer;

/// One character's drawing surface. Frames are re-recorded into a
/// DrawingVisual, so presenting never triggers layout, and hit testing
/// follows the drawn shape — the transparent window passes clicks through
/// everywhere else.
public sealed class BotSurface : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    public BotSurface(double size)
    {
        Width = size;
        Height = size;
        AddVisualChild(_visual);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    public void Present(string svg, SvgRenderer renderer)
    {
        using var dc = _visual.RenderOpen();
        renderer.Render(svg, dc);
    }

    /// Static stand-in when the Grok engine is unavailable: a coloured disc
    /// whose eyes close for out-of-quota and widen when the agent needs you.
    public void PresentFallback(Color color, AgentPhase phase)
    {
        var size = Width;
        var body = new SolidColorBrush(color);
        body.Freeze();
        using var dc = _visual.RenderOpen();
        var center = new Point(size / 2, size / 2);
        dc.DrawEllipse(body, null, center, size * 0.42, size * 0.42);
        var eyeY = size * 0.46;
        var eyeDx = size * 0.11;
        if (phase == AgentPhase.RateLimited)
        {
            var pen = new Pen(Brushes.White, size * 0.05) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawLine(pen, new Point(center.X - eyeDx - size * 0.05, eyeY), new Point(center.X - eyeDx + size * 0.05, eyeY));
            dc.DrawLine(pen, new Point(center.X + eyeDx - size * 0.05, eyeY), new Point(center.X + eyeDx + size * 0.05, eyeY));
            return;
        }
        var tall = phase is AgentPhase.Finished or AgentPhase.Asking ? 0.11 : 0.08;
        dc.DrawEllipse(Brushes.White, null, new Point(center.X - eyeDx, eyeY), size * 0.045, size * tall);
        dc.DrawEllipse(Brushes.White, null, new Point(center.X + eyeDx, eyeY), size * 0.045, size * tall);
    }
}
