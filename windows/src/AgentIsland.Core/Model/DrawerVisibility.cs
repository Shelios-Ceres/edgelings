namespace AgentIsland.Model;

/// Saved values are the user's choices, one per provider; merging changes only the rendered slots.
public static class DrawerVisibility
{
    public static IReadOnlyDictionary<DisplayProvider, bool> Characters(
        IReadOnlyDictionary<DisplayProvider, bool> requested, bool merged)
    {
        var shown = DisplayProviders.All.ToDictionary(p => p, p => requested.TryGetValue(p, out var value) && value);
        if (merged)
        {
            shown[DisplayProvider.Grok] |= shown[DisplayProvider.Cursor];
            shown[DisplayProvider.Cursor] = false;
        }
        // Keep an entry point when every provider has been hidden.
        if (!shown.Values.Any(value => value)) shown[DisplayProviders.All[0]] = true;
        return shown;
    }

    /// The drawer menu's "Shown agents" switches, one per character. While
    /// Grok and Cursor share a character, Grok's switch stands for both: it is
    /// on while either choice shows the character, and it sets both (SetBy),
    /// so the shared character can always be hidden.
    public static IReadOnlyList<(DisplayProvider Provider, bool On)> Switches(
        IReadOnlyList<(DisplayProvider Provider, bool Requested)> requested, bool merged)
    {
        bool Requested(DisplayProvider provider) => requested.Any(choice => choice.Provider == provider && choice.Requested);
        return requested
            .Where(choice => !(merged && choice.Provider == DisplayProvider.Cursor))
            .Select(choice => (choice.Provider, merged && choice.Provider == DisplayProvider.Grok
                ? Requested(DisplayProvider.Grok) || Requested(DisplayProvider.Cursor)
                : choice.Requested))
            .ToList();
    }

    /// The Agents page's groups, each in the user's order: agents on this
    /// computer, agents not found here, and the ones the user hid.
    public static (IReadOnlyList<DisplayProvider> Detected, IReadOnlyList<DisplayProvider> Missing, IReadOnlyList<DisplayProvider> Hidden) Groups(
        IEnumerable<DisplayProvider> order, Func<DisplayProvider, bool> hidden, Func<DisplayProvider, bool> detected)
    {
        var all = order.ToList();
        return (all.Where(p => !hidden(p) && detected(p)).ToList(),
            all.Where(p => !hidden(p) && !detected(p)).ToList(),
            all.Where(hidden).ToList());
    }

    /// The saved choices one switch sets.
    public static IReadOnlyList<DisplayProvider> SetBy(DisplayProvider provider, bool merged) =>
        merged && provider == DisplayProvider.Grok
            ? new[] { DisplayProvider.Grok, DisplayProvider.Cursor }
            : new[] { provider };
}
