using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT.Interop;

namespace AgentIsland.WinUI.Settings;

/// Debug films of the window's motion, stepped by hand and printed frame by
/// frame into strips.
internal sealed partial class SettingsWindow
{
    /// AGENTISLAND_DEBUG_CONTROL_FILM=<dir>: the kit controls' popup moments:
    /// a tooltip (M14), a drop-down's open (M13, Pixel; Classic keeps
    /// Fluent's own) and a dialog's open and close (M15).
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

    /// AGENTISLAND_DEBUG_SETTINGS_FILM=<dir>: Settings' own moments, stepped
    /// by hand on the shared clock and printed frame by frame into strips: a
    /// page switch (M8); an agent's card opening and closing (M9); the last
    /// agent on this computer hidden and shown again (M10); that agent opened
    /// from the drawer while the window shows another page, scrolled to from
    /// the top of a long page, and a hidden agent opened (M19); a meter
    /// counting (M16); a row moved by its arrows (M11); a switch flipping
    /// both ways and, under Classic, a button pressed (M12); and the first
    /// agent hidden, which opens the hidden section (M10). The run hides and
    /// moves agents, and flips a switch: run it on settings you restore.
    /// AGENTISLAND_DEBUG_FILM_ONLY=<names>: only the moments whose strips
    /// start with one of these comma-separated names.
    public static async Task FilmSettingsAsync(string directory)
    {
        Open();
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        var pixel = Looks.Pixel;
        var name = (pixel ? "pixel" : "classic") + "-" + (Brand.IsDark() ? "dark" : "light") + (Motion.ReducedMotion ? "-reduced" : "");
        var only = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FILM_ONLY")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Wanted(string moment) => only is null || only.Any(moment.StartsWith);
        await Task.Delay(1500);
        UiMoment.Manual = true;
        // The live reports would set the meters back while they count.
        window._tick.Stop();
        try
        {
            var size = window.Content is FrameworkElement content ? new Size(content.ActualWidth, content.ActualHeight) : new Size(980, 720);
            var whole = Region(window, new Rect(0, 0, size.Width, size.Height));
            var page = Region(window, new Rect(200, 0, size.Width - 200, size.Height));
            Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> Around(FrameworkElement element, double room)
            {
                var at = element.TransformToVisual(window.Content).TransformPoint(default);
                var top = Math.Max(0, at.Y - room);
                return Region(window, new Rect(200, top, size.Width - 200, Math.Min(size.Height - top, element.ActualHeight + 2 * room)));
            }
            async Task Settle()
            {
                UiMoment.Finish();
                await Task.Delay(600);
            }
            async Task Go(Page target)
            {
                window.SelectPage(target);
                await Settle();
            }
            async Task Film(string moment, Action start, Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> capture)
            {
                if (!Wanted(moment)) return;
                await PopupPrint.FilmSequenceAsync(Path.Combine(directory, $"{moment}-{name}.png"), start, pixel, capture);
                await Settle();
            }
            var visibility = ProviderVisibilityStore.Shared;

            await Go(Page.Agents);
            await Film("m8-switch", () => window.SelectPage(Page.Drawer), whole);

            await Go(Page.Agents);
            if (window._agents.Values.FirstOrDefault()?.Expander is { } first)
            {
                first.IsExpanded = false;
                await Settle();
                var top = Math.Max(0, first.TransformToVisual(window.Content).TransformPoint(default).Y - 12);
                var card = Region(window, new Rect(200, top, size.Width - 200, size.Height - top));
                await Film("m9-open", () => first.IsExpanded = true, card);
                await Film("m9-close", () => first.IsExpanded = false, card);
            }

            var (detected, _, hiddenAgents) = AgentGroups();
            if (detected.Count > 1)
            {
                var agent = detected[^1];
                window._scroll.ChangeView(null, window._scroll.ScrollableHeight, null, disableAnimation: true);
                await Settle();
                await Film("m10-hide", () => visibility.SetShown(agent, false), page);
                if (window._hiddenList is { } hidden) hidden.IsExpanded = true;
                window._scroll.ChangeView(null, window._scroll.ScrollableHeight, null, disableAnimation: true);
                await Settle();
                await Film("m10-show", () => visibility.SetShown(agent, true), page);

                await Go(Page.Drawer);
                await Film("m19-open", () => Open(agent), whole);

                // From the top of a long page: the first cards open, so the last is out of view.
                await Go(Page.Agents);
                foreach (var section in window._agents.Values.Take(2)) section.Expander.IsExpanded = true;
                await Settle();
                window._scroll.ChangeView(null, 0, null, disableAnimation: true);
                await Settle();
                await Film("m19-scroll", () => Open(agent), whole);
            }
            if (hiddenAgents.Count > 0)
            {
                await Go(Page.Drawer);
                await Film("m19-hidden", () => Open(hiddenAgents[0]), whole);
            }

            // M16: the first agent's first meter counts up past 90 %, then back.
            await Go(Page.Agents);
            if (window._agents.Values.FirstOrDefault() is { } counted)
            {
                counted.Expander.IsExpanded = true;
                await Settle();
                window._scroll.ChangeView(null, 0, null, disableAnimation: true);
                await Settle();
                await Film("m16-count-up", () => counted.DebugCount(0.95), page);
                await Film("m16-count-down", () => counted.DebugCount(0.3), page);
            }

            // M11 and M12 on the Drawer page: the Order list's first row down
            // and back up; the first switch off and on; a button pressed.
            await Go(Page.Drawer);
            var body = (DependencyObject)window._scroll.Content;
            if (Below<ListView>(body, list => AutomationProperties.GetAutomationId(list) == "Settings.Drawer.Order") is { } order)
            {
                order.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.3 });
                await Settle();
                var rows = Around(order, 16);
                // The first row's down arrow, then its new row's up arrow.
                Button? Arrow(string end) => Below<Button>(order, arrow => arrow.IsEnabled && AutomationProperties.GetAutomationId(arrow).EndsWith(end));
                if (Arrow(".Down") is { } down) await Film("m11-down", () => new ButtonAutomationPeer(down).Invoke(), rows);
                await Task.Delay(600);
                if (Arrow(".Up") is { } up) await Film("m11-up", () => new ButtonAutomationPeer(up).Invoke(), rows);
            }
            window._scroll.ChangeView(null, 0, null, disableAnimation: true);
            await Settle();
            if (Below<ToggleSwitch>((DependencyObject)window._scroll.Content) is { } toggle)
            {
                var around = Around(toggle, 24);
                foreach (var moment in new[] { "m12-flip-a", "m12-flip-b" })
                {
                    if (Wanted(moment)) await FilmFlipAsync(Path.Combine(directory, $"{moment}-{name}.png"), toggle, pixel, around);
                    await Task.Delay(600);
                }
            }
            // Classic's press, on the Usage page's Scan now: pressed by hand, never clicked.
            if (!pixel)
            {
                await Go(Page.Usage);
                if (Below<Button>((DependencyObject)window._scroll.Content, candidate => candidate.IsEnabled) is { } button)
                {
                    var around = Around(button, 24);
                    await Film("m12-press", () => ControlMotion.Press(button, down: true), around);
                    await Film("m12-release", () => ControlMotion.Press(button, down: false), around);
                }
            }

            // Last, as it shows every hidden agent: hiding one then opens the
            // section, in a window tall enough for the whole page.
            if (detected.Count > 1 && hiddenAgents.Count > 0 && Wanted("m10-first"))
            {
                await Go(Page.Agents);
                foreach (var hidden in hiddenAgents) visibility.SetShown(hidden, true);
                await Task.Delay(800);
                UiMoment.Finish();
                var normal = window.AppWindow.Size;
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(normal.Width, normal.Height * 8 / 5));
                window._scroll.ChangeView(null, 0, null, disableAnimation: true);
                await Settle();
                var tall = window.Content is FrameworkElement grown ? new Size(grown.ActualWidth, grown.ActualHeight) : size;
                await Film("m10-first", () => visibility.SetShown(detected[^1], false), Region(window, new Rect(200, 0, tall.Width - 200, tall.Height)));
                window.AppWindow.Resize(normal);
            }
        }
        catch (Exception error)
        {
            DebugTrace.Write("settings film failed: " + error);
        }
        finally
        {
            UiMoment.Manual = false;
            window._tick.Start();
        }
        DebugTrace.Write("settings film saved in " + directory);
    }

    /// A switch's flip (M12) is a storyboard in its kit's template: paused as
    /// it starts and moved by hand, Pixel every tick (mid-tick), Classic every
    /// 33 ms, past its end.
    private static async Task FilmFlipAsync(string path, ToggleSwitch toggle, bool pixel,
        Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> capture)
    {
        var from = toggle.IsOn ? "On" : "Off";
        var to = toggle.IsOn ? "Off" : "On";
        var flip = VisualTreeHelper.GetChildrenCount(toggle) > 0 && VisualTreeHelper.GetChild(toggle, 0) is FrameworkElement root
            ? VisualStateManager.GetVisualStateGroups(root).FirstOrDefault(group => group.Name == "ToggleStates")?.Transitions
                .FirstOrDefault(transition => transition.From == from && transition.To == to)?.Storyboard
            : null;
        toggle.IsOn = !toggle.IsOn;
        flip?.Pause();
        var step = pixel ? Motion.TickMs : 1000.0 / 30;
        var length = pixel ? 3 * Motion.TickMs : 367;
        var frames = new List<(string, List<(byte[], int, int, int, int)>)>();
        for (var k = 0; k * step <= length + step; k++)
        {
            var ms = pixel && k > 0 ? k * step + step / 2 : k * step;
            flip?.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(Math.Min(ms, length)));
            await Task.Delay(150);
            frames.Add((pixel ? $"t={k}" : $"{ms:F0} ms", await Task.Run(capture)));
        }
        flip?.Resume();
        await PopupPrint.SaveStrip(path, frames);
    }

    /// The window's print, cut to the dialog's place (centred, 450 DIP wide)
    /// and 48 DIP of smoke round it; measured here, it may still be a ghost.
    private static Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> Around(SettingsWindow window, ContentDialog dialog)
    {
        var size = window.Content is FrameworkElement content ? new Size(content.ActualWidth, content.ActualHeight) : new Size(980, 720);
        return Region(window, new Rect(size.Width / 2 - 225 - 48, size.Height / 2 - 180, 450 + 96, 360));
    }

    /// The window's print, cut to a rectangle in DIP.
    private static Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> Region(SettingsWindow window, Rect rect)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var scale = window.Content.XamlRoot?.RasterizationScale ?? 1;
        int left = Math.Max(0, (int)(rect.X * scale)), top = Math.Max(0, (int)(rect.Y * scale));
        int width = (int)(rect.Width * scale), height = (int)(rect.Height * scale);
        return () =>
        {
            if (WindowPrint.Client(hwnd) is not { } shot) return new();
            var w = Math.Min(width, shot.Width - left);
            var h = Math.Min(height, shot.Height - top);
            if (w <= 0 || h <= 0) return new();
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                Buffer.BlockCopy(shot.Pixels, ((top + y) * shot.Width + left) * 4, pixels, y * w * 4, w * 4);
            }
            return new() { (pixels, left, top, w, h) };
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
