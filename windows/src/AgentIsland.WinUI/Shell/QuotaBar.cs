using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// A filled quota bar: a rounded track tinted with the agent's colour and a
/// solid fill for the share used (or left). A fill under one bar-height
/// still shows as a dot, so a small share never disappears into the track.
/// A new share counts in over 500 ms on out (M16), the warning ink blending
/// in over 167 ms from where the fill crosses its line.
internal sealed class QuotaBar : Grid, IQuotaBar
{
    private readonly Border _fill;
    private double _ratio;
    private double? _shown;
    private Color _ink;
    private bool _warn;
    private UiMoment? _count;

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
    public void Set(double? ratio, Color color, bool warn, bool dark, Action<double, bool>? follow = null)
    {
        _count?.Stop();
        var to = Math.Clamp(ratio ?? 0, 0, 1);
        var critical = Fluent.Resource<SolidColorBrush>("SystemFillColorCriticalBrush", dark).Color;
        var ink = warn ? critical : color;
        Opacity = ratio is null ? 0.45 : 1;
        if (_shown is not { } from || Math.Abs(from - to) < 0.0005 || Motion.ReducedMotion || !IsLoaded)
        {
            Paint(to, ink, dark);
            _shown = to;
            _warn = warn;
            follow?.Invoke(to, warn);
            return;
        }
        var line = QuotaCount.Line(from, to, _warn, warn);
        var was = _ink;
        double? crossed = null;
        // A crossing late in the count still blends for its full 167 ms.
        _count = UiMoment.Play(line is null ? 500 : 667, t =>
        {
            var share = from + (to - from) * Motion.Out(Math.Min(t, 500) / 500);
            var past = line is { } at && (to > from ? share >= at : share <= at);
            if (past && crossed is null) crossed = t;
            var shownWarn = line is null ? warn : past ? warn : !warn;
            var blend = crossed is { } since ? Motion.Linear((t - since) / 167) : line is null ? 1 : 0;
            Paint(share, QuotaCount.Mix(was, ink, blend), dark);
            _shown = share;
            follow?.Invoke(share, shownWarn);
        }, () =>
        {
            Paint(to, ink, dark);
            _shown = to;
            follow?.Invoke(to, warn);
        });
        _warn = warn;
    }

    private void Paint(double ratio, Color ink, bool dark)
    {
        _ratio = ratio;
        _ink = ink;
        _fill.Background = new SolidColorBrush(ink);
        Background = new SolidColorBrush(Color.FromArgb((byte)(dark ? 0x40 : 0x2E), ink.R, ink.G, ink.B));
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
    /// ratio null means the window failed to load. A bar already showing a
    /// share counts to a new one (M4, M16); `follow` hears every frame's
    /// share and whether it reads as nearly used up, so a label can count
    /// along. A bar's first share shows at once.
    void Set(double? ratio, Color color, bool warn, bool dark, Action<double, bool>? follow = null);
}

/// What both bars share while they count.
internal static class QuotaCount
{
    /// Where a count crosses into or out of the warning: 90 % of a bar of
    /// what is used, 10 % of one of what is left (the warning turning on as
    /// the share falls). Null when the warning stays as it was.
    public static double? Line(double from, double to, bool warnBefore, bool warnAfter) =>
        warnBefore == warnAfter ? null : warnAfter == to > from ? 0.9 : 0.1;

    public static Color Mix(Color from, Color to, double share)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * share);
        return Color.FromArgb(Channel(from.A, to.A), Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    /// A label that reads as a whole percentage of the bar ("56%") counts
    /// through the shares; any other reads its final text at once.
    public static string Percent(double share, double final, string text) =>
        text.EndsWith('%') && int.TryParse(text.AsSpan(0, text.Length - 1), out var value) && value == (int)Math.Round(final * 100)
            ? $"{(int)Math.Round(share * 100)}%"
            : text;
}
