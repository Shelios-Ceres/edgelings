using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AgentIsland.Model;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The real brand marks (macOS ProviderMark): the mac vector PDFs rendered
/// to alpha PNGs, tinted once into a cached bitmap — template rendering
/// without an OpacityMask, which costs an offscreen layer on every frame a
/// page scrolls. Antigravity and Kimi Code keep their own colours:
/// repainting them in a tint would erase the colours that ARE the brand.
public static class ProviderMarks
{
    private static readonly Dictionary<DisplayProvider, BitmapSource?> MaskCache = new();
    private static readonly Dictionary<(DisplayProvider, Color), BitmapSource> TintCache = new();
    private static BitmapImage? _antigravity;

    /// tint: null keeps the brand accent (dark surfaces); light surfaces pass
    /// GrokTheme.Brand so near-white brands stay visible.
    public static UIElement Mark(DisplayProvider provider, double size, double tintOpacity = 0.95, Color? tint = null)
    {
        var color = tint ?? ProviderColors.Accent(provider);
        if (((BitmapSource?)FullColor(provider) ?? Tinted(provider, IslandColors.Alpha(color, tintOpacity))) is { } bitmap)
        {
            var image = new Image
            {
                Source = bitmap,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        // Missing asset: the accent ring macOS falls back to.
        return new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = IslandColors.Brush(IslandColors.Alpha(color, tintOpacity)),
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// Marks whose colours ARE the brand: Antigravity's Google sweep and
    /// Kimi Code's two-tone mascot are never repainted in a tint.
    private static BitmapImage? FullColor(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Antigravity => _antigravity ??= Load("mark-antigravity.png"),
        DisplayProvider.Kimi => _kimi ??= Load("mark-kimi.png"),
        _ => null,
    };

    private static BitmapImage? _kimi;

    /// The mask's alpha carried by one solid colour, frozen and cached per
    /// (provider, colour).
    private static BitmapSource? Tinted(DisplayProvider provider, Color color)
    {
        if (TintCache.TryGetValue((provider, color), out var cached)) return cached;
        if (MaskFor(provider) is not { } mask) return null;
        var straight = new FormatConvertedBitmap(mask, PixelFormats.Bgra32, null, 0);
        var width = straight.PixelWidth;
        var height = straight.PixelHeight;
        var pixels = new byte[width * height * 4];
        straight.CopyPixels(pixels, width * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = (byte)(pixels[i + 3] * color.A / 255);
        }
        var tinted = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        tinted.Freeze();
        TintCache[(provider, color)] = tinted;
        return tinted;
    }

    private static BitmapSource? MaskFor(DisplayProvider provider)
    {
        if (MaskCache.TryGetValue(provider, out var cached)) return cached;
        var name = provider switch
        {
            DisplayProvider.Claude => "mark-claude.png",
            DisplayProvider.Codex => "mark-openai.png",
            DisplayProvider.Grok => "mark-grok.png",
            DisplayProvider.Cursor => "mark-cursor.png",
            DisplayProvider.DeepSeek => "mark-deepseek.png",
            _ => null,
        };
        var bitmap = name is null ? null : Load(name);
        MaskCache[provider] = bitmap;
        return bitmap;
    }

    private static BitmapImage? Load(string name)
    {
        try
        {
            return new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}"));
        }
        catch
        {
            return null;
        }
    }
}
