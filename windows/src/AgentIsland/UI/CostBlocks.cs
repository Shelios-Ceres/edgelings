using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentIsland.Cost;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// One provider's cost column.
public sealed class CostBlock : StackPanel
{
    private readonly TextBlock _hero;
    private readonly TextBlock _heroCaption;
    private readonly Sparkline _sparkline;
    private readonly TextBlock _monthLine;
    private readonly TextBlock _tokenLine;
    private readonly CountUp _countUp;
    private readonly bool _showsDollars;

    public CostBlock(Color tint, bool showsDollars = true)
    {
        _showsDollars = showsDollars;
        Orientation = Orientation.Vertical;
        Margin = new Thickness(12, 0, 12, 0);

        var heroRow = new StackPanel { Orientation = Orientation.Horizontal };
        // Ink hero with a count-up; the brand colour lives in the sparkline.
        // The dark island's spend glow has no light-surface equivalent.
        _hero = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
        };
        _countUp = new CountUp(_hero);
        _heroCaption = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 6),
            Text = Localization.L10n.Tr("today"),
        };
        heroRow.Children.Add(_hero);
        heroRow.Children.Add(_heroCaption);
        Children.Add(heroRow);

        _sparkline = new Sparkline(tint)
        {
            Height = 30,
            Margin = new Thickness(0, 8, 0, 8),
        };
        Children.Add(_sparkline);

        _monthLine = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextSecondary),
        };
        Children.Add(_monthLine);

        _tokenLine = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 10,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
            Margin = new Thickness(0, 3, 0, 0),
        };
        Children.Add(_tokenLine);
    }

    public void Update(ProviderCostSummary summary)
    {
        // Tokens-only providers (Cursor, Kimi Code, DeepSeek): no price, so
        // the tile leads with the token count, the month line counts tokens
        // too, and no dollar figure appears anywhere. This
        // ignores the cost-style preference because the dollar/trend styles
        // have nothing to show here, and a "$0.00" would be a fabricated
        // number the publish gate forbids. The cumulative series is dollars
        // (all zero without a price), so the sparkline sits flat and honest.
        if (!_showsDollars)
        {
            _countUp.Animate(summary.TodayTokens, "tokens",
                v => Core.Formatting.CompactTokens((long)Math.Round(v)));
            _heroCaption.Text = Localization.L10n.Tr("tokens today");
            _sparkline.SetSeries(summary.TodayCumulativeDollars);
            _monthLine.Text = Localization.L10n.TrFormat(
                "{0} tokens this month", Core.Formatting.CompactTokens(summary.MonthTokens));
            _tokenLine.Text = Localization.L10n.TrFormat(
                "{0} tokens · {1} billable",
                Core.Formatting.CompactTokens(summary.TodayTokens),
                Core.Formatting.CompactTokens(summary.TodayBillableTokens));
            return;
        }
        switch (CostStylePreferenceStore.Shared.Style)
        {
            case CostStyle.Tokens:
                _countUp.Animate(summary.TodayTokens, "tokens",
                    v => Core.Formatting.CompactTokens((long)Math.Round(v)));
                _heroCaption.Text = Localization.L10n.Tr("tokens today");
                _sparkline.SetSeries(summary.TodayCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens this month", Core.Formatting.CompactTokens(summary.MonthTokens));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} billable", Core.Formatting.CompactTokens(summary.TodayBillableTokens));
                break;
            case CostStyle.Trend:
                _countUp.Animate(summary.MonthDollars, "money", Core.Formatting.Money);
                _heroCaption.Text = Localization.L10n.Tr("this month");
                _sparkline.SetSeries(summary.MonthCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} today", Core.Formatting.Money(summary.TodayDollars));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens · {1} billable",
                    Core.Formatting.CompactTokens(summary.MonthTokens),
                    Core.Formatting.CompactTokens(summary.MonthBillableTokens));
                break;
            case CostStyle.Multi:
                _countUp.Animate(summary.TodayDollars, "money", Core.Formatting.Money);
                _heroCaption.Text = Localization.L10n.Tr("today");
                _sparkline.SetSeries(summary.TodayCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} this month · {1} tokens",
                    Core.Formatting.Money(summary.MonthDollars),
                    Core.Formatting.CompactTokens(summary.MonthTokens));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens · {1} billable",
                    Core.Formatting.CompactTokens(summary.TodayTokens),
                    Core.Formatting.CompactTokens(summary.TodayBillableTokens));
                break;
            case CostStyle.Dollar:
            default:
                _countUp.Animate(summary.TodayDollars, "money", Core.Formatting.Money);
                _heroCaption.Text = Localization.L10n.Tr("today");
                _sparkline.SetSeries(summary.TodayCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} this month", Core.Formatting.Money(summary.MonthDollars));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens · {1} billable",
                    Core.Formatting.CompactTokens(summary.TodayTokens),
                    Core.Formatting.CompactTokens(summary.TodayBillableTokens));
                break;
        }
    }
}

/// 0.65s cubic-ease-out numeric count-up — the macOS CountUpDollar
/// behavior, driven by a 60Hz dispatcher timer only while animating.
/// The `unit` guards against counting across incompatible scales: switching
/// the cost style from tokens (millions) to dollars must snap, not tick a
/// 2,000,000 token count rendered as "$2,000,000" down to "$15".
internal sealed class CountUp
{
    private const double Seconds = 0.65;
    private readonly TextBlock _target;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Func<double, string> _format = _ => "";
    private string _unit = "";
    private double _from;
    private double _to;
    private double _current;
    private bool _seeded;
    private DateTime _start;

    public CountUp(TextBlock target)
    {
        _target = target;
        _timer.Tick += (_, _) => Tick();
    }

    public void Animate(double to, string unit, Func<double, string> format)
    {
        _format = format;
        // First appearance: reveal by counting from zero.
        if (!_seeded)
        {
            _seeded = true;
            _unit = unit;
            Run(0, to);
            return;
        }
        // Unit change (tokens <-> dollars): snap, never count across scales.
        if (unit != _unit)
        {
            _unit = unit;
            _timer.Stop();
            _to = _current = to;
            _target.Text = format(to);
            return;
        }
        // Same value already shown: nothing to animate.
        if (Math.Abs(to - _to) < 0.000001)
        {
            if (!_timer.IsEnabled) _target.Text = format(to);
            return;
        }
        // Same unit, new value: continue from what's on screen right now.
        Run(_current, to);
    }

    private void Run(double from, double to)
    {
        _from = from;
        _to = to;
        _current = from;
        _start = DateTime.UtcNow;
        _target.Text = _format(from);
        _timer.Start();
    }

    private void Tick()
    {
        var x = Math.Clamp((DateTime.UtcNow - _start).TotalSeconds / Seconds, 0, 1);
        var eased = 1 - Math.Pow(1 - x, 3);
        _current = _from + (_to - _from) * eased;
        _target.Text = _format(_current);
        if (x >= 1) _timer.Stop();
    }
}

/// Minimal cumulative-cost sparkline: soft area fill under a tinted line.
public sealed class Sparkline : Grid
{
    private readonly Polyline _line;
    private readonly Polygon _fill;
    private IReadOnlyList<double> _series = Array.Empty<double>();

    public Sparkline(Color tint)
    {
        _fill = new Polygon
        {
            Fill = new LinearGradientBrush(
                IslandColors.Alpha(tint, 0.22),
                IslandColors.Alpha(tint, 0),
                90),
        };
        _line = new Polyline
        {
            Stroke = IslandColors.Brush(tint),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        Children.Add(_fill);
        Children.Add(_line);
        SizeChanged += (_, _) => Render();
    }

    public void SetSeries(IReadOnlyList<double> series)
    {
        _series = series;
        Render();
    }

    private void Render()
    {
        _line.Points.Clear();
        _fill.Points.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0 || _series.Count < 2) return;
        var max = Math.Max(_series.Max(), 0.0001);
        for (var i = 0; i < _series.Count; i++)
        {
            var x = width * i / (_series.Count - 1);
            var y = height - height * (_series[i] / max) * 0.92 - 1;
            _line.Points.Add(new Point(x, y));
            _fill.Points.Add(new Point(x, y));
        }
        _fill.Points.Add(new Point(width, height));
        _fill.Points.Add(new Point(0, height));
    }
}
