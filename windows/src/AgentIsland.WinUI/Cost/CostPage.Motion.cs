using System.Numerics;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Cost;

/// The usage numbers' motion (M17). A new period: the tabs' marker glides to
/// it, the charts grow and the numbers count from what was shown. A new scan
/// counts the same columns and numbers to their new values. Pixel steps on
/// its 30-tick clock: the underline on in2, columns and numbers on count4,
/// the columns in three groups a tick apart. Classic eases: the puck over 333
/// ms on inOut with a 10 % stretch, the old bars down over 167 ms, the new up
/// over 500 ms 10 ms apart, idle dots popping in, numbers over 500 ms.
/// Nothing moves as the page comes; reduced motion goes at once.
internal sealed partial class CostPage
{
    // The tabs sit over one marker of their own (Classic's paper puck, Pixel's
    // accent underline), which glides to a new period while the tab's own
    // waits, hidden.
    private Grid _tabs = null!;
    private FrameworkElement _tabMarker = null!;
    private SelectorBarItem? _waitingTab;
    private UiMoment? _glide;
    private UiMoment? _count;

    // What the window shows, for the next render to count from.
    private readonly Dictionary<string, double> _shown = new();
    private readonly Dictionary<DisplayProvider, IReadOnlyList<double>> _shownBars = new();

    // The numbers and chart columns Compose builds.
    private readonly List<Number> _numbers = new();
    private readonly List<Column> _columns = new();

    /// A number that counts: its text, its value and how it reads.
    private sealed record Number(string Key, TextBlock Text, double Value, Func<double, string> Format);

    /// A chart column: Classic's bar (its height) or idle dot; Pixel's stack
    /// (its blocks), an idle one a single faint block.
    private sealed record Column(DisplayProvider Provider, int Index, int Of, FrameworkElement Element, double Size, bool Idle);

    /// AGENTISLAND_DEBUG_COST_FILM: a period chosen as a click chooses it.
    internal void DebugPeriod(int index) => _periods.SelectedItem = _periods.Items[index];

    /// The film's scan: the numbers shown taken down to two thirds and the
    /// columns to half, then counted back up as a new scan counts.
    internal void DebugScan()
    {
        foreach (var key in _shown.Keys.ToList()) _shown[key] *= 2.0 / 3;
        foreach (var provider in _shownBars.Keys.ToList())
        {
            _shownBars[provider] = _shownBars[provider].Select(size => _pixel ? Math.Floor(size / 2) : size / 2).ToList();
        }
        Recount();
    }

    private TextBlock Counted(TextBlock text, string key, double value, Func<double, string> format)
    {
        text.Text = format(value);
        _numbers.Add(new Number(key, text, value, format));
        return text;
    }

    /// The numbers and columns on screen, for the next render to count from.
    private void Remember()
    {
        _shown.Clear();
        foreach (var number in _numbers) _shown[number.Key] = number.Value;
        _shownBars.Clear();
        foreach (var chart in _columns.GroupBy(column => column.Provider)) _shownBars[chart.Key] = chart.Select(column => column.Size).ToList();
    }

    // MARK: - Tabs

    /// Classic's paper puck with its lift, or Pixel's accent underline.
    private FrameworkElement TabMarker()
    {
        var marker = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        if (_pixel)
        {
            marker.Children.Add(new Rectangle { Fill = Res<Brush>("PixelAccent") });
            return marker;
        }
        var puck = new Rectangle { RadiusX = 16, RadiusY = 16, Fill = Res<Brush>("ClassicPaper") };
        marker.Children.Add(new SoftShadow { Kind = "Lift", Target = puck });
        marker.Children.Add(puck);
        return marker;
    }

    /// Hides a tab's own mark while the marker stands in for it: Pixel's
    /// underline by scale (its state sets its opacity), Classic's puck and
    /// lift by opacity (their states set fill and visibility).
    private void HoldTab(SelectorBarItem tab, bool hold)
    {
        if (_pixel)
        {
            if (ControlMotion.Find<FrameworkElement>(tab, "PART_SelectionVisual") is { } underline) underline.Scale = hold ? new Vector3(0, 0, 1) : Vector3.One;
            return;
        }
        foreach (var name in new[] { "Pill", "Lift" })
        {
            if (ControlMotion.Find<FrameworkElement>(tab, name) is { } part) part.Opacity = hold ? 0 : 1;
        }
    }

    private void ReleaseTab()
    {
        if (_waitingTab is { } tab) HoldTab(tab, hold: false);
        _waitingTab = null;
        _tabMarker.Visibility = Visibility.Collapsed;
    }

    /// The marker glides from the old tab to the new: Pixel's underline (12
    /// DIP in from each side, 4 tall at the bottom) moves and resizes on in2;
    /// Classic's puck over 333 ms on inOut, stretching 10 % wide at halfway,
    /// while every tab's label comes back from 60 % over 167 ms.
    private void PlayTabs(int from, int to)
    {
        (double X, double Width)? gliding = _tabMarker.Visibility == Visibility.Visible ? (_tabMarker.Translation.X, _tabMarker.Width) : null;
        _glide?.Stop();
        ReleaseTab();
        if (from == to || _periods.Items.ElementAtOrDefault(from) is not { } old || _periods.Items.ElementAtOrDefault(to) is not { } tab || tab.ActualWidth <= 0) return;
        Rect Mark(SelectorBarItem item)
        {
            var at = item.TransformToVisual(_tabs).TransformPoint(default);
            return _pixel
                ? new Rect(at.X + 12, at.Y + item.ActualHeight - 4, Math.Max(0, item.ActualWidth - 24), 4)
                : new Rect(at.X, at.Y, item.ActualWidth, item.ActualHeight);
        }
        var start = Mark(old);
        var end = Mark(tab);
        double x0 = gliding?.X ?? start.X, w0 = gliding?.Width ?? start.Width;
        void Place(double x, double width)
        {
            _tabMarker.Width = width;
            _tabMarker.Translation = new Vector3((float)x, (float)end.Y, 0);
        }
        _tabMarker.Height = end.Height;
        _tabMarker.Scale = Vector3.One;
        Place(x0, w0);
        _tabMarker.Visibility = Visibility.Visible;
        _waitingTab = tab;
        HoldTab(tab, hold: true);
        if (_pixel)
        {
            var xs = Motion.Remaining(x0, end.X, Motion.In2);
            var widths = Motion.Remaining(w0, end.Width, Motion.In2);
            _glide = UiMoment.Play(2 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k >= 2) ReleaseTab();
                else Place(k == 0 ? x0 : xs[0], k == 0 ? w0 : widths[0]);
            }, ReleaseTab);
            return;
        }
        var labels = _periods.Items.SelectMany(item => new[] { "PART_TextVisual", "PART_IconVisual" }
            .Select(name => ControlMotion.Find<FrameworkElement>(item, name))).OfType<FrameworkElement>().ToList();
        _glide = UiMoment.Play(333, t =>
        {
            var p = Motion.InOut(t / 333);
            var width = w0 + (end.Width - w0) * p;
            Place(x0 + (end.X - x0) * p, width);
            _tabMarker.CenterPoint = new Vector3((float)(width / 2), (float)(end.Height / 2), 0);
            _tabMarker.Scale = new Vector3((float)(1 + 0.1 * (1 - Math.Abs(2 * p - 1))), 1, 1);
            foreach (var label in labels) label.Opacity = 0.6 + 0.4 * Motion.Linear(t / 167);
            if (t >= 333) ReleaseTab();
        }, ReleaseTab);
    }

    // MARK: - Periods and scans

    /// A new period: the marker glides; Classic's old bars sink over 167 ms
    /// as the numbers start counting, then the new page comes in and its
    /// charts grow; Pixel's new page comes in at once and builds up.
    private void SwitchPeriod(int from, int to)
    {
        _count?.Stop();
        if (!_visible() || Motion.ReducedMotion)
        {
            Render();
            return;
        }
        PlayTabs(from, to);
        var counted = new Dictionary<string, double>(_shown);
        var sinking = _columns.Where(column => !column.Idle).Select(column => (column.Element, column.Size)).ToList();
        var oldNumbers = _numbers.ToList();
        var page = Compose();
        var numbers = _numbers.ToList();
        var columns = _columns.ToList();
        if (_pixel)
        {
            Commit(page);
            Hide(columns);
            _count = UiMoment.Play(6 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                StepNumbers(numbers, counted, k);
                foreach (var column in columns) Blocks(column, Grown(column, k));
            }, Remember);
            return;
        }
        var committed = false;
        var stagger = 10.0 * Math.Max(0, columns.Select(column => column.Index).DefaultIfEmpty(0).Max());
        _count = UiMoment.Play(167 + 500 + stagger, t =>
        {
            var share = Motion.Out(t / 500);
            if (t < 167)
            {
                var left = 1 - Motion.In(t / 167);
                foreach (var (element, size) in sinking) element.Height = size * left;
                // The old texts count toward the new values until the new page comes.
                foreach (var number in oldNumbers)
                {
                    if (numbers.FirstOrDefault(fresh => fresh.Key == number.Key) is { } target)
                    {
                        number.Text.Text = target.Format(Between(counted, target, share));
                    }
                }
                return;
            }
            if (!committed)
            {
                committed = true;
                Commit(page);
            }
            foreach (var number in numbers) number.Text.Text = number.Format(Between(counted, number, share));
            foreach (var column in columns) Ease(column, t - 167 - 10 * column.Index, grow: true, from: 0);
        }, Remember);
    }

    /// A new scan of the same period: the numbers and the columns count to
    /// their new values; a chart whose columns changed in number grows anew.
    private void Recount()
    {
        _count?.Stop();
        var counted = new Dictionary<string, double>(_shown);
        var bars = new Dictionary<DisplayProvider, IReadOnlyList<double>>(_shownBars);
        var page = Compose();
        Commit(page);
        if (!_visible() || Motion.ReducedMotion || counted.Count == 0)
        {
            Remember();
            return;
        }
        var numbers = _numbers.ToList();
        var columns = _columns.ToList();
        // A column counts from its old self while its chart keeps its columns;
        // a chart with new columns grows anew.
        var before = columns.Select(column =>
            bars.TryGetValue(column.Provider, out var sizes) && sizes.Count == column.Of ? (Fresh: false, From: sizes[column.Index]) : (Fresh: true, From: 0.0)).ToList();
        if (_pixel)
        {
            for (var i = 0; i < columns.Count; i++) Blocks(columns[i], (int)before[i].From);
            _count = UiMoment.Play(4 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                StepNumbers(numbers, counted, k);
                for (var i = 0; i < columns.Count; i++) Blocks(columns[i], Count4((int)before[i].From, (int)columns[i].Size, k));
            }, Remember);
            return;
        }
        _count = UiMoment.Play(500, t =>
        {
            var share = Motion.Out(t / 500);
            foreach (var number in numbers) number.Text.Text = number.Format(Between(counted, number, share));
            for (var i = 0; i < columns.Count; i++) Ease(columns[i], t, grow: before[i].Fresh, from: before[i].From);
        }, Remember);
    }

    private static double Between(Dictionary<string, double> counted, Number number, double share)
    {
        var from = counted.GetValueOrDefault(number.Key);
        return from + (number.Value - from) * share;
    }

    /// Pixel's count4 for numbers: a quarter of the way each tick, exact on
    /// the fourth.
    private static void StepNumbers(List<Number> numbers, Dictionary<string, double> counted, int tick)
    {
        foreach (var number in numbers) number.Text.Text = number.Format(Between(counted, number, Math.Min(tick, 4) / 4.0));
    }

    /// count4 for blocks: at most a quarter of the change, rounded up, a tick.
    private static int Count4(int from, int to, int tick)
    {
        if (from == to) return to;
        var step = (int)Math.Ceiling(Math.Abs(to - from) / 4.0);
        return from + Math.Sign(to - from) * Math.Min(Math.Abs(to - from), tick * step);
    }

    /// A Pixel column building up from nothing: its group (a third of the
    /// chart, left to right) starts a tick after the last, then count4.
    private static int Grown(Column column, int tick)
    {
        var group = column.Of == 0 ? 0 : column.Index * 3 / column.Of;
        return Count4(0, (int)column.Size, Math.Max(0, tick - group));
    }

    private static void Hide(IEnumerable<Column> columns)
    {
        foreach (var column in columns) Blocks(column, 0);
    }

    /// A Pixel stack shows only its bottom `count` blocks.
    private static void Blocks(Column column, int count)
    {
        if (column.Element is not Panel stack) return;
        var blocks = stack.Children.Count;
        for (var i = 0; i < blocks; i++) stack.Children[i].Visibility = i >= blocks - count ? Visibility.Visible : Visibility.Collapsed;
    }

    /// A Classic column ms into its own animation: a bar eases from a height
    /// to its own over 500 ms on out; an idle dot pops in over 250 ms.
    private static void Ease(Column column, double ms, bool grow, double from)
    {
        if (column.Idle)
        {
            if (!grow) return;
            var pop = (float)Motion.Pop(ms / 250);
            column.Element.CenterPoint = new Vector3((float)(column.Element.ActualWidth / 2), (float)(column.Element.ActualHeight / 2), 0);
            column.Element.Scale = new Vector3(pop, pop, 1);
            return;
        }
        column.Element.Height = from + (column.Size - from) * Motion.Out(ms / 500);
    }
}
