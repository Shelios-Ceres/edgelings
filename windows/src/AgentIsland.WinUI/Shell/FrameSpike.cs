using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using AgentIsland.WinUI.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

/// AGENTISLAND_DEBUG_FRAME_SPIKE=1: Classic's window frame in a bare window,
/// to check its mechanics before the Settings window wears it. It logs what
/// it can prove without the pointer (styles, the hit-test codes the window
/// answers at a list of points, GDI and USER object counts) to
/// AGENTISLAND_DEBUG_TRACE, and saves a render of its content.
internal sealed class FrameSpike : Window
{
    private readonly ClassicFrame _frame;
    private readonly TextBlock _status = new() { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
    private readonly IntPtr _hwnd;

    public FrameSpike()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Frame spike";
        var dark = Brand.IsDark();
        _frame = new ClassicFrame(this);
        var bar = new Grid { Height = ClassicFrame.TitleHeight, VerticalAlignment = VerticalAlignment.Top };
        bar.Children.Add(_frame.CreateButtons());
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ClassicFrame.TitleHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock { Text = "Frame spike", Margin = new Thickness(14, 7, 0, 0) });
        root.Children.Add(bar);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        Content = _frame.Wrap(root);
        var scale = _frame.Scale;
        var margin = ClassicFrame.Room;
        AppWindow.Resize(new SizeInt32(
            (int)((700 + margin.Left + margin.Right) * scale), (int)((480 + margin.Top + margin.Bottom) * scale)));
        Activate();
        ((FrameworkElement)Content).Loaded += async (_, _) =>
        {
            await Task.Delay(1500);
            _frame.SetRegions();
            Probe();
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SNAPSHOT") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                await MaximizeProbeAsync();
                var target = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await target.RenderAsync((UIElement)Content);
                var xaml = (await target.GetPixelsAsync()).ToArray();
                await SaveOverAsync(Path.Combine(directory, "frame-xaml.png"), xaml, target.PixelWidth, target.PixelHeight);
                if (WindowPrint.Client(_hwnd) is { } shot)
                {
                    DebugTrace.Write($"window print {shot.Width}x{shot.Height}, alpha present: {AlphaSeen(shot.Pixels)}");
                    await SaveOverAsync(Path.Combine(directory, "frame-print.png"), shot.Pixels, shot.Width, shot.Height);
                }
            }
        };
    }

    /// Maximizes the spike window, logs where its rectangle, its client and
    /// the monitor's work area lie, saves a print, and restores it.
    private async Task MaximizeProbeAsync()
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter) return;
        presenter.Maximize();
        await Task.Delay(1500);
        Win32.GetWindowRect(_hwnd, out var window);
        Win32.GetClientRect(_hwnd, out var client);
        var monitor = Win32.MonitorFromWindow(_hwnd, Win32.MonitorDefaultToNearest);
        Win32.TryGetWorkArea(monitor, out var work);
        var info = new System.Text.StringBuilder();
        info.AppendLine($"maximized: window {window.Left},{window.Top} {window.Right - window.Left}x{window.Bottom - window.Top}; client {client.Right}x{client.Bottom}; work {work.Left},{work.Top} {work.Right - work.Left}x{work.Bottom - work.Top}");
        var frame = new Win32.RECT();
        Win32.DwmGetExtendedFrameBounds(_hwnd, out frame);
        info.AppendLine($"extended frame bounds {frame.Left},{frame.Top} {frame.Right - frame.Left}x{frame.Bottom - frame.Top}; zoomed {Win32.IsZoomed(_hwnd)}; arranged {Win32.IsWindowArranged(_hwnd)}");
        DebugTrace.Write(info.ToString());
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SNAPSHOT") is { Length: > 0 } directory && WindowPrint.Client(_hwnd) is { } shot)
            await SaveOverAsync(Path.Combine(directory, "frame-maximized-print.png"), shot.Pixels, shot.Width, shot.Height);
        presenter.Restore();
        await Task.Delay(800);
    }

    private static bool AlphaSeen(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] is > 0 and < 255) return true;
        }
        return false;
    }

    /// The pixels laid over stripes of two known greys, so transparent corners
    /// and the shadow's fall-off show as what they are.
    private static async Task SaveOverAsync(string path, byte[] bgra, int width, int height)
    {
        var pixels = (byte[])bgra.Clone();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var back = ((x / 16) + (y / 16)) % 2 == 0 ? (byte)0x9C : (byte)0x7C;
                var a = pixels[i + 3];
                for (var c = 0; c < 3; c++) pixels[i + c] = (byte)(pixels[i + c] + back * (255 - a) / 255);
                pixels[i + 3] = 255;
            }
        }
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 144, 144, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var data = new byte[stream.Size];
        await stream.ReadAsync(data.AsBuffer(), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, data);
    }

    private static string Flags(long value, params (string Name, long Bit)[] bits) =>
        string.Join('|', bits.Where(b => (value & b.Bit) != 0).Select(b => b.Name));

    private void Probe()
    {
        var log = new StringBuilder();
        var style = (long)Win32.GetWindowLongPtr(_hwnd, -16);
        var ex = (long)Win32.GetWindowLongPtr(_hwnd, -20);
        log.AppendLine("style " + Flags(style, ("THICKFRAME", 0x40000), ("CAPTION", 0xC00000), ("SYSMENU", 0x80000), ("MAXBOX", 0x10000), ("MINBOX", 0x20000), ("POPUP", 0x80000000), ("OVERLAPPED_BORDER", 0x800000)));
        log.AppendLine("exstyle " + Flags(ex, ("LAYERED", 0x80000), ("NOREDIRECTIONBITMAP", 0x200000), ("TOOLWINDOW", 0x80), ("APPWINDOW", 0x40000), ("WINDOWEDGE", 0x100), ("CLIENTEDGE", 0x200)));
        Win32.GetWindowRect(_hwnd, out var rect);
        Win32.GetClientRect(_hwnd, out var client);
        log.AppendLine($"window {rect.Right - rect.Left}x{rect.Bottom - rect.Top}, client {client.Right}x{client.Bottom}, scale {_frame.Scale}");
        var children = new List<IntPtr>();
        Win32.EnumChildWindows(_hwnd, (child, _) =>
        {
            children.Add(child);
            return true;
        }, IntPtr.Zero);
        log.AppendLine("children " + string.Join(", ", children.Select(c => Win32.ClassOf(c))));
        var scale = _frame.Scale;
        var margin = ClassicFrame.Room;
        double W = client.Right / scale, H = client.Bottom / scale;
        var probes = new (string Name, double X, double Y)[]
        {
            ("top-left corner", 2, 2), ("top edge", W / 2, 3), ("top-right corner", W - 2, 2),
            ("left edge", 3, H / 2), ("right edge", W - 3, H / 2),
            ("bottom-left corner", 2, H - 2), ("bottom edge", W / 2, H - 3), ("bottom-right corner", W - 2, H - 2),
            ("inside body, top-left", margin.Left + 3, margin.Top + 3),
            ("caption", W / 2, margin.Top + 16),
            ("minimize cell", W - margin.Right - 2.5 * ClassicFrame.ButtonWidth, margin.Top + 16),
            ("maximize cell", W - margin.Right - 1.5 * ClassicFrame.ButtonWidth, margin.Top + 16),
            ("close cell", W - margin.Right - 0.5 * ClassicFrame.ButtonWidth, margin.Top + 16),
            ("body centre", W / 2, H / 2),
            ("body bottom edge", W / 2, H - margin.Bottom - 1),
        };
        foreach (var (name, x, y) in probes)
        {
            var sx = rect.Left + (int)(x * scale);
            var sy = rect.Top + (int)(y * scale);
            var lParam = new IntPtr((sy << 16) | (sx & 0xFFFF));
            var hit = children.Select(c => (int)Win32.SendMessage(c, 0x84, IntPtr.Zero, lParam)).ToArray();
            var top = (int)Win32.SendMessage(_hwnd, 0x84, IntPtr.Zero, lParam);
            log.AppendLine($"hit {name,-26} child {string.Join(",", hit.Select(Hit))}  window {Hit(top)}");
        }
        foreach (var e in typeof(Microsoft.UI.Input.InputNonClientPointerSource).GetEvents())
        {
            var args = e.EventHandlerType!.GetGenericArguments();
            log.AppendLine($"event {e.Name}: {string.Join(",", args.Select(t => t.Name + "{" + string.Join(",", t.GetProperties().Select(pr => pr.Name + ":" + pr.PropertyType.Name)) + "}"))}");
        }
        log.AppendLine($"objects: GDI {Win32.GuiObjects(0)}, USER {Win32.GuiObjects(1)}");
        _status.Text = log.ToString();
        DebugTrace.Write("frame spike\n" + log);
    }

    private static string Hit(int code) => code switch
    {
        0 => "HTNOWHERE", 1 => "HTCLIENT", 2 => "HTCAPTION", 8 => "HTMINBUTTON", 9 => "HTMAXBUTTON", 10 => "HTLEFT", 11 => "HTRIGHT", 12 => "HTTOP",
        13 => "HTTOPLEFT", 14 => "HTTOPRIGHT", 15 => "HTBOTTOM", 16 => "HTBOTTOMLEFT", 17 => "HTBOTTOMRIGHT", 20 => "HTCLOSE", -1 => "HTTRANSPARENT",
        _ => code.ToString(),
    };
}
