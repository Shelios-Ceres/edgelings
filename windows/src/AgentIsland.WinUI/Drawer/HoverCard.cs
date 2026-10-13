using System.Numerics;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// The drawer's hover card: a pane of frosted glass beside the character it
/// describes, each quota window as a filled bar in that character's colour.
/// It is a windowed popup, so it can extend past the drawer's narrow window,
/// and the glass is the system's own acrylic, blurring whatever is behind.
internal sealed class HoverCard
{
    private const double CardWidth = 252;
    private const double Gap = 10;
    private const double LabelWidth = 44;
    private const double ValueWidth = 64;

    // Motion: the card grows out of the side facing the character and drifts
    // in from it on an ease-out cubic — no overshoot, and gentle enough that
    // the growth reads instead of popping in the first few frames.
    private const float StartScale = 0.95f;
    private const float Drift = 8;
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(120);

    private readonly Popup _popup;
    private readonly Grid _host = new();
    private readonly Border _card;
    private readonly StackPanel _body = new();
    private readonly Panel _root;
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _easeOut;
    private readonly DispatcherQueueTimer _closeTimer;
    private int _generation;
    private bool _closing;
    private bool _reduced;
    private bool? _glassDark;
    private static bool _dark;

    public HoverCard(Panel root)
    {
        _root = root;
        _card = new Border
        {
            Child = _body,
            Width = CardWidth,
            Padding = new Thickness(14, 12, 14, 13),
            CornerRadius = new CornerRadius(8),
        };
        _host.Children.Add(_card);
        // The element's own Scale/Translation/Opacity are animated, so its
        // composition visual must not be handed off; any compositor will do.
        _compositor = CompositionTarget.GetCompositorForCurrentThread();
        _easeOut = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f));
        _popup = new Popup
        {
            Child = _host,
            ShouldConstrainToRootBounds = false,
            IsHitTestVisible = false,
        };
        root.Children.Add(_popup);
        _closeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) =>
        {
            if (!_closing) return;
            _closing = false;
            _popup.IsOpen = false;
        };
    }

    /// Open and not on its way out.
    public bool IsOpen => _popup.IsOpen && !_closing;

    /// The card's content, for the verification snapshot.
    public UIElement Visual => _host;

    /// anchor and area are in the drawer window's DIPs; characterRight means
    /// the character is to the card's right (drawer on the right edge).
    /// retarget means an open card now describes another character: it swaps
    /// in place.
    public void Show(IReadOnlyList<(StatusCard Card, uint Swatch)> sections, bool characterRight, Rect anchor, Rect area, bool reducedMotion, bool retarget = false)
    {
        // Before the drawer's content loads there is no root to open into.
        if (_root.XamlRoot is not { } xamlRoot) return;
        _popup.XamlRoot = xamlRoot;
        var scale = xamlRoot.RasterizationScale;
        var dark = Brand.IsDark();
        _dark = dark;
        if (_glassDark != dark)
        {
            // The glass follows the system theme; a new backdrop per theme.
            _glassDark = dark;
            _popup.SystemBackdrop = new GlassBackdrop(dark);
        }
        // No outline: the glass is the edge. A card about one character is
        // washed in its colour; the all-characters card stays neutral.
        _card.Background = sections.Count == 1 ? GlassBackdrop.Wash(sections[0].Swatch, dark) : null;
        Build(sections);

        _host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _host.DesiredSize;
        var aim = anchor.Y + anchor.Height / 2;
        var top = Math.Clamp(aim - size.Height / 2, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - 6 - size.Height));
        _popup.HorizontalOffset = GlassBackdrop.Snap(characterRight ? anchor.Left - Gap - CardWidth : anchor.Right + Gap, scale);
        _popup.VerticalOffset = GlassBackdrop.Snap(top, scale);

        // Grows from the edge that faces the character, level with it.
        var faceY = Math.Clamp(aim - top, 0, size.Height);
        _host.CenterPoint = new Vector3((float)(characterRight ? CardWidth : 0), (float)faceY, 0);
        _reduced = reducedMotion;
        var fresh = !_popup.IsOpen;
        var returning = _closing;
        _closing = false;
        _closeTimer.Stop();
        if (fresh)
        {
            _generation++;
            // The first frame must already be the starting pose: opening at
            // full size and only then shrinking to grow is the jump.
            _host.Opacity = 0;
            _host.Scale = reducedMotion ? Vector3.One : new Vector3(StartScale, StartScale, 1);
            _host.Translation = reducedMotion ? Vector3.Zero : new Vector3(characterRight ? Drift : -Drift, 0, 0);
            _popup.IsOpen = true;
            AfterFirstFrame(_generation, Enter);
        }
        else if (returning)
        {
            Enter();
        }
        else if (retarget)
        {
            _host.Opacity = 0.35;
            Fade(1, FadeDuration);
        }
    }

    /// animated fades the card out; otherwise it goes at once (drags, menus,
    /// the drawer hiding).
    public void Hide(bool animated = false)
    {
        if (!_popup.IsOpen) return;
        _generation++;
        if (!animated || _closing)
        {
            _closeTimer.Stop();
            _closing = false;
            _popup.IsOpen = false;
            return;
        }
        _closing = true;
        Fade(0, ExitDuration);
        if (!_reduced) Move("Scale", new Vector3(0.97f, 0.97f, 1), ExitDuration);
        _closeTimer.Interval = ExitDuration + TimeSpan.FromMilliseconds(20);
        _closeTimer.Start();
    }

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

        // Fixed label and value columns line the rows up across sections.
        var top = 7.0;
        if (card.Tasks is { } tasks)
        {
            var line = Line(top);
            Place(line, Caption(tasks.Label, "TextFillColorSecondaryBrush"), 0, 0);
            Place(line, Emphasised(tasks.Text, tasks.Number), 0, 1);
            panel.Children.Add(line);
            top = 4;
        }
        var meters = new Grid { Margin = new Thickness(13, top, 0, 0), RowSpacing = 4, ColumnSpacing = 8 };
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
        if (row > 0)
        {
            panel.Children.Add(meters);
            top = 4;
        }
        if (card.Tokens is { } tokens)
        {
            var line = Line(top);
            Place(line, Caption(tokens.Label, "TextFillColorSecondaryBrush"), 0, 0);
            Place(line, Emphasised(tokens.Today, tokens.TodayNumber), 0, 1);
            var month = Emphasised(tokens.Month, tokens.MonthNumber);
            month.HorizontalAlignment = HorizontalAlignment.Right;
            Place(line, month, 0, 2);
            panel.Children.Add(line);
        }
        if (card.Note.Count > 0)
        {
            var note = new TextBlock
            {
                FontSize = 12,
                Foreground = Res<Brush>("TextFillColorTertiaryBrush"),
                Margin = new Thickness(13, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            for (var i = 0; i < card.Note.Count; i++)
            {
                if (i > 0) note.Inlines.Add(new Run { Text = " · " });
                Append(note, card.Note[i].Text, card.Note[i].Numbers);
            }
            panel.Children.Add(note);
        }
        return panel;
    }

    /// A label column, a stretching value and an end-aligned value.
    private static Grid Line(double top)
    {
        var line = new Grid { Margin = new Thickness(13, top, 0, 0), ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return line;
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

    /// A tertiary phrase whose numbers are lifted to primary ink.
    private static TextBlock Emphasised(string phrase, params string[] numbers)
    {
        var text = new TextBlock { FontSize = 12, Foreground = Res<Brush>("TextFillColorTertiaryBrush") };
        Append(text, phrase, numbers);
        return text;
    }

    /// Adds phrase to text, lifting each number, found in order, to primary ink.
    private static void Append(TextBlock text, string phrase, IReadOnlyList<string> numbers)
    {
        var from = 0;
        foreach (var number in numbers)
        {
            var at = phrase.IndexOf(number, from, StringComparison.Ordinal);
            if (at < 0 || number.Length == 0) continue;
            if (at > from) text.Inlines.Add(new Run { Text = phrase[from..at] });
            text.Inlines.Add(new Run
            {
                Text = number,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Res<Brush>("TextFillColorPrimaryBrush"),
            });
            from = at + number.Length;
        }
        if (from < phrase.Length) text.Inlines.Add(new Run { Text = phrase[from..] });
    }

    /// Grows out of the pointer's tip and settles from its drift; reduced
    /// motion keeps only the fade. Runs on the compositor, not the UI thread.
    private void Enter()
    {
        Fade(1, FadeDuration);
        if (_reduced) return;
        Move("Scale", Vector3.One, EnterDuration);
        Move("Translation", Vector3.Zero, EnterDuration);
    }

    private void Fade(float to, TimeSpan duration)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(1, to, _easeOut);
        fade.Duration = duration;
        _host.StartAnimation(fade);
    }

    private void Move(string property, Vector3 to, TimeSpan duration)
    {
        var move = _compositor.CreateVector3KeyFrameAnimation();
        move.Target = property;
        move.InsertKeyFrame(1, to, _easeOut);
        move.Duration = duration;
        _host.StartAnimation(move);
    }

    /// Runs once the popup has put a frame on screen at the starting pose,
    /// unless the card was hidden meanwhile; content refreshes keep the entry.
    private void AfterFirstFrame(int generation, Action action)
    {
        var frames = 0;
        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            if (++frames < 2) return;
            CompositionTarget.Rendering -= tick;
            if (generation == _generation) action();
        };
        CompositionTarget.Rendering += tick;
    }

    private static T Res<T>(string key) => Fluent.Resource<T>(key, _dark);
}
