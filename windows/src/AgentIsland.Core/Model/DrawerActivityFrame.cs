using AgentIsland.Core;

namespace AgentIsland.Model;

public sealed record DrawerActivity(AgentPhase Phase, string? Detail, int Running)
{
    public static readonly DrawerActivity Idle = new(AgentPhase.Idle, null, 0);
}

/// Provider observations stay independent of the characters used to present them.
public sealed class DrawerActivityFrame
{
    private readonly IReadOnlyDictionary<DisplayProvider, DrawerActivity> _providers;

    public DrawerActivityFrame(IEnumerable<(DisplayProvider Provider, AgentPhase Phase, string? Detail, int Running)> readings) =>
        _providers = readings.ToDictionary(r => r.Provider, r => new DrawerActivity(r.Phase, r.Detail, r.Running));

    public DrawerActivity ForProvider(DisplayProvider provider) =>
        _providers.TryGetValue(provider, out var reading) ? reading : DrawerActivity.Idle;

    /// What a character shows: its own agent's activity, and for Grok's the
    /// busier of Grok and Cursor, their running tasks added up.
    public DrawerActivity ForCharacter(DisplayProvider provider)
    {
        var reading = ForProvider(provider);
        if (provider != DisplayProvider.Grok) return reading;
        var cursor = ForProvider(DisplayProvider.Cursor);
        var (phase, detail) = SharedSubscription.Pick((reading.Phase, reading.Detail), (cursor.Phase, cursor.Detail));
        return new DrawerActivity(phase, detail, reading.Running + cursor.Running);
    }
}
