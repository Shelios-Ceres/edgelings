using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// Minimize / maximize-restore / close, embedded in a window's top-right
/// corner — the in-page caption strip used instead of a system title bar
/// (settings window).
public static class CaptionButtons
{
    /// onClose lets a window route its close through its own teardown;
    /// defaults to Window.Close().
    public static UIElement Build(Window window, Action? onClose = null)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        UIElement Make(string glyph, Action click, bool destructive, out TextBlock text)
        {
            var glyphText = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 9.5,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text = glyphText;
            var host = new Border
            {
                Width = 38,
                Height = 28,
                Background = Brushes.Transparent,
                Child = glyphText,
            };
            host.MouseEnter += (_, _) =>
            {
                host.Background = destructive
                    ? IslandColors.Brush(Color.FromRgb(0xC4, 0x2B, 0x1C))
                    : GrokTheme.InkBrush(GrokTheme.Tertiary);
                glyphText.Foreground = destructive ? Brushes.White : GrokTheme.InkBrush(GrokTheme.TextPrimary);
            };
            host.MouseLeave += (_, _) =>
            {
                host.Background = Brushes.Transparent;
                glyphText.Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary);
            };
            host.MouseLeftButtonUp += (_, args) =>
            {
                args.Handled = true;
                click();
            };
            // Also swallow the press so borderless windows don't treat the
            // click as a DragMove, and WindowChrome captions don't eat it.
            host.MouseLeftButtonDown += (_, args) => args.Handled = true;
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(host, true);
            return host;
        }

        panel.Children.Add(Make("", () => window.WindowState = WindowState.Minimized, destructive: false, out _));
        TextBlock? maxGlyph = null;
        var maximize = Make("", () =>
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            if (maxGlyph is not null)
            {
                maxGlyph.Text = window.WindowState == WindowState.Maximized ? "" : "";
            }
        }, destructive: false, out var glyphRef);
        maxGlyph = glyphRef;
        panel.Children.Add(maximize);
        panel.Children.Add(Make("", onClose ?? window.Close, destructive: true, out _));
        return panel;
    }
}
