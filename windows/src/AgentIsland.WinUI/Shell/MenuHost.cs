using AgentIsland.WinUI.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

/// Opens the tray's and the drawer's menus from one invisible window at the
/// pointer. The drawer never takes activation and the tray has no window,
/// so a menu needs a window that is foreground to be dismissed by an outside
/// click — the WinUI form of the SetForegroundWindow rule of tray menus.
internal static class MenuHost
{
    private static Window? _window;
    private static Grid? _root;
    private static IntPtr _hwnd;
    private static MenuFlyout? _open;
    private static bool _keepOpen;

    /// keepOpen is for the verification hooks, which have no click to grant
    /// the host the foreground.
    /// at is a screen point in physical pixels; the pointer by default.
    public static void Show(MenuFlyout menu, bool keepOpen = false, PointInt32? at = null, bool pixel = false)
    {
        Ensure();
        _open?.Hide();
        // The tray's menu stays Fluent; only a pixel menu brings the kit.
        _root!.Resources.MergedDictionaries.Clear();
        if (pixel)
        {
            _root.Resources.MergedDictionaries.Add(Looks.NewKit(Win32.GetDpiForWindow(_hwnd) / 96.0));
            Pixelate(menu);
        }
        _keepOpen = keepOpen;
        Win32.GetCursorPos(out var cursor);
        var point = at ?? new PointInt32(cursor.X, cursor.Y);
        _window!.AppWindow.MoveAndResize(new RectInt32(point.X, point.Y, 1, 1));
        _root!.RequestedTheme = Brand.IsDark() ? ElementTheme.Dark : ElementTheme.Light;
        // An empty region: the host is foreground but shows nothing, not
        // even the frame shadow DWM gives an active window.
        Win32.SetWindowRgn(_hwnd, Win32.CreateRectRgn(0, 0, 0, 0), true);
        _window.Activate();
        Win32.SetForegroundWindow(_hwnd);

        _open = menu;
        // The menu comes from sender: a handler that captured it would form a
        // cycle through WinUI that keeps the menu, popup window and all,
        // alive until a full GC.
        menu.Closed += (sender, _) =>
        {
            if (!ReferenceEquals(sender, _open)) return;
            _open = null;
            // Quitting from this menu closes the host before the menu.
            _window.AppWindow?.Hide();
        };
        // The host's content loads after its first activation; a flyout
        // shown before that has no root and fails fast.
        if (_root.IsLoaded)
        {
            Open(menu);
            return;
        }
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            _root.Loaded -= loaded;
            if (_open == menu) Open(menu);
        };
        _root.Loaded += loaded;
    }

    private static void Open(MenuFlyout menu) => menu.ShowAt(_root!, new FlyoutShowOptions
    {
        Position = new Windows.Foundation.Point(0, 0),
        ShowMode = FlyoutShowMode.Standard,
    });

    private static void Ensure()
    {
        if (_window is not null) return;
        _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _window = new Window { Content = _root, SystemBackdrop = new TransparentBackdrop(), Title = "Agent Island menu" };
        _hwnd = WindowNative.GetWindowHandle(_window);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        var style = (long)Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle);
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlExStyle, (IntPtr)(style | Win32.WsExToolWindow));
        var noBorder = unchecked((int)0xFFFFFFFE);
        Win32.DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));
        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !_keepOpen) _open?.Hide();
        };
        // Alt+F4 on the host closes its menu, not the host: a closed host
        // has no AppWindow left for the next menu.
        _window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            _open?.Hide();
        };
    }

    /// The pixel kit for a menu: its presenter, each item's style by kind,
    /// and the kit's drawing for every glyph it has one for. Menus open in
    /// popups, beyond any window's styles, so nothing here is implicit.
    public static void Pixelate(MenuFlyout menu)
    {
        menu.MenuFlyoutPresenterStyle = (Style)Looks.Resource("PixelMenuFlyoutPresenterStyle");
        Pixelate(menu.Items);
    }

    private static void Pixelate(IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            item.Style = (Style)Looks.Resource(item switch
            {
                MenuFlyoutSubItem => "PixelMenuFlyoutSubItemStyle",
                ToggleMenuFlyoutItem => "PixelToggleMenuFlyoutItemStyle",
                RadioMenuFlyoutItem => "PixelRadioMenuFlyoutItemStyle",
                MenuFlyoutSeparator => "PixelMenuFlyoutSeparatorStyle",
                _ => "PixelMenuFlyoutItemStyle",
            });
            switch (item)
            {
                case MenuFlyoutSubItem sub:
                    if (sub.Icon is FontIcon { Glyph: var subGlyph } && PixelIcons.NameFor(subGlyph) is { } subName) sub.Icon = PixelIcons.Icon(subName);
                    Pixelate(sub.Items);
                    break;
                case MenuFlyoutItem { Icon: FontIcon { Glyph: var glyph } } plain when PixelIcons.NameFor(glyph) is { } name:
                    plain.Icon = PixelIcons.Icon(name);
                    break;
            }
        }
    }

    /// A Segoe Fluent Icons glyph for a menu item.
    public static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets") };

    public static MenuFlyoutItem Item(string text, string? glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph is not null) item.Icon = Glyph(glyph);
        item.Click += (_, _) => action();
        return item;
    }
}
