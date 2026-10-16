using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentIsland.WinUI.Shell;

/// Shows the top Shown DIP of its one child (NaN: all of it) and clips the
/// rest, while the child keeps its own height: what follows moves up and
/// down with the reveal, and nothing is squeezed. An expander's rows open
/// and close in one (M9).
internal sealed class RevealPanel : Panel
{
    private double _shown = double.NaN;

    /// The child's own height at the last layout.
    public double Natural { get; private set; }

    public double Shown
    {
        get => _shown;
        set
        {
            if (_shown.Equals(value)) return;
            _shown = value;
            InvalidateMeasure();
        }
    }

    public UIElement? Child
    {
        get => Children.Count > 0 ? Children[0] : null;
        set
        {
            Children.Clear();
            if (value is not null) Children.Add(value);
        }
    }

    /// The bottom of the shown height the child keeps clear while part way
    /// shown, as its own bottom margin does at rest: an outline drawn under
    /// it (Pixel's card) stays whole, its corners too.
    public double Hem { get; set; }

    private double Visible => double.IsNaN(_shown) ? Natural : Math.Clamp(_shown, 0, Natural);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return new Size();
        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        Natural = child.DesiredSize.Height;
        return new Size(child.DesiredSize.Width, Visible);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Natural));
        // Only the bottom is cut: focus rings and shadows keep their room.
        Clip = double.IsNaN(_shown) ? null : new RectangleGeometry { Rect = new Rect(-16, 0, finalSize.Width + 32, Math.Max(0, Visible - Hem)) };
        return finalSize;
    }
}
