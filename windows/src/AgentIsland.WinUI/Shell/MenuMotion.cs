using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentIsland.WinUI.Shell;

/// M7: a kit menu's motion, played on each of its panels as it loads, the
/// menu from the pointer's corner and a submenu out of its parent item.
/// Pixel grows ghosts of the panel (full width at a third and two thirds of
/// its height, a submenu at half its width) and shows the items on the last
/// frame; Classic grows from the corner with a soft overshoot as it drops
/// in, and fades out on closing. Reduced: Pixel at once, Classic an 83-ms
/// fade. Fluent's own open and close transitions are off.
internal static class MenuMotion
{
    private static MenuFlyout? _menu;
    private static FrameworkElement? _anchor;
    private static MenuFlyoutPresenter? _root;
    private static bool _fading;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(MenuMotion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is MenuFlyoutPresenter presenter && e.NewValue is true) presenter.Loaded += OnLoaded;
        }));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    /// The kit menu last given motion, for the menu film.
    internal static MenuFlyout? Menu => _menu;

    /// Its panels open now, the menu's first.
    internal static List<MenuFlyoutPresenter> OpenPanels() => _anchor?.XamlRoot is { } xamlRoot
        ? VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Select(popup => popup.Child).OfType<MenuFlyoutPresenter>().Reverse().ToList()
        : new List<MenuFlyoutPresenter>();

    /// Gives a kit menu its motion; it opens from anchor's top-left corner.
    public static void Use(MenuFlyout menu, FrameworkElement anchor)
    {
        _menu = menu;
        _anchor = anchor;
        _root = null;
        _fading = false;
        menu.AreOpenCloseAnimationsEnabled = false;
        menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
        {
            BasedOn = menu.MenuFlyoutPresenterStyle,
            Setters = { new Setter(EnabledProperty, true) },
        };
        if (!Looks.Pixel)
        {
            menu.Closing += FadeOut;
            menu.Closed += (_, _) => _fading = false;
        }
    }

    /// A panel loads before it is measured: it stays hidden until its first
    /// size, then plays from there.
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutPresenter panel || _menu is null) return;
        if (panel.ActualWidth > 0 && panel.ActualHeight > 0)
        {
            Start(panel);
            return;
        }
        if (!(Looks.Pixel && Motion.ReducedMotion)) panel.Opacity = 0;
        panel.SizeChanged += OnSized;
    }

    private static void OnSized(object sender, SizeChangedEventArgs e)
    {
        if (sender is not MenuFlyoutPresenter panel || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        panel.SizeChanged -= OnSized;
        Start(panel);
    }

    private static void Start(MenuFlyoutPresenter panel)
    {
        if (_menu is not { } menu || _anchor is not { } anchor)
        {
            panel.Opacity = 1;
            return;
        }
        var root = menu.Items.Count > 0 && panel.Items.Contains(menu.Items[0]);
        if (root) _root = panel;
        var reduced = Motion.ReducedMotion;
        var at = Origin(panel, anchor);
        double width = panel.ActualWidth, height = panel.ActualHeight;
        // Which way it opened: up or left of the pointer, or a submenu left
        // of its parent.
        var up = root && at.Y < -1;
        var left = root ? at.X < -1 : _root is { } parent && at.X < Origin(parent, anchor).X;
        if (Looks.Pixel)
        {
            if (reduced) panel.Opacity = 1;
            else Grow(panel, root, width, height, up, left);
            return;
        }
        if (reduced)
        {
            UiMoment.Play(83, t => panel.Opacity = Motion.Linear(t / 83));
            return;
        }
        if (root)
        {
            panel.CenterPoint = new Vector3(left ? (float)width : 0, up ? (float)height : 0, 0);
            var drop = up ? 4f : -4f;
            UiMoment.Play(250, t =>
            {
                panel.Opacity = Motion.Linear(t / 167);
                var scale = (float)(0.95 + 0.05 * Motion.SoftPop(t / 250));
                panel.Scale = new Vector3(scale, scale, 1);
                panel.Translation = new Vector3(0, (float)(drop * (1 - Motion.Out(t / 250))), 0);
            });
            return;
        }
        // A submenu drifts out of its parent item.
        var from = left ? 6f : -6f;
        UiMoment.Play(167, t =>
        {
            panel.Opacity = Motion.Linear(t / 167);
            panel.Translation = new Vector3((float)(from * (1 - Motion.Out(t / 167))), 0, 0);
        });
    }

    /// Pixel: ghosts of the panel with its items hidden, grown from the edge
    /// it is anchored on, on the 30-tick clock.
    private static void Grow(MenuFlyoutPresenter panel, bool root, double width, double height, bool up, bool left)
    {
        var items = Items(panel);
        var cell = PixelPlate.Cell;
        var ticks = root ? 3 : 2;
        UiMoment.Play(ticks * Motion.TickMs, t =>
        {
            var k = Motion.Tick(t);
            if (k == 0)
            {
                panel.Opacity = 0;
                return;
            }
            panel.Opacity = 1;
            if (k >= ticks)
            {
                panel.ClearValue(FrameworkElement.WidthProperty);
                panel.ClearValue(FrameworkElement.HeightProperty);
                panel.Translation = Vector3.Zero;
                if (items is not null) items.Opacity = 1;
                return;
            }
            if (items is not null) items.Opacity = 0;
            if (root)
            {
                var ghost = Math.Max(3 * cell, Motion.Cell(height * k / 3));
                panel.Height = ghost;
                panel.Translation = new Vector3(0, up ? (float)(height - ghost) : 0, 0);
            }
            else
            {
                var ghost = Math.Max(3 * cell, Motion.Cell(width / 2));
                panel.Width = ghost;
                panel.Translation = new Vector3(left ? (float)(width - ghost) : 0, 0, 0);
            }
        });
    }

    /// Where a panel's corner sits relative to the anchor; the origin when it
    /// can't be told.
    private static Point Origin(UIElement panel, UIElement anchor)
    {
        try
        {
            return panel.TransformToVisual(anchor).TransformPoint(new Point(0, 0));
        }
        catch (ArgumentException)
        {
            return new Point(0, 0);
        }
    }

    /// The panel's scroller, which holds its items.
    private static UIElement? Items(DependencyObject panel)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
        {
            var child = VisualTreeHelper.GetChild(panel, i);
            if (child is ScrollViewer { Name: "MenuFlyoutPresenterScrollViewer" } scroller) return scroller;
            if (Items(child) is { } found) return found;
        }
        return null;
    }

    /// Classic closes with an 83-ms fade of every open panel; the menu then
    /// closes for real.
    private static void FadeOut(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        // A menu closing as another opens goes at once.
        if (_fading || sender != _menu || Motion.ReducedMotion || _anchor?.XamlRoot is not { } xamlRoot) return;
        var panels = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot)
            .Select(popup => popup.Child)
            .OfType<MenuFlyoutPresenter>()
            .ToList();
        if (panels.Count == 0) return;
        args.Cancel = true;
        _fading = true;
        var menu = sender;
        UiMoment.Play(83, t =>
        {
            foreach (var panel in panels) panel.Opacity = 1 - Motion.Linear(t / 83);
        }, menu.Hide);
    }
}
