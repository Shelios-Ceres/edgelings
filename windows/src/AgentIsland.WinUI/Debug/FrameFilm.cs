using AgentIsland.WinUI.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

/// AGENTISLAND_DEBUG_FRAME_FILM=<dir>: Classic's window frame drawn by the
/// app inside an ordinary window, over stripes of two known greys, so the
/// corners' antialiasing and the shadow's fall-off read as what they are;
/// printed from the window itself, never from the desktop. One still per
/// state (restored, maximized and snapped; active and inactive), each also
/// cut to the top-right and bottom-left corners at 4x, and the caption
/// buttons at rest, hover and pressed. Light or dark follows
/// AGENTISLAND_DEBUG_THEME.
internal sealed class FrameFilm : Window
{
    private readonly string _directory;
    private readonly Grid _stage = new();
    private readonly IntPtr _hwnd;

    public FrameFilm(string directory)
    {
        _directory = directory;
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Frame film";
        Directory.CreateDirectory(directory);
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1000 * scale), (int)(760 * scale)));
        Content = _stage;
        // The app quits only when told (App), and this run has no tray to tell it.
        Closed += (_, _) => Application.Current.Exit();
        _stage.Loaded += async (_, _) =>
        {
            try
            {
                await RunAsync();
            }
            catch (Exception error)
            {
                DebugTrace.Write("frame film failed: " + error);
            }
            DebugTrace.Write("frame film saved in " + directory);
            Close();
        };
        Activate();
    }

    private async Task RunAsync()
    {
        var dark = Brand.IsDark();
        var mode = dark ? "dark" : "light";
        var configs = new (string Name, bool Square, bool Maximized, bool Active)[]
        {
            ("restored-active", false, false, true),
            ("restored-inactive", false, false, false),
            ("snapped-active", true, false, true),
            ("maximized-active", true, true, true),
        };
        foreach (var (name, square, maximized, active) in configs)
        {
            var frame = Build(out var layout, active);
            frame.ShowAs(square, maximized, active);
            await Task.Delay(900);
            await Save($"frame-{mode}-{name}", layout);
        }
        foreach (var state in new[] { "rest", "hover", "pressed" })
        {
            var frame = Build(out var layout);
            frame.ShowAs(false, false, true);
            foreach (var kind in new[] { NonClientRegionKind.Minimize, NonClientRegionKind.Maximize, NonClientRegionKind.Close }) frame.ShowButton(kind, state);
            await Task.Delay(900);
            await Save($"frame-{mode}-buttons-{state}", layout, buttonsOnly: true);
        }
        // Each cell on its own at hover and pressed, with the others at rest.
        foreach (var kind in new[] { NonClientRegionKind.Minimize, NonClientRegionKind.Maximize, NonClientRegionKind.Close })
        {
            foreach (var state in new[] { "hover", "pressed" })
            {
                var frame = Build(out var layout);
                frame.ShowAs(false, false, true);
                frame.ShowButton(kind, state);
                await Task.Delay(700);
                await Save($"frame-{mode}-{kind.ToString().ToLowerInvariant()}-{state}", layout, buttonsOnly: true);
            }
        }
    }

    /// A frame around a placeholder page, on a striped stage.
    private ClassicFrame Build(out FrameworkElement layout, bool active = true)
    {
        var dark = Brand.IsDark();
        _stage.Children.Clear();
        _stage.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8C, 0x90, 0x98));
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        // Stripes: 16-DIP squares of a second grey.
        var pattern = new Grid();
        var rows = (int)Math.Ceiling(760.0 / 16);
        var cols = (int)Math.Ceiling(1000.0 / 16);
        var tiles = new Canvas { Width = 1000, Height = 760 };
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                if ((r + c) % 2 == 0) continue;
                var tile = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7A, 0x7E, 0x86)) };
                Canvas.SetLeft(tile, c * 16);
                Canvas.SetTop(tile, r * 16);
                tiles.Children.Add(tile);
            }
        }
        pattern.Children.Add(tiles);
        _stage.Children.Add(pattern);

        var frame = new ClassicFrame(this, visualOnly: true);
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ClassicFrame.TitleHeight) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new Grid { Padding = new Thickness(14, 0, 0, 0) };
        // The logo, then the title, as the real window's bar has them.
        bar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "agentisland_logo_small.png"))),
                    Width = 16,
                    Height = 16,
                },
                new TextBlock
                {
                    Text = "Edgelings — Settings",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ClassicTokens.Brush(active ? "Text" : "Text3", dark),
                },
            },
        });
        bar.Children.Add(frame.CreateButtons());
        content.Children.Add(bar);
        // A page on the layer, its top-left corner rounded into the nav pane.
        var layer = new Border
        {
            Margin = new Thickness(200, 0, 0, 0),
            Background = ClassicTokens.Brush("Layer", dark),
            BorderBrush = ClassicTokens.Brush("LineSoft", dark),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(16, 0, 0, 0),
        };
        Grid.SetRow(layer, 1);
        content.Children.Add(layer);
        var host = (FrameworkElement)frame.Wrap(content);
        host.Width = 760 + ClassicFrame.Room.Left + ClassicFrame.Room.Right;
        host.Height = 520 + ClassicFrame.Room.Top + ClassicFrame.Room.Bottom;
        host.HorizontalAlignment = HorizontalAlignment.Center;
        host.VerticalAlignment = VerticalAlignment.Center;
        _stage.Children.Add(host);
        layout = host;
        return frame;
    }

    /// The window's print, the whole stage and the frame's two corners at 4x.
    private async Task Save(string name, FrameworkElement host, bool buttonsOnly = false)
    {
        if (WindowPrint.Client(_hwnd) is not { } shot) return;
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        var transform = host.TransformToVisual(_stage).TransformPoint(new Windows.Foundation.Point(0, 0));
        int x = (int)Math.Round(transform.X * scale), y = (int)Math.Round(transform.Y * scale);
        int w = (int)Math.Round(host.ActualWidth * scale), h = (int)Math.Round(host.ActualHeight * scale);
        if (buttonsOnly)
        {
            // The title bar's right end.
            var cw = (int)(260 * scale);
            await Png(Path.Combine(_directory, name + ".png"), Crop(shot.Pixels, shot.Width, x + w - cw, y, cw, (int)(60 * scale)), cw, (int)(60 * scale), 3);
            return;
        }
        await Png(Path.Combine(_directory, name + ".png"), Crop(shot.Pixels, shot.Width, x, y, w, h), w, h, 1);
        var corner = (int)(60 * scale);
        await Png(Path.Combine(_directory, name + "-top-right.png"), Crop(shot.Pixels, shot.Width, x + w - corner, y, corner, corner), corner, corner, 4);
        await Png(Path.Combine(_directory, name + "-bottom-left.png"), Crop(shot.Pixels, shot.Width, x, y + h - corner, corner, corner), corner, corner, 4);
    }

    private static byte[] Crop(byte[] pixels, int stride, int x, int y, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (var row = 0; row < h; row++) Buffer.BlockCopy(pixels, ((y + row) * stride + x) * 4, output, row * w * 4, w * 4);
        return output;
    }

    /// Opaque pixels, enlarged by whole-number nearest-neighbour steps.
    private static async Task Png(string path, byte[] bgra, int w, int h, int zoom)
    {
        var big = new byte[w * zoom * h * zoom * 4];
        for (var y = 0; y < h * zoom; y++)
        {
            for (var x = 0; x < w * zoom; x++)
            {
                var src = ((y / zoom) * w + x / zoom) * 4;
                var dst = (y * w * zoom + x) * 4;
                big[dst] = bgra[src];
                big[dst + 1] = bgra[src + 1];
                big[dst + 2] = bgra[src + 2];
                big[dst + 3] = 255;
            }
        }
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)(w * zoom), (uint)(h * zoom), 144, 144, big);
        await encoder.FlushAsync();
        stream.Seek(0);
        var data = new byte[stream.Size];
        await stream.ReadAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(data), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, data);
    }
}
