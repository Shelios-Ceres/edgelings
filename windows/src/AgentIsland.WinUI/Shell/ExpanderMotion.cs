using System.Numerics;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentIsland.WinUI.Shell;

/// M9, switched on by the kits' SettingsExpander styles: the rows (a
/// RevealPanel named Rows) open and close from the top, so the page below
/// follows, and the chevron turns with them. Pixel steps the rows' height (a
/// third, two thirds, all; closing, half, none) and flips the chevron on
/// frame 1. Classic eases the height, fades the rows in after 83 ms as they
/// settle 4 DIP, and turns the chevron half a turn. Reduced motion, or a
/// change before the expander is on screen, shows the new state at once.
internal static class ExpanderMotion
{
    private static readonly ConditionalWeakTable<SettingsExpander, object> Hooked = new();
    private static readonly ConditionalWeakTable<SettingsExpander, UiMoment> Playing = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ExpanderMotion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not SettingsExpander expander || e.NewValue is not true || Hooked.TryGetValue(expander, out _)) return;
            Hooked.Add(expander, new object());
            expander.Loaded += (_, _) => Rest(expander);
            expander.RegisterPropertyChangedCallback(SettingsExpander.IsExpandedProperty, (_, _) => Toggle(expander));
        }));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    /// The rows and chevron as the expander stands, with nothing playing.
    private static void Rest(SettingsExpander expander)
    {
        Stop(expander);
        var open = expander.IsExpanded;
        if (ControlMotion.Find<RevealPanel>(expander, "Rows") is { } rows)
        {
            rows.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            rows.Shown = double.NaN;
            if (rows.Child is { } content)
            {
                content.Opacity = 1;
                content.Translation = Vector3.Zero;
            }
        }
        Chevron(expander, open ? 1 : 0);
    }

    private static void Stop(SettingsExpander expander)
    {
        if (!Playing.TryGetValue(expander, out var moment)) return;
        moment.Stop();
        Playing.Remove(expander);
    }

    private static void Toggle(SettingsExpander expander)
    {
        if (!expander.IsLoaded || Motion.ReducedMotion || ControlMotion.Find<RevealPanel>(expander, "Rows") is not { } rows || rows.Child is not { } content)
        {
            Rest(expander);
            return;
        }
        Stop(expander);
        var open = expander.IsExpanded;
        // From the frame on screen: closed, all of it, or part way.
        var closed = rows.Visibility == Visibility.Collapsed;
        var from = closed ? 0 : double.IsNaN(rows.Shown) ? rows.Natural : Math.Min(rows.Shown, rows.Natural);
        rows.Visibility = Visibility.Visible;
        rows.Shown = from;
        var turn = Turn(expander);
        UiMoment moment;
        if (Looks.Pixel)
        {
            moment = open
                ? UiMoment.Play(3 * Motion.TickMs, t =>
                {
                    var k = Motion.Tick(t);
                    if (k >= 1) Chevron(expander, 1);
                    rows.Shown = k >= 3 ? double.NaN : k == 0 ? from : Math.Max(from, Motion.Cell(rows.Natural * k / 3));
                }, () => Playing.Remove(expander))
                : UiMoment.Play(2 * Motion.TickMs, t =>
                {
                    var k = Motion.Tick(t);
                    if (k >= 1) Chevron(expander, 0);
                    if (k >= 2)
                    {
                        rows.Visibility = Visibility.Collapsed;
                        rows.Shown = double.NaN;
                        return;
                    }
                    rows.Shown = k == 0 ? from : Motion.Cell(from / 2);
                }, () => Playing.Remove(expander));
        }
        else if (open)
        {
            var opacity = closed ? 0 : content.Opacity;
            var rise = closed ? -4 : content.Translation.Y;
            moment = UiMoment.Play(333, t =>
            {
                rows.Shown = t >= 333 ? double.NaN : from + (rows.Natural - from) * Motion.Out(t / 333);
                content.Opacity = opacity + (1 - opacity) * Motion.Linear((t - 83) / 167);
                content.Translation = new Vector3(0, (float)(rise * (1 - Motion.Out(t / 333))), 0);
                Chevron(expander, turn + (1 - turn) * Motion.InOut(t / 250));
            }, () => Playing.Remove(expander));
        }
        else
        {
            var opacity = content.Opacity;
            moment = UiMoment.Play(250, t =>
            {
                rows.Shown = from * (1 - Motion.InOut(t / 250));
                content.Opacity = opacity * (1 - Motion.Linear(t / 83));
                Chevron(expander, turn * (1 - Motion.InOut(t / 250)));
                if (t < 250) return;
                rows.Visibility = Visibility.Collapsed;
                rows.Shown = double.NaN;
                content.Opacity = 1;
                content.Translation = Vector3.Zero;
            }, () => Playing.Remove(expander));
        }
        Playing.AddOrUpdate(expander, moment);
    }

    /// The chevron, in the header's chevron box: Pixel's down or up drawing,
    /// or Classic's glyph turned by `turn` of half a turn.
    private static void Chevron(SettingsExpander expander, double turn)
    {
        if (ControlMotion.Find<FrameworkElement>(expander, "ChevronBox") is not { } box) return;
        if (ControlMotion.Find<FrameworkElement>(box, "Down") is { } down && ControlMotion.Find<FrameworkElement>(box, "Up") is { } up)
        {
            down.Visibility = turn >= 0.5 ? Visibility.Collapsed : Visibility.Visible;
            up.Visibility = turn >= 0.5 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (ControlMotion.Find<FrameworkElement>(box, "Chevron") is not { } chevron) return;
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        if (chevron.RenderTransform is not RotateTransform rotate) chevron.RenderTransform = rotate = new RotateTransform();
        rotate.Angle = 180 * turn;
    }

    /// How far the chevron has turned toward open, 0 to 1.
    private static double Turn(SettingsExpander expander)
    {
        if (ControlMotion.Find<FrameworkElement>(expander, "ChevronBox") is not { } box) return 0;
        if (ControlMotion.Find<FrameworkElement>(box, "Up") is { } up) return up.Visibility == Visibility.Visible ? 1 : 0;
        return ControlMotion.Find<FrameworkElement>(box, "Chevron") is { RenderTransform: RotateTransform rotate } ? rotate.Angle / 180 : 0;
    }
}
