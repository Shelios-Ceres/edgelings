using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

/// Phrases set one after another, a separator between those that share a
/// line: a phrase that would not fit starts the next line instead of
/// breaking. A phrase too long for a whole line breaks only at its spaces
/// (Words), so a word, an address or a path moves whole to the next line;
/// only a word longer than a line breaks inside. The panel grows with its
/// lines. Lines after the first start Hang in.
internal sealed class PhraseFlow : Panel
{
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
    private static readonly Dictionary<(string Family, double Size, double Spacing), double> Spaces = new();

    // Each child's place from the last measure; a separator that would start
    // a line has none.
    private readonly List<Rect?> _places = new();
    // Children alternate phrase, separator, phrase…; words have spaces between.
    private readonly bool _separated;

    private PhraseFlow(bool separated)
    {
        _separated = separated;
    }

    public double Hang { get; init; }

    /// The second phrase starts the second line whether it fits or not.
    public bool BreakFirst { get; set; }

    /// Runs when the second phrase starts the second line.
    public Action? Broke { get; set; }

    /// The phrases, with a separator made for each gap between two.
    public static PhraseFlow Of(IReadOnlyList<FrameworkElement> phrases, Func<FrameworkElement> separator, double hang = 0)
    {
        var flow = new PhraseFlow(separated: true) { Hang = hang };
        for (var i = 0; i < phrases.Count; i++)
        {
            if (i > 0) flow.Children.Add(separator());
            flow.Children.Add(phrases[i]);
        }
        return flow;
    }

    /// One phrase as its words, a space between those that share a line.
    public static PhraseFlow Words(IEnumerable<TextBlock> words, double hang = 0)
    {
        var flow = new PhraseFlow(separated: false) { Hang = hang };
        foreach (var word in words) flow.Children.Add(word);
        return flow;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        var step = _separated ? 2 : 1;
        _places.Clear();
        double x = 0, y = 0, line = 0, widest = 0;
        // The children of the line being set, to centre them on it.
        var current = new List<int>();
        void EndLine()
        {
            foreach (var index in current)
            {
                if (_places[index] is { } place) _places[index] = new Rect(place.X, y + (line - place.Height) / 2, place.Width, place.Height);
            }
            current.Clear();
        }
        for (var i = 0; i < Children.Count; i += step)
        {
            var phrase = Children[i];
            phrase.Measure(Unbounded);
            var size = phrase.DesiredSize;
            var separator = _separated && i > 0 ? Children[i - 1] : null;
            separator?.Measure(Unbounded);
            var gap = separator?.DesiredSize.Width ?? (i > 0 ? Space(phrase) : 0);
            var forced = BreakFirst && i == step;
            if (i > 0 && !forced && x + gap + size.Width <= width)
            {
                if (separator is not null)
                {
                    _places.Add(new Rect(x, 0, gap, separator.DesiredSize.Height));
                    current.Add(_places.Count - 1);
                }
                x += gap;
            }
            else
            {
                if (i > 0)
                {
                    EndLine();
                    if (separator is not null) _places.Add(null);
                    if (i == step) Broke?.Invoke();
                    y += line;
                    line = 0;
                    x = Hang;
                }
                // Too long for a whole line: it breaks there.
                if (x + size.Width > width)
                {
                    phrase.Measure(new Size(Math.Max(0, width - x), double.PositiveInfinity));
                    size = phrase.DesiredSize;
                }
            }
            _places.Add(new Rect(x, 0, size.Width, size.Height));
            current.Add(_places.Count - 1);
            x += size.Width;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x);
        }
        EndLine();
        return new Size(double.IsInfinity(width) ? widest : Math.Min(widest, width), y + line);
    }

    /// A separator with no place gets an empty slot, which clips it away;
    /// opacity stays free for the card's own motion (M4).
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < Children.Count && i < _places.Count; i++)
        {
            Children[i].Arrange(_places[i] ?? new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }

    /// A space in a word's own face: a text block measures without trailing
    /// spaces, so it is the width a space adds between two letters.
    private static double Space(UIElement element)
    {
        if (element is not TextBlock word) return 4;
        var key = (word.FontFamily?.Source ?? "", word.FontSize, (double)word.CharacterSpacing);
        if (Spaces.TryGetValue(key, out var space)) return space;
        var probe = new TextBlock { FontFamily = word.FontFamily, FontSize = word.FontSize, CharacterSpacing = word.CharacterSpacing };
        double Width(string text)
        {
            probe.Text = text;
            probe.Measure(Unbounded);
            return probe.DesiredSize.Width;
        }
        return Spaces[key] = Math.Max(0, Width("x x") - Width("xx"));
    }
}
