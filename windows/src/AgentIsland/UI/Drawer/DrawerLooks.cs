using System.Windows.Media;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.UI.Drawer;

/// How a provider's character looks and which Grok Bot state each phase
/// plays. Morph animations keep only the colour of a character, so the
/// default colours are five clearly different hues.
public static class DrawerLooks
{
    public static (string Shape, string Color) Default(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => ("sparkle", "orange"),
        DisplayProvider.Codex => ("cloud", "blue"),
        DisplayProvider.Antigravity => ("arch", "violet"),
        DisplayProvider.Grok => ("wedge", "gray"),
        DisplayProvider.Cursor => ("hex", "green"),
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
        AgentPhase.Asking => "alerting",
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

    /// Approximate engine palette, for the fallback characters drawn when the
    /// engine is unavailable.
    public static Color Swatch(string color) => color switch
    {
        "black" => Color.FromRgb(0x1A, 0x1A, 0x1A),
        "brown" => Color.FromRgb(0x8D, 0x5A, 0x3B),
        "red" => Color.FromRgb(0xF0, 0x44, 0x38),
        "orange" => Color.FromRgb(0xFF, 0x6A, 0x00),
        "yellow" => Color.FromRgb(0xFF, 0xC3, 0x00),
        "green" => Color.FromRgb(0x22, 0xC5, 0x5E),
        "cyan" => Color.FromRgb(0x06, 0xB6, 0xD4),
        "violet" => Color.FromRgb(0x91, 0x56, 0xF5),
        "magenta" => Color.FromRgb(0xE0, 0x40, 0xFB),
        "gray" => Color.FromRgb(0x80, 0x80, 0x80),
        _ => Color.FromRgb(0x10, 0x84, 0xFE),
    };
}
