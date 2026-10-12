using System.Numerics;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// A character's line: a small pane of the hover card's glass beside it,
/// growing out of the side facing the character and fading away on its own.
internal sealed class SpeechBubble
{
    private const double MaxWidth = 240;
    private const double Gap = 10;
    private const float StartScale = 0.9f;
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(4.5);
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(180);

    private readonly Panel _root;
    private readonly Popup _popup;
    private readonly Border _pane;
    private readonly TextBlock _text = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = MaxWidth - 28 };
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _easeOut;
    private readonly DispatcherQueueTimer _linger;
    private readonly DispatcherQueueTimer _close;
    private bool? _glassDark;
    private bool _reduced;

    public SpeechBubble(Panel root)
    {
        _root = root;
        _pane = new Border
        {
            Child = _text,
            Padding = new Thickness(14, 9, 14, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Translation = new Vector3(0, 0, 16),
            Shadow = new ThemeShadow(),
        };
        _popup = new Popup { Child = _pane, ShouldConstrainToRootBounds = false, IsHitTestVisible = false };
        root.Children.Add(_popup);
        _compositor = CompositionTarget.GetCompositorForCurrentThread();
        _easeOut = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f));
        var queue = DispatcherQueue.GetForCurrentThread();
        _linger = queue.CreateTimer();
        _linger.IsRepeating = false;
        _linger.Interval = Linger;
        _linger.Tick += (_, _) => Hide(animated: true);
        _close = queue.CreateTimer();
        _close.IsRepeating = false;
        _close.Interval = ExitDuration + TimeSpan.FromMilliseconds(20);
        _close.Tick += (_, _) => _popup.IsOpen = false;
    }

    public bool IsOpen => _popup.IsOpen;

    /// anchor and area are in the drawer window's DIPs; characterRight means
    /// the character is to the bubble's right.
    public void Say(string line, bool characterRight, Rect anchor, Rect area, bool reducedMotion)
    {
        if (_root.XamlRoot is not { } xamlRoot) return;
        _popup.XamlRoot = xamlRoot;
        var dark = Brand.IsDark();
        if (_glassDark != dark)
        {
            _glassDark = dark;
            _popup.SystemBackdrop = new GlassBackdrop(dark);
        }
        _pane.BorderBrush = Brand.Brush(0xFFFFFF, (byte)(dark ? 0x1F : 0x8C));
        _text.Foreground = Fluent.Resource<Brush>("TextFillColorPrimaryBrush", dark);
        _text.Text = line;

        _pane.Measure(new Size(MaxWidth, double.PositiveInfinity));
        var size = _pane.DesiredSize;
        var aim = anchor.Y + anchor.Height / 2;
        var top = Math.Clamp(aim - size.Height / 2, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - 6 - size.Height));
        _popup.HorizontalOffset = characterRight ? anchor.Left - Gap - size.Width : anchor.Right + Gap;
        _popup.VerticalOffset = top;
        _pane.CenterPoint = new Vector3((float)(characterRight ? size.Width : 0), (float)Math.Clamp(aim - top, 0, size.Height), 0);

        _reduced = reducedMotion;
        _close.Stop();
        _linger.Stop();
        var fresh = !_popup.IsOpen;
        _pane.Opacity = 0;
        _pane.Scale = reducedMotion ? Vector3.One : new Vector3(StartScale, StartScale, 1);
        if (fresh) _popup.IsOpen = true;
        Animate("Opacity", 1f, EnterDuration);
        if (!reducedMotion) Animate("Scale", Vector3.One, EnterDuration);
        _linger.Start();
    }

    /// animated fades the line away; otherwise it goes at once.
    public void Hide(bool animated)
    {
        _linger.Stop();
        if (!_popup.IsOpen) return;
        if (!animated)
        {
            _close.Stop();
            _popup.IsOpen = false;
            return;
        }
        Animate("Opacity", 0f, ExitDuration);
        if (!_reduced) Animate("Scale", new Vector3(0.96f, 0.96f, 1), ExitDuration);
        _close.Start();
    }

    private void Animate(string property, float to, TimeSpan duration)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Target = property;
        animation.InsertKeyFrame(1, to, _easeOut);
        animation.Duration = duration;
        _pane.StartAnimation(animation);
    }

    private void Animate(string property, Vector3 to, TimeSpan duration)
    {
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Target = property;
        animation.InsertKeyFrame(1, to, _easeOut);
        animation.Duration = duration;
        _pane.StartAnimation(animation);
    }
}
