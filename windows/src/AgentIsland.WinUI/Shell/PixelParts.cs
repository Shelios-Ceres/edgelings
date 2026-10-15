using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AgentIsland.WinUI.Shell;

/// The pixel kit's dashed divider: one cell tall, two cells on and two off,
/// each dash on whole pixels.
public sealed class PixelRule : Grid
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PixelRule), new PropertyMetadata(null, (d, e) => ((PixelRule)d)._dashes.Fill = (Brush?)e.NewValue));

    private readonly Path _dashes = new();

    public PixelRule()
    {
        Height = PixelPlate.Cell;
        IsHitTestVisible = false;
        Children.Add(_dashes);
        SizeChanged += (_, _) => Draw();
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private void Draw()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        double Px(double dip) => Math.Round(dip * scale) / scale;
        var dashes = new GeometryGroup();
        for (var x = 0.0; x < ActualWidth; x += 4 * PixelPlate.Cell)
        {
            var right = Math.Min(x + 2 * PixelPlate.Cell, ActualWidth);
            dashes.Children.Add(new RectangleGeometry { Rect = new Rect(Px(x), 0, Px(right) - Px(x), Px(PixelPlate.Cell)) });
        }
        _dashes.Data = dashes;
    }
}

/// The pixel kit's busy sign: six blocks around a 20-DIP square, one in the
/// accent and the two behind it fading, stepping eight times a second; no
/// smooth rotation.
public sealed class PixelSpinner : Canvas
{
    private static readonly (double X, double Y)[] Spots = { (8, 0), (16, 4), (16, 12), (8, 16), (0, 12), (0, 4) };

    private readonly Microsoft.UI.Xaml.Shapes.Rectangle[] _blocks;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private bool _active;
    private int _head;

    public PixelSpinner()
    {
        Width = Height = 10 * PixelPlate.Cell;
        _blocks = Spots.Select(spot =>
        {
            var block = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 2 * PixelPlate.Cell, Height = 2 * PixelPlate.Cell };
            SetLeft(block, spot.X);
            SetTop(block, spot.Y);
            Children.Add(block);
            return block;
        }).ToArray();
        Loaded += (_, _) => Run();
        Unloaded += (_, _) => _timer?.Stop();
        Paint();
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            Run();
        }
    }

    private void Run()
    {
        if (!_active || !IsLoaded)
        {
            _timer?.Stop();
            return;
        }
        if (_timer is null)
        {
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(125);
            _timer.Tick += (_, _) =>
            {
                _head = (_head + 1) % _blocks.Length;
                Paint();
            };
        }
        _timer.Start();
    }

    private void Paint()
    {
        var dark = Brand.IsDark();
        var accent = PixelTokens.Get("Accent", dark);
        var rest = PixelTokens.Brush("LineSoft", dark);
        for (var i = 0; i < _blocks.Length; i++)
        {
            var behind = (_head - i + _blocks.Length) % _blocks.Length;
            _blocks[i].Fill = behind switch
            {
                0 => new SolidColorBrush(accent),
                1 => new SolidColorBrush(Windows.UI.Color.FromArgb(166, accent.R, accent.G, accent.B)),
                2 => new SolidColorBrush(Windows.UI.Color.FromArgb(77, accent.R, accent.G, accent.B)),
                _ => rest,
            };
        }
    }
}

/// Visible while the bound value is true: an opened expander's rows.
public sealed class VisibleWhenTrue : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

/// One style for every item: the pixel rows of an opened SettingsExpander.
public sealed class FixedStyleSelector : StyleSelector
{
    public Style? Style { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container) => Style;
}
