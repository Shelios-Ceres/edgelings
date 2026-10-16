using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AgentIsland.WinUI.Shell;

/// The kits' control motion, switched on by their styles: tooltips (M14),
/// dialogs (M15) and Pixel's drop-down lists (M13). Pixel steps ghosts on
/// its 30-tick clock; Classic eases on its curves. Reduced: Pixel at once,
/// Classic an 83-ms fade.
internal static class ControlMotion
{
    public static readonly DependencyProperty TipProperty = DependencyProperty.RegisterAttached(
        "Tip", typeof(bool), typeof(ControlMotion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is ToolTip tip && e.NewValue is true) tip.Opened += OnTipOpened;
        }));

    public static readonly DependencyProperty DialogProperty = DependencyProperty.RegisterAttached(
        "Dialog", typeof(bool), typeof(ControlMotion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not ContentDialog dialog || e.NewValue is not true) return;
            dialog.Opened += OnDialogOpened;
            dialog.Closing += OnDialogClosing;
        }));

    public static readonly DependencyProperty DropDownProperty = DependencyProperty.RegisterAttached(
        "DropDown", typeof(bool), typeof(ControlMotion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is ComboBox box && e.NewValue is true) box.DropDownOpened += OnDropDownOpened;
        }));

    public static bool GetTip(DependencyObject element) => (bool)element.GetValue(TipProperty);

    public static void SetTip(DependencyObject element, bool value) => element.SetValue(TipProperty, value);

    public static bool GetDialog(DependencyObject element) => (bool)element.GetValue(DialogProperty);

    public static void SetDialog(DependencyObject element, bool value) => element.SetValue(DialogProperty, value);

    public static bool GetDropDown(DependencyObject element) => (bool)element.GetValue(DropDownProperty);

    public static void SetDropDown(DependencyObject element, bool value) => element.SetValue(DropDownProperty, value);

    // MARK: - M14 tooltip

    /// Pixel: a ghost a cell smaller all round, then the tip. Classic: it
    /// fades in and rises 4 DIP as it grows from 0.96.
    private static void OnTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip) return;
        var reduced = Motion.ReducedMotion;
        if (Looks.Pixel)
        {
            if (reduced || Find<Grid>(tip, "LayoutRoot") is not { } root || root.Children.Count < 2) return;
            var plate = root.Children[0] as FrameworkElement;
            var content = root.Children[1];
            UiMoment.Play(2 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                tip.Opacity = k == 0 ? 0 : 1;
                if (plate is not null) plate.Margin = k == 1 ? new Thickness(PixelPlate.Cell) : new Thickness(0);
                content.Opacity = k >= 2 ? 1 : 0;
            });
            return;
        }
        if (reduced)
        {
            UiMoment.Play(83, t => tip.Opacity = Motion.Linear(t / 83));
            return;
        }
        tip.CenterPoint = new Vector3((float)tip.ActualWidth / 2, (float)tip.ActualHeight / 2, 0);
        UiMoment.Play(250, t =>
        {
            tip.Opacity = Motion.Linear(t / 167);
            var rise = Motion.Out(t / 250);
            tip.Translation = new Vector3(0, (float)(4 * (1 - rise)), 0);
            var scale = (float)(0.96 + 0.04 * rise);
            tip.Scale = new Vector3(scale, scale, 1);
        });
    }

    // MARK: - M15 dialog

    /// Pixel: the smoke steps to half, then all of it, while the dialog grows
    /// from its centre in ghosts of a third and two thirds; the content comes
    /// on the third tick. Classic: the smoke fades in over 250 ms while the
    /// dialog fades in and grows from 0.92 with a soft overshoot.
    private static void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (Parts(sender) is not { } parts) return;
        var (smoke, box, content) = parts;
        var reduced = Motion.ReducedMotion;
        // The dialog opens before its first layout: its size is the
        // template's width, or measured here.
        var width = double.IsNaN(box.Width) ? box.ActualWidth : box.Width;
        box.Measure(new Windows.Foundation.Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        if (width <= 0) width = box.DesiredSize.Width;
        var height = Math.Max(box.ActualHeight, box.DesiredSize.Height);
        if (width <= 0 || height <= 0) return;
        if (Looks.Pixel)
        {
            if (reduced) return;
            // The template's minimum height would hold the ghosts tall.
            var minimum = box.MinHeight;
            UiMoment.Play(3 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                smoke.Opacity = k == 0 ? 0 : k == 1 ? 0.5 : 1;
                if (k >= 3)
                {
                    box.Width = width;
                    box.ClearValue(FrameworkElement.HeightProperty);
                    box.MinHeight = minimum;
                    box.Opacity = 1;
                    content.Opacity = 1;
                    return;
                }
                box.MinHeight = 0;
                box.Opacity = k == 0 ? 0 : 1;
                content.Opacity = 0;
                box.Width = Math.Max(3 * PixelPlate.Cell, Motion.Cell(width * k / 3));
                box.Height = Math.Max(3 * PixelPlate.Cell, Motion.Cell(height * k / 3));
            });
            return;
        }
        if (reduced)
        {
            UiMoment.Play(83, t => smoke.Opacity = box.Opacity = Motion.Linear(t / 83));
            return;
        }
        UiMoment.Play(333, t =>
        {
            smoke.Opacity = Motion.Linear(t / 250);
            box.Opacity = Motion.Linear(t / 167);
            Scale(box, 0.92 + 0.08 * Motion.SoftPop(t / 333));
        });
    }

    /// The dialog's close plays before it goes, on a deferral so the button
    /// pressed still decides its result. Pixel: the dialog goes and the
    /// smoke halves for a tick, then it goes too. Classic: the dialog fades
    /// and shrinks to 0.97 over 167 ms as the smoke fades over 250.
    private static void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (Parts(sender) is not { } parts) return;
        var (smoke, box, _) = parts;
        var reduced = Motion.ReducedMotion;
        if (Looks.Pixel && reduced) return;
        var deferral = args.GetDeferral();
        if (Looks.Pixel)
        {
            UiMoment.Play(2 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                box.Opacity = k == 0 ? 1 : 0;
                smoke.Opacity = k == 0 ? 1 : k == 1 ? 0.5 : 0;
            }, deferral.Complete);
            return;
        }
        if (reduced)
        {
            UiMoment.Play(83, t => smoke.Opacity = box.Opacity = 1 - Motion.Linear(t / 83), deferral.Complete);
            return;
        }
        UiMoment.Play(250, t =>
        {
            smoke.Opacity = 1 - Motion.Linear(t / 250);
            var fade = Motion.In(t / 167);
            box.Opacity = 1 - fade;
            Scale(box, 1 - 0.03 * fade);
        }, deferral.Complete);
    }

    /// The dialog box's own scale transform (about its centre): it holds a
    /// RenderTransform, which rules out UIElement.Scale.
    private static void Scale(FrameworkElement box, double scale)
    {
        if (box.RenderTransform is not ScaleTransform transform) return;
        transform.ScaleX = transform.ScaleY = scale;
    }

    /// The template's smoke, the dialog's box and its content. A shown dialog
    /// lifts its smoke into a popup of its own, so each part is looked for
    /// in the dialog and in every open popup, roots included.
    private static (Rectangle Smoke, FrameworkElement Box, UIElement Content)? Parts(ContentDialog dialog)
    {
        var roots = new List<DependencyObject> { dialog };
        if (dialog.XamlRoot is { } xamlRoot) roots.AddRange(VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Select(p => p.Child).OfType<DependencyObject>());
        T? Part<T>(string name) where T : FrameworkElement =>
            roots.Select(root => root is T self && self.Name == name ? self : Find<T>(root, name)).FirstOrDefault(found => found is not null);
        return Part<Rectangle>("SmokeLayerBackground") is { } smoke && Part<FrameworkElement>("BackgroundElement") is { } box
            && Part<FrameworkElement>("DialogSpace") is { } content
                ? (smoke, box, content)
                : null;
    }

    // MARK: - M13 drop-down (Pixel)

    /// The list unfolds from the selected row: a ghost exactly over it with
    /// that row shown (it is the box's own content), then a ghost covering
    /// half the rows above and below, then the list.
    private static void OnDropDownOpened(object? sender, object e)
    {
        if (!Looks.Pixel || Motion.ReducedMotion || sender is not ComboBox box) return;
        if (Find<ScrollViewer>(box, "ScrollViewer") is not { } scroller) return;
        if (scroller.ActualHeight > 0) Unfold(box, scroller);
        else
        {
            scroller.Opacity = 0;
            SizeChangedEventHandler? sized = null;
            sized = (_, args) =>
            {
                if (args.NewSize.Height <= 0) return;
                scroller.SizeChanged -= sized;
                scroller.Opacity = 1;
                Unfold(box, scroller);
            };
            scroller.SizeChanged += sized;
        }
    }

    private static void Unfold(ComboBox box, ScrollViewer scroller)
    {
        if (Find<FrameworkElement>(scroller, "Plate") is not { } plate) return;
        var rows = Enumerable.Range(0, box.Items.Count).Select(i => box.ContainerFromIndex(i) as UIElement).OfType<UIElement>().ToList();
        var chosen = box.SelectedIndex >= 0 ? box.ContainerFromIndex(box.SelectedIndex) as FrameworkElement : null;
        var height = scroller.ActualHeight;
        var cell = PixelPlate.Cell;
        double top = 0, bottom = height;
        if (chosen is not null)
        {
            var at = chosen.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            top = Math.Clamp(Motion.Cell(at - cell), 0, height);
            bottom = Math.Clamp(Motion.Cell(at + chosen.ActualHeight + cell), top, height);
        }
        UiMoment.Play(3 * Motion.TickMs, t =>
        {
            var k = Motion.Tick(t);
            if (k >= 2)
            {
                var half = k >= 3 ? 1.0 : 0.5;
                plate.Margin = new Thickness(0, Motion.Cell(top * (1 - half)), 0, Motion.Cell((height - bottom) * (1 - half)));
                foreach (var row in rows) row.Opacity = k >= 3 ? 1 : 0;
                return;
            }
            plate.Margin = new Thickness(0, top, 0, height - bottom);
            foreach (var row in rows) row.Opacity = row == chosen ? 1 : 0;
        });
    }

    /// A named part of a control's template, found in its visual tree.
    private static T? Find<T>(DependencyObject root, string name) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && child is FrameworkElement { Name: var found } && found == name) return match;
            if (child is Popup { Child: { } inner })
            {
                if (inner is T popupMatch && inner is FrameworkElement { Name: var innerName } && innerName == name) return popupMatch;
                if (Find<T>(inner, name) is { } inPopup) return inPopup;
            }
            if (Find<T>(child, name) is { } deeper) return deeper;
        }
        return null;
    }
}
