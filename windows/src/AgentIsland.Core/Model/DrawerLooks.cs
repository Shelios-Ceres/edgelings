using AgentIsland.Core;
using AgentIsland.Localization;
namespace AgentIsland.Model;

/// How a provider's character looks and which Grok Bot state each phase
/// plays. Default looks follow each brand (Kimi Code's round blue mascot,
/// DeepSeek's blue whale, Cursor's black); brands that share a colour share
/// it here too, by the user's choice (2026-10-11).
public static class DrawerLooks
{
    public static (string Shape, string Color) Default(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => ("sparkle", "orange"),
        DisplayProvider.Codex => ("cloud", "blue"),
        DisplayProvider.Antigravity => ("arch", "violet"),
        DisplayProvider.Grok => ("wedge", "gray"),
        DisplayProvider.Cursor => ("hex", "black"),
        DisplayProvider.Kimi => ("blob", "blue"),
        DisplayProvider.DeepSeek => ("teardrop", "blue"),
        _ => ("blob", "blue"),
    };

    public static (string Shape, string Color) For(DisplayProvider provider) =>
        DrawerStore.Shared.Look(provider) ?? Default(provider);

    /// The phase-to-animation mapping the user approved (2026-10-11).
    public static string Entry(AgentPhase phase) => phase switch
    {
        AgentPhase.Thinking => "thinking",
        AgentPhase.Streaming => "streaming",
        AgentPhase.Reading => "reading",
        AgentPhase.Searching => "searching",
        AgentPhase.WebSearching => "radar",
        AgentPhase.Writing => "writing",
        AgentPhase.Running => "running",
        AgentPhase.Controlling => "controlling",
        AgentPhase.Spawning => "spawning",
        AgentPhase.WaitingOnAgents => "orbit",
        AgentPhase.Sending => "sending",
        AgentPhase.Asking or AgentPhase.Approving => "alerting",
        AgentPhase.Compacting => "loading",
        AgentPhase.Working => "working",
        AgentPhase.Finished => "finished",
        AgentPhase.Failed => "failed",
        AgentPhase.NetworkTrouble => "blocked",
        AgentPhase.RateLimited => "sleeping",
        AgentPhase.AuthRequired => "confused",
        _ => "idle",
    };

    /// State re-entered when a self-completing entry falls back to idle.
    /// After "finished" the character listens for your reply: the "waiting"
    /// morph's dots read as thinking at drawer size and drop the shape that
    /// identifies the provider.
    public static string Hold(AgentPhase phase) => phase == AgentPhase.Finished ? "listening" : Entry(phase);

    public static readonly HashSet<string> CalmStates = new() { "idle", "listening", "sleeping", "blocked", "drowsy", "bored" };

    // MARK: - Pixel theme
    // Mirrors the bundled pixel engine (WinUI Drawer/Characters/characters.mjs);
    // PixelThemeTests checks every name and colour against that file.

    /// The pixel engine's bot for each provider.
    public static string PixelKind(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Codex => "codex",
        DisplayProvider.Antigravity => "antigravity",
        DisplayProvider.Grok => "grok",
        DisplayProvider.Cursor => "cursor",
        DisplayProvider.Kimi => "kimi",
        DisplayProvider.DeepSeek => "deepseek",
        _ => "claude",
    };

    /// Each pixel bot's own colour (the engine's swatchOf).
    public static uint PixelSwatch(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Codex => 0x9EA1AB,
        DisplayProvider.Antigravity => 0x3D7BF5,
        DisplayProvider.Grok => 0x0B0B0C,
        DisplayProvider.Cursor => 0x45454F,
        DisplayProvider.Kimi => 0x2B8BFF,
        DisplayProvider.DeepSeek => 0x4D6BFE,
        _ => 0xD77757,
    };

    /// A provider's colour in a theme: the pixel bot's own, or the classic
    /// colour the user picked.
    public static uint Swatch(DisplayProvider provider, string color, CharacterTheme theme) =>
        theme == CharacterTheme.Pixel ? PixelSwatch(provider) : Swatch(color);

    /// The state a phase enters in a theme; the pixel bots have one per phase.
    public static string Entry(AgentPhase phase, CharacterTheme theme) => theme == CharacterTheme.Classic ? Entry(phase) : phase switch
    {
        AgentPhase.Thinking => "thinking",
        AgentPhase.Streaming => "streaming",
        AgentPhase.Reading => "reading",
        AgentPhase.Searching => "searching",
        AgentPhase.WebSearching => "web",
        AgentPhase.Writing => "writing",
        AgentPhase.Running => "running",
        AgentPhase.Controlling => "controlling",
        AgentPhase.Spawning => "spawning",
        AgentPhase.WaitingOnAgents => "orbit",
        AgentPhase.Sending => "sending",
        AgentPhase.Asking => "asking",
        AgentPhase.Approving => "approving",
        AgentPhase.Compacting => "compacting",
        AgentPhase.Working => "working",
        AgentPhase.Finished => "finished",
        AgentPhase.Failed => "failed",
        AgentPhase.NetworkTrouble => "offline",
        AgentPhase.RateLimited => "sleeping",
        AgentPhase.AuthRequired => "locked",
        _ => "idle",
    };

    /// Where a phase rests; the pixel engine settles finished into listening
    /// and failed into broken by itself.
    public static string Hold(AgentPhase phase, CharacterTheme theme) => theme == CharacterTheme.Classic ? Hold(phase) : phase switch
    {
        AgentPhase.Finished => "listening",
        AgentPhase.Failed => "broken",
        _ => Entry(phase, theme),
    };

    /// The pixel engine's CALM list: states fine at the calm frame rate.
    public static readonly HashSet<string> PixelCalmStates = new(StringComparer.Ordinal)
    {
        "idle", "listening", "broken", "offline", "sleeping", "locked",
    };

    public static bool IsCalm(string state, CharacterTheme theme) =>
        (theme == CharacterTheme.Pixel ? PixelCalmStates : CalmStates).Contains(state);

    /// Entries that play once from their start.
    public static bool IsOneShot(string state, CharacterTheme theme) =>
        theme == CharacterTheme.Pixel ? state is "finished" or "failed" : OneShotStates.Contains(state);

    /// Entries that play once from their start (measured in the engine);
    /// every other state loops and may begin anywhere in its cycle.
    public static readonly HashSet<string> OneShotStates = new(StringComparer.Ordinal) { "finished", "failed", "approving", "waking" };

    /// A character's own pace and idle habits, so the drawer never moves in
    /// lockstep: the speed its engine runs at, and the little things it does
    /// now and then while its agent has nothing on.
    public sealed record Personality(double Tempo, string[] Fidgets);

    public static Personality PersonalityOf(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => new(0.93, new[] { "curious", "humming" }),
        DisplayProvider.Codex => new(1.07, new[] { "playful", "bouncing" }),
        DisplayProvider.Grok => new(1.12, new[] { "suspicious", "laughing" }),
        DisplayProvider.Kimi => new(0.97, new[] { "shy", "happy" }),
        DisplayProvider.DeepSeek => new(0.9, new[] { "drowsy", "curious" }),
        DisplayProvider.Cursor => new(1.03, new[] { "proud", "excited" }),
        DisplayProvider.Antigravity => new(1.0, new[] { "bored", "playful" }),
        _ => new(1.0, new[] { "curious" }),
    };

    public static string PhaseLabel(AgentPhase phase) => L10n.Tr(phase switch
    {
        AgentPhase.Thinking => "Thinking",
        AgentPhase.Streaming => "Replying",
        AgentPhase.Reading => "Reading files",
        AgentPhase.Searching => "Searching code",
        AgentPhase.WebSearching => "Searching the web",
        AgentPhase.Writing => "Editing code",
        AgentPhase.Running => "Running commands",
        AgentPhase.Controlling => "Operating apps",
        AgentPhase.Spawning => "Starting a subagent",
        AgentPhase.WaitingOnAgents => "Waiting on subagents",
        AgentPhase.Sending => "Sending a message",
        AgentPhase.Asking => "Asking you",
        AgentPhase.Approving => "Waiting for your approval",
        AgentPhase.Compacting => "Compacting context",
        AgentPhase.Working => "Working",
        AgentPhase.Finished => "Waiting for your reply",
        AgentPhase.Failed => "Something went wrong",
        AgentPhase.NetworkTrouble => "Network trouble",
        AgentPhase.RateLimited => "Out of quota",
        AgentPhase.AuthRequired => "Sign-in needed",
        _ => "Idle",
    });

    public static string ShapeLabel(string shape) =>
        L10n.Tr(shape.Length == 0 ? shape : char.ToUpperInvariant(shape[0]) + shape[1..]);

    public static string ColorLabel(string color) =>
        L10n.Tr(color.Length == 0 ? color : char.ToUpperInvariant(color[0]) + color[1..]);

    /// The engine's palette (0xRRGGBB), for card dots, quota bars and the
    /// fallback characters drawn when the engine is unavailable.
    public static uint Swatch(string color) => color switch
    {
        "black" => 0x000000,
        "brown" => 0x936439,
        "red" => 0xFF263C,
        "orange" => 0xFF6700,
        "yellow" => 0xFF9800,
        "green" => 0x00C972,
        "cyan" => 0x00BCA6,
        "violet" => 0x9159FE,
        "magenta" => 0xFF309B,
        "gray" => 0x777777,
        _ => 0x1084FE,
    };
}
