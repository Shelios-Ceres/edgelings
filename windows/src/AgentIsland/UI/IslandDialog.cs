using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// App-styled dialog in Grok's light dress — a white floating card, the
/// brand mark on a soft brand wash, headline, message, optional
/// caption/value meta rows, and stacked buttons. Replaces every raw Win32
/// MessageBox in the app.
public sealed class IslandDialog : Window
{
    private IslandDialog(
        string title,
        string message,
        Color tint,
        UIElement markGlyph,
        IReadOnlyList<(string Caption, string Value)>? meta,
        string primaryLabel,
        Action? primaryAction,
        string? secondaryLabel)
    {
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = title;
        System.Windows.Media.TextOptions.SetTextFormattingMode(
            this, System.Windows.Media.TextFormattingMode.Display);

        var root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = GrokTheme.Brush(GrokTheme.Elevated),
            BorderBrush = GrokTheme.InkBrush(GrokTheme.StrokeSecondary),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(16),
            Effect = GrokTheme.Float(0.18),
        };
        Content = root;

        var stack = new StackPanel { Margin = new Thickness(30, 26, 30, 24) };
        root.Child = stack;

        // The provider's REAL mark (masked bitmap or full-color art) on a
        // soft wash of its own colour.
        stack.Children.Add(new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            Background = IslandColors.Brush(IslandColors.Alpha(tint, 0.10)),
            Child = new ContentControl
            {
                Content = markGlyph,
                Width = 34,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        });

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = IslandFonts.Ui,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontFamily = IslandFonts.Ui,
            FontSize = 12.5,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            LineHeight = 19,
        });

        if (meta is { Count: > 0 })
        {
            var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            for (var i = 0; i < meta.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            for (var i = 0; i < meta.Count; i++)
            {
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(new TextBlock
                {
                    Text = meta[i].Caption.ToUpperInvariant(),
                    FontFamily = IslandFonts.Ui,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GrokTheme.InkBrush(GrokTheme.TextQuaternary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                cell.Children.Add(new TextBlock
                {
                    Text = meta[i].Value,
                    FontFamily = IslandFonts.Ui,
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 110,
                });
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            stack.Children.Add(grid);
        }

        var primary = GrokButton.Create(primaryLabel, primary: true);
        primary.Height = 36;
        primary.HorizontalAlignment = HorizontalAlignment.Stretch;
        primary.Margin = new Thickness(0, 22, 0, 0);
        primary.Click += (_, _) =>
        {
            Close();
            primaryAction?.Invoke();
        };
        stack.Children.Add(primary);
        if (secondaryLabel is not null)
        {
            var secondary = GrokButton.Create(secondaryLabel, primary: false);
            secondary.Height = 36;
            secondary.HorizontalAlignment = HorizontalAlignment.Stretch;
            secondary.Margin = new Thickness(0, 8, 0, 0);
            secondary.Click += (_, _) => Close();
            stack.Children.Add(secondary);
        }

        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) Close();
        };
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };
        IslandMotion.AnimateEntrance(this, root);
    }

    /// Provider-tinted dialog carrying that provider's REAL mark.
    public static void Show(
        Core.TriggerTool tool,
        string title,
        string message,
        IReadOnlyList<(string Caption, string Value)>? meta = null,
        string? primaryLabel = null,
        Action? primaryAction = null,
        string? secondaryLabel = null)
    {
        var brand = GrokTheme.Brand(tool.ToDisplayProvider());
        Present(new IslandDialog(
            title, message, brand,
            ProviderMarks.Mark(tool.ToDisplayProvider(), 34, tintOpacity: 1, tint: brand), meta,
            primaryLabel ?? Localization.L10n.Tr("I know"), primaryAction, secondaryLabel));
    }

    /// The five-blade app mark for provider-neutral dialogs.
    private static UIElement AppMark()
    {
        try
        {
            var image = new System.Windows.Controls.Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Assets/agentisland_logo_small.png")),
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        catch
        {
            return new System.Windows.Shapes.Ellipse
            {
                Width = 34,
                Height = 34,
                Stroke = GrokTheme.InkBrush(GrokTheme.TextSecondary),
                StrokeThickness = 2,
            };
        }
    }

    /// App-branded dialog for provider-neutral messages.
    public static void ShowApp(
        string title,
        string message,
        string? primaryLabel = null,
        Action? primaryAction = null,
        string? secondaryLabel = null)
    {
        Present(new IslandDialog(
            title, message, GrokTheme.Accent,
            AppMark(), null,
            primaryLabel ?? Localization.L10n.Tr("I know"), primaryAction, secondaryLabel));
    }

    private static void Present(IslandDialog dialog)
    {
        dialog.Show();
        dialog.Activate();
    }
}
