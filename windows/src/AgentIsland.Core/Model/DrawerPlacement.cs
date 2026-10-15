namespace AgentIsland.Model;

/// Where the drawer sits along its edge, and the order its characters stand
/// in. Pure, so the rules can be tested without a window.
public static class DrawerPlacement
{
    /// The drawer's top in screen pixels. Unlocked, it follows the offset: 0
    /// at the top of the work area, 1 at the bottom. Locked, its top edge stays
    /// pinned, so a character appearing or leaving never shifts the ones above
    /// it; it moves only as far as it must to stay on screen.
    public static int Top(int workTop, int workBottom, int height, double scale, double offset, double? pinnedTopDip)
    {
        var travel = Math.Max(0, workBottom - workTop - height);
        if (pinnedTopDip is { } pinned) return workTop + Math.Clamp((int)Math.Round(pinned * scale), 0, travel);
        return workTop + (int)Math.Round(offset * travel);
    }

    /// The pinned top, in DIPs below the work area, for a drawer at `top`.
    public static double PinFor(int top, int workTop, double scale) => (top - workTop) / scale;

    /// The offset that keeps a drawer at `top` where it is once unpinned.
    public static double OffsetFor(int top, int workTop, int workBottom, int height)
    {
        var travel = workBottom - workTop - height;
        return travel > 0 ? Math.Clamp((double)(top - workTop) / travel, 0, 1) : 0.5;
    }

    /// The characters' order, top first: the saved one, then any provider it
    /// lacks in the default order. Unknown and repeated names are dropped.
    public static IReadOnlyList<DisplayProvider> Order(IEnumerable<string>? saved)
    {
        var order = new List<DisplayProvider>();
        foreach (var raw in saved ?? Array.Empty<string>())
        {
            if (DisplayProviders.Parse(raw) is { } provider && !order.Contains(provider)) order.Add(provider);
        }
        order.AddRange(DisplayProviders.All.Where(provider => !order.Contains(provider)));
        return order;
    }

    /// The saved order with the given providers rearranged into the slots
    /// they held. A provider left out keeps its slot, so a character missing
    /// from the list (Cursor while it shares Grok's) returns where it stood.
    public static IReadOnlyList<DisplayProvider> Reordered(IEnumerable<DisplayProvider> saved, IEnumerable<DisplayProvider> arranged)
    {
        var order = Order(saved.Select(provider => provider.RawValue()));
        var moved = arranged.Distinct().Where(order.Contains).ToList();
        var next = new Queue<DisplayProvider>(moved);
        return order.Select(provider => moved.Contains(provider) ? next.Dequeue() : provider).ToList();
    }
}
