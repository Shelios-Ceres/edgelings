namespace AgentIsland.Model;

/// Menu values represent the user's choices; merging changes only the rendered slots.
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
}
