using System.Numerics;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AgentIsland.WinUI.Drawer;

// 任务列表：主对话、按层缩进的子任务、折叠行，和每行的状态标记（在跑、等你、出错、刚完成）。
internal sealed partial class HoverCard
{
    // 标记列宽，也是每一层的缩进。
    private const double Mark = 12;

    private bool _reduced;
    // Classic 在跑的圆点（透明度循环），Pixel 在跑的方块（全卡共用一个计时器步进），Pixel 刚进入等你的空框（闪两次）。
    private readonly List<Visual> _pulses = new();
    private readonly List<Rectangle> _spinners = new();
    private readonly List<UIElement> _flashes = new();
    private DispatcherQueueTimer? _markerTimer;
    private int _markerTick;

    private FrameworkElement Tasks(IReadOnlyList<StatusTaskRow> rows, double indent, double width, bool pixel, Dictionary<string, StatusTaskState> states)
    {
        var list = new StackPanel { Margin = new Thickness(indent, 8, 0, 0) };
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var top = i == 0 ? 0 : row.IsMain || row.Kind == StatusRowKind.MoreConversations ? 8 : 2;
            FrameworkElement line = row.Kind switch
            {
                StatusRowKind.Task when row.IsMain => MainRow(row, width, pixel, states),
                StatusRowKind.Task => SubRow(row, pixel, states),
                _ => Caption(row.Name, "TextFillColorTertiaryBrush"),
            };
            line.Margin = new Thickness(row.Depth * Mark, top, 0, 0);
            list.Children.Add(line);
        }
        return list;
    }

    /// 主对话：第一行是标记和任务名（最醒目），第二行是在做什么。
    private FrameworkElement MainRow(StatusTaskRow row, double width, bool pixel, Dictionary<string, StatusTaskState> states)
    {
        var stack = new StackPanel { Spacing = 2 };
        var first = new Grid();
        first.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Mark) });
        first.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(first, Marker(row, pixel, states), 0, 0);
        var name = Caption(row.Name, "TextFillColorPrimaryBrush", NameGap(pixel));
        name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        name.Tag = SystemFace;
        name.VerticalAlignment = VerticalAlignment.Center;
        Place(first, name, 0, 1);
        stack.Children.Add(first);
        var room = width - Mark;
        if (row.Metrics is not { } metrics)
        {
            var alone = Activity(row, room, withObject: true).Text;
            alone.Margin = new Thickness(Mark, 0, 0, 0);
            stack.Children.Add(alone);
            return stack;
        }
        // 第二行右侧是缓存率和速度；有指标时左边只写状态词，不写对象（设计稿 §2.4）。
        var parts = Metrics(metrics);
        Faced(parts.Line);
        _metricParts[row.Id] = parts;
        if (!_metricsBelow.Contains(row.Id))
        {
            var (text, fits) = Activity(row, room - Wide(parts.Line) - MetricsGap, withObject: false);
            if (fits)
            {
                var second = new Grid { Margin = new Thickness(Mark, 0, 0, 0), ColumnSpacing = MetricsGap };
                second.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                second.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Place(second, text, 0, 0);
                parts.Line.HorizontalAlignment = HorizontalAlignment.Right;
                parts.Line.VerticalAlignment = VerticalAlignment.Center;
                Place(second, parts.Line, 0, 1);
                stack.Children.Add(second);
                return stack;
            }
            // 短词也放不下：指标挪到第 3 行，卡关闭前不再挪回来。
            _metricsBelow.Add(row.Id);
        }
        var said = Activity(row, room, withObject: false).Text;
        said.Margin = new Thickness(Mark, 0, 0, 0);
        stack.Children.Add(said);
        parts.Line.Margin = new Thickness(Mark, 0, 0, 0);
        parts.Line.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(parts.Line);
        return stack;
    }

    /// 第二行：状态词按状态上色，后面跟对象（次墨色），从末尾截断。完整说法放不下就换短词，
    /// 并记住这一行，卡关闭前不再换回去（设计稿 §2.4）。对象连头几个字都放不下时整段不写，
    /// 免得只剩一个省略号。withObject 为 false（这一行右边有指标）时不写对象，
    /// Fits 说的是状态词（完整的或短的）放不放得下。
    private (TextBlock Text, bool Fits) Activity(StatusTaskRow row, double width, bool withObject)
    {
        TextBlock Make(string said, string? detail)
        {
            var text = new TextBlock
            {
                FontSize = 12,
                Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            text.Inlines.Add(new Run { Text = said, Foreground = StateInk(row.State) });
            if (detail is not null) text.Inlines.Add(new Run { Text = " · " + detail });
            return Faced(text);
        }
        double Measure(string text) => Wide(Faced(Caption(text, "TextFillColorSecondaryBrush")));
        var full = row.Activity ?? row.Word;
        var said = row.Word;
        if (!_shortened.Contains(row.Id))
        {
            if (Measure(full) <= width) said = full;
            else _shortened.Add(row.Id);
        }
        var fits = Measure(said) <= width;
        var detail = withObject ? row.Object : null;
        if (detail is not null && Measure($"{said} · {detail}") > width
            && Measure($"{said} · {detail[..Math.Min(detail.Length, ObjectAtLeast)]}…") > width)
        {
            detail = null;
        }
        return (Make(said, detail), fits);
    }

    // 对象至少要露出这么多个字才值得写。
    private const int ObjectAtLeast = 4;

    // 第二行里状态词和指标之间的间距。
    private const double MetricsGap = 8;

    // 指标挪到第 3 行的主对话，和各主对话指标里的文字块（按任务 id，卡内容没变时原地换数字）。
    private readonly HashSet<string> _metricsBelow = new();
    private readonly Dictionary<string, MetricParts> _metricParts = new();

    /// 一行指标的各个部分：“缓存 92% · 41 tok/s”。标签、分隔和单位用三级墨色，数字用二级；
    /// 数字各有 MinWidth（按 “100%” 和 “999” 的宽度）并右对齐，数字变化时文字不左右跳（设计稿 §4.1）。
    /// 只有缓存率或只有速度时，另一半和分隔符收起。
    private sealed record MetricParts(StackPanel Line, TextBlock Label, TextBlock Cache, TextBlock Separator, TextBlock Speed, TextBlock Unit);

    private MetricParts Metrics(StatusMetrics metrics)
    {
        TextBlock Part(string text, string brush, Thickness margin)
        {
            var part = Caption(text, brush, margin);
            part.TextTrimming = TextTrimming.None;
            part.VerticalAlignment = VerticalAlignment.Center;
            return part;
        }
        TextBlock Number(string widest)
        {
            var number = Part("", "TextFillColorSecondaryBrush", default);
            number.TextAlignment = TextAlignment.Right;
            number.MinWidth = Math.Max(Wide(Faced(Caption(widest, "TextFillColorSecondaryBrush"))), widest == "999" ? Wide(Faced(Caption("1.2k", "TextFillColorSecondaryBrush"))) : 0);
            return number;
        }
        var label = Part(L10n.Tr("Cache"), "TextFillColorTertiaryBrush", new Thickness(0, 0, 4, 0));
        var cache = Number("100%");
        var separator = Part("·", "TextFillColorTertiaryBrush", new Thickness(4, 0, 4, 0));
        var speed = Number("999");
        var unit = Part("tok/s", "TextFillColorTertiaryBrush", new Thickness(4, 0, 0, 0));
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var part in new[] { label, cache, separator, speed, unit }) line.Children.Add(part);
        var parts = new MetricParts(line, label, cache, separator, speed, unit);
        ApplyMetrics(parts, metrics);
        return parts;
    }

    private static void ApplyMetrics(MetricParts parts, StatusMetrics metrics)
    {
        var cache = metrics.CachePercent is not null;
        var speed = metrics.Speed is not null;
        if (metrics.CachePercent is { } percent) parts.Cache.Text = percent + "%";
        if (metrics.Speed is { } text) parts.Speed.Text = text;
        parts.Label.Visibility = parts.Cache.Visibility = cache ? Visibility.Visible : Visibility.Collapsed;
        parts.Speed.Visibility = parts.Unit.Visibility = speed ? Visibility.Visible : Visibility.Collapsed;
        parts.Separator.Visibility = cache && speed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// 卡内容没变（key 不含数字）时，把新的缓存率和速度原地换进已画好的文字块。
    /// 有没有缓存率、有没有速度变了，key 会变，卡会重建，到不了这里。
    private void UpdateMetrics(IEnumerable<StatusCard> cards)
    {
        foreach (var card in cards)
        {
            foreach (var row in card.TaskRows)
            {
                if (row.Metrics is { } metrics && _metricParts.TryGetValue(row.Id, out var parts)) ApplyMetrics(parts, metrics);
            }
        }
    }

    /// 子任务：标记、名字（常规字重；Pixel 没有粗体，用次墨色和主对话区分）、右侧的短状态词。
    /// 状态词列优先，名字从末尾截断。
    private FrameworkElement SubRow(StatusTaskRow row, bool pixel, Dictionary<string, StatusTaskState> states)
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Mark) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(line, Marker(row, pixel, states), 0, 0);
        var name = Caption(row.Name, pixel ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush", NameGap(pixel));
        name.Tag = SystemFace;
        name.VerticalAlignment = VerticalAlignment.Center;
        Place(line, name, 0, 1);
        var word = Caption(row.Word, "TextFillColorSecondaryBrush");
        word.Foreground = StateInk(row.State);
        word.Margin = new Thickness(8, 0, 0, 0);
        word.VerticalAlignment = VerticalAlignment.Center;
        Place(line, word, 0, 2);
        return line;
    }

    /// Pixel 的标记占满 10 DIP，离名字再空一格；Classic 的标记本身就比列窄。
    private static Thickness NameGap(bool pixel) => new(pixel ? PixelPlate.Cell : 0, 0, 0, 0);

    /// 只有等你、出错、刚完成上色；在跑用中性墨色。
    private static Brush StateInk(StatusTaskState state) => state switch
    {
        StatusTaskState.NeedsYou => Ink("Caution"),
        StatusTaskState.Error => Ink("Critical"),
        StatusTaskState.Done => Ink("Success"),
        _ => Res<Brush>("TextFillColorSecondaryBrush"),
    };

    private FrameworkElement Marker(StatusTaskRow row, bool pixel, Dictionary<string, StatusTaskState> states)
    {
        states[row.Id] = row.State;
        var entering = row.State == StatusTaskState.NeedsYou
            && _rowStates.TryGetValue(row.Id, out var was) && was != StatusTaskState.NeedsYou;
        var mark = pixel ? PixelMarker(row.State, entering) : ClassicMarker(row.State, entering);
        mark.HorizontalAlignment = HorizontalAlignment.Left;
        mark.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Width = Mark, VerticalAlignment = VerticalAlignment.Center, Children = { mark } };
    }

    /// Classic：在跑是 6 DIP 的实心圆点，透明度 1 → 0.35 → 1 循环 1600 ms；等你是 8 DIP 的空心环，
    /// 刚进入时从 0.6 放大到 1；出错和刚完成是 Segoe Fluent Icons 的 ✕ 和 ✓。
    private FrameworkElement ClassicMarker(StatusTaskState state, bool entering)
    {
        FrameworkElement mark = state switch
        {
            StatusTaskState.Running => new Ellipse { Width = 6, Height = 6, Fill = Res<Brush>("TextFillColorSecondaryBrush") },
            StatusTaskState.NeedsYou => new Ellipse { Width = 8, Height = 8, Stroke = Ink("Caution"), StrokeThickness = 1.5 },
            StatusTaskState.Error => new FontIcon { Glyph = "", FontSize = 10, FontFamily = Icons, Foreground = Ink("Critical") },
            _ => new FontIcon { Glyph = "", FontSize = 10, FontFamily = Icons, Foreground = Ink("Success") },
        };
        if (_reduced) return mark;
        if (state == StatusTaskState.Running) Pulse(mark);
        else if (entering) Pop(mark);
        return mark;
    }

    private void Pulse(UIElement mark)
    {
        var visual = ElementCompositionPreview.GetElementVisual(mark);
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.65f, 0), new Vector2(0.35f, 1));
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 1);
        animation.InsertKeyFrame(0.5f, 0.35f, ease);
        animation.InsertKeyFrame(1, 1, ease);
        animation.Duration = TimeSpan.FromMilliseconds(1600);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
        _pulses.Add(visual);
    }

    private void Pop(UIElement mark)
    {
        var visual = ElementCompositionPreview.GetElementVisual(mark);
        visual.CenterPoint = new Vector3(4, 4, 0);
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, new Vector3(0.6f, 0.6f, 1));
        animation.InsertKeyFrame(1, Vector3.One, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1), new Vector2(0.36f, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(167);
        visual.StartAnimation("Scale", animation);
    }

    // Pixel 的标记画在 5×5 的格子上（每格 2 DIP），不靠像素字体里有没有 ✓✕。
    private static readonly (int Column, int Row)[] Check = { (4, 0), (3, 1), (0, 2), (2, 2), (1, 3) };
    private static readonly (int Column, int Row)[] Corners = { (0, 0), (3, 0), (3, 3), (0, 3) };

    private static IEnumerable<(int Column, int Row)> Cells(StatusTaskState state)
    {
        if (state == StatusTaskState.Done)
        {
            foreach (var cell in Check) yield return cell;
            yield break;
        }
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 5; column++)
            {
                var on = state == StatusTaskState.Error
                    ? row == column || row + column == 4
                    : row == 0 || row == 4 || column == 0 || column == 4;
                if (on) yield return (column, row);
            }
        }
    }

    /// Pixel：在跑是一个 2×2 的块沿四角顺时针走；等你是空框，刚进入时闪两次；出错是 ✕，刚完成是 ✓。
    private FrameworkElement PixelMarker(StatusTaskState state, bool entering)
    {
        var cell = PixelPlate.Cell;
        var canvas = new Canvas { Width = 5 * cell, Height = 5 * cell };
        if (state == StatusTaskState.Running)
        {
            var block = new Rectangle { Width = 2 * cell, Height = 2 * cell, Fill = Res<Brush>("TextFillColorSecondaryBrush") };
            canvas.Children.Add(block);
            if (!_reduced) _spinners.Add(block);
            return canvas;
        }
        var ink = StateInk(state);
        foreach (var (column, row) in Cells(state))
        {
            var square = new Rectangle { Width = cell, Height = cell, Fill = ink };
            Canvas.SetLeft(square, column * cell);
            Canvas.SetTop(square, row * cell);
            canvas.Children.Add(square);
        }
        if (entering && !_reduced) _flashes.Add(canvas);
        return canvas;
    }

    // Pixel 在跑的方块每走一格停这么多 tick：设计稿写的是 2 tick（一圈约 0.27 秒），
    // 在一张列着好几行的卡上太闹，放慢到和 Classic 呼吸点差不多的节奏（一圈约 1 秒）。
    private const int SpinnerTicks = 8;

    private void StartMarkers()
    {
        if (_spinners.Count == 0 && _flashes.Count == 0) return;
        _markerTick = 0;
        if (_markerTimer is null)
        {
            _markerTimer = _root.DispatcherQueue.CreateTimer();
            _markerTimer.Interval = TimeSpan.FromMilliseconds(Motion.TickMs);
            _markerTimer.Tick += (_, _) => StepMarkers();
        }
        _markerTimer.Start();
    }

    private void StepMarkers()
    {
        _markerTick++;
        // 等你：灭 1 tick、亮 1 tick，两次，之后常亮。
        if (_markerTick <= 4)
        {
            foreach (var flash in _flashes) flash.Opacity = _markerTick % 2 == 1 ? 0 : 1;
        }
        else if (_flashes.Count > 0)
        {
            foreach (var flash in _flashes) flash.Opacity = 1;
            _flashes.Clear();
        }
        if (_markerTick % SpinnerTicks == 0)
        {
            var (column, row) = Corners[_markerTick / SpinnerTicks % Corners.Length];
            foreach (var spinner in _spinners)
            {
                Canvas.SetLeft(spinner, column * PixelPlate.Cell);
                Canvas.SetTop(spinner, row * PixelPlate.Cell);
            }
        }
        if (_spinners.Count == 0 && _flashes.Count == 0) _markerTimer?.Stop();
    }

    /// 卡关闭或内容重建时停掉所有标记动画。
    private void StopMarkers()
    {
        _markerTimer?.Stop();
        foreach (var flash in _flashes) flash.Opacity = 1;
        _flashes.Clear();
        _spinners.Clear();
        foreach (var visual in _pulses)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
        }
        _pulses.Clear();
    }
}
