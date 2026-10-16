namespace AgentIsland.Model;

/// Saved values are the user's choices, one per character; Cursor's work
/// shows under Grok's character (DisplayProviderExtensions.Character).
public static class DrawerVisibility
{
    public static IReadOnlyDictionary<DisplayProvider, bool> Characters(IReadOnlyDictionary<DisplayProvider, bool> requested)
    {
        var shown = DisplayProviders.All.ToDictionary(p => p, p => p.HasCharacter() && requested.TryGetValue(p, out var value) && value);
        // Keep an entry point when every provider has been hidden.
        if (!shown.Values.Any(value => value)) shown[DisplayProviders.All[0]] = true;
        return shown;
    }

    /// The drawer menu's "Shown agents" switches, one per character.
    public static IReadOnlyList<(DisplayProvider Provider, bool On)> Switches(
        IReadOnlyList<(DisplayProvider Provider, bool Requested)> requested) =>
        requested.Where(choice => choice.Provider.HasCharacter()).ToList();

    /// The Agents page's groups, each in the user's order: agents on this
    /// computer, agents not found here, and the ones the user hid.
    public static (IReadOnlyList<DisplayProvider> Detected, IReadOnlyList<DisplayProvider> Missing, IReadOnlyList<DisplayProvider> Hidden) Groups(
        IEnumerable<DisplayProvider> order, Func<DisplayProvider, bool> hidden, Func<DisplayProvider, bool> detected)
    {
        var all = order.Where(p => p.HasCharacter()).ToList();
        return (all.Where(p => !hidden(p) && detected(p)).ToList(),
            all.Where(p => !hidden(p) && !detected(p)).ToList(),
            all.Where(hidden).ToList());
    }
}
