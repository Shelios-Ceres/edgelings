using AgentIsland.Core;
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

    /// The card's sections, divided. The all-agents card stands in one
    /// column while it fits the screen's height; past it, its sections run
    /// down one column and on to the next, in as few columns as fit, cut so
    /// the tallest is as short as it can be, each column as wide as one
    /// character's card, with a divider between them.
    private void Build(Request request)
    {
        var pixel = request.Pixel;
        _body.Children.Clear();
        _built = new();
        _counting.Clear();
        var padding = pixel ? PixelPadding : PaperPadding;
        var column = (pixel ? PixelCardWidth : CardWidth) - padding.Left - padding.Right;
        var sections = request.Sections.Select(section =>
        {
            var built = Section(section.Card, section.Swatch, pixel);
            if (pixel) ApplyPixelFace(built, PixelPlate.FontSize(request.Scale));
            return built;
        }).ToList();
        var columns = Columns(sections, column, pixel, Room(request));
        _width = padding.Left + columns.Count * column + (columns.Count - 1) * Gutter(pixel) + padding.Right;
        if (columns.Count == 1)
        {
            Stack(_body, sections, column, pixel);
            _shares = _built;
            return;
        }
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
        _shares = _built;
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
            if (i > 0)
            {
                panel.Children.Add(pixel
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
                    });
            }
            panel.Children.Add(sections[i]);
        }
    }

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
            case TextBlock text:
                text.FontFamily = PixelPlate.Font;
                text.FontSize = size;
                text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                text.CharacterSpacing = 0;
                foreach (var inline in text.Inlines) inline.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                break;
        }
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

    private UIElement Section(StatusCard card, uint swatch, bool pixel)
    {
        // Rows indent past the face; the pixel panel keeps whole cells.
        var indent = pixel ? 14.0 : 24.0;
        var labelWidth = pixel ? PixelLabelWidth : LabelWidth;
        var valueWidth = pixel ? PixelValueWidth : ValueWidth;
        var panel = new StackPanel();
        // Marker, name and plan. Pixel follows them with the phase when it
        // fits, else gives it the next line; Classic starts line 2 with it,
        // since English phases rarely fit beside a name and plan at 264 DIP.
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
        if (card.Plan is { Length: > 0 } plan)
        {
            var chip = PlanChip(plan, swatch, pixel);
            chip.Margin = new Thickness(6, 0, 0, 0);
            who.Children.Add(chip);
        }
        // A phrase that breaks carries on about a space in (two whole cells
        // under Pixel), so it reads as one.
        const double carry = 4;
        if (pixel)
        {
            var head = PhraseFlow.Of(new FrameworkElement[] { who, Phrase(card.Phase, "TextFillColorSecondaryBrush", carry, 13) },
                () => Separator("TextFillColorSecondaryBrush", 13), hang: indent);
            head.BreakFirst = _phaseBelow.Contains(card.Provider);
            head.Broke = () => _phaseBelow.Add(card.Provider);
            panel.Children.Add(head);
            if (card.Detail is { } detail)
            {
                panel.Children.Add(Flow(Parts(detail).Select(part => Phrase(part, "TextFillColorTertiaryBrush", carry)), "TextFillColorTertiaryBrush", new Thickness(indent, 1, 0, 0)));
            }
        }
        else
        {
            panel.Children.Add(who);
            // The phase, then the detail's parts; what doesn't fit moves to the next line.
            var status = new List<FrameworkElement> { Phrase(card.Phase, "TextFillColorPrimaryBrush", carry) };
            if (card.Detail is { } detail) status.AddRange(Parts(detail).Select(part => Phrase(part, "TextFillColorSecondaryBrush", carry)));
            panel.Children.Add(Flow(status, "TextFillColorSecondaryBrush", new Thickness(indent, 1, 0, 0)));
        }

        // Fixed label and value columns line the rows up across sections.
        var top = 7.0;
        if (card.Tasks is { } tasks)
        {
            var line = Line(top, indent, labelWidth);
            Place(line, Caption(tasks.Label, "TextFillColorSecondaryBrush"), 0, 0);
            Place(line, Emphasised(tasks.Text, tasks.Number), 0, 1);
            panel.Children.Add(line);
            top = 4;
        }
        if (card.Waiting.Rows.Count > 0)
        {
            panel.Children.Add(WaitingList(card.Waiting, indent, top, pixel));
            top = 4;
        }
        var meters = new Grid { Margin = new Thickness(indent, top, 0, 0), RowSpacing = 6, ColumnSpacing = 8 };
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
        var row = 0;
        foreach (var meter in card.Meters)
        {
            meters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var warn = meter.Used >= 0.9;
            Place(meters, Caption(meter.CardLabel, "TextFillColorSecondaryBrush"), row, 0);
            FrameworkElement bar = pixel
                ? new PixelBar { VerticalAlignment = VerticalAlignment.Center }
                : new QuotaBar(6) { VerticalAlignment = VerticalAlignment.Center };
            Place(meters, bar, row, 1);
            var value = Caption(meter.Value, warn ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            value.HorizontalAlignment = HorizontalAlignment.Right;
            if (warn && !pixel) value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            Place(meters, value, row, 2);
            Count((IQuotaBar)bar, bar, value, $"{(int)card.Provider}|{meter.Label}", meter, warn, Brand.Rgb(swatch), pixel);
            row++;
        }
        if (row > 0)
        {
            panel.Children.Add(meters);
            top = 4;
        }
        if (card.Tokens is { } tokens)
        {
            var line = new Grid { Margin = new Thickness(indent, top, 0, 0), ColumnSpacing = 8, RowSpacing = 2 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
            line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Caption(tokens.Label, "TextFillColorSecondaryBrush");
            label.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRowSpan(label, 2);
            Place(line, label, 0, 0);
            if (tokens.TodayNumber.Length == 0)
            {
                var pending = Caption(tokens.Today, "TextFillColorTertiaryBrush");
                Grid.SetColumnSpan(pending, 2);
                Place(line, pending, 0, 1);
            }
            else
            {
                Place(line, Caption(L10n.Tr("Today"), "TextFillColorSecondaryBrush"), 0, 1);
                Place(line, Caption(L10n.Tr("This month"), "TextFillColorSecondaryBrush"), 1, 1);
                var today = Emphasised(tokens.TodayNumber, tokens.TodayNumber);
                today.HorizontalAlignment = HorizontalAlignment.Right;
                Place(line, today, 0, 2);
                var month = Emphasised(tokens.MonthNumber, tokens.MonthNumber);
                month.HorizontalAlignment = HorizontalAlignment.Right;
                Place(line, month, 1, 2);
            }
            panel.Children.Add(line);
        }
        if (card.Note.Count > 0)
        {
            // The notes flow, but for one with a lead: its lead on a line,
            // each of its parts on a line under it.
            var notes = new StackPanel { Margin = new Thickness(indent, pixel ? 5 : 6, 0, 0) };
            var run = new List<FrameworkElement>();
            void EndRun()
            {
                if (run.Count > 0) notes.Children.Add(Flow(run, "TextFillColorTertiaryBrush", default));
                run = new List<FrameworkElement>();
            }
            foreach (var note in card.Note)
            {
                var parts = Parts(note.Text).Select(part => Phrase(part, "TextFillColorTertiaryBrush", carry, numbers: note.Numbers));
                if (note.Lead is not { } lead)
                {
                    run.AddRange(parts);
                    continue;
                }
                EndRun();
                notes.Children.Add(Phrase(lead, "TextFillColorTertiaryBrush", carry));
                foreach (var part in parts) notes.Children.Add(part);
            }
            EndRun();
            panel.Children.Add(notes);
        }
        return panel;
    }

    /// The sessions that wait on the user: a title and what it waits for on
    /// each row. A title is the user's own text, so it trims after two lines
    /// rather than flows.
    private static StackPanel WaitingList(StatusWaitingList waiting, double indent, double top, bool pixel)
    {
        var list = new StackPanel { Margin = new Thickness(indent, top, 0, 0), Spacing = 2 };
        foreach (var row in waiting.Rows)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Caption(row.Title, "TextFillColorSecondaryBrush");
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxLines = 2;
            Place(line, title, 0, 0);
            var word = Caption(row.Word, row.Kind == SessionEventKind.Error ? "SystemFillColorCriticalBrush" : "TextFillColorTertiaryBrush");
            if (!pixel)
            {
                // Classic's words are coloured by what they ask for.
                word.Foreground = row.Kind switch
                {
                    SessionEventKind.Approval => ClassicTokens.Brush("Caution", _dark),
                    SessionEventKind.Question => ClassicTokens.Brush("AccentText", _dark),
                    SessionEventKind.Error or SessionEventKind.TaskSkipped => ClassicTokens.Brush("Critical", _dark),
                    _ => ClassicTokens.Brush("Text2", _dark),
                };
                title.Foreground = ClassicTokens.Brush("Text", _dark);
            }
            word.HorizontalAlignment = HorizontalAlignment.Right;
            word.VerticalAlignment = VerticalAlignment.Top;
            Place(line, word, 0, 1);
            list.Children.Add(line);
        }
        if (waiting.More > 0)
        {
            list.Children.Add(Caption(string.Format(L10n.Tr("+{0} more"), waiting.More), pixel ? "TextFillColorSecondaryBrush" : "TextFillColorTertiaryBrush"));
        }
        return list;
    }

    /// A label column, a stretching value and an end-aligned value.
    private static Grid Line(double top, double indent, double labelWidth)
    {
        var line = new Grid { Margin = new Thickness(indent, top, 0, 0), ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
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

    /// A meter's bar and value. A card refreshing in place counts them from
    /// what it showed before (M4), once the new bar is on screen; otherwise
    /// they show their share at once.
    private void Count(IQuotaBar bar, FrameworkElement element, TextBlock value, string key, StatusMeter meter, bool warn, Windows.UI.Color color, bool pixel)
    {
        var share = meter.Shown ?? 0;
        _built[key] = (share, warn);
        if (!_recount || meter.Shown is null || !_shares.TryGetValue(key, out var was) || Math.Abs(was.Share - share) < 0.0005)
        {
            bar.Set(meter.Shown, color, warn, _dark);
            return;
        }
        void Follow(double shown, bool warning)
        {
            value.Text = QuotaCount.Percent(shown, share, meter.Value);
            value.Foreground = Res<Brush>(warning ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            if (!pixel) value.FontWeight = warning ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        bar.Set(was.Share, color, was.Warn, _dark);
        Follow(was.Share, was.Warn);
        _counting.Add(value);
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            element.Loaded -= loaded;
            bar.Set(meter.Shown, color, warn, _dark, Follow);
        };
        element.Loaded += loaded;
    }

    private static TextBlock Caption(string text, string brush, Thickness margin = default) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res<Brush>(brush),
        Margin = margin,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// Details and notes carry warnings (a dropped connection, stale quota):
    /// their phrases flow whole from line to line, never shortened.
    private static PhraseFlow Flow(IEnumerable<FrameworkElement> phrases, string separator, Thickness margin)
    {
        var flow = PhraseFlow.Of(phrases.ToList(), () => Separator(separator));
        flow.Margin = margin;
        return flow;
    }

    /// One phrase as its words, its numbers lifted to primary ink: whole
    /// where a line holds it, else breaking only at its spaces, carrying on
    /// a step in; a word longer than a line breaks inside.
    private static PhraseFlow Phrase(string text, string brush, double carry, double size = 12, IReadOnlyList<string>? numbers = null) =>
        PhraseFlow.Words(GrokBot.Words(text).Select(word =>
        {
            var block = new TextBlock { FontSize = size, Foreground = Res<Brush>(brush), TextWrapping = TextWrapping.Wrap };
            Append(block, word, numbers?.Where(word.Contains).ToList() ?? new List<string>());
            return block;
        }), carry);

    private static TextBlock Separator(string brush, double size = 12) => new()
    {
        // Room as margins: a text block measures without trailing spaces.
        Text = "·",
        Margin = new Thickness(4, 0, 4, 0),
        FontSize = size,
        Foreground = Res<Brush>(brush),
    };

    /// A detail's or a note's parts, as they are joined ("Cursor · Bash").
    private static IEnumerable<string> Parts(string detail) =>
        detail.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
}
