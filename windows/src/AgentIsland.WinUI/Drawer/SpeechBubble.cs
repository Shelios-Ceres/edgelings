using System.Numerics;
using AgentIsland.Core;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// How a line pops (M5): a finished agent's line hops, a question or a
/// request knocks, trouble shakes, and the locked drawer's answer just pops.
internal enum LineKind
{
    Done,
    Waiting,
    Error,
    Locked,
}

/// A character's line beside it: paper like the hover card's, its tail
/// centred on the speaker, or with the pixel bots a pixel panel pointing at
/// it, the line in English in the pixel face. It pops from the speaker and
/// goes on its own after a while (M5).
internal sealed class SpeechBubble
{
    private const double MaxWidth = 240;
    private const double Gap = 10;
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(4.5);

    private readonly Panel _root;
    private static readonly Thickness TextPadding = new(14, 9, 14, 10);
    private static readonly Thickness PaperTextPadding = new(15, 9, 15, 10);
    private const double TailReach = 7;
    // The popup window leaves room around the pane for the paper's shadow.
    private static readonly Thickness ShadowRoom = new(16, 12, 16, 24);

    private readonly Popup _popup;
    private readonly Border _pane;
    private readonly Grid _content = new();
    private readonly PixelPlate _plate = new() { Visibility = Visibility.Collapsed };
    private readonly PaperPlate _paper = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _text = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = MaxWidth - 28 };
    private readonly DispatcherQueueTimer _linger;
    private UiMoment? _motion;
    private bool _pixel;
    private bool _reduced;
    private bool _characterRight;
    private Size _plateSize;
    private double _aim;
    private Windows.UI.Color _outline;
    private bool _dark;

    public SpeechBubble(Panel root, bool inWindow = false)
    {
        _root = root;
        _content.Children.Add(_plate);
        _content.Children.Add(_paper);
        _content.Children.Add(_text);
        _pane = new Border { Child = _content, Padding = TextPadding };
        _popup = new Popup
        {
            Child = new Border { Child = _pane, Padding = ShadowRoom },
            ShouldConstrainToRootBounds = inWindow,
            IsHitTestVisible = false,
        };
        root.Children.Add(_popup);
        _linger = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _linger.IsRepeating = false;
        _linger.Interval = Linger;
        _linger.Tick += (_, _) => Hide(animated: true);
    }

    public bool IsOpen => _popup.IsOpen;

    /// The pop each kind of line gets.
    public static LineKind KindOf(AgentPhase phase) => phase switch
    {
        AgentPhase.Finished => LineKind.Done,
        AgentPhase.Failed or AgentPhase.NetworkTrouble => LineKind.Error,
        _ => LineKind.Waiting,
    };

    /// anchor and area are in the drawer window's DIPs; characterRight means
    /// the character is to the bubble's right.
    /// swatch is the speaking character's colour, outlining the pixel panel.
    public void Say(string line, uint swatch, bool pixel, bool characterRight, Rect anchor, Rect area, bool reducedMotion, LineKind kind)
    {
        if (_root.XamlRoot is not { } xamlRoot) return;
        _popup.XamlRoot = xamlRoot;
        var scale = xamlRoot.RasterizationScale;
        var dark = Brand.IsDark();
        // The pane's padding on the speaker's side holds the pointer or tail.
        var reach = pixel ? PixelPlate.PointerReach : TailReach + 1;
        _pane.Padding = characterRight ? new Thickness(0, 0, reach, 0) : new Thickness(reach, 0, 0, 0);
        if (pixel)
        {
            _text.Margin = TextPadding;
            _text.FontFamily = PixelPlate.Font;
            _text.FontSize = PixelPlate.FontSize(scale);
            _text.ClearValue(TextBlock.LineHeightProperty);
            _text.Foreground = Fluent.Resource<Brush>("TextFillColorPrimaryBrush", dark);
        }
        else
        {
            _text.Margin = PaperTextPadding;
            _text.ClearValue(TextBlock.FontFamilyProperty);
            _text.FontSize = 13;
            _text.LineHeight = 18;
            _text.Foreground = ClassicTokens.Brush("Text", dark);
        }
        _plate.Visibility = pixel ? Visibility.Visible : Visibility.Collapsed;
        _paper.Visibility = pixel ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = line;

        _pane.Measure(new Size(MaxWidth, double.PositiveInfinity));
        var size = _pane.DesiredSize;
        var aim = anchor.Y + anchor.Height / 2;
        var top = Math.Clamp(aim - size.Height / 2, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - 6 - size.Height));
        _pixel = pixel;
        _reduced = reducedMotion;
        _characterRight = characterRight;
        _plateSize = new Size(size.Width - reach, size.Height);
        _aim = aim - top;
        _outline = Brand.Rgb(swatch);
        _dark = dark;
        if (pixel) DrawPlate(_plateSize.Width, _plateSize.Height, 0, 0);
        else
        {
            _paper.Set(_plateSize.Width, size.Height, radius: 12, halfBase: 6, reach: TailReach, pointLeft: !characterRight,
                tailY: _aim, ClassicTokens.Get("Paper", dark), ClassicTokens.Get("Hairline", dark));
            _paper.HorizontalAlignment = characterRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        _popup.HorizontalOffset = Pixels.Snap((characterRight ? anchor.Left - Gap - size.Width : anchor.Right + Gap) - ShadowRoom.Left, scale);
        _popup.VerticalOffset = Pixels.Snap(top - ShadowRoom.Top, scale);
        // Classic grows from the tail's tip.
        _pane.CenterPoint = new Vector3((float)(characterRight ? size.Width : 0), (float)Math.Clamp(_aim, 0, size.Height), 0);

        _linger.Stop();
        _motion?.Stop();
        Rest();
        if (!_popup.IsOpen) _popup.IsOpen = true;
        Pop(kind);
        _linger.Start();
    }

    /// animated plays the dismissal; otherwise the line goes at once.
    public void Hide(bool animated)
    {
        _linger.Stop();
        if (!_popup.IsOpen) return;
        _motion?.Stop();
        if (!animated || (_pixel && _reduced))
        {
            Close();
            return;
        }
        if (_pixel)
        {
            // The text goes and a ghost at half the bubble is left at the
            // pointer for a tick, then nothing.
            _motion = UiMoment.Play(2 * Motion.TickMs, t =>
            {
                if (Motion.Tick(t) >= 1) Ghost(0.5);
            }, Close);
            return;
        }
        // Classic shrinks toward the tail as it fades; reduced, it fades.
        var opacity = _pane.Opacity;
        var scale = _pane.Scale.X;
        var reduced = _reduced;
        _motion = UiMoment.Play(reduced ? 83 : 167, t =>
        {
            _pane.Opacity = opacity * (1 - (reduced ? Motion.Linear(t / 83) : Motion.In(t / 167)));
            if (!reduced) _pane.Scale = Uniform(Lerp(scale, 0.85, Motion.In(t / 167)));
        }, Close);
    }

    /// M5. Pixel: a ghost at a third of the bubble, one a cell larger all
    /// round (the overshoot), then the bubble; done then hops, and error
    /// shakes in place of the overshoot. Classic grows from the tail with a
    /// small overshoot; done then hops and lands with a squash, waiting
    /// knocks twice, and error grows without overshoot and shakes. Reduced:
    /// Pixel shows at once, Classic fades in.
    private void Pop(LineKind kind)
    {
        if (_pixel)
        {
            if (_reduced) return;
            var ticks = kind switch { LineKind.Done => 5, LineKind.Error => 4, _ => 3 };
            _motion = UiMoment.Play(ticks * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k == 0) { _pane.Opacity = 0; return; }
                _pane.Opacity = 1;
                if (k == 1) Ghost(1 / 3.0);
                else if (kind == LineKind.Error)
                {
                    Whole();
                    _pane.Translation = new Vector3(k switch { 2 => 2, 3 => -2, _ => 0 }, 0, 0);
                }
                else if (k == 2) Overshoot();
                else
                {
                    Whole();
                    _pane.Translation = new Vector3(0, kind == LineKind.Done && k == 4 ? -2 : 0, 0);
                }
            }, Rest);
            return;
        }
        if (_reduced)
        {
            _motion = UiMoment.Play(83, t => _pane.Opacity = Motion.Linear(t / 83), Rest);
            return;
        }
        var length = kind switch { LineKind.Done => 917, LineKind.Waiting => 833, LineKind.Error => 500, _ => 333 };
        _motion = UiMoment.Play(length, t =>
        {
            _pane.Opacity = Motion.Linear(t / 83);
            if (kind == LineKind.Error)
            {
                _pane.Scale = Uniform(Lerp(0.6, 1, Motion.Out(t / 167)));
                _pane.Translation = new Vector3((float)Keys(Motion.InOut((t - 167) / 333), 0, 5, -5, 3, -2, 0), 0, 0);
                return;
            }
            var scale = Lerp(0.6, 1, Motion.Pop(t / 333));
            var squash = Vector2.One;
            var y = 0.0;
            if (kind == LineKind.Done)
            {
                y = t < 500 ? Lerp(0, -5, Motion.Out((t - 333) / 167)) : Lerp(-5, 0, Motion.In((t - 500) / 167));
                if (t >= 667)
                {
                    var land = Motion.Pop((t - 667) / 250);
                    squash = new Vector2((float)Lerp(1.04, 1, land), (float)Lerp(0.94, 1, land));
                }
            }
            else if (kind == LineKind.Waiting)
            {
                // Two knocks: up 4 % and back, the second a quarter-second on.
                if (t >= 583) scale *= Keys(Motion.InOut((t - 583) / 250), 1, 1.04, 1);
                else if (t >= 333) scale *= Keys(Motion.InOut((t - 333) / 250), 1, 1.04, 1);
            }
            _pane.Scale = new Vector3((float)scale * squash.X, (float)scale * squash.Y, 1);
            _pane.Translation = new Vector3(0, (float)y, 0);
        }, Rest);
    }

    /// The pixel panel at a share of the bubble's size and no text, grown
    /// from the pointer: on the edge facing the speaker, centred on it.
    private void Ghost(double share)
    {
        _text.Visibility = Visibility.Collapsed;
        var cell = PixelPlate.Cell;
        var width = Math.Max(3 * cell, Motion.Cell(_plateSize.Width * share));
        var height = Math.Max(3 * cell, Motion.Cell(_plateSize.Height * share));
        var x = _characterRight ? _plateSize.Width - width : 0;
        var y = Math.Clamp(Motion.Cell(_aim - height / 2), 0, Math.Max(0, _plateSize.Height - height));
        DrawPlate(width, height, x, y);
    }

    /// The pixel panel a cell larger than the bubble on every side, no text.
    private void Overshoot()
    {
        _text.Visibility = Visibility.Collapsed;
        var cell = PixelPlate.Cell;
        DrawPlate(_plateSize.Width + 2 * cell, _plateSize.Height + 2 * cell, -cell, -cell);
    }

    private void Whole()
    {
        _text.Visibility = Visibility.Visible;
        if (_pixel) DrawPlate(_plateSize.Width, _plateSize.Height, 0, 0);
    }

    /// The pixel panel at a size and an offset within the bubble, pointing
    /// at the speaker.
    private void DrawPlate(double width, double height, double x, double y)
    {
        _plate.Set(width, height, _outline, HoverCard.PixelFill(_dark), pointLeft: !_characterRight, pointerY: _aim - y);
        _plate.HorizontalAlignment = HorizontalAlignment.Left;
        _plate.VerticalAlignment = VerticalAlignment.Top;
        _plate.RenderTransform = x == 0 && y == 0 ? null : new TranslateTransform { X = x, Y = y };
    }

    private void Rest()
    {
        _motion?.Stop();
        _motion = null;
        _pane.Opacity = 1;
        _pane.Scale = Vector3.One;
        _pane.Translation = Vector3.Zero;
        Whole();
    }

    private void Close()
    {
        _motion?.Stop();
        _motion = null;
        _popup.IsOpen = false;
        Rest();
    }

    private static Vector3 Uniform(double scale) => new((float)scale, (float)scale, 1);

    private static double Lerp(double from, double to, double share) => from + (to - from) * share;

    /// Evenly spaced keys, eased as one: where `share` falls between them.
    private static double Keys(double share, params double[] keys)
    {
        var at = Math.Clamp(share, 0, 1) * (keys.Length - 1);
        var i = Math.Min((int)at, keys.Length - 2);
        return Lerp(keys[i], keys[i + 1], at - i);
    }
}
