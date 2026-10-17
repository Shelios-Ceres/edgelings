using AgentIsland.Core;
using AgentIsland.Localization;
namespace AgentIsland.Model;

/// How a provider's character looks and which state each phase plays. The
/// drawer's two character sets, the pixel bots and the Classic shapes, are
/// bundled engines that share one set of state names. Classic's default
/// looks follow each brand (Kimi Code's round blue mascot, DeepSeek's blue
/// whale, Cursor's black); brands that share a colour share it here too, by
/// the user's choice (2026-10-11).
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

    // MARK: - Legacy WPF drawer
    // The WPF app's drawer still runs the local Grok Bot study copy, whose
    // state names these are. The WinUI drawer uses the engines' map below.

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

    // MARK: - Engines
    // Mirrors the bundled engines (WinUI Drawer/Characters/characters.mjs and
    // classic.mjs); PixelThemeTests and ClassicThemeTests check every name and
    // colour against those files.

    /// Each provider's bot, under the same kind name in both engines.
    public static string Kind(DisplayProvider provider) => provider switch
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

    /// The state a phase enters, one per phase. Both themes' engines draw
    /// these names; a theme with names of its own would map them here.
    public static string Entry(AgentPhase phase, CharacterTheme theme) => phase switch
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

    /// Where a phase rests; the engines settle finished into listening and
    /// failed into broken by themselves.
    public static string Hold(AgentPhase phase, CharacterTheme theme) => phase switch
    {
        AgentPhase.Finished => "listening",
        AgentPhase.Failed => "broken",
        _ => Entry(phase, theme),
    };

    /// The engines' CALM list: states fine at the calm frame rate, with
    /// resting, which an idle character takes while its agent's app is out
    /// of sight, wherever a set draws it.
    public static readonly HashSet<string> EngineCalmStates = new(StringComparer.Ordinal)
    {
        "idle", "listening", "broken", "offline", "sleeping", "locked", "resting",
    };

    public static bool IsCalm(string state, CharacterTheme theme) => EngineCalmStates.Contains(state);

    /// Entries that play once from their start; every other state loops and
    /// may begin anywhere in its cycle.
    public static bool IsOneShot(string state, CharacterTheme theme) => state is "finished" or "failed";

    /// A character's own pace, so the drawer never moves in lockstep. Its
    /// idle habits (fidgets) come from its engine.
    public static double TempoOf(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => 0.93,
        DisplayProvider.Codex => 1.07,
        DisplayProvider.Grok => 1.12,
        DisplayProvider.Kimi => 0.97,
        DisplayProvider.DeepSeek => 0.9,
        DisplayProvider.Cursor => 1.03,
        DisplayProvider.Antigravity => 1.0,
        _ => 1.0,
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

    /// The Classic engine's palette (its colorOf, 0xRRGGBB), for card dots,
    /// quota bars and the fallback characters drawn when the engine cannot run.
    public static uint Swatch(string color) => color switch
    {
        "black" => 0x24252C,
        "brown" => 0x9C6B45,
        "red" => 0xF04A55,
        "orange" => 0xFF7A2E,
        "yellow" => 0xFFB51F,
        "green" => 0x22BF7A,
        "cyan" => 0x17B5C7,
        "violet" => 0x8E63F5,
        "magenta" => 0xE9529E,
        "gray" => 0x858A96,
        _ => 0x2D8CFF,
    };
}
