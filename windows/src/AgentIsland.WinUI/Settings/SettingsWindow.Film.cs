using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT.Interop;

namespace AgentIsland.WinUI.Settings;

/// AGENTISLAND_DEBUG_CONTROL_FILM=<dir>: the kit controls' popup moments,
/// stepped by hand and printed frame by frame into strips: a tooltip (M14),
/// a drop-down's open (M13, Pixel; Classic keeps Fluent's own) and a
/// dialog's open and close (M15).
internal sealed partial class SettingsWindow
{
    public static async Task FilmControlsAsync(string directory)
    {
        Open();
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        var pixel = Looks.Pixel;
        var name = (pixel ? "pixel" : "classic") + "-" + (Brand.IsDark() ? "dark" : "light") + (Motion.ReducedMotion ? "-reduced" : "");
        await Task.Delay(1500);
        UiMoment.Manual = true;
        try
        {
            window.SelectPage(Page.Agents);
            await Task.Delay(800);
            if (Descendants(window._scroll).Select(ToolTipService.GetToolTip).OfType<ToolTip>().FirstOrDefault() is { } tip)
            {
                var before = UiMoment.Latest;
                tip.IsOpen = true;
                if (await PopupPrint.NextMoment(before) is { } moment) await PopupPrint.FilmAsync(Path.Combine(directory, $"m14-tooltip-{name}.png"), moment, pixel);
                tip.IsOpen = false;
            }
            window.SelectPage(Page.Drawer);
            await Task.Delay(800);
            if (Descendants(window._scroll).OfType<ComboBox>().FirstOrDefault() is { } box)
            {
                var before = UiMoment.Latest;
                box.IsDropDownOpen = true;
                if (await PopupPrint.NextMoment(before) is { } moment) await PopupPrint.FilmAsync(Path.Combine(directory, $"m13-open-{name}.png"), moment, pixel);
                box.IsDropDownOpen = false;
                await Task.Delay(400);
            }
            if (window.Content.XamlRoot is { } root)
            {
                var dialog = SettingsUi.Dialog(root);
                dialog.Title = "Save current account?";
                dialog.Content = new TextBlock { Text = "A synthetic dialog for the motion film.", TextWrapping = TextWrapping.Wrap };
                dialog.PrimaryButtonText = "Save";
                dialog.CloseButtonText = "Cancel";
                var before = UiMoment.Latest;
                _ = dialog.ShowAsync();
                if (await PopupPrint.NextMoment(before) is { } open)
                {
                    var capture = Around(window, dialog);
                    await PopupPrint.FilmAsync(Path.Combine(directory, $"m15-open-{name}.png"), open, pixel, capture);
                    before = UiMoment.Latest;
                    dialog.Hide();
                    if (await PopupPrint.NextMoment(before) is { } close) await PopupPrint.FilmAsync(Path.Combine(directory, $"m15-close-{name}.png"), close, pixel, capture);
                }
            }
        }
        catch (Exception error)
        {
            DebugTrace.Write("control film failed: " + error);
        }
        finally
        {
            UiMoment.Manual = false;
        }
        DebugTrace.Write("control film saved in " + directory);
    }

    /// The window's print, cut to the dialog's place (centred, 450 DIP wide)
    /// and 48 DIP of smoke round it; measured here, it may still be a ghost.
    private static Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> Around(SettingsWindow window, ContentDialog dialog)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var scale = window.Content.XamlRoot?.RasterizationScale ?? 1;
        var size = window.Content is FrameworkElement content ? new Size(content.ActualWidth, content.ActualHeight) : new Size(980, 720);
        var rect = new Rect(size.Width / 2 - 225 - 48, size.Height / 2 - 180, 450 + 96, 360);
        int left = Math.Max(0, (int)(rect.X * scale)), top = Math.Max(0, (int)(rect.Y * scale));
        int width = (int)(rect.Width * scale), height = (int)(rect.Height * scale);
        return () =>
        {
            if (WindowPrint.Client(hwnd) is not { } shot) return new();
            width = Math.Min(width, shot.Width - left);
            height = Math.Min(height, shot.Height - top);
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(shot.Pixels, ((top + y) * shot.Width + left) * 4, pixels, y * width * 4, width * 4);
            }
            return new() { (pixels, left, top, width, height) };
        };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
