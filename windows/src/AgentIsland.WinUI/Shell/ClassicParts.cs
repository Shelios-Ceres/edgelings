using System.Numerics;
using AgentIsland.Model;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AgentIsland.WinUI.Shell;

/// The Classic dot bot's face, from the app icon: a disc in the character's
/// colour with two upright oval eyes, where a plain swatch would go.
public sealed class FaceChip : Grid
{
    private readonly Ellipse _disc = new();
    private readonly Ellipse[] _eyes = { new(), new() };

    /// lookRight turns the eyes a little toward the text after the face.
    public FaceChip(double size, Color color, bool lookRight = false)
    {
        Width = Height = size;
        IsHitTestVisible = false;
        Children.Add(_disc);
        var w = 0.17 * size;
        var h = 0.28 * size;
        var shift = lookRight ? 0.06 * size : 0;
        for (var i = 0; i < 2; i++)
        {
            var side = i == 0 ? -1 : 1;
            _eyes[i].Width = w;
            _eyes[i].Height = h;
            _eyes[i].HorizontalAlignment = HorizontalAlignment.Left;
            _eyes[i].VerticalAlignment = VerticalAlignment.Top;
            _eyes[i].Margin = new Thickness(size / 2 + side * 0.155 * size - w / 2 + shift, size * (0.5 - 0.17), 0, 0);
            Children.Add(_eyes[i]);
        }
        Color = color;
    }

    public Color Color
    {
        set
        {
            _disc.Fill = new SolidColorBrush(value);
            var eyes = new SolidColorBrush(ClassicTokens.EyesOn(value));
            foreach (var eye in _eyes) eye.Fill = eyes;
        }
    }
}

/// A face chip as a bitmap, for places that take an icon (menu items):
/// drawn at the display's pixels with 4 x 4 samples per pixel.
internal static class FaceBitmap
{
    private static readonly Dictionary<(Color, int), Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap> Cache = new();

    public static ImageSource Of(Color disc, double dip, double scale)
    {
        var size = (int)Math.Round(dip * scale);
        if (Cache.TryGetValue((disc, size), out var cached)) return cached;
        var eyes = ClassicTokens.EyesOn(disc);
        var pixels = new byte[size * size * 4];
        double r = size / 2.0, ew = 0.17 * size / 2, eh = 0.28 * size / 2, ey = size * (0.5 - 0.17) + eh;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int inDisc = 0, inEye = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        double px = x + (sx + 0.5) / 4, py = y + (sy + 0.5) / 4;
                        if ((px - r) * (px - r) + (py - r) * (py - r) > r * r) continue;
                        inDisc++;
                        foreach (var side in new[] { -1, 1 })
                        {
                            var ex = r + side * 0.155 * size;
                            if ((px - ex) * (px - ex) / (ew * ew) + (py - ey) * (py - ey) / (eh * eh) <= 1) inEye++;
                        }
                    }
                }
                var a = inDisc / 16.0;
                var e = inDisc == 0 ? 0 : (double)inEye / inDisc;
                byte Channel(byte d, byte i) => (byte)Math.Round((d * (1 - e) + i * e) * a);
                var o = (y * size + x) * 4;
                pixels[o] = Channel(disc.B, eyes.B);
                pixels[o + 1] = Channel(disc.G, eyes.G);
                pixels[o + 2] = Channel(disc.R, eyes.R);
                pixels[o + 3] = (byte)Math.Round(a * 255);
            }
        }
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(size, size);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
        {
            stream.Write(pixels, 0, pixels.Length);
        }
        bitmap.Invalidate();
        Cache[(disc, size)] = bitmap;
        return bitmap;
    }
}

/// The Classic busy sign (M16): an accent face whose eyes glance left, then
/// right, and back over 1.6 s on inOut, blinking near the end of each look.
/// It runs only while busy and on screen, and holds still with animations
/// off.
public sealed class FaceSpinner : Grid
{
    private const double Size = 20;
    private const double Loop = 1600;
    // Where the eyes look (DIP) at each share of the loop, eased as a whole.
    private static readonly (double At, double Value)[] Glance = { (0, 0), (0.15, -1.5), (0.35, -1.5), (0.5, 1.5), (0.7, 1.5), (0.85, 0), (1, 0) };
    // How open they are at each share of the loop.
    private static readonly (double At, double Value)[] Blink = { (0, 1), (0.86, 1), (0.9, 0.16), (0.94, 1), (1, 1) };
    private readonly TranslateTransform _look = new();
    private readonly ScaleTransform _blink = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private bool _active;
    private bool _running;

    public FaceSpinner()
    {
        Width = Height = Size;
        var dark = Brand.IsDark();
        Children.Add(new Ellipse { Fill = ClassicTokens.Brush("Accent", dark) });
        var w = 0.17 * Size;
        var h = 0.28 * Size;
        // The blink closes the eyes about their own middle.
        _blink.CenterY = Size * (0.5 - 0.17) + h / 2;
        var eyes = new Grid { RenderTransform = new TransformGroup { Children = { _blink, _look } } };
        foreach (var side in new[] { -1, 1 })
        {
            eyes.Children.Add(new Ellipse
            {
                Width = w,
                Height = h,
                Fill = ClassicTokens.Brush("OnAccent", dark),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(Size / 2 + side * 0.155 * Size - w / 2, Size * (0.5 - 0.17), 0, 0),
            });
        }
        Children.Add(eyes);
        Loaded += (_, _) => Run();
        Unloaded += (_, _) => Stop();
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
        if (!_active || !IsLoaded || Motion.ReducedMotion)
        {
            Stop();
            return;
        }
        if (_running) return;
        _running = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    /// Stopped, not paused: the face rests looking ahead.
    private void Stop()
    {
        if (_running) CompositionTarget.Rendering -= OnRendering;
        _running = false;
        _look.X = 0;
        _blink.ScaleY = 1;
    }

    private void OnRendering(object? sender, object e)
    {
        var share = _clock.Elapsed.TotalMilliseconds % Loop / Loop;
        _look.X = Along(Glance, Motion.InOut(share));
        _blink.ScaleY = Along(Blink, share);
    }

    private static double Along((double At, double Value)[] keys, double share)
    {
        for (var i = 1; i < keys.Length; i++)
        {
            if (share > keys[i].At) continue;
            var (at, value) = keys[i - 1];
            return value + (keys[i].Value - value) * (share - at) / (keys[i].At - at);
        }
        return keys[^1].Value;
    }
}

/// An agent's puck: its brand mark on a disc of its tint. Settings cards
/// draw a plain puck for their glyph; SetTint colours an agent's, through
/// the card's Tag (a template can't bind an attached property of ours).
public static class Puck
{
    public static void SetTint(FrameworkElement card, Brush? tint) => card.Tag = tint;

    /// A puck as an element, where no template draws one: size and the
    /// mark's size inside it.
    public static Grid Of(DisplayProvider provider, double size, double mark)
    {
        var dark = Brand.IsDark();
        var puck = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        puck.Children.Add(new Ellipse { Fill = new SolidColorBrush(ClassicTokens.Tint(ClassicTokens.Identity(provider, dark), dark)) });
        var image = new Image { Width = mark, Height = mark, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        puck.Children.Add(image);
        _ = LoadAsync(image, provider, dark);
        return puck;
    }

    private static async Task LoadAsync(Image image, DisplayProvider provider, bool dark)
    {
        if (await Brand.MarkAsync(provider, dark) is { } mark) image.Source = mark;
    }
}

/// A soft shadow under a sibling shape: Composition drop shadows masked by
/// the shape's own alpha, so the shadow follows its outline. Layers are
/// (offset y, blur, colour).
public sealed class SoftShadow : Grid
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(Shape), typeof(SoftShadow), new PropertyMetadata(null, (d, _) => ((SoftShadow)d).Attach()));

    private readonly List<SpriteVisual> _layers = new();
    private ContainerVisual? _container;
    private Shape? _watched;

    public SoftShadow()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) =>
        {
            Watch(null);
            Detach();
        };
        SizeChanged += (_, _) => Resize();
    }

    /// The shape whose outline casts the shadow; it sits over this element.
    public Shape? Target
    {
        get => (Shape?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// "Lift" for raised items, "Float" for floating surfaces, "Paper" for
    /// the drawer's paper card and bubbles.
    public string Kind { get; set; } = "Lift";

    private void Watch(Shape? target)
    {
        if (_watched == target) return;
        if (_watched is not null) _watched.SizeChanged -= OnTargetSized;
        _watched = target;
        if (target is not null) target.SizeChanged += OnTargetSized;
    }

    private void OnTargetSized(object sender, SizeChangedEventArgs e)
    {
        if (_container is null) Attach();
    }

    /// A shape has an alpha to mask with only once it has a size: until then,
    /// or while it can't hand one over, the shadow waits for its next size.
    private void Attach()
    {
        Detach();
        if (!IsLoaded || Target is not { } target)
        {
            Watch(null);
            return;
        }
        Watch(target);
        if (target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
        CompositionBrush mask;
        try
        {
            mask = target.GetAlphaMask();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _container = compositor.CreateContainerVisual();
        foreach (var (y, blur, color) in Layers(Kind, Brand.IsDark()))
        {
            var shadow = compositor.CreateDropShadow();
            shadow.Mask = mask;
            shadow.BlurRadius = (float)blur;
            shadow.Offset = new Vector3(0, (float)y, 0);
            shadow.Color = color;
            var layer = compositor.CreateSpriteVisual();
            layer.Shadow = shadow;
            _layers.Add(layer);
            _container.Children.InsertAtTop(layer);
        }
        ElementCompositionPreview.SetElementChildVisual(this, _container);
        Resize();
    }

    private void Resize()
    {
        var size = new Vector2((float)ActualWidth, (float)ActualHeight);
        foreach (var layer in _layers) layer.Size = size;
    }

    private void Detach()
    {
        if (_container is null) return;
        ElementCompositionPreview.SetElementChildVisual(this, null);
        foreach (var layer in _layers)
        {
            layer.Shadow?.Dispose();
            layer.Dispose();
        }
        _layers.Clear();
        _container.Dispose();
        _container = null;
    }

    /// The handoff's shadow tokens, layer by layer.
    public static IReadOnlyList<(double Y, double Blur, Color Color)> Layers(string kind, bool dark)
    {
        Color Ink(double alpha) => dark ? Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0) : Color.FromArgb((byte)Math.Round(alpha * 255), 0x17, 0x19, 0x1F);
        return kind switch
        {
            "Float" => dark
                ? new[] { (12.0, 32.0, Ink(0.50)), (2.0, 8.0, Ink(0.30)) }
                : new[] { (10.0, 28.0, Ink(0.16)), (2.0, 6.0, Ink(0.08)) },
            "Paper" => dark
                ? new[] { (8.0, 16.0, Ink(0.45)), (1.0, 2.0, Ink(0.35)) }
                : new[] { (8.0, 14.0, Ink(0.13)), (1.0, 2.0, Ink(0.10)) },
            _ => dark
                ? new[] { (1.0, 2.0, Ink(0.35)), (3.0, 8.0, Ink(0.25)) }
                : new[] { (1.0, 2.0, Ink(0.10)), (3.0, 8.0, Ink(0.07)) },
        };
    }
}
