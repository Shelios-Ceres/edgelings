using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

// What the card holds: its sections, rows, columns and texts, in either style, and the inks they use.
internal sealed partial class HoverCard
{
    /// The room the all-agents card has: its height on the screen, and its
    /// width beside the drawer. One character's card needs none.
    private static Size Room(Request request)
    {
        if (request.Sections.Count < 2) return default;
        var beside = request.CharacterRight
            ? request.Anchor.Left - Gap - request.Area.Left
            : request.Area.Right - request.Anchor.Right - Gap;
        return new Size(Math.Max(0, beside - 6), Math.Max(0, request.Area.Height - 12));
    }

    // The room the content was built for.
    private Size _room;

    // 正在构建的是 Pixel 卡时它的字号（量宽度之前就要套上像素字体），Classic 为 null。
    private double? _face;

    /// The card's sections, divided. The all-agents card stands in one
    /// column while it fits the screen's height; past it, its sections run
    /// down one column and on to the next, in as few columns as fit, cut so
    /// the tallest is as short as it can be, each column as wide as one
    /// character's card, with a divider between them. 全员卡的面板头和合计行横跨所有栏，不参与分栏。
    private void Build(Request request)
    {
        var pixel = request.Pixel;
        _body.Children.Clear();
        StopMarkers();
        _reduced = request.ReducedMotion;
        var size = PixelPlate.FontSize(FontScale(request.Scale));
        _face = pixel ? size : null;
        var states = new Dictionary<string, StatusTaskState>();
        var padding = pixel ? PixelPadding : PaperPadding;
        var column = (pixel ? PixelCardWidth : CardWidth) - padding.Left - padding.Right;
        var single = request.Sections.Count == 1;
        var sections = request.Sections.Select(section =>
        {
            var built = Section(section.Card, section.Swatch, pixel, single, column, states);
            if (pixel) ApplyPixelFace(built, size);
            return built;
        }).ToList();
        _rowStates = states;
        var head = request.Panel is { } panel && !single ? PanelHead(request, panel, column) : null;
        var foot = request.Panel?.Total is { } total && !single ? TokenLine(total, 0, column, pixel, "tokens:total", 0) : null;
        if (pixel)
        {
            if (head is not null) ApplyPixelFace(head, size);
            if (foot is not null) ApplyPixelFace(foot, size);
        }
        var room = Room(request);
        var chrome = 0.0;
        foreach (var extra in new UIElement?[] { head, foot })
        {
            if (extra is null) continue;
            extra.Measure(new Size(column, double.PositiveInfinity));
            chrome += extra.DesiredSize.Height + 2 * 10 + 1;
        }
        var columns = Columns(sections, column, pixel, new Size(room.Width, Math.Max(0, room.Height - chrome)));
        _width = padding.Left + columns.Count * column + (columns.Count - 1) * Gutter(pixel) + padding.Right;
        if (head is not null) _body.Children.Add(head);
        if (columns.Count == 1)
        {
            Stack(_body, sections, column, pixel);
        }
        else
        {
            var grid = new Grid();
            var tallest = 0.0;
            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter(pixel)) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column) });
                var stack = new StackPanel();
                Stack(stack, columns[c], column, pixel);
                Grid.SetColumn(stack, 2 * c);
                grid.Children.Add(stack);
                tallest = Math.Max(tallest, Tall(columns[c], pixel));
            }
            for (var c = 1; c < columns.Count; c++)
            {
                var rule = Rule(pixel, tallest);
                Grid.SetColumn(rule, 2 * c - 1);
                grid.Children.Add(rule);
            }
            _body.Children.Add(grid);
        }
        if (foot is not null)
        {
            _body.Children.Add(Divider(pixel, _width - padding.Left - padding.Right));
            _body.Children.Add(foot);
        }
        StartMarkers();
    }

    /// The sections in columns, as few as fit the room's height and width,
    /// each run as short as the cut allows; one column if none fits beside
    /// the drawer.
    private static List<List<UIElement>> Columns(List<UIElement> sections, double column, bool pixel, Size room)
    {
        var one = new List<List<UIElement>> { sections };
        if (sections.Count < 2) return one;
        foreach (var section in sections) section.Measure(new Size(column, double.PositiveInfinity));
        var padding = pixel ? PixelPadding : PaperPadding;
        var height = room.Height - padding.Top - padding.Bottom;
        if (Tall(sections, pixel) <= height) return one;
        List<List<UIElement>>? best = null;
        var shortest = double.MaxValue;
        for (var count = 2; count <= sections.Count; count++)
        {
            if (padding.Left + count * column + (count - 1) * Gutter(pixel) + padding.Right > room.Width) break;
            var (cuts, tallest) = Cut(sections, 0, count, pixel);
            if (tallest >= shortest) continue;
            shortest = tallest;
            best = new List<List<UIElement>>();
            var from = 0;
            foreach (var to in cuts.Append(sections.Count))
            {
                best.Add(sections.GetRange(from, to - from));
                from = to;
            }
            if (tallest <= height) break;
        }
        return best ?? one;
    }

    /// Where to cut the sections from `from` on into `count` runs so the
    /// tallest is as short as it can be: the index each later run starts
    /// at. Of equal cuts the earlier runs take more.
    private static (List<int> Cuts, double Tallest) Cut(List<UIElement> sections, int from, int count, bool pixel)
    {
        if (count == 1) return (new List<int>(), Tall(sections.GetRange(from, sections.Count - from), pixel));
        (List<int> Cuts, double Tallest) best = (new List<int>(), double.MaxValue);
        for (var cut = from + 1; cut <= sections.Count - count + 1; cut++)
        {
            var rest = Cut(sections, cut, count - 1, pixel);
            var tallest = Math.Max(Tall(sections.GetRange(from, cut - from), pixel), rest.Tallest);
            if (tallest <= best.Tallest) best = (rest.Cuts.Prepend(cut).ToList(), tallest);
        }
        return best;
    }

    /// A column's height: its measured sections and the dividers between.
    private static double Tall(List<UIElement> sections, bool pixel) =>
        sections.Sum(section => section.DesiredSize.Height) + (sections.Count - 1) * (pixel ? 2 * 9 + PixelPlate.Cell : 2 * 10 + 1);

    /// Between columns: the padding on each side of a divider.
    private static double Gutter(bool pixel) => (pixel ? PixelPadding.Left : PaperPadding.Left) * 2 + (pixel ? PixelPlate.Cell : 1);

    /// Sections one under another, a divider between each two.
    private static void Stack(Panel panel, List<UIElement> sections, double width, bool pixel)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0) panel.Children.Add(Divider(pixel, width));
            panel.Children.Add(sections[i]);
        }
    }

    /// The divider between sections: Pixel's dashed a cell thick, Classic's a hairline.
    private static FrameworkElement Divider(bool pixel, double width) => pixel
        ? new Line
        {
            X2 = width,
            StrokeThickness = PixelPlate.Cell,
            StrokeDashArray = new DoubleCollection { 1, 1 },
            Stroke = Res<Brush>("DividerStrokeColorDefaultBrush"),
            Margin = new Thickness(0, 9, 0, 9),
        }
        : new Rectangle
        {
            Height = 1,
            Fill = Res<Brush>("DividerStrokeColorDefaultBrush"),
            Margin = new Thickness(0, 10, 0, 10),
        };

    /// The divider between columns, down the tallest: Pixel's dashed a cell
    /// wide on whole cells, Classic's a hairline.
    private static FrameworkElement Rule(bool pixel, double height) => pixel
        ? new Line
        {
            X1 = PixelPlate.Cell / 2,
            X2 = PixelPlate.Cell / 2,
            Y2 = Math.Floor(height / PixelPlate.Cell) * PixelPlate.Cell,
            Width = PixelPlate.Cell,
            StrokeThickness = PixelPlate.Cell,
            StrokeDashArray = new DoubleCollection { 1, 1 },
            Stroke = Res<Brush>("DividerStrokeColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        }
        : new Rectangle
        {
            Width = 1,
            Fill = Res<Brush>("DividerStrokeColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

    // D6：Pixel 英文界面里任务名这一列用系统字体，像素字体画不了的中文标题照原样显示。
    private const string SystemFace = "system-face";
    private static readonly FontFamily SystemFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

    /// Every line of a pixel panel in the pixel face, at a size that keeps
    /// its pixels whole. Weights stay regular: the face has no bold, and a
    /// synthetic one smears it; emphasis is the primary ink alone.
    internal static void UsePixelFace(Panel panel, double size)
    {
        foreach (var child in panel.Children) ApplyPixelFace(child, size);
    }

    private static void ApplyPixelFace(UIElement element, double size)
    {
        switch (element)
        {
            case Panel panel:
                UsePixelFace(panel, size);
                break;
            case Border { Child: { } child }:
                ApplyPixelFace(child, size);
                break;
            case TextBlock { Tag: SystemFace } name when !L10n.IsChinese:
                // 系统字体有粗体：主对话名照 Classic 保留 SemiBold。
                name.FontFamily = SystemFont;
                name.FontSize = 12;
                break;
            case TextBlock text:
                text.FontFamily = PixelPlate.Font;
                text.FontSize = size;
                text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                text.CharacterSpacing = 0;
                foreach (var inline in text.Inlines) inline.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                break;
        }
    }

    /// 量宽度之前先套上这张卡的字体：Pixel 的像素字体比系统字体宽。
    private T Faced<T>(T element) where T : UIElement
    {
        if (_face is { } size) ApplyPixelFace(element, size);
        return element;
    }

    private static double Wide(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    /// The plan as a small chip in the agent's tint, like the Settings
    /// badge: an 18-DIP pill, square on the pixel panel.
    private static Border PlanChip(string plan, uint swatch, bool pixel)
    {
        var ink = Brand.Rgb(swatch);
        return new Border
        {
            Background = new SolidColorBrush(pixel ? Windows.UI.Color.FromArgb((byte)(_dark ? 0x40 : 0x2E), ink.R, ink.G, ink.B) : ClassicTokens.Tint(ink, _dark)),
            CornerRadius = new CornerRadius(pixel ? 0 : 9),
            Height = pixel ? double.NaN : 18,
            Padding = pixel ? new Thickness(PixelPlate.Cell * 2, 0, PixelPlate.Cell * 2, 0) : new Thickness(7, 0, 7, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = plan,
                FontSize = 10,
                LineHeight = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res<Brush>("TextFillColorPrimaryBrush"),
            },
        };
    }

    /// 一个 agent 的分段：头部一行（头像、名字、套餐，右边是额度或“空闲”），任务列表，
    /// 警告，单角色卡再加 token 行。
    private UIElement Section(StatusCard card, uint swatch, bool pixel, bool single, double column, Dictionary<string, StatusTaskState> states)
    {
        // Rows indent past the face; the pixel panel keeps whole cells.
        var indent = pixel ? 14.0 : 24.0;
        var panel = new StackPanel();
        panel.Children.Add(Head(card, swatch, pixel, single, column));
        if (card.TaskRows.Count > 0)
        {
            panel.Children.Add(Tasks(card.TaskRows, indent, column - indent, pixel, states));
        }
        else if (card.Loading)
        {
            panel.Children.Add(Caption(L10n.Tr("Reading sessions…"), "TextFillColorTertiaryBrush", new Thickness(indent, 8, 0, 0)));
        }
        if (card.Warnings.Count > 0) panel.Children.Add(Warnings(card.Warnings, indent, pixel));
        if (single && card.Tokens is { } tokens)
        {
            panel.Children.Add(TokenLine(tokens, indent, column - indent, pixel, $"tokens:{(int)card.Provider}", card.TaskRows.Count > 0 ? 12 : 8));
        }
        return panel;
    }

    /// 头部一行。单角色卡右边是这一家的紧凑额度；没有任务时右边写“空闲”或应用不在的说法。
    /// 放不下时先去掉套餐 chip，再只留用得更多的那个窗口（设计稿 §2.6）。
    private FrameworkElement Head(StatusCard card, uint swatch, bool pixel, bool single, double column)
    {
        var who = new StackPanel { Orientation = Orientation.Horizontal };
        who.Children.Add(pixel
            ? new Rectangle
            {
                Width = 3 * PixelPlate.Cell,
                Height = 3 * PixelPlate.Cell,
                Fill = Brand.Brush(swatch),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 8, 0),
            }
            : new FaceChip(16, Brand.Rgb(swatch))
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
        who.Children.Add(new TextBlock
        {
            Text = card.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Border? chip = null;
        if (card.Plan is { Length: > 0 } plan)
        {
            chip = PlanChip(plan, swatch, pixel);
            chip.Margin = new Thickness(6, 0, 0, 0);
            who.Children.Add(chip);
        }
        FrameworkElement? right = null;
        StatusQuota? quota = null;
        if (card.TaskRows.Count == 0 && !card.Loading)
        {
            right = Caption(card.Away ?? L10n.Tr("Idle"), "TextFillColorTertiaryBrush");
        }
        else if (single && card.Quota is { } own)
        {
            quota = own;
            right = QuotaText(own, own.Windows, null, pixel);
        }
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(head, who, 0, 0);
        if (right is null) return head;
        Faced(who);
        Faced(right);
        bool Fits() => Wide(who) + 8 + Wide(right) <= column;
        if (!Fits() && chip is not null) who.Children.Remove(chip);
        if (!Fits() && quota is { Windows.Count: > 1, Busiest: { } busiest }) right = Faced(QuotaText(quota, new[] { busiest }, null, pixel));
        right.VerticalAlignment = VerticalAlignment.Center;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Place(head, right, 0, 1);
        return head;
    }

    /// 紧凑额度：“5h 38% · 周 61%”。标签用三级墨色，数字用二级（旧读数也用三级，Classic 后面加时钟图标），
    /// 到 90% 的数字用 Critical（Classic 再加粗）。lead 是全员卡钉住的那家的名字。
    private static TextBlock QuotaText(StatusQuota quota, IEnumerable<StatusQuotaWindow> windows, string? lead, bool pixel)
    {
        var text = new TextBlock { FontSize = pixel ? 12 : 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var label = Res<Brush>("TextFillColorTertiaryBrush");
        if (lead is not null) text.Inlines.Add(new Run { Text = lead + " ", Foreground = label });
        var first = true;
        foreach (var window in windows)
        {
            if (!first) text.Inlines.Add(new Run { Text = " · ", Foreground = label });
            first = false;
            text.Inlines.Add(new Run { Text = window.Label + " ", Foreground = label });
            var value = new Run
            {
                Text = window.Value,
                Foreground = window.Warn ? Ink("Critical")
                    : Res<Brush>(quota.Stale || window.Used is null ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush"),
            };
            if (window.Warn && !pixel) value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            text.Inlines.Add(value);
        }
        if (quota.Stale && !pixel)
        {
            text.Inlines.Add(new Run { Text = " ", FontFamily = Icons, FontSize = 10, Foreground = label });
        }
        return text;
    }

    private static readonly FontFamily Icons = new("Segoe Fluent Icons,Segoe MDL2 Assets");

    /// 全员卡的面板头：左边是汇总（有出错的用 Critical 色，有等你的用 Caution 色），右边是钉住的额度。
    /// 放不下时汇总只留最紧急的一段，再放不下额度只留用得更多的窗口。
    private FrameworkElement PanelHead(Request request, HoverCardPanel panel, double width)
    {
        var rows = request.Sections.SelectMany(section => section.Card.TaskRows).ToList();
        var tally = StatusTaskRows.Tally(rows);
        TextBlock Summary(bool urgentOnly)
        {
            var text = Caption(StatusTaskRows.Summary(rows, urgentOnly), "TextFillColorSecondaryBrush");
            if (tally.Errors > 0) text.Foreground = Ink("Critical");
            else if (tally.Waiting > 0) text.Foreground = Ink("Caution");
            return Faced(text);
        }
        var head = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = Summary(false);
        if (panel.Pinned is { } quota)
        {
            var right = Faced(QuotaText(quota, quota.Windows, panel.PinnedName, request.Pixel));
            if (Wide(left) + 8 + Wide(right) > width) left = Summary(true);
            if (Wide(left) + 8 + Wide(right) > width && quota is { Windows.Count: > 1, Busiest: { } busiest })
            {
                right = Faced(QuotaText(quota, new[] { busiest }, panel.PinnedName, request.Pixel));
            }
            right.HorizontalAlignment = HorizontalAlignment.Right;
            Place(head, right, 0, 1);
        }
        Place(head, left, 0, 0);
        return head;
    }

    /// 留在卡上的 agent 警告：图标加二级墨色的文字，可以折行。不用红色，这不是任务出错。
    private static FrameworkElement Warnings(IReadOnlyList<string> warnings, double indent, bool pixel)
    {
        var line = new Grid { Margin = new Thickness(indent, 8, 0, 0), ColumnSpacing = 6 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement sign;
        if (pixel)
        {
            var icon = PixelIcons.Icon("warning");
            icon.Foreground = Res<Brush>("TextFillColorSecondaryBrush");
            sign = icon;
        }
        else
        {
            sign = new FontIcon { Glyph = "", FontSize = 12, FontFamily = Icons, Foreground = Res<Brush>("TextFillColorSecondaryBrush") };
        }
        sign.VerticalAlignment = VerticalAlignment.Top;
        sign.Margin = new Thickness(0, 2, 0, 0);
        Place(line, sign, 0, 0);
        var text = Caption(string.Join(" · ", warnings), "TextFillColorSecondaryBrush");
        text.TextTrimming = TextTrimming.None;
        text.TextWrapping = TextWrapping.Wrap;
        Place(line, text, 0, 1);
        return line;
    }

    /// Token 行：先试一行“今天 1.2M · 本月 38.4M”，放不下就回退成两行（今天、本月各一行，
    /// 数字右对齐）。回退过的在卡关闭前保持两行。
    private FrameworkElement TokenLine(StatusTokens tokens, double indent, double width, bool pixel, string id, double top)
    {
        var labelWidth = pixel ? PixelLabelWidth : LabelWidth;
        var line = new Grid { Margin = new Thickness(indent, top, 0, 0), ColumnSpacing = 8, RowSpacing = 2 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = Caption(tokens.Label, "TextFillColorSecondaryBrush");
        label.VerticalAlignment = VerticalAlignment.Top;
        Place(line, label, 0, 0);
        if (tokens.TodayNumber.Length == 0)
        {
            Place(line, Caption(tokens.Today, "TextFillColorTertiaryBrush"), 0, 1);
            return line;
        }
        if (!_shortened.Contains(id))
        {
            var one = Faced(Emphasised($"{tokens.Today} · {tokens.Month}", tokens.TodayNumber, tokens.MonthNumber));
            if (Wide(one) <= width - labelWidth - 8)
            {
                Place(line, one, 0, 1);
                return line;
            }
            _shortened.Add(id);
        }
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pixel ? PixelValueWidth : ValueWidth) });
        line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRowSpan(label, 2);
        Place(line, Caption(L10n.Tr("Today"), "TextFillColorSecondaryBrush"), 0, 1);
        Place(line, Caption(L10n.Tr("This month"), "TextFillColorSecondaryBrush"), 1, 1);
        var today = Emphasised(tokens.TodayNumber, tokens.TodayNumber);
        today.HorizontalAlignment = HorizontalAlignment.Right;
        Place(line, today, 0, 2);
        var month = Emphasised(tokens.MonthNumber, tokens.MonthNumber);
        month.HorizontalAlignment = HorizontalAlignment.Right;
        Place(line, month, 1, 2);
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
        var text = new TextBlock { FontSize = 12, Foreground = Res<Brush>("TextFillColorTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
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

    /// Fluent's inks, or Classic's on the paper card.
    private static T Res<T>(string key) => _paperInk && Paper(key) is T paper ? paper : Fluent.Resource<T>(key, _dark);

    private static object? Paper(string key) => key switch
    {
        "TextFillColorPrimaryBrush" => ClassicTokens.Brush("Text", _dark),
        "TextFillColorSecondaryBrush" => ClassicTokens.Brush("Text2", _dark),
        "TextFillColorTertiaryBrush" => ClassicTokens.Brush("Text3", _dark),
        "SystemFillColorCriticalBrush" => ClassicTokens.Brush("Critical", _dark),
        "DividerStrokeColorDefaultBrush" => ClassicTokens.Brush("LineSoft", _dark),
        _ => null,
    };

    /// 状态色（Caution、Critical、Success）取这套外观自己的 token。
    private static SolidColorBrush Ink(string token) => _paperInk ? ClassicTokens.Brush(token, _dark) : PixelTokens.Brush(token, _dark);
}
