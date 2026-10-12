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
        AgentPhase.Stalled => "blocked",
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
        AgentPhase.Stalled => "Stalled",
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
