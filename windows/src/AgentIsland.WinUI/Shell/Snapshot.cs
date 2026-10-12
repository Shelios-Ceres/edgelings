using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentIsland.WinUI.Shell;

/// Renders a window's own content to a PNG for verification, so checking a
/// page never needs a capture of the user's screen.
internal static class Snapshot
{
    /// Transparent pixels (Mica, acrylic) are laid over the theme's base
    /// colour so the image reads like the window.
    public static async Task SaveAsync(UIElement element, string path)
    {
        try
        {
            var target = new RenderTargetBitmap();
            await target.RenderAsync(element);
            var pixels = (await target.GetPixelsAsync()).ToArray();
            var backdrop = Brand.IsDark() ? (byte)0x20 : (byte)0xF3;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var clear = 255 - pixels[i + 3];
                pixels[i] = (byte)(pixels[i] + backdrop * clear / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] + backdrop * clear / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] + backdrop * clear / 255);
                pixels[i + 3] = 255;
            }
            var scale = element.XamlRoot?.RasterizationScale ?? 1;
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)target.PixelWidth, (uint)target.PixelHeight, 96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            await using var file = File.Create(path);
            await stream.AsStreamForRead().CopyToAsync(file);
            DebugTrace.Write($"snapshot {target.PixelWidth}x{target.PixelHeight} -> {path}");
        }
        catch (Exception error)
        {
            DebugTrace.Write("snapshot failed: " + error.Message);
        }
    }
}
