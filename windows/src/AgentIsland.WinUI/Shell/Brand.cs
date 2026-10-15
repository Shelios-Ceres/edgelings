using System.Runtime.InteropServices.WindowsRuntime;
using AgentIsland.Model;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// Brand colours and marks for WinUI surfaces.
internal static class Brand
{
    public static Color Rgb(uint rgb, byte alpha = 255) =>
        Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static SolidColorBrush Brush(uint rgb, byte alpha = 255) => new(Rgb(rgb, alpha));

    /// The brand colour that reads on the current surface: Grok's steel and
    /// Cursor's bone are near-white, so light themes get their ink.
    public static Color OnSurface(DisplayProvider provider, bool dark) => provider switch
    {
        DisplayProvider.Grok => dark ? Rgb(0xE8E8E8) : Rgb(0x141414),
        DisplayProvider.Cursor => dark ? Rgb(0xF5F3EE) : Rgb(0x26251E),
        DisplayProvider.Codex => dark ? Rgb(0xF2F2F2) : Rgb(0x141414),
        _ => Rgb(ProviderIdentity.Accent(provider)),
    };

    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();

    /// AGENTISLAND_DEBUG_THEME=light|dark: verification renders in a theme
    /// other than Windows'.
    public static bool? ForcedDark { get; } = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_THEME") switch
    {
        "light" => false,
        "dark" => true,
        _ => null,
    };

    /// The system app theme right now (it can change while the app runs).
    public static bool IsDark()
    {
        if (ForcedDark is { } forced) return forced;
        var background = Settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        return background.R + background.G + background.B < 3 * 128;
    }

    /// A character colour that stays visible on the card: black and the
    /// near-black browns lift to a light ink on dark surfaces.
    public static uint Readable(uint swatch, bool dark)
    {
        var luma = 0.2126 * (swatch >> 16 & 0xFF) + 0.7152 * (swatch >> 8 & 0xFF) + 0.0722 * (swatch & 0xFF);
        return dark && luma < 48 ? 0xD6D6D6 : swatch;
    }

    private static readonly Dictionary<(DisplayProvider, Color), ImageSource> Cache = new();

    /// The provider's mark. Antigravity and Kimi Code keep their own colours;
    /// the single-colour marks are tinted for the surface.
    public static async Task<ImageSource?> MarkAsync(DisplayProvider provider, bool dark)
    {
        var file = provider switch
        {
            DisplayProvider.Claude => "mark-claude.png",
            DisplayProvider.Codex => "mark-openai.png",
            DisplayProvider.Antigravity => "mark-antigravity.png",
            DisplayProvider.Grok => "mark-grok.png",
            DisplayProvider.Cursor => "mark-cursor.png",
            DisplayProvider.Kimi => "mark-kimi.png",
            DisplayProvider.DeepSeek => "mark-deepseek.png",
            _ => null,
        };
        if (file is null) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        if (!File.Exists(path)) return null;
        if (provider is DisplayProvider.Antigravity or DisplayProvider.Kimi)
        {
            return new BitmapImage(new Uri(path));
        }
        var tint = OnSurface(provider, dark);
        if (Cache.TryGetValue((provider, tint), out var cached)) return cached;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                pixels[i] = (byte)(tint.B * alpha / 255);
                pixels[i + 1] = (byte)(tint.G * alpha / 255);
                pixels[i + 2] = (byte)(tint.R * alpha / 255);
            }
            var bitmap = new WriteableBitmap((int)decoder.PixelWidth, (int)decoder.PixelHeight);
            using (var target = bitmap.PixelBuffer.AsStream())
            {
                await target.WriteAsync(pixels);
            }
            bitmap.Invalidate();
            Cache[(provider, tint)] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
