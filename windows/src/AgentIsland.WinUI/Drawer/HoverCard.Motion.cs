using System.Numerics;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

// The card's moments (M3 open, close and switch; M4 refresh in place) and the clock they play on.
internal sealed partial class HoverCard
{
    /// M3 open. Pixel grows ghosts of the card from the pointer, a third and
    /// two thirds of it, then shows it whole; Classic comes out of the bot,
    /// growing from the tail's tip. Reduced, Pixel shows at once and Classic
    /// fades in.
    private void Open(Request request)
    {
        Present(request);
        Rest();
        _popup.IsOpen = true;
        if (request.Pixel)
        {
            if (request.ReducedMotion) return;
            Play(3 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k == 0) _host.Opacity = 0;
                else if (k < 3)
                {
                    _host.Opacity = 1;
                    Ghost(k / 3.0);
                }
                else Whole();
            }, CompletePresentation);
            return;
        }
        if (request.ReducedMotion)
        {
            Play(83, t => _host.Opacity = (float)Motion.Linear(t / 83), CompletePresentation);
            return;
        }
        Play(250, t => Opening(request, t), CompletePresentation);
    }

    /// Classic's M3 open, ms in: it fades in over 167 ms and grows from 0.9
    /// over 250, drifting in 8 DIP from the bot's side.
    private void Opening(Request request, double ms)
    {
        var drift = request.CharacterRight ? 8.0 : -8.0;
        _host.Opacity = (float)Motion.Linear(ms / 167);
        _host.Scale = Uniform(Lerp(0.9, 1, Motion.SoftPop(ms / 250)));
        _host.Translation = Target + new Vector3((float)Lerp(drift, 0, Motion.Out(ms / 250)), 0, 0);
    }

    /// A card on its way out comes back, from the frame on screen.
    private void Return(Request request)
    {
        if (request.Pixel || request.ReducedMotion)
        {
            CompletePresentation();
            return;
        }
        var opacity = _host.Opacity;
        var scale = _host.Scale.X;
        Play(250, t =>
        {
            _host.Opacity = (float)Lerp(opacity, 1, Motion.Linear(t / 167));
            _host.Scale = Uniform(Lerp(scale, 1, Motion.SoftPop(t / 250)));
        }, CompletePresentation);
    }

    /// M3 switch: the pointer moved to another bot while the card was open.
    /// Pixel swaps in the new card on the first tick and steps its y to the
    /// new bot (35 % of the way left, then there). Classic glides the paper's
    /// top, height and tail to the new bot while the old content fades out
    /// and the new fades in over it. Reduced, it swaps at once.
    private void Switch(Request request)
    {
        _closing = false;
        _refresh = null;
        if (request.ReducedMotion)
        {
            Present(request);
            CompletePresentation();
            return;
        }
        if (request.Pixel)
        {
            var from = _host.Translation.Y;
            var swapped = false;
            Play(2 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k < 1) return;
                if (!swapped)
                {
                    swapped = true;
                    Present(request);
                    Whole();
                    SettleHeight();
                }
                var to = _bounds.Top;
                var y = k >= 2 ? to : to - Motion.Cell((to - from) * 0.35);
                _host.Translation = new Vector3((float)_bounds.Left, (float)y, 0);
                DrawPlate(request, _width, _contentSize.Height, _aim - y);
            }, CompletePresentation);
            return;
        }
        var left = (double)_host.Translation.X;
        var top = (double)_host.Translation.Y;
        var width = _width;
        var height = _plateHeight;
        var aim = _aim;
        var reach = _reach;
        var outgoing = Detach();
        Present(request);
        if (Math.Abs(_width - width) > 0.5)
        {
            SwitchWidth(request, outgoing, new Rect(left, top, width, height), aim - top, reach);
            return;
        }
        Whole();
        _card.Opacity = 0;
        var top1 = _bounds.Top;
        var height1 = _contentSize.Height;
        var aim1 = _aim;
        var reach1 = request.Sections.Count == 1 ? TailReach : 0;
        Play(250, t =>
        {
            var e = Motion.InOut(t / 250);
            var y = Lerp(top, top1, e);
            var h = Lerp(height, height1, e);
            _host.Translation = new Vector3((float)_bounds.Left, (float)y, 0);
            DrawPaper(request, h, Lerp(aim, aim1, e) - y, Lerp(reach, reach1, e));
            _size.InsertScalar("Height", (float)Math.Max(1, h));
            outgoing.Opacity = (float)(1 - Motion.Linear(t / 83));
            _card.Opacity = (float)Motion.Linear((t - 42) / 167);
        }, () =>
        {
            Drop(outgoing);
            CompletePresentation();
        });
    }

    /// M3 switch to a card of another width under Classic (to or from the
    /// all-agents card in columns): the paper can't glide between widths, so
    /// the old card fades out over 83 ms on its own paper while the new one
    /// opens (M3) from 42 ms, the new over the old, and the card never reads
    /// blank. old is the old paper in drawer DIPs, aim and reach its tail's.
    private void SwitchWidth(Request request, Border outgoing, Rect old, double aim, double reach)
    {
        _host.Children.Remove(outgoing);
        _outgoing = null;
        var paper = new PaperPlate();
        paper.Set(old.Width, old.Height, radius: 16, halfBase: 10, reach: reach, pointLeft: !request.CharacterRight,
            tailY: reach > 0.01 ? aim : null, ClassicTokens.Get("Paper", request.Dark), ClassicTokens.Get("Hairline", request.Dark));
        var leaving = new Grid
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Translation = new Vector3((float)old.Left, (float)old.Top, 0),
            Children = { paper, outgoing },
        };
        Canvas.SetLeft(leaving, Canvas.GetLeft(_host));
        Canvas.SetTop(leaving, Canvas.GetTop(_host));
        _stage.Children.Insert(0, leaving);
        Rest();
        Play(42 + 250, t =>
        {
            leaving.Opacity = (float)(1 - Motion.Linear(t / 83));
            Opening(request, t - 42);
        }, CompletePresentation);
        _leaving = leaving;
    }

    // The old card fading out of a switch between widths.
    private Grid? _leaving;

    private void EndLeaving()
    {
        if (_leaving is not { } leaving) return;
        _leaving = null;
        _stage.Children.Remove(leaving);
        foreach (var layer in leaving.Children.OfType<Border>()) layer.Child = null;
        leaving.Children.Clear();
    }

    /// M4 under Classic: the paper glides to the new content's height and
    /// tail over 250 ms on inOut; each text that changed fades out over 83 ms
    /// as its new reading fades in after it over 167, settling 3 DIP, while
    /// its block eases from its old height to its new one with the paper, so
    /// the rows below glide and the bottom padding holds. The meters count by
    /// themselves. Content laid out anew cross-fades whole. Either way, while
    /// the paper glides, the content stays inside it, clear of its padding.
    private void Refresh(Request request)
    {
        var top = (double)_host.Translation.Y;
        var width = _width;
        var height = _plateHeight;
        var aim = _aim;
        var reach = _reach;
        // Read before the old content leaves the card, which resets its sizes.
        var olds = Texts(_body).Select(text => (Text: text, Slot: text.DesiredSize.Height)).ToList();
        var outgoing = Detach();
        _recount = true;
        Present(request);
        _recount = false;
        if (Math.Abs(_width - width) > 0.5)
        {
            // The all-agents card gained or lost a column: it swaps at once.
            Drop(outgoing);
            Rest();
            return;
        }
        Whole();
        var top1 = _bounds.Top;
        var height1 = _contentSize.Height;
        var aim1 = _aim;
        var reach1 = request.Sections.Count == 1 ? TailReach : 0;
        // The old texts against the new, in reading order, with the layout
        // slots (rounded, margins in) of those that changed.
        var news = Texts(_body).ToList();
        var swaps = new List<(FrameworkElement Old, FrameworkElement New, double From, double To, Thickness Margin)>();
        var whole = olds.Count != news.Count;
        if (!whole)
        {
            for (var i = 0; i < olds.Count; i++)
            {
                var (old, fresh) = (olds[i].Text, news[i]);
                if (Said(old) == Said(fresh) || _counting.Contains(fresh)) continue;
                swaps.Add((old, fresh, olds[i].Slot, fresh.DesiredSize.Height, fresh.Margin));
            }
            // A change in height the changed texts don't account for is a new layout.
            whole = Math.Abs(swaps.Sum(swap => swap.To - swap.From) - (height1 - height)) > 1;
        }
        if (whole) _card.Opacity = 0;
        else
        {
            Keep(outgoing, swaps.Select(swap => (DependencyObject)swap.Old).ToHashSet());
            foreach (var swap in swaps)
            {
                swap.New.Opacity = 0;
                _swapping.Add((swap.New, swap.Margin));
            }
        }
        // Room for the taller of the two, so a shrinking card isn't cut to its new height.
        _host.Height = _plate.Height = Math.Max(height, height1);
        var padding = _card.Padding.Bottom;
        Play(250, t =>
        {
            var e = Motion.InOut(t / 250);
            var y = Lerp(top, top1, e);
            var h = Lerp(height, height1, e);
            _host.Translation = new Vector3((float)_bounds.Left, (float)y, 0);
            DrawPaper(request, h, Lerp(aim, aim1, e) - y, Lerp(reach, reach1, e));
            // Both layers end above the paper's bottom padding.
            _size.InsertScalar("TargetHeight", (float)Math.Max(1, whole ? height1 : h));
            _size.InsertScalar("Height", (float)Math.Max(1, h - padding));
            outgoing.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _width, Math.Max(0, h - padding)) };
            var fadeOut = 1 - Motion.Linear(t / 83);
            var fadeIn = Motion.Linear((t - 83) / 167);
            if (whole)
            {
                outgoing.Opacity = fadeOut;
                _card.Opacity = fadeIn;
                return;
            }
            var rise = 3 * (1 - Motion.Out((t - 83) / 167));
            foreach (var (old, fresh, from, to, margin) in swaps)
            {
                old.Opacity = fadeOut;
                fresh.Opacity = fadeIn;
                fresh.Translation = new Vector3(0, (float)rise, 0);
                if (Math.Abs(to - from) < 0.5) continue;
                // The block between the rows eases from the old text's slot
                // to the new's; each text shows only inside it. The shift is
                // whole pixels: off by a hair, a slot on a half pixel rounds
                // the other way and the rows hop a pixel.
                var shift = Math.Round((from + (to - from) * e - to) * request.Scale) / request.Scale;
                fresh.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + shift);
                var block = to + shift - margin.Top - margin.Bottom;
                fresh.Clip = new RectangleGeometry { Rect = new Rect(-8, -rise, 10000, block) };
                old.Clip = new RectangleGeometry { Rect = new Rect(-8, 0, 10000, block) };
            }
        }, () =>
        {
            Drop(outgoing);
            CompletePresentation();
        });
    }

    /// The card's texts in reading order. A flow of phrases is one text:
    /// whatever it says, it swaps whole, its block easing with the paper.
    private static IEnumerable<FrameworkElement> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock or PhraseFlow) yield return (FrameworkElement)child;
            else foreach (var deeper in Texts(child)) yield return deeper;
        }
    }

    private static string Said(FrameworkElement text) => text switch
    {
        TextBlock block => block.Text,
        _ => string.Join("\n", Texts(text).Select(Said)),
    };

    /// The old content's layer keeps only `kept`: everything else is hidden,
    /// since the new content already shows it.
    private static void Keep(DependencyObject element, HashSet<DependencyObject> kept)
    {
        if (kept.Contains(element)) return;
        if (!Holds(element, kept))
        {
            if (element is UIElement shown) shown.Opacity = 0;
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Keep(VisualTreeHelper.GetChild(element, i), kept);
    }

    private static bool Holds(DependencyObject element, HashSet<DependencyObject> kept)
    {
        if (kept.Contains(element)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (Holds(VisualTreeHelper.GetChild(element, i), kept)) return true;
        }
        return false;
    }

    private void CompletePresentation()
    {
        _animating = false;
        Rest();
        if (_refresh is not { } refresh) return;
        _refresh = null;
        Present(refresh);
        Rest();
    }

    private void SettleHeight()
    {
        _size.InsertScalar("TargetHeight", (float)Math.Max(1, _contentSize.Height));
        _size.InsertScalar("Height", (float)Math.Max(1, _contentSize.Height));
    }

    /// Plays a card moment (M3), replacing any in flight; `completed` runs
    /// when it lands unless another replaces it first. The preview harness
    /// slows it with DebugDurationScale.
    private void Play(double ms, Action<double> frame, Action completed)
    {
        _motion?.Stop();
        EndLeaving();
        _animating = true;
        _transitions++;
        var generation = ++_generation;
        var slow = Math.Clamp(DebugDurationScale, 0.1, 8);
        _motion = UiMoment.Play(ms * slow, t => frame(t / slow), () =>
        {
            if (_disposed || generation != _generation) return;
            _motion = null;
            completed();
        });
    }

    private static Vector3 Uniform(double scale) => new((float)scale, (float)scale, 1);

    private static double Lerp(double from, double to, double share) => from + (to - from) * share;
}
