using System.Numerics;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// The drawer's hover card: a Fluent flyout-style card whose pointer aims at
/// the character it describes, each quota window as a thin bar in that
/// character's colour. It is a windowed popup, so it can extend past the
/// drawer's narrow window.
internal sealed class HoverCard
{
    private const double CardWidth = 252;
    private const double Tip = 7;
    private const double Gap = 4;
    private const double LabelWidth = 44;
    private const double ValueWidth = 64;

    private readonly Popup _popup;
    private readonly Grid _host = new();
    private readonly Border _card;
    private readonly StackPanel _body = new();
    private readonly Polygon _tip = new() { StrokeThickness = 1 };
    private readonly ScaleTransform _scale = new();
    private readonly Panel _root;
    private static bool _dark;

    public HoverCard(Panel root)
    {
        _root = root;
        _card = new Border
        {
            Child = _body,
            Width = CardWidth,
            Padding = new Thickness(12, 10, 12, 11),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Translation = new Vector3(0, 0, 24),
            Shadow = new ThemeShadow(),
        };
        _host.Children.Add(_tip);
        _host.Children.Add(_card);
        _host.RenderTransform = _scale;
        _popup = new Popup
        {
            Child = _host,
            ShouldConstrainToRootBounds = false,
            IsHitTestVisible = false,
        };
        root.Children.Add(_popup);
    }

    public bool IsOpen => _popup.IsOpen;

    /// The card's content, for the verification snapshot.
    public UIElement Visual => _host;

    /// anchor and area are in the drawer window's DIPs; pointRight means the
    /// character is to the card's right (drawer on the right edge). A card
    /// for every character points at none of them.
    public void Show(IReadOnlyList<(StatusCard Card, uint Swatch)> sections, bool pointRight, bool pointing, Rect anchor, Rect area, bool reducedMotion)
    {
        // Before the drawer's content loads there is no root to open into.
        if (_root.XamlRoot is not { } xamlRoot) return;
        _popup.XamlRoot = xamlRoot;
        // Solid, not acrylic: in-app acrylic has nothing to sample in a
        // windowed popup, and the pointer must match the card exactly.
        var dark = Brand.IsDark();
        var background = Brand.Brush(dark ? 0x2C2C2Cu : 0xFCFCFCu);
        var stroke = Brand.Brush(0x000000, (byte)(dark ? 0x59 : 0x17));
        _dark = dark;
        _card.Background = background;
        _card.BorderBrush = stroke;
        _tip.Fill = background;
        _tip.Stroke = stroke;
        Build(sections);

        _card.Margin = pointRight ? new Thickness(0, 0, Tip, 0) : new Thickness(Tip, 0, 0, 0);
        _host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _host.DesiredSize;
        var aim = anchor.Y + anchor.Height / 2;
        var top = Math.Clamp(aim - size.Height / 2, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - 6 - size.Height));
        var tipY = Math.Clamp(aim - top, 14, Math.Max(14, size.Height - 14));
        var edge = pointRight ? CardWidth - 1 : Tip + 1;
        var point = pointRight ? CardWidth + Tip : 0;
        _tip.Points = new PointCollection { new(edge, tipY - Tip), new(point, tipY), new(edge, tipY + Tip) };
        _tip.Visibility = pointing ? Visibility.Visible : Visibility.Collapsed;
        _popup.HorizontalOffset = pointRight ? anchor.Left - Gap - (CardWidth + Tip) : anchor.Right + Gap;
        _popup.VerticalOffset = top;

        var opening = !_popup.IsOpen;
        _popup.IsOpen = true;
        if (opening) PlayIn(new Point(pointRight ? (CardWidth + Tip) : 0, tipY), reducedMotion);
    }

    public void Hide() => _popup.IsOpen = false;

    private void Build(IReadOnlyList<(StatusCard Card, uint Swatch)> sections)
    {
        _body.Children.Clear();
        for (var i = 0; i < sections.Count; i++)
        {
            var (card, swatch) = sections[i];
            if (i > 0)
            {
                _body.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill = Res<Brush>("DividerStrokeColorDefaultBrush"),
                    Margin = new Thickness(0, 9, 0, 9),
                });
            }
            _body.Children.Add(Section(card, swatch));
        }
    }

    private static UIElement Section(StatusCard card, uint swatch)
    {
        var panel = new StackPanel();
        var title = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new Run { Text = card.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Inlines.Add(new Run { Text = " · " + card.Phase, Foreground = Res<Brush>("TextFillColorSecondaryBrush") });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = Brand.Brush(swatch),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 6, 0),
                },
                title,
            },
        });
        if (card.Detail is { } detail)
        {
            panel.Children.Add(Caption(detail, "TextFillColorTertiaryBrush", new Thickness(13, 1, 0, 0)));
        }

        // Fixed label and value columns line the bars up across sections.
        var meters = new Grid { Margin = new Thickness(13, 7, 0, 0), RowSpacing = 4, ColumnSpacing = 8 };
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueWidth) });
        var row = 0;
        foreach (var meter in card.Meters)
        {
            meters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var warn = meter.Used >= 0.9;
            Place(meters, Caption(meter.Label, "TextFillColorSecondaryBrush"), row, 0);
            var bar = new QuotaBar(6) { VerticalAlignment = VerticalAlignment.Center };
            bar.Set(meter.Shown, Brand.Rgb(swatch), warn, _dark);
            Place(meters, bar, row, 1);
            var value = Caption(meter.Value, warn ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Place(meters, value, row, 2);
            row++;
        }
        if (row > 0) panel.Children.Add(meters);
        if (card.Tokens is { } tokens)
        {
            var line = new Grid { Margin = new Thickness(13, row > 0 ? 4 : 7, 0, 0), ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(line, Caption(tokens.Label, "TextFillColorSecondaryBrush"), 0, 0);
            Place(line, Emphasised(tokens.Today, tokens.TodayNumber), 0, 1);
            var month = Emphasised(tokens.Month, tokens.MonthNumber);
            month.HorizontalAlignment = HorizontalAlignment.Right;
            Place(line, month, 0, 2);
            panel.Children.Add(line);
        }
        if (card.Note is { } note)
        {
            panel.Children.Add(Caption(note, "TextFillColorTertiaryBrush", new Thickness(13, 5, 0, 0)));
        }
        return panel;
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static TextBlock Caption(string text, string brush, Thickness margin = default) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res<Brush>(brush),
        Margin = margin,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// A tertiary phrase whose number is lifted to primary ink.
    private static TextBlock Emphasised(string phrase, string number)
    {
        var text = new TextBlock { FontSize = 12, Foreground = Res<Brush>("TextFillColorTertiaryBrush") };
        var at = phrase.IndexOf(number, StringComparison.Ordinal);
        if (at < 0)
        {
            text.Text = phrase;
            return text;
        }
        if (at > 0) text.Inlines.Add(new Run { Text = phrase[..at] });
        text.Inlines.Add(new Run
        {
            Text = number,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Res<Brush>("TextFillColorPrimaryBrush"),
        });
        if (at + number.Length < phrase.Length) text.Inlines.Add(new Run { Text = phrase[(at + number.Length)..] });
        return text;
    }

    /// Grows out of the pointer's tip; reduced motion keeps only the fade.
    private void PlayIn(Point origin, bool reducedMotion)
    {
        _scale.CenterX = origin.X;
        _scale.CenterY = origin.Y;
        var story = new Storyboard();
        Add(story, _host, "Opacity", 0, 1, 140, null);
        if (!reducedMotion)
        {
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            Add(story, _scale, "ScaleX", 0.88, 1, 220, ease);
            Add(story, _scale, "ScaleY", 0.88, 1, 220, ease);
        }
        story.Begin();
    }

    private static void Add(Storyboard story, DependencyObject target, string property, double from, double to, int ms, EasingFunctionBase? ease)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }

    private static T Res<T>(string key) => Fluent.Resource<T>(key, _dark);
}
