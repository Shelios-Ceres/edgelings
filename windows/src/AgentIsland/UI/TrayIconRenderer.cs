using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AgentIsland.UI;

/// Draws the system-tray icon: the Agent Island mark alone. The status badge
/// it used to carry was removed at the user's request (2026-10-11); status
/// lives in the drawer and the tray tooltip.
internal static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// The 32px cut of the mark, decoded once from the WPF resource. Copied out of the
    /// stream-backed bitmap because GDI+ requires the source stream to stay
    /// alive otherwise.
    private static readonly Lazy<Bitmap?> Logo = new(() =>
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/agentisland_tray.png"));
            if (resource is null) return null;
            using var stream = resource.Stream;
            using var streamBacked = new Bitmap(stream);
            return new Bitmap(streamBacked);
        }
        catch
        {
            return null;
        }
    });

    public static Icon Render()
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            if (Logo.Value is { } logo)
            {
                g.DrawImage(logo, new Rectangle(0, 0, s, s));
            }
            else
            {
                using var disc = new SolidBrush(Color.FromArgb(235, 0x14, 0x14, 0x18));
                g.FillEllipse(disc, 1, 1, s - 2, s - 2);
            }

        }

        // GetHicon leaks its HICON unless destroyed; clone into an owned Icon,
        // then free the temporary handle.
        var handle = bmp.GetHicon();
        try
        {
            using var shared = Icon.FromHandle(handle);
            return (Icon)shared.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
