using AgentIsland.Core;
using AgentIsland.Localization;
namespace AgentIsland.Model;

/// How a provider's character looks and which state each phase plays. The
/// drawer's two character sets, the pixel bots and the Classic desk
/// companions, are bundled engines that share one set of state names; each
/// agent wears one look in each (LookCatalog), picked in Settings or from the
/// drawer's menu, and the look brings the agent's colour.
public static class DrawerLooks
{
    /// The WPF drawer's look picker (the WinUI drawer has none): each agent's
    /// default shape and colour, after its brand.
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
    // classic.mjs); PixelThemeTests and ClassicThemeTests check every state
    // name against those files, LookCatalogTests every look and colour.

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

    /// The colour of the look an agent wears now in a set (the engine's
    /// swatchOf for the picked look): the card, bubbles, charts and meters
    /// all follow it. Cursor wears Grok's pick; Pixel draws it in Grok's
    /// colours and Classic in its own.
    public static uint Swatch(DisplayProvider provider, CharacterTheme theme) =>
        LookCatalog.Of(theme, provider).Swatch;

    /// A provider's pixel bot's colour in its picked look.
    public static uint PixelSwatch(DisplayProvider provider) => Swatch(provider, CharacterTheme.Pixel);

    /// A provider's desk companion's colour in its picked look; Cursor's
    /// black is lifted on dark surfaces by Brand.Readable.
    public static uint ClassicSwatch(DisplayProvider provider) => Swatch(provider, CharacterTheme.Classic);

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

    /// States that hold still under reduced motion, in their settled pose:
    /// `resting`, Pixel's `sleeping` (the breathing doze), and `offline` in the
    /// three Pixel looks where it flickers (the puff cloud's thinning line, the
    /// terminal pal's and the astronaut's static). Classic's `sleeping` keeps animating.
    public static bool HoldsStill(string state, CharacterTheme theme, bool reduced, string? look) =>
        reduced && (state == "resting"
            || theme == CharacterTheme.Pixel && (state == "sleeping" || state == "offline" && look is "puff" or "terminal" or "astronaut"));

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

    /// The head word of a character's card. Pixel's words follow its looks:
    /// an agent whose app is away is "Sleeping", one out of quota "Resting".
    /// Classic keeps its own: "Resting" while away, "Out of quota".
    public static string CardPhase(AgentPhase phase, bool away, CharacterTheme theme)
    {
        if (theme != CharacterTheme.Pixel) return away ? L10n.Tr("Resting") : PhaseLabel(phase);
        if (away) return L10n.Tr("Sleeping");
        return phase == AgentPhase.RateLimited ? L10n.Tr("Resting") : PhaseLabel(phase);
    }

    /// The WPF drawer's look picker labels.
    public static string ShapeLabel(string shape) =>
        L10n.Tr(shape.Length == 0 ? shape : char.ToUpperInvariant(shape[0]) + shape[1..]);

    public static string ColorLabel(string color) =>
        L10n.Tr(color.Length == 0 ? color : char.ToUpperInvariant(color[0]) + color[1..]);

    /// The WPF drawer's palette (0xRRGGBB), for its look picker's colours.
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
