using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The on/off switch shared by every Settings row, in Grok's light dress:
/// an accent track with a white knob when on, a faint ink track when off
/// (macOS SettingsToggle geometry: 34x19 track, 15pt knob).
public sealed class CobaltToggle : Border
{
    // Track 34, knob 15, 2pt inset → the knob travels 15pt between rests.
    private const double KnobTravel = 34 - 15 - 2 * 2;

    private readonly Ellipse _dot;
    private readonly TranslateTransform _slide = new();
    private readonly SolidColorBrush _track = new();
    private readonly SolidColorBrush _rim = new();
    private readonly SolidColorBrush _knob = new();
    private bool _isOn;
    private bool _hovered;
    private bool _seeded;

    public event Action<bool>? Toggled;

    public CobaltToggle(bool isOn)
    {
        _isOn = isOn;
        Width = 34;
        Height = 19;
        CornerRadius = new CornerRadius(9.5);
        BorderThickness = new Thickness(1);
        Background = _track;
        BorderBrush = _rim;
        Cursor = System.Windows.Input.Cursors.Hand;
        // The knob's lift is a drawn shadow, not a DropShadowEffect: an effect
        // re-renders on every frame the page scrolls, and eight of them cost
        // the Settings page a fifth of its frame rate.
        _dot = new Ellipse { Width = 15, Height = 15, Fill = _knob };
        Child = new Grid
        {
            Width = 15,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
            RenderTransform = _slide,
            Children =
            {
                new Ellipse
                {
                    Width = 15,
                    Height = 15,
                    Fill = GrokTheme.InkBrush(0.16),
                    RenderTransform = new TranslateTransform(0, 0.8),
                },
                _dot,
            },
        };
        MouseLeftButtonUp += (_, args) =>
        {
            _isOn = !_isOn;
            Render();
            Toggled?.Invoke(_isOn);
            args.Handled = true;
        };
        MouseEnter += (_, _) => { _hovered = true; Render(); };
        MouseLeave += (_, _) => { _hovered = false; Render(); };
        Render();
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            Render();
        }
    }

    /// The knob SLIDES and the washes crossfade (macOS SettingsToggle
    /// spring ~0.3s) — the first paint lands instantly so a freshly built
    /// settings page doesn't ripple with settling toggles.
    private void Render()
    {
        var track = _isOn
            ? (_hovered ? GrokTheme.AccentHover : GrokTheme.Accent)
            : GrokTheme.InkA(_hovered ? GrokTheme.Active : GrokTheme.Secondary);
        var rim = _isOn ? Colors.Transparent : GrokTheme.InkA(GrokTheme.StrokeTertiary);
        var knob = Colors.White;
        var offset = _isOn ? KnobTravel : 0;
        if (!_seeded)
        {
            _seeded = true;
            _track.Color = track;
            _rim.Color = rim;
            _knob.Color = knob;
            _slide.X = offset;
            return;
        }
        var beat = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new System.Windows.Media.Animation.QuadraticEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
        };
        _track.BeginAnimation(SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(track, beat) { EasingFunction = ease });
        _rim.BeginAnimation(SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(rim, beat) { EasingFunction = ease });
        _knob.BeginAnimation(SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(knob, beat) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(offset, beat) { EasingFunction = ease });
    }
}

/// Pill-shaped segmented control (Refresh interval, Token counting, Mac
/// type pickers).
public sealed class Segmented : Border
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    private readonly List<Border> _cells = new();
    private int _selected;

    public event Action<int>? SelectionChanged;

    public Segmented(IReadOnlyList<string> labels, int selected)
    {
        _selected = selected;
        CornerRadius = new CornerRadius(9);
        Background = GrokTheme.InkBrush(GrokTheme.Quaternary);
        Padding = new Thickness(2);
        Child = _items;
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var text = new TextBlock
            {
                Text = labels[i],
                FontFamily = IslandFonts.Ui,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
            };
            var cell = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0.5, 0.5, 0.5, 1),
                Padding = new Thickness(11, 4, 11, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            cell.MouseLeftButtonUp += (_, args) =>
            {
                Select(index);
                SelectionChanged?.Invoke(index);
                args.Handled = true;
            };
            _cells.Add(cell);
            _items.Children.Add(cell);
        }
        Select(selected);
    }

    /// A white thumb lifted off a faint ink track by a hairline with a heavier
    /// bottom edge (a drawn lift: effects are too costly while scrolling); the
    /// selected label goes full ink, the rest stay tertiary.
    public void Select(int index)
    {
        _selected = Math.Clamp(index, 0, _cells.Count - 1);
        for (var i = 0; i < _cells.Count; i++)
        {
            var isOn = i == _selected;
            _cells[i].Background = isOn ? GrokTheme.Brush(GrokTheme.Elevated) : Brushes.Transparent;
            _cells[i].BorderBrush = isOn ? GrokTheme.InkBrush(GrokTheme.StrokeSecondary) : Brushes.Transparent;
            var label = (TextBlock)_cells[i].Child!;
            label.Foreground = GrokTheme.InkBrush(isOn ? GrokTheme.TextPrimary : GrokTheme.TextTertiary);
            label.FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Medium;
        }
    }
}

/// Grok's buttons ("Refresh", "Cost…"): secondary is an ink wash that deepens
/// on hover; primary is the accent fill with a white label.
public sealed class PillButtonControl : Border
{
    private readonly TextBlock _label;

    public event Action? Clicked;

    public PillButtonControl(string label, bool primary = false)
    {
        _label = new TextBlock
        {
            Text = label,
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = primary ? Brushes.White : GrokTheme.InkBrush(GrokTheme.TextPrimary),
        };
        Child = _label;
        CornerRadius = new CornerRadius(6);
        var rest = primary ? GrokTheme.Brush(GrokTheme.Accent) : GrokTheme.InkBrush(GrokTheme.Tertiary);
        var hover = primary ? GrokTheme.Brush(GrokTheme.AccentHover) : GrokTheme.InkBrush(GrokTheme.Secondary);
        Background = rest;
        Padding = new Thickness(12, 5, 12, 5);
        Cursor = System.Windows.Input.Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        MouseEnter += (_, _) => Background = hover;
        MouseLeave += (_, _) => Background = rest;
        MouseLeftButtonUp += (_, args) =>
        {
            Clicked?.Invoke();
            args.Handled = true;
        };
    }

    public string Label
    {
        get => _label.Text;
        set => _label.Text = value;
    }
}

/// A single Settings list row: title (+ optional brand dot and plan chip),
/// subtitle, trailing control. Hover lifts the background to a faint wash.
public sealed class SettingsRowControl : Border
{
    public SettingsRowControl(
        string title,
        string? subtitle,
        UIElement trailing,
        Color? dot = null,
        string? chip = null,
        bool monospaceTitle = false)
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10, 11, 10, 11);
        Background = Brushes.Transparent;
        MouseEnter += (_, _) => Background = GrokTheme.InkBrush(GrokTheme.Field);
        MouseLeave += (_, _) => Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Child = grid;

        var text = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot is { } dotColor)
        {
            titleRow.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = IslandColors.Brush(dotColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }
        titleRow.Children.Add(new TextBlock
        {
            Text = monospaceTitle ? title : Localization.L10n.Tr(title),
            FontFamily = monospaceTitle ? IslandFonts.Mono : IslandFonts.Ui,
            FontSize = monospaceTitle ? 10 : 13,
            FontWeight = FontWeights.Medium,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
        });
        if (!string.IsNullOrEmpty(chip))
        {
            titleRow.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Text = chip,
                    FontFamily = IslandFonts.Ui,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = GrokTheme.InkBrush(GrokTheme.TextSecondary),
                },
                CornerRadius = new CornerRadius(4),
                Background = GrokTheme.InkBrush(GrokTheme.Quaternary),
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        text.Children.Add(titleRow);
        if (!string.IsNullOrEmpty(subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = Localization.L10n.Tr(subtitle!),
                FontFamily = IslandFonts.Ui,
                FontSize = 11,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                // Stretch + MaxWidth centers the box in WPF — the subtitle
                // must sit flush with the title's left edge.
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var trailingHost = new ContentControl
        {
            Content = trailing,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetColumn(trailingHost, 1);
        grid.Children.Add(trailingHost);
    }
}

/// WPF's default ComboBox chrome read as cheap next to the rest of the page
/// (owner review, 2026-08-09: 质感太差). One Grok-style template — white
/// face, hairline ink stroke, floating white list — parsed once and shared
/// by every picker.
public static class GrokComboStyle
{
    private const string TemplateXaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="ComboBox">
  <Grid>
    <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
      <ToggleButton.Template>
        <ControlTemplate TargetType="ToggleButton">
          <Border x:Name="Face" CornerRadius="6" Background="#FFFFFFFF"
                  BorderBrush="#1F141414" BorderThickness="1">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>
              <ContentPresenter Grid.Column="0" Margin="10,0,4,0"
                                HorizontalAlignment="Left" VerticalAlignment="Center"/>
              <Path Grid.Column="1" Margin="0,0,9,0" VerticalAlignment="Center"
                    Data="M 0 0 L 3.5 3.5 L 7 0" Stroke="#8A141414" StrokeThickness="1.4"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Face" Property="Background" Value="#FFF4F4F3"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter Content="{TemplateBinding SelectionBoxItem}"
                      ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                      Margin="10,0,24,0" HorizontalAlignment="Left"
                      VerticalAlignment="Center" IsHitTestVisible="False"
                      TextBlock.Foreground="#EB141414"/>
    <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom"
           HorizontalOffset="-10" VerticalOffset="-6" AllowsTransparency="True"
           Focusable="False" PopupAnimation="Fade">
      <Border Background="#FFFFFFFF" CornerRadius="8" BorderBrush="#1F141414"
              BorderThickness="1" MinWidth="{TemplateBinding ActualWidth}"
              MaxHeight="{TemplateBinding MaxDropDownHeight}" Padding="4" Margin="10">
        <Border.Effect>
          <DropShadowEffect ShadowDepth="3" Direction="270" BlurRadius="14" Opacity="0.14" Color="#141414"/>
        </Border.Effect>
        <ScrollViewer VerticalScrollBarVisibility="Auto"
                      HorizontalScrollBarVisibility="Disabled">
          <ItemsPresenter/>
        </ScrollViewer>
      </Border>
    </Popup>
  </Grid>
</ControlTemplate>
""";

    private const string ItemXaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="ComboBoxItem">
  <Border x:Name="Row" CornerRadius="5" Padding="8,5,8,5" Background="Transparent">
    <ContentPresenter TextBlock.Foreground="#EB141414"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsHighlighted" Value="True">
      <Setter TargetName="Row" Property="Background" Value="#0F141414"/>
    </Trigger>
    <Trigger Property="IsSelected" Value="True">
      <Setter TargetName="Row" Property="Background" Value="#24599CE7"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""";

    private static ControlTemplate? _template;
    private static ControlTemplate? _itemTemplate;

    public static ComboBox Apply(ComboBox box)
    {
        _template ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(TemplateXaml);
        _itemTemplate ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(ItemXaml);
        box.Template = _template;
        box.Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary);
        box.FontFamily = Charts.IslandFonts.Ui;
        box.FontSize = 12;
        box.Height = 28;
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, _itemTemplate));
        itemStyle.Setters.Add(new Setter(Control.FontFamilyProperty, Charts.IslandFonts.Ui));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        box.ItemContainerStyle = itemStyle;
        return box;
    }
}

/// Grok's text buttons for dialogs and cards: primary is the accent fill
/// with a white label, secondary an ink wash; both deepen on hover.
public static class GrokButton
{
    public static Button Create(string label, bool primary, double fontSize = 13)
    {
        var rest = primary ? GrokTheme.Brush(GrokTheme.Accent) : GrokTheme.InkBrush(GrokTheme.Tertiary);
        var hover = primary ? GrokTheme.Brush(GrokTheme.AccentHover) : GrokTheme.InkBrush(GrokTheme.Secondary);
        var button = new Button
        {
            Content = label,
            FontFamily = IslandFonts.Ui,
            FontSize = fontSize,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = primary ? Brushes.White : GrokTheme.InkBrush(GrokTheme.TextPrimary),
            Background = rest,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 7, 16, 7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        // Own template so the default WPF chrome (and its blue hover) never
        // shows through.
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = face };
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = rest;
        IslandMotion.AttachPressFeedback(button);
        return button;
    }
}
