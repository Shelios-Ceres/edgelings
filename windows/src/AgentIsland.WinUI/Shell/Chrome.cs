using AgentIsland.WinUI.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

/// The app windows' shared frame: a 32-DIP title bar drawn by the content
/// with the app's mark, centred on the work area, and the look's kit. Under
/// Classic: the window draws its own frame (ClassicFrame: an opaque desk,
/// 16-DIP corners, an outline, a soft shadow and its own caption buttons),
/// or under a high-contrast theme Mica and Windows' own frame. Under Pixel:
/// an opaque desk, square corners inside a pixel frame, the pixel logo and
/// the pixel app icon.
internal static class Chrome
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DoNotRound = 1;
    private const int DefaultCorners = 0;
    private const int DefaultBorder = unchecked((int)0xFFFFFFFF);

    // By window handle, while the window is open.
    private static readonly Dictionary<IntPtr, WindowIcons> Icons = new();
    private static readonly HashSet<IntPtr> Pixelated = new();
    // Windows under Classic's own frame.
    private static readonly Dictionary<IntPtr, ClassicFrame> Frames = new();
    // The smallest body each window allows, in DIP.
    private static readonly Dictionary<IntPtr, (double Width, double Height)> Minimums = new();

    /// Classic draws its own frame, except under a high-contrast theme, which
    /// keeps Windows' own frame and system colours.
    private static bool OwnFrame => !Looks.Pixel && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;

    /// Returns the window's root: the title bar in row 0, content goes in
    /// row 1.
    public static Grid Apply(Window window, string title, double width, double height, double minWidth, double minHeight)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        Minimums[hwnd] = (minWidth, minHeight);
        var root = Restyle(window, title);
        var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
        // The sizes are the body's: the frame's transparent margin goes outside them.
        var room = RoomOf(hwnd);
        Centre(window, (int)((width + room.Left + room.Right) * scale), (int)((height + room.Top + room.Bottom) * scale), room, scale);
        return root;
    }

    private static Thickness RoomOf(IntPtr hwnd) => Frames.ContainsKey(hwnd) ? ClassicFrame.Room : new Thickness(0);

    /// The window's minimum: its body's, with the frame's margin around it.
    /// Classic's frame brings its own presenter, which forgets the old one's
    /// minimum, so every restyle sets it again.
    private static void KeepMinimum(Window window, IntPtr hwnd)
    {
        if (!Minimums.TryGetValue(hwnd, out var minimum) || window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
        var room = RoomOf(hwnd);
        presenter.PreferredMinimumWidth = (int)((minimum.Width + room.Left + room.Right) * scale);
        presenter.PreferredMinimumHeight = (int)((minimum.Height + room.Top + room.Bottom) * scale);
    }

    /// Centres the window, or its body when a frame's margin surrounds it.
    private static void Centre(Window window, int width, int height, Thickness room = default, double scale = 1)
    {
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int rl = (int)(room.Left * scale), rt = (int)(room.Top * scale), rr = (int)(room.Right * scale), rb = (int)(room.Bottom * scale);
        var w = Math.Min(width, area.Width + rl + rr);
        var h = Math.Min(height, area.Height + rt + rb);
        var x = area.X + (area.Width - (w - rl - rr)) / 2 - rl;
        var y = area.Y + (area.Height - (h - rt - rb)) / 2 - rt;
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    /// Keeps a window Apply framed for its next opening: the system's close
    /// (title bar, Alt+F4, taskbar) hides it instead, and hidden lets it drop
    /// its pages. Each window that draws into its title bar leaves about 20
    /// GDI objects behind when it is destroyed (Windows App SDK 2.5.1), so in
    /// an app that runs for weeks every close would leak. Close() from code
    /// still destroys the window.
    public static void HideOnClose(Window window, Action hidden)
    {
        window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            // Dialogs, menus and tooltips open in popups, which would outlive
            // the window's pages and wait on screen.
            if (window.Content?.XamlRoot is { } xamlRoot)
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                {
                    if (popup.Child is ContentDialog dialog) dialog.Hide();
                    else popup.IsOpen = false;
                }
            }
            window.AppWindow.Hide();
            hidden();
        };
    }

    /// Brings a window up where it was left, restored if minimized; centred
    /// again only if no display shows it any more (one was unplugged while it
    /// was hidden).
    public static void Show(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        if (DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.None) is null)
        {
            Centre(window, window.AppWindow.Size.Width, window.AppWindow.Size.Height);
        }
        window.Activate();
    }

    /// A new root in the current look for a window Apply framed, which keeps
    /// its place and size. Look and language switches restyle open windows
    /// this way rather than reopening them: each window that draws into its
    /// title bar leaves about 20 GDI objects behind when it closes (Windows
    /// App SDK 2.5.1), so every switch would leak.
    public static Grid Restyle(Window window, string title)
    {
        window.Title = title;
        var hwnd = WindowNative.GetWindowHandle(window);
        var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
        var pixel = Looks.Pixel;
        var framed = OwnFrame;
        // Back to Windows' own frame (Pixel, or a high-contrast theme).
        if (!framed && Frames.Remove(hwnd, out var left)) left.Dispose();
        window.ExtendsContentIntoTitleBar = !framed;

        var root = new Grid();
        if (!Icons.TryGetValue(hwnd, out var icons))
        {
            icons = Icons[hwnd] = new WindowIcons(hwnd);
            window.Closed += (_, _) =>
            {
                icons.Dispose();
                Icons.Remove(hwnd);
                Pixelated.Remove(hwnd);
                Minimums.Remove(hwnd);
            };
        }
        icons.Show(pixel, root);
        var kit = Looks.NewKit(scale);
        root.Resources.MergedDictionaries.Add(kit);
        if (pixel)
        {
            root.Style = (Style)kit["PixelWindowStyle"];
            window.SystemBackdrop = null;
            Pixelate(window, hwnd);
            Pixelated.Add(hwnd);
        }
        else
        {
            if (Pixelated.Remove(hwnd)) Unpixelate(window, hwnd);
            if (framed)
            {
                if (!Frames.ContainsKey(hwnd))
                {
                    Frames[hwnd] = new ClassicFrame(window);
                    window.Closed += (_, _) =>
                    {
                        if (Frames.Remove(hwnd, out var gone)) gone.Dispose();
                    };
                }
            }
            else if (window.SystemBackdrop is not MicaBackdrop) window.SystemBackdrop = new MicaBackdrop();
        }
        if (!framed) window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        KeepMinimum(window, hwnd);

        var bar = new Grid { Padding = new Thickness(pixel ? 12 : 14, 0, 0, 0) };
        bar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image
                {
                    Source = pixel
                        ? PixelIcons.Logo()
                        : new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "agentisland_logo_small.png"))),
                    Width = pixel ? 20 : 16,
                    Height = pixel ? 20 : 16,
                },
                new TextBlock
                {
                    Text = title,
                    Style = (Style)Looks.Resource(pixel ? "PixelBodyTextStyle" : "CaptionTextBlockStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        if (framed) bar.Children.Add(Frames[hwnd].CreateButtons());
        if (pixel)
        {
            var frame = new Border { Style = (Style)Looks.Resource("PixelFrameStyle") };
            Grid.SetRowSpan(frame, 2);
            Canvas.SetZIndex(frame, 1);
            root.Children.Add(frame);
        }
        if (framed)
        {
            window.Content = Frames[hwnd].Wrap(root);
            return root;
        }
        window.Content = root;
        window.SetTitleBar(bar);
        return root;
    }

    /// Places content in a root made by Apply.
    public static void SetBody(Grid root, UIElement body)
    {
        Grid.SetRow((FrameworkElement)body, 1);
        root.Children.Add(body);
    }

    /// Square corners with the outline's colour, and caption buttons in the
    /// kit's ink; the close button keeps the system's red.
    private static void Pixelate(Window window, IntPtr hwnd)
    {
        var dark = Brand.IsDark();
        var corner = DoNotRound;
        Win32.DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));
        var line = PixelTokens.Get("Line", dark);
        var border = line.R | line.G << 8 | line.B << 16;
        Win32.DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
        var bar = window.AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = PixelTokens.Get("Text", dark);
        bar.ButtonHoverForegroundColor = PixelTokens.Get("Text", dark);
        bar.ButtonPressedForegroundColor = PixelTokens.Get("Text", dark);
        bar.ButtonHoverBackgroundColor = PixelTokens.Get("Hover", dark);
        bar.ButtonPressedBackgroundColor = PixelTokens.Get("Pressed", dark);
        bar.ButtonInactiveForegroundColor = PixelTokens.Get("Text3", dark);
    }

    /// The system's corners, outline and caption buttons again. The clear
    /// button backgrounds are what WinUI sets for a content title bar, so
    /// they stay.
    private static void Unpixelate(Window window, IntPtr hwnd)
    {
        var corner = DefaultCorners;
        Win32.DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));
        var border = DefaultBorder;
        Win32.DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
        var bar = window.AppWindow.TitleBar;
        bar.ButtonForegroundColor = null;
        bar.ButtonHoverForegroundColor = null;
        bar.ButtonPressedForegroundColor = null;
        bar.ButtonHoverBackgroundColor = null;
        bar.ButtonPressedBackgroundColor = null;
        bar.ButtonInactiveForegroundColor = null;
    }

    /// A window's icons, loaded by the app at exactly the size Windows shows
    /// them, reloaded when the window's scale changes and destroyed with it.
    /// Windows never frees the icons a window is handed, and
    /// AppWindow.SetIcon leaks one on every call.
    private sealed class WindowIcons : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly TypedEventHandler<XamlRoot, XamlRootChangedEventArgs> _rescale;
        private XamlRoot? _xamlRoot;
        private IntPtr _small;
        private IntPtr _big;
        private bool _pixel;
        private (bool Pixel, uint Dpi, int Bar) _loaded;

        public WindowIcons(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _rescale = (_, _) => Load();
        }

        /// The look's icons; once root loads they follow its window's scale.
        public void Show(bool pixel, FrameworkElement root)
        {
            _pixel = pixel;
            Load();
            root.Loaded += (_, _) => Follow(root.XamlRoot);
        }

        private void Follow(XamlRoot? xamlRoot)
        {
            if (xamlRoot == _xamlRoot) return;
            if (_xamlRoot is not null) _xamlRoot.Changed -= _rescale;
            _xamlRoot = xamlRoot;
            if (xamlRoot is not null) xamlRoot.Changed += _rescale;
        }

        private void Load()
        {
            var dpi = Win32.GetDpiForWindow(_hwnd);
            var bar = Win32.TaskbarIconDip();
            if (_small != IntPtr.Zero && _loaded == (_pixel, dpi, bar)) return;
            // Without the pixel file the classic icon stands in.
            if (Send(Sizes(_pixel, dpi, bar)) || (_pixel && Send(Sizes(false, dpi, bar)))) _loaded = (_pixel, dpi, bar);
        }

        /// Pixel: ICON_SMALL at the small-icon metric (24 at 150 %) and
        /// ICON_BIG at the taskbar's icon size (24 DIP, 36 at 150 %; 16 DIP
        /// with Windows 11's smaller taskbar buttons), since a scaled pixel
        /// icon turns soft. Classic: both at the large-icon metric (48), as
        /// AppWindow.SetIcon loaded it.
        private static (string File, int Small, int Big) Sizes(bool pixel, uint dpi, int bar) => pixel
            ? ("AgentIsland-pixel.ico", Win32.GetSystemMetricsForDpi(Win32.SmCxSmIcon, dpi), (int)Math.Round(bar * dpi / 96.0))
            : ("AgentIsland.ico", Win32.GetSystemMetricsForDpi(Win32.SmCxIcon, dpi), Win32.GetSystemMetricsForDpi(Win32.SmCxIcon, dpi));

        /// False if the file won't load; the window keeps the icons it has.
        private bool Send((string File, int Small, int Big) icons)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", icons.File);
            var small = Win32.LoadIconFile(path, icons.Small);
            var big = Win32.LoadIconFile(path, icons.Big);
            if (small == IntPtr.Zero || big == IntPtr.Zero)
            {
                if (small != IntPtr.Zero) Win32.DestroyIcon(small);
                if (big != IntPtr.Zero) Win32.DestroyIcon(big);
                return false;
            }
            Win32.SendMessage(_hwnd, Win32.WmSetIcon, Win32.IconSmall, small);
            Win32.SendMessage(_hwnd, Win32.WmSetIcon, Win32.IconBig, big);
            Destroy();
            (_small, _big) = (small, big);
            return true;
        }

        private void Destroy()
        {
            if (_small != IntPtr.Zero) Win32.DestroyIcon(_small);
            if (_big != IntPtr.Zero) Win32.DestroyIcon(_big);
            _small = _big = IntPtr.Zero;
        }

        public void Dispose()
        {
            Follow(null);
            Win32.SendMessage(_hwnd, Win32.WmSetIcon, Win32.IconSmall, IntPtr.Zero);
            Win32.SendMessage(_hwnd, Win32.WmSetIcon, Win32.IconBig, IntPtr.Zero);
            Destroy();
        }
    }
}
