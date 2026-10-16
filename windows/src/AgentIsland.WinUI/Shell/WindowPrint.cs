using System.Runtime.InteropServices;

namespace AgentIsland.WinUI.Shell;

/// Debug captures of the app's own windows with PrintWindow, which composes
/// what Windows draws for the window (shadows and transforms included)
/// without reading the screen. Pixels are BGRA, premultiplied, top down.
internal static class WindowPrint
{
    private const uint RenderFullContent = 2;

    /// The window's client area, in physical pixels.
    public static (byte[] Pixels, int Width, int Height)? Client(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var client) || !GetWindowRect(hwnd, out var window)) return null;
        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin)) return null;
        int width = window.Right - window.Left, height = window.Bottom - window.Top;
        int clientWidth = client.Right, clientHeight = client.Bottom;
        if (width <= 0 || height <= 0 || clientWidth <= 0 || clientHeight <= 0) return null;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Size = Marshal.SizeOf<BitmapInfo>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        var bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            var old = SelectObject(memory, bitmap);
            var printed = PrintWindow(hwnd, memory, RenderFullContent);
            SelectObject(memory, old);
            if (!printed) return null;
            var all = new byte[width * height * 4];
            Marshal.Copy(bits, all, 0, all.Length);
            int left = origin.X - window.Left, top = origin.Y - window.Top;
            var pixels = new byte[clientWidth * clientHeight * 4];
            for (var y = 0; y < clientHeight; y++)
            {
                Buffer.BlockCopy(all, ((top + y) * width + left) * 4, pixels, y * clientWidth * 4, clientWidth * 4);
            }
            return (pixels, clientWidth, clientHeight);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
