using AgentIsland.Model;

namespace AgentIsland.WinUI.Drawer;

/// What the drawer needs from a character set: one bot per provider, added
/// in drawer order so engine indices match the drawer's, driven by state
/// names and handing back one SVG per bot per tick. The pixel and Classic
/// engines both run in V8 behind it.
internal interface ICharacterEngine : IDisposable
{
    CharacterTheme Theme { get; }

    int AddBot(DisplayProvider provider, string shape, string color, double tempo, int seed);

    /// Recolours or reshapes a bot; a no-op for sets with one look per bot.
    void SetLook(int bot, string shape, string color);

    /// phase is where in its cycle the state starts (0-1); negative lets the
    /// engine pick, so bots entering a state together never move as one.
    void SetState(int bot, string state, double phase);

    /// The pointer at (x, y) and the bot's box, all in the drawer's DIPs.
    void SetGaze(int bot, double left, double top, double width, double height, double x, double y);

    void ClearGaze(int bot);

    /// A click: a nod or a hop.
    void Poke(int bot);

    /// The little things the provider's bot does now and then while idle.
    string[] Fidgets(DisplayProvider provider);

    /// Whether the set draws a state (its PHASES): one added later, like
    /// resting, falls back where a set doesn't have it yet.
    bool Draws(string state);

    /// Advances each bot whose dt is a number (NaN skips it) and returns its
    /// (engine state, SVG); skipped bots come back null.
    (string State, string Svg)?[] Tick(IReadOnlyList<double> dts, double size);
}
