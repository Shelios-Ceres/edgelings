using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The shared tile chrome for both style pickers — macOS StyleTile: selected
/// = white 0.12 fill + white 0.6 hairline + white 0.95 semibold label;
/// unselected = white 0.025 fill, invisible border, white 0.55 label; hover
/// lifts an unselected tile to 0.05 fill + 0.10 border.
internal static class StyleTileChrome
{
    public static void Paint(Border tile, TextBlock label, bool isOn)
    {
        tile.Tag = isOn;
        tile.Background = IslandColors.Brush(IslandColors.White(isOn ? 0.12 : 0.025));
        tile.BorderBrush = isOn ? IslandColors.Brush(IslandColors.White(0.6)) : Brushes.Transparent;
        label.Foreground = IslandColors.Brush(IslandColors.White(isOn ? 0.95 : 0.55));
        label.FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Medium;
    }

    public static void AttachHover(Border tile)
    {
        // macOS StyleTile hover: an unselected tile lifts to 1.02 with a
        // soft drop and a faint border, easing out over ~0.12s.
        var scale = new ScaleTransform(1, 1);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = new Point(0.5, 0.5);

        void Ease(double target)
        {
            var beat = new Duration(TimeSpan.FromMilliseconds(120));
            var ease = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(target, beat) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new System.Windows.Media.Animation.DoubleAnimation(target, beat) { EasingFunction = ease });
        }

        tile.MouseEnter += (_, _) =>
        {
            if (tile.Tag is true) return;
            tile.Background = IslandColors.Brush(IslandColors.White(0.05));
            tile.BorderBrush = IslandColors.Brush(IslandColors.White(0.10));
            Ease(1.02);
        };
        tile.MouseLeave += (_, _) =>
        {
            Ease(1.0);
            if (tile.Tag is true) return;
            tile.Background = IslandColors.Brush(IslandColors.White(0.025));
            tile.BorderBrush = Brushes.Transparent;
        };
    }
}

/// Cost display picker: USD / VALUE / TOKENS / TREND preview tiles, the
/// same white-only voice as the usage picker.
public sealed class CostStylePickerControl : Grid
{
    private readonly List<Border> _tiles = new();

    public event Action<CostStyle>? StyleSelected;

    public CostStylePickerControl(CostStyle selected)
    {
        var styles = Enum.GetValues<CostStyle>();
        for (var i = 0; i < styles.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var i = 0; i < styles.Length; i++)
        {
            var style = styles[i];
            var tile = MakeTile(style);
            SetColumn(tile, i);
            _tiles.Add(tile);
            Children.Add(tile);
        }
        Select(selected);
    }

    private readonly List<TextBlock> _labels = new();

    private Border MakeTile(CostStyle style)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var preview = new Grid { Height = 40, Margin = new Thickness(0, 0, 0, 10) };
        preview.Children.Add(MakePreview(style));
        stack.Children.Add(preview);
        var label = new TextBlock
        {
            Text = ChipLabel(style),
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _labels.Add(label);
        stack.Children.Add(label);
        var tile = new Border
        {
            Child = stack,
            Height = 100,
            CornerRadius = new CornerRadius(9),
            Background = IslandColors.Brush(IslandColors.White(0.025)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.MouseLeftButtonUp += (_, args) =>
        {
            Select(style);
            StyleSelected?.Invoke(style);
            args.Handled = true;
        };
        StyleTileChrome.AttachHover(tile);
        return tile;
    }

    public void Select(CostStyle selected)
    {
        var styles = Enum.GetValues<CostStyle>();
        for (var i = 0; i < _tiles.Count; i++)
        {
            StyleTileChrome.Paint(_tiles[i], _labels[i], styles[i] == selected);
        }
    }

    /// Drawn previews, not typed-out strings — "◞◠◞◠" rendered as tofu-ish
    /// glyph soup on Windows fonts, and the value tile reads as a chart on
    /// macOS, not a dollar string.
    /// macOS CostStylePicker previews — active elements pure white (the
    /// value capsule and spark carry a soft white glow), suffixes at 0.5.
    private static UIElement MakePreview(CostStyle style)
    {
        switch (style)
        {
            case CostStyle.Dollar:
            {
                var text = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = IslandFonts.Mono,
                };
                text.Inlines.Add(new System.Windows.Documents.Run("$")
                {
                    FontSize = 11.5,
                    Foreground = IslandColors.Brush(IslandColors.White(0.5)),
                });
                text.Inlines.Add(new System.Windows.Documents.Run("87")
                {
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                });
                return text;
            }
            case CostStyle.Multi:
            {
                // The value view sketch: a dim short capsule beside a tall
                // white one with a soft glow.
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                row.Children.Add(new Border
                {
                    Width = 8,
                    Height = 6,
                    CornerRadius = new CornerRadius(4),
                    Background = IslandColors.Brush(IslandColors.White(0.20)),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 4, 0),
                });
                row.Children.Add(new Border
                {
                    Width = 8,
                    Height = 18,
                    CornerRadius = new CornerRadius(4),
                    Background = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 0,
                        BlurRadius = 6,
                        Color = Colors.White,
                        Opacity = 0.6,
                    },
                });
                return row;
            }
            case CostStyle.Tokens:
            {
                var text = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = IslandFonts.Mono,
                };
                text.Inlines.Add(new System.Windows.Documents.Run("2.4")
                {
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                });
                text.Inlines.Add(new System.Windows.Documents.Run("M")
                {
                    FontSize = 11,
                    Foreground = IslandColors.Brush(IslandColors.White(0.5)),
                });
                return text;
            }
            case CostStyle.Trend:
            default:
            {
                // The macOS spark path, normalized to a 32x16 box, white
                // with a soft white glow.
                var line = new Polyline
                {
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 0,
                        BlurRadius = 4,
                        Color = Colors.White,
                        Opacity = 0.6,
                    },
                };
                var points = new (double X, double Y)[]
                {
                    (0.00, 0.92), (0.16, 0.78), (0.34, 0.65), (0.50, 0.50),
                    (0.69, 0.38), (0.84, 0.22), (1.00, 0.10),
                };
                foreach (var (x, y) in points)
                {
                    line.Points.Add(new Point(x * 32, y * 16));
                }
                return line;
            }
        }
    }

    private static string ChipLabel(CostStyle style) => Localization.L10n.Tr(style switch
    {
        CostStyle.Dollar => "USD",
        CostStyle.Multi => "Value",
        CostStyle.Tokens => "TOKEN",
        CostStyle.Trend => "Trend",
        _ => "USD",
    });
}
