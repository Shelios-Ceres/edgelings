using System.Globalization;
using System.Text;
using AgentIsland.Model;

namespace AgentIsland.WinUI.Drawer;

internal static class HoverCardContentKey
{
    public static string Create(IReadOnlyList<(StatusCard Card, uint Swatch)> sections, bool dark, bool pixel, double scale)
    {
        var key = new StringBuilder();
        void Add(object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            key.Append(text.Length).Append(':').Append(text);
        }
        Add(dark);
        Add(pixel);
        // Pixel font sizing changes with physical scale even if every value is unchanged.
        if (pixel) Add(scale);
        Add(sections.Count);
        foreach (var (card, swatch) in sections)
        {
            Add(card.Provider); Add(swatch); Add(card.Name); Add(card.Phase); Add(card.Detail is not null); Add(card.Detail);
            Add(card.Plan);
            Add(card.Tasks is not null);
            if (card.Tasks is { } tasks) { Add(tasks.Label); Add(tasks.Text); Add(tasks.Number); }
            Add(card.Waiting.More); Add(card.Waiting.Rows.Count);
            foreach (var row in card.Waiting.Rows) { Add(row.Title); Add(row.Word); }
            Add(card.Meters.Count);
            foreach (var meter in card.Meters)
            {
                Add(meter.Label); Add(meter.CardLabel); Add(meter.Value); Add(meter.Shown); Add(meter.Used >= 0.9);
            }
            Add(card.Tokens is not null);
            if (card.Tokens is { } tokens)
            {
                Add(tokens.Label); Add(tokens.Today); Add(tokens.TodayNumber); Add(tokens.Month); Add(tokens.MonthNumber);
            }
            Add(card.Note.Count);
            foreach (var note in card.Note)
            {
                Add(note.Lead); Add(note.Text); Add(note.Numbers.Count);
                foreach (var number in note.Numbers) Add(number);
            }
        }
        return key.ToString();
    }
}
