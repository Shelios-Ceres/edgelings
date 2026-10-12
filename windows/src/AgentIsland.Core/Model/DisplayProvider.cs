using AgentIsland.Core;

namespace AgentIsland.Model;

/// Display-layer provider identity for the island's two silhouette slots.
/// Strictly a presentation concept: full monitoring (usage windows, turn
/// alarms, attention states) stays with Claude and Codex — the guest slots
/// are usage badges, logo + quota pill and nothing deeper.
public enum DisplayProvider
{
    Claude,
    Codex,
    Antigravity,
    Grok,
    Cursor,
    Kimi,
    DeepSeek,
}

public static class DisplayProviders
{
    /// Declaration order is canonical slot order: Claude before Codex before
    /// the guests, matching the pre-slot fixed layout so migrated users see
    /// zero movement.
    public static readonly DisplayProvider[] All =
    {
        DisplayProvider.Claude,
        DisplayProvider.Codex,
        DisplayProvider.Antigravity,
        DisplayProvider.Grok,
        DisplayProvider.Cursor,
        DisplayProvider.Kimi,
        DisplayProvider.DeepSeek,
    };

    /// The two providers that carry the full monitoring stack.
    public static readonly DisplayProvider[] FullyMonitored =
    {
        DisplayProvider.Claude,
        DisplayProvider.Codex,
    };

    /// Zero-intrusion providers, re-probed on the refresh cadence.
    public static readonly DisplayProvider[] Guests =
    {
        DisplayProvider.Antigravity,
        DisplayProvider.Grok,
        DisplayProvider.Cursor,
        DisplayProvider.Kimi,
        DisplayProvider.DeepSeek,
    };

    public static DisplayProvider? Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "claude" => DisplayProvider.Claude,
        "codex" => DisplayProvider.Codex,
        "antigravity" => DisplayProvider.Antigravity,
        // Legacy spelling from the Gemini era — persisted slot picks keep
        // resolving to the same seat.
        "gemini" => DisplayProvider.Antigravity,
        "grok" => DisplayProvider.Grok,
        "cursor" => DisplayProvider.Cursor,
        "kimi" => DisplayProvider.Kimi,
        "deepseek" => DisplayProvider.DeepSeek,
        _ => null,
    };
}

public static class DisplayProviderExtensions
{
    /// Persisted spelling. Stays lowercase and stable — it is the on-disk
    /// form of the slot selection.
    public static string RawValue(this DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => "claude",
        DisplayProvider.Codex => "codex",
        DisplayProvider.Antigravity => "antigravity",
        DisplayProvider.Grok => "grok",
        DisplayProvider.Cursor => "cursor",
        DisplayProvider.Kimi => "kimi",
        DisplayProvider.DeepSeek => "deepseek",
        _ => provider.ToString().ToLowerInvariant(),
    };

    public static string DisplayName(this DisplayProvider provider) => ProviderIdentity.DisplayName(provider);

    /// Full monitoring = official usage windows + transcript activity +
    /// approaching-limit alerts. Badge-only providers report quota and stop.
    public static bool HasFullMonitoring(this DisplayProvider provider) =>
        provider is DisplayProvider.Claude or DisplayProvider.Codex;

    /// Position in canonical slot order; the enum's declaration order is it.
    public static int SlotOrder(this DisplayProvider provider) => (int)provider;

    public static TriggerTool ToTriggerTool(this DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => TriggerTool.Claude,
        DisplayProvider.Codex => TriggerTool.Codex,
        DisplayProvider.Antigravity => TriggerTool.Antigravity,
        DisplayProvider.Grok => TriggerTool.Grok,
        DisplayProvider.Cursor => TriggerTool.Cursor,
        DisplayProvider.Kimi => TriggerTool.Kimi,
        DisplayProvider.DeepSeek => TriggerTool.DeepSeek,
        _ => TriggerTool.Claude,
    };

    public static DisplayProvider ToDisplayProvider(this TriggerTool tool) => tool switch
    {
        TriggerTool.Claude => DisplayProvider.Claude,
        TriggerTool.Codex => DisplayProvider.Codex,
        TriggerTool.Antigravity => DisplayProvider.Antigravity,
        TriggerTool.Grok => DisplayProvider.Grok,
        TriggerTool.Cursor => DisplayProvider.Cursor,
        TriggerTool.Kimi => DisplayProvider.Kimi,
        TriggerTool.DeepSeek => DisplayProvider.DeepSeek,
        _ => DisplayProvider.Claude,
    };
}
