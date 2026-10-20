using AgentIsland.Model;

namespace AgentIsland.WinUI.Drawer;

/// What the drawer needs from a character set: one bot per provider, each in
/// the look its agent wears (LookCatalog), added in drawer order so engine
/// indices match the drawer's, driven by state names and handing back one SVG
/// per bot per tick. The pixel and Classic engines both run in V8 behind it.
internal interface ICharacterEngine : IDisposable
{
    CharacterTheme Theme { get; }

    /// look is a LookCatalog id; none, or one the engine lacks, is the default.
    int AddBot(DisplayProvider provider, double tempo, int seed, string? look);

    /// Swaps the bot's look in place: a new bot with the same seed and tempo
    /// takes the old one's state. Gaze is cleared; the host sets it again.
    void SetLook(int bot, string? look);

    /// phase is where in its cycle the state starts (0-1); negative lets the
    /// engine pick, so bots entering a state together never move as one.
    void SetState(int bot, string state, double phase);

    /// The pointer at (x, y) and the bot's box, all in the drawer's DIPs.
    void SetGaze(int bot, double left, double top, double width, double height, double x, double y);

    void ClearGaze(int bot);

    /// A click: a nod or a hop.
    void Poke(int bot);

    /// A one-shot cue for a session's event while the bot's state stays the
    /// agent's: done, note, ask, alert or error. Classic plays its own; Pixel hops.
    void Cue(int bot, string kind);

    /// The little things the provider's bot does now and then while idle,
    /// in the given look.
    string[] Fidgets(DisplayProvider provider, string? look);

    /// Whether the set draws a state (its PHASES): one added later, like
    /// resting, falls back where a set doesn't have it yet.
    bool Draws(string state);

    /// Advances each bot whose dt is a number (NaN skips it) and returns its
    /// (engine state, SVG); skipped bots come back null.
    (string State, string Svg)?[] Tick(IReadOnlyList<double> dts, double size);
}
