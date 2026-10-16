using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;

namespace AgentIsland.WinUI.Shell;

/// Debug films of the app's own popup windows (menus, drop-downs): every
/// visible popup of this process, printed with PrintWindow and laid out
/// where it sits on screen, one frame per step of a moment.
internal static class PopupPrint
{
    private const string PopupClass = "Microsoft.UI.Content.PopupWindowSiteBridge";

    /// The popups as they are now: each one's pixels and screen rectangle.
    public static List<(byte[] Pixels, int Left, int Top, int Width, int Height)> Capture()
    {
        var shots = new List<(byte[], int, int, int, int)>();
        var pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;
            var name = new StringBuilder(128);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != PopupClass || !GetWindowRect(hwnd, out var rect)) return true;
            if (WindowPrint.Client(hwnd) is { } shot) shots.Add((shot.Pixels, rect.Left, rect.Top, shot.Width, shot.Height));
            return true;
        }, IntPtr.Zero);
        return shots;
    }

    /// One moment's frames, stepped by hand: Pixel every tick (mid-tick),
    /// Classic every 33 ms, until a frame past its end. `capture` returns
    /// what to print for a frame; by default every popup of the app.
    public static async Task FilmAsync(string path, UiMoment moment, bool pixel,
        Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>>? capture = null)
    {
        var step = pixel ? Motion.TickMs : 1000.0 / 30;
        var count = (int)Math.Ceiling(moment.DurationMs / step - 1e-6) + 2;
        var frames = new List<(string, List<(byte[], int, int, int, int)>)>();
        for (var k = 0; k < count; k++)
        {
            var ms = pixel ? k * step + step / 2 : k * step;
            if (k > 0) moment.Step(ms);
            await Task.Delay(150);
            // PrintWindow waits on the UI thread to compose the popup.
            frames.Add((pixel ? $"t={k}" : $"{ms:F0} ms", await Task.Run(capture ?? Capture)));
        }
        await SaveStrip(path, frames);
    }

    /// Moments that follow or overlap one another (a page switch, then a
    /// scroll, then an expander) on the hand clock: `start` sets them off,
    /// then every frame moves the clock a tick (Pixel, mid-tick) or 33 ms
    /// (Classic), until every moment has landed, and a frame more.
    public static async Task FilmSequenceAsync(string path, Action start, bool pixel,
        Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> capture, double limitMs = 3000, bool before = false)
    {
        UiMoment.ResetClock();
        var frames = new List<(string, List<(byte[], int, int, int, int)>)>();
        // A change that lands at once shows against the frame before it.
        if (before) frames.Add(("before", await Task.Run(capture)));
        start();
        // A start that goes through the dispatcher (a store change) begins a moment or two ticks on.
        for (var i = 0; i < 20 && !UiMoment.AnyPending; i++) await Task.Delay(50);
        var step = pixel ? Motion.TickMs : 1000.0 / 30;
        var settled = 0;
        for (var k = 0; settled < 2 && k * step <= limitMs; k++)
        {
            var ms = k == 0 ? 0 : pixel ? k * step + step / 2 : k * step;
            if (k > 0) UiMoment.Advance(ms);
            await Task.Delay(150);
            frames.Add((pixel ? $"t={k}" : $"{ms:F0} ms", await Task.Run(capture)));
            if (!UiMoment.AnyPending) settled++;
        }
        await SaveStrip(path, frames);
    }

    /// The moment that starts after `before`, waiting up to three seconds.
    public static async Task<UiMoment?> NextMoment(UiMoment? before)
    {
        for (var i = 0; i < 60; i++)
        {
            if (UiMoment.Latest is { } latest && latest != before) return latest;
            await Task.Delay(50);
        }
        return null;
    }

    /// Frames side by side, eight to a row, each the union of every frame's
    /// popups so they stay in place from frame to frame.
    public static async Task SaveStrip(string path, IReadOnlyList<(string Label, List<(byte[] Pixels, int Left, int Top, int Width, int Height)> Popups)> frames)
    {
        var all = frames.SelectMany(f => f.Popups).ToList();
        if (all.Count == 0) return;
        int left = all.Min(p => p.Left) - 12, top = all.Min(p => p.Top) - 12;
        int width = all.Max(p => p.Left + p.Width) + 12 - left, height = all.Max(p => p.Top + p.Height) + 12 - top;
        const int perRow = 8, label = 24, gap = 6;
        var rows = (frames.Count + perRow - 1) / perRow;
        var device = CanvasDevice.GetSharedDevice();
        using var strip = new CanvasRenderTarget(device, gap + Math.Min(frames.Count, perRow) * (width + gap), gap + rows * (height + label + gap), 96);
        using (var ds = strip.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(255, 0x80, 0x80, 0x80));
            var text = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 16 };
            var ground = Brand.IsDark() ? Windows.UI.Color.FromArgb(255, 0x1E, 0x1F, 0x24) : Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF0);
            for (var k = 0; k < frames.Count; k++)
            {
                float x = gap + k % perRow * (width + gap), y = gap + k / perRow * (height + label + gap);
                ds.FillRectangle(x, y, width, height, ground);
                foreach (var popup in frames[k].Popups)
                {
                    using var bitmap = CanvasBitmap.CreateFromBytes(device, popup.Pixels, popup.Width, popup.Height,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                    ds.DrawImage(bitmap, x + popup.Left - left, y + popup.Top - top);
                }
                ds.DrawText(frames[k].Label, x + 4, y + height + 1, Windows.UI.Color.FromArgb(255, 255, 255, 255), text);
            }
        }
        await strip.SaveAsync(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}
