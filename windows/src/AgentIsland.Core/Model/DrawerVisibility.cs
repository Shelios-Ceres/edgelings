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

    /// The Agents page's one list, in the drawer's order: the characters it
    /// shows, then the rest at the bottom (hidden, or not on this computer),
    /// each with the same switch.
    public static IReadOnlyList<(DisplayProvider Provider, bool Shown)> Lineup(
        IEnumerable<DisplayProvider> order, Func<DisplayProvider, bool> shown)
    {
        var all = order.Where(p => p.HasCharacter()).ToList();
        return all.Where(shown).Select(p => (p, true))
            .Concat(all.Where(p => !shown(p)).Select(p => (p, false)))
            .ToList();
    }
}
