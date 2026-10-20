namespace AgentIsland.Model;

/// Every look each agent can wear in each character set, and the colour each
/// brings. It mirrors the bundled engines' LOOKS and swatchOf (the rows are
/// in LookCatalog.Data.cs, generated from them); LookCatalogTests checks every
/// row against the engines the app ships. A kind's default look comes first,
/// and it is today's look, so nobody sees a change until they pick one.
public static partial class LookCatalog
{
    /// A look's saved id, its English name and its identity colour (0xRRGGBB).
    public sealed record Look(string Id, string Name, uint Swatch);

    /// The looks a provider can wear in a set, default first. Cursor draws
    /// Grok's character, so its ids are Grok's; only the colour may differ.
    public static IReadOnlyList<Look> For(CharacterTheme theme, DisplayProvider provider) =>
        Data.TryGetValue((theme, DrawerLooks.Kind(provider)), out var looks) ? looks : Data[(theme, "claude")];

    /// The look an id names, or the default for an id the engine no longer
    /// knows (or none).
    public static Look Resolve(CharacterTheme theme, DisplayProvider provider, string? id)
    {
        var looks = For(theme, provider);
        return looks.FirstOrDefault(look => look.Id == id) ?? looks[0];
    }

    /// A look's colour on paper: darkened to at most 118 luma in light, and
    /// lightened toward white to at least 165 in dark, so a bar, a chip's text
    /// and a chart read as the default looks' pairs do.
    public static uint OnPaper(uint swatch, bool dark)
    {
        double r = swatch >> 16 & 0xFF, g = swatch >> 8 & 0xFF, b = swatch & 0xFF;
        double Luma() => 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (!dark && Luma() > 118)
        {
            var scale = 118 / Luma();
            (r, g, b) = (r * scale, g * scale, b * scale);
        }
        else if (dark && Luma() < 165)
        {
            // Toward white by the share that lifts the luma to 165.
            var share = (165 - Luma()) / (255 - Luma());
            (r, g, b) = (r + (255 - r) * share, g + (255 - g) * share, b + (255 - b) * share);
        }
        return (uint)Math.Round(r) << 16 | (uint)Math.Round(g) << 8 | (uint)Math.Round(b);
    }

    /// Every look's colour, for checks.
    public static IEnumerable<uint> AllSwatches() => Data.Values.SelectMany(looks => looks.Select(look => look.Swatch)).Distinct();

    /// The look an agent wears now: the pick saved for the set, or its default.
    public static Look Of(CharacterTheme theme, DisplayProvider provider) =>
        Resolve(theme, provider, DrawerStore.Shared.LookId(theme, provider));
}
