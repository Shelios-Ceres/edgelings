using AgentIsland.Core;

namespace AgentIsland.Model;

/// One authority for "what does this provider look like": display name,
/// brand colours, CLI executable. Colours are 0xRRGGBB so each UI framework
/// builds its own colour type from them.
public static class ProviderIdentity
{
    /// The short product name: drawer rows, Settings, chips.
    public static string DisplayName(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => "Claude",
        DisplayProvider.Codex => "Codex",
        DisplayProvider.Antigravity => "Antigravity",
        DisplayProvider.Grok => "Grok",
        DisplayProvider.Cursor => "Cursor",
        DisplayProvider.Kimi => "Kimi Code",
        DisplayProvider.DeepSeek => "DeepSeek",
        _ => "Claude",
    };

    public static string DisplayName(TriggerTool tool) => DisplayName(tool.ToDisplayProvider());

    /// Brand accent. Grok's steel and Cursor's bone are near-white: on light
    /// surfaces use the ink stand-ins the UI themes define.
    public static uint Accent(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => 0xCC785C,      // terracotta
        DisplayProvider.Codex => 0x5AA8F0,       // sky blue
        DisplayProvider.Antigravity => 0x4285F4, // Google blue
        DisplayProvider.Grok => 0xD8DEE4,        // cool steel
        DisplayProvider.Cursor => 0xF5F3EE,      // bone
        DisplayProvider.Kimi => 0x2E8FF7,        // mascot blue
        DisplayProvider.DeepSeek => 0x4D6BFE,    // whale indigo
        _ => 0xCC785C,
    };

    public static uint Accent(TriggerTool tool) => Accent(tool.ToDisplayProvider());

    /// The brand ramp: Antigravity carries Google's four hues, everyone else
    /// their accent twice, so every consumer can treat the brand as a gradient.
    public static IReadOnlyList<uint> BrandRamp(DisplayProvider provider) =>
        provider == DisplayProvider.Antigravity ? GoogleRamp : new[] { Accent(provider), Accent(provider) };

    private static readonly uint[] GoogleRamp = { 0x4285F4, 0x34A853, 0xFBBC05, 0xEA4335 };

    /// CLI executable name; null where the product is app-only.
    public static string? CliName(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => "claude",
        DisplayProvider.Codex => "codex",
        DisplayProvider.Antigravity => "agy",
        DisplayProvider.Grok => "grok",
        DisplayProvider.Kimi => "kimi",
        _ => null,
    };

    public static string? CliName(TriggerTool tool) => CliName(tool.ToDisplayProvider());
}
