using AgentIsland.WinUI.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentIsland.WinUI.Shell;

/// Closes a tooltip the pointer opened once the pointer leaves its owner.
/// WinUI keeps an open tooltip while the pointer stays anywhere in the hull
/// of the owner and the tooltip (ToolTipService's safe zone), and a wide
/// tooltip docked at the pointer makes that much of the card around a small
/// switch; moving onto the tooltip's own popup window doesn't even tell the
/// owner the pointer left. So the cursor is read every 100 ms while the tip
/// is open. A tip opened with the pointer elsewhere (keyboard focus, a film)
/// is left to WinUI.
internal static class TipGuard
{
    // The guard running: WinUI shows one tooltip at a time, and a timer
    // nothing holds may be collected before it ticks.
    private static DispatcherQueueTimer? _timer;

    public static void Watch(ToolTip tip, FrameworkElement owner)
    {
        _timer?.Stop();
        _timer = null;
        var over = Over(owner);
        DebugTrace.Write($"tip guard: opened, pointer over its owner: {over}");
        if (over != true) return;
        var timer = _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) =>
        {
            if (tip.IsOpen && owner.IsLoaded && Over(owner) != false) return;
            timer.Stop();
            if (_timer == timer) _timer = null;
            DebugTrace.Write($"tip guard: closing, open {tip.IsOpen}");
            tip.IsOpen = false;
        };
        timer.Start();
    }

    /// Whether the cursor is over the owner, or null when that can't be told.
    private static bool? Over(FrameworkElement owner)
    {
        if (owner.XamlRoot is not { } root || !Win32.GetCursorPos(out var cursor)) return null;
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        var origin = new Win32.POINT();
        if (!Win32.ClientToScreen(hwnd, ref origin)) return null;
        var scale = root.RasterizationScale;
        var bounds = owner.TransformToVisual(null).TransformBounds(new Rect(0, 0, owner.ActualWidth, owner.ActualHeight));
        var x = (cursor.X - origin.X) / scale;
        var y = (cursor.Y - origin.Y) / scale;
        return x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
    }
}
