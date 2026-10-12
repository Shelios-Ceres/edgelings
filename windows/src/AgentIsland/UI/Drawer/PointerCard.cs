using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI.Drawer;

/// One quota window on the card. Shown is the 0-1 share the bar fills (used
/// or remaining, per the user's mode) and Used drives the warning tint; both
/// are null when the window failed to load.
public sealed record CardMeter(string Label, string Value, double? Shown, double? Used);

/// Local token counts for the card's last row; each Number is the part of
/// its phrase that gets emphasised.
public sealed record CardTokens(string Label, string Today, string TodayNumber, string Month, string MonthNumber);

/// One character's slice of the card.
public sealed record CardSection(
    string Name, string Phase, string? Detail, Color Accent, IReadOnlyList<CardMeter> Meters, CardTokens? Tokens,
    string? Note);

/// The drawer's hover card: a white Grok card whose pointer aims at the
/// character it describes, each quota window drawn as a thin bar in that
/// character's colour. The tray's reveal stacks every character's section.
public sealed class PointerCard : FrameworkElement
{
    /// Margin around the card that the soft shadow spreads into.
    public const double Room = 16;

    private const double PadX = 12;
    private const double PadY = 10;
    private const double Radius = 10;
    private const double Tip = 7;
    private const double MinContent = 176;
    private const double MinBar = 64;
    private const double DotColumn = 13;
    private const double MeterTop = 7;
    private const double MeterHeight = 15;
    private const double MeterGap = 3;
    private const double SectionGap = 9;
    private static readonly FontFamily Family = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private static readonly Typeface TitleFace = new(Family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface BodyFace = new(Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Color Warn = Color.FromRgb(0xE5, 0x48, 0x4D);
    private static readonly Brush Paper = GrokTheme.Brush(GrokTheme.Elevated);
    private static readonly Brush Track = GrokTheme.InkBrush(GrokTheme.Tertiary);
    private static readonly Pen Edge = FrozenPen(GrokTheme.InkA(GrokTheme.StrokeTertiary));

    private sealed record Meter(FormattedText Label, FormattedText Value, double? Shown, double? Used);

    private sealed record Tokens(FormattedText Label, FormattedText Today, FormattedText Month);

    private sealed record Section(
        FormattedText Name, FormattedText Phase, FormattedText? Detail, Brush Accent, Color AccentColor,
        IReadOnlyList<Meter> Meters, Tokens? Tokens, FormattedText? Note);

    private readonly List<Section> _sections = new();
    private Size _card;
    private double _labelWidth;
    private double _valueWidth;
    private double _pointerY;
    private bool _pointRight = true;

    public PointerCard()
    {
        RenderTransform = new ScaleTransform(1, 1);
        IsHitTestVisible = false;
        Effect = new DropShadowEffect
        {
            ShadowDepth = 3,
            Direction = 270,
            BlurRadius = 14,
            Color = GrokTheme.Ink,
            Opacity = 0.16,
        };
    }

    /// The card body, without the pointer or the shadow room.
    public Size CardSize => _card;

    /// Where the pointer's tip sits inside this element.
    public Point TipOffset => new(_pointRight ? Room + _card.Width + Tip : Room, Room + _pointerY);

    public void SetContent(IReadOnlyList<CardSection> sections, bool pointRight)
    {
        _pointRight = pointRight;
        _sections.Clear();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double text = 0, height = 0, tokenRow = 0;
        _labelWidth = _valueWidth = 0;
        foreach (var section in sections)
        {
            var name = Text(section.Name, 13, TitleFace, GrokTheme.InkA(GrokTheme.TextPrimary), dpi);
            var phase = Text(" · " + section.Phase, 13, BodyFace, GrokTheme.InkA(GrokTheme.TextSecondary), dpi);
            var detail = section.Detail is { } d ? Text(d, 11.5, BodyFace, GrokTheme.InkA(GrokTheme.TextTertiary), dpi) : null;
            var note = section.Note is { } n ? Text(n, 11, BodyFace, GrokTheme.InkA(GrokTheme.TextTertiary), dpi) : null;
            var meters = section.Meters.Select(m => new Meter(
                Text(m.Label, 11, BodyFace, GrokTheme.InkA(GrokTheme.TextTertiary), dpi),
                Text(m.Value, 11, BodyFace, m.Used >= 0.9 ? Warn : GrokTheme.InkA(GrokTheme.TextSecondary), dpi),
                m.Shown,
                m.Used)).ToList();
            foreach (var meter in meters)
            {
                _labelWidth = Math.Max(_labelWidth, meter.Label.WidthIncludingTrailingWhitespace);
                _valueWidth = Math.Max(_valueWidth, meter.Value.WidthIncludingTrailingWhitespace);
            }
            Tokens? tokens = null;
            if (section.Tokens is { } t)
            {
                tokens = new Tokens(
                    Text(t.Label, 11, BodyFace, GrokTheme.InkA(GrokTheme.TextTertiary), dpi),
                    Emphasised(t.Today, t.TodayNumber, dpi),
                    Emphasised(t.Month, t.MonthNumber, dpi));
                _labelWidth = Math.Max(_labelWidth, tokens.Label.WidthIncludingTrailingWhitespace);
                tokenRow = Math.Max(tokenRow,
                    tokens.Today.WidthIncludingTrailingWhitespace + 16 + tokens.Month.WidthIncludingTrailingWhitespace);
            }
            text = Math.Max(text, name.WidthIncludingTrailingWhitespace + phase.WidthIncludingTrailingWhitespace);
            text = Math.Max(text, Math.Max(detail?.WidthIncludingTrailingWhitespace ?? 0, note?.WidthIncludingTrailingWhitespace ?? 0));

            if (_sections.Count > 0) height += 2 * SectionGap + 1;
            height += name.Height + (detail?.Height ?? 0);
            var rows = meters.Count + (tokens is null ? 0 : 1);
            if (rows > 0) height += MeterTop + rows * MeterHeight + (rows - 1) * MeterGap;
            if (note is not null) height += 4 + note.Height;
            _sections.Add(new Section(name, phase, detail, GrokTheme.Brush(section.Accent), section.Accent, meters, tokens, note));
        }
        var meterRow = _labelWidth + 8 + Math.Max(MinBar + 8 + _valueWidth, tokenRow);
        var content = DotColumn + Math.Max(Math.Max(text, meterRow), MinContent - DotColumn);
        _card = new Size(Math.Ceiling(content + 2 * PadX), Math.Ceiling(height + 2 * PadY));
        _pointerY = _card.Height / 2;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// Moves the pointer to a height on the card (when the card had to be
    /// nudged to stay on screen), kept clear of the rounded corners.
    public void AimAt(double y)
    {
        _pointerY = Math.Clamp(y, Radius + Tip, Math.Max(Radius + Tip, _card.Height - Radius - Tip));
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(_card.Width + Tip + 2 * Room, _card.Height + 2 * Room);

    protected override void OnRender(DrawingContext dc)
    {
        if (_sections.Count == 0) return;
        var card = new Rect(Room + (_pointRight ? 0 : Tip), Room, _card.Width, _card.Height);
        var y0 = card.Top + _pointerY;
        var baseX = _pointRight ? card.Right - 1 : card.Left + 1;
        var tipX = _pointRight ? card.Right + Tip : card.Left - Tip;
        var pointer = new StreamGeometry();
        using (var g = pointer.Open())
        {
            g.BeginFigure(new Point(baseX, y0 - Tip), true, true);
            g.LineTo(new Point(tipX, y0), true, true);
            g.LineTo(new Point(baseX, y0 + Tip), true, true);
        }
        var shape = new CombinedGeometry(GeometryCombineMode.Union, new RectangleGeometry(card, Radius, Radius), pointer);
        dc.DrawGeometry(Paper, Edge, shape);

        var left = card.Left + PadX;
        var indent = left + DotColumn;
        var right = card.Right - PadX;
        var y = card.Top + PadY;
        for (var i = 0; i < _sections.Count; i++)
        {
            var section = _sections[i];
            if (i > 0)
            {
                y += SectionGap;
                dc.DrawRectangle(Edge.Brush, null, new Rect(left, y, right - left, 1));
                y += 1 + SectionGap;
            }
            dc.DrawEllipse(section.Accent, null, new Point(left + 3.5, y + section.Name.Height / 2), 3.5, 3.5);
            dc.DrawText(section.Name, new Point(indent, y));
            dc.DrawText(section.Phase, new Point(indent + section.Name.WidthIncludingTrailingWhitespace, y));
            y += section.Name.Height;
            if (section.Detail is { } detail)
            {
                dc.DrawText(detail, new Point(indent, y));
                y += detail.Height;
            }
            if (section.Meters.Count > 0 || section.Tokens is not null)
            {
                y += MeterTop;
                var barLeft = indent + _labelWidth + 8;
                var barRight = right - _valueWidth - 8;
                foreach (var meter in section.Meters)
                {
                    var middle = y + MeterHeight / 2;
                    dc.DrawText(meter.Label, new Point(indent, middle - meter.Label.Height / 2));
                    var bar = new Rect(barLeft, middle - 2, barRight - barLeft, 4);
                    dc.DrawRoundedRectangle(Track, null, bar, 2, 2);
                    if (meter.Shown is > 0 and var shown)
                    {
                        var fill = meter.Used >= 0.9 ? GrokTheme.Brush(Warn) : section.Accent;
                        var width = Math.Max(4, bar.Width * Math.Min(1, shown));
                        dc.DrawRoundedRectangle(fill, null, new Rect(bar.Left, bar.Top, width, bar.Height), 2, 2);
                    }
                    dc.DrawText(meter.Value, new Point(right - meter.Value.WidthIncludingTrailingWhitespace, middle - meter.Value.Height / 2));
                    y += MeterHeight + MeterGap;
                }
                if (section.Tokens is { } tokens)
                {
                    var middle = y + MeterHeight / 2;
                    dc.DrawText(tokens.Label, new Point(indent, middle - tokens.Label.Height / 2));
                    dc.DrawText(tokens.Today, new Point(barLeft, middle - tokens.Today.Height / 2));
                    dc.DrawText(tokens.Month, new Point(right - tokens.Month.WidthIncludingTrailingWhitespace, middle - tokens.Month.Height / 2));
                    y += MeterHeight + MeterGap;
                }
                y -= MeterGap;
            }
            if (section.Note is { } note)
            {
                y += 4;
                dc.DrawText(note, new Point(indent, y));
                y += note.Height;
            }
        }
    }

    /// Grows out of the pointer's tip: a short, slightly overshooting scale
    /// plus a fade; reduced motion keeps only the fade.
    public void PlayIn(bool reducedMotion)
    {
        var size = DesiredSize;
        var tip = TipOffset;
        RenderTransformOrigin = size.Width > 0 && size.Height > 0
            ? new Point(tip.X / size.Width, tip.Y / size.Height)
            : new Point(_pointRight ? 1 : 0, 0.5);
        var scale = (ScaleTransform)RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(reducedMotion ? 120 : 140)));
        if (reducedMotion) return;
        var pop = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private static FormattedText Text(string text, double size, Typeface face, Color color, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, GrokTheme.Brush(color), dpi);

    /// Tertiary phrase with its number lifted to secondary ink and medium weight.
    private static FormattedText Emphasised(string phrase, string number, double dpi)
    {
        var text = Text(phrase, 11, BodyFace, GrokTheme.InkA(GrokTheme.TextTertiary), dpi);
        var at = phrase.IndexOf(number, StringComparison.Ordinal);
        if (at >= 0)
        {
            text.SetForegroundBrush(GrokTheme.InkBrush(GrokTheme.TextPrimary), at, number.Length);
            text.SetFontWeight(FontWeights.Medium, at, number.Length);
        }
        return text;
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(GrokTheme.Brush(color), 1);
        pen.Freeze();
        return pen;
    }
}
