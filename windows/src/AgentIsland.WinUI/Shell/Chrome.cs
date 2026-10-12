using AgentIsland.WinUI.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

/// The app windows' shared frame: Mica, a tall title bar drawn by the
/// content with the app's mark, centred on the work area.
internal static class Chrome
{
    /// Returns the window's root: the title bar in row 0, content goes in
    /// row 1.
    public static Grid Apply(Window window, string title, double width, double height, double minWidth, double minHeight)
    {
        window.Title = title;
        window.SystemBackdrop = new MicaBackdrop();
        window.ExtendsContentIntoTitleBar = true;
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AgentIsland.ico"));

        var bar = new Grid { Padding = new Thickness(16, 0, 0, 0) };
        bar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image
                {
                    Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "agentisland_logo_small.png"))),
                    Width = 18,
                    Height = 18,
                },
                new TextBlock
                {
                    Text = title,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        window.Content = root;
        window.SetTitleBar(bar);

        var scale = Win32.GetDpiForWindow(WindowNative.GetWindowHandle(window)) / 96.0;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(width * scale), area.Width);
        var h = Math.Min((int)(height * scale), area.Height);
        window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(minWidth * scale);
            presenter.PreferredMinimumHeight = (int)(minHeight * scale);
        }
        return root;
    }

    /// Places content in a root made by Apply.
    public static void SetBody(Grid root, UIElement body)
    {
        Grid.SetRow((FrameworkElement)body, 1);
        root.Children.Add(body);
    }
}
