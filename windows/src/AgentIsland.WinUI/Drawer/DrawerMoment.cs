using AgentIsland.WinUI.Shell;
using static AgentIsland.WinUI.Shell.Motion;

namespace AgentIsland.WinUI.Drawer;

/// What a drawer moment can move on a bot.
internal enum BotProp
{
    /// Along the edge's axis, positive toward the screen edge.
    X,
    Y,
    /// Degrees, about the bot's base, mirrored on the left edge.
    Lean,
    ScaleX,
    ScaleY,
    Opacity,
    /// 0-1: Pixel's hard shadow, Classic's floor shadow.
    Shadow,
    /// Classic's floor shadow drop, in DIP.
    ShadowY,
}

/// One bot as a drawer moment draws it.
internal readonly record struct BotPose(float X, float Y, float Lean, float ScaleX, float ScaleY, float Opacity, float Shadow, float ShadowY)
{
    public static readonly BotPose Rest = new(0, 0, 0, 1, 1, 1, 0, 0);
}

/// A drawer moment (M1, M2, M6, M18) as the game loop plays it: which bots
/// it draws and where they rest, in window DIPs, and the tracks that move
/// them from its start. The UI thread starts it and cleans up after; the
/// loop only reads it.
internal sealed class DrawerMoment
{
    private readonly Dictionary<(int Index, BotProp Prop), ITrack> _tracks = new();
    private readonly HashSet<int> _moved = new();

    public required (int Index, float X, float Y)[] Bots { get; init; }

    /// On the drawer's clock, in seconds.
    public required double Start { get; init; }

    public required bool Pixel { get; init; }

    public required bool Dark { get; init; }

    /// +1 when the drawer docks right, -1 left: X tracks point at the edge.
    public required float Edge { get; init; }

    /// A held moment keeps its last frame until another replaces it: bots
    /// lifted while the drawer is dragged, or gone while it is hidden.
    public bool Holds { get; init; }

    /// Classic's locked-drag tug: the live pull is added to every bot's y.
    public bool Tug { get; init; }

    public double DurationMs { get; private set; }

    /// Set by the loop once it has drawn the last frame.
    public bool Ended { get; set; }

    public DrawerMoment Set(int index, BotProp prop, ITrack track)
    {
        _tracks[(index, prop)] = track;
        _moved.Add(index);
        DurationMs = Math.Max(DurationMs, track.EndMs);
        return this;
    }

    public bool Moving(double ms) => ms < DurationMs;

    public BotPose Pose(int index, double ms)
    {
        float Get(BotProp prop, float rest) => _tracks.TryGetValue((index, prop), out var track) ? (float)track.Value(ms) : rest;
        return new BotPose(Get(BotProp.X, 0), Get(BotProp.Y, 0), Get(BotProp.Lean, 0), Get(BotProp.ScaleX, 1), Get(BotProp.ScaleY, 1),
            Get(BotProp.Opacity, 1), Get(BotProp.Shadow, 0), Get(BotProp.ShadowY, 0));
    }

    /// Where its bots are drawn ms in, for a moment that takes over: a new
    /// trigger starts from the frame on screen. Null for a bot it leaves at rest.
    public DrawerMoves.OnScreen OnScreenAt(double ms) => index => _moved.Contains(index) ? Pose(index, ms) : null;
}

/// The drawer moments' numbers per style: the Pixel and Classic specs
/// (windows/notes/pixel-motion-20261015/MOTION.md and
/// classic-grok-20261015/motion/MOTION.md). Each starts from the pose on
/// screen when a moment interrupts another.
internal static class DrawerMoves
{
    /// A bot's pose on screen as a moment starts, or null where nothing moves it.
    public delegate BotPose? OnScreen(int index);

    /// Far enough out that a bot is off the screen edge.
    private const float Off = 50;

    /// M1 reveal: each bot slides in from beyond the screen edge, Pixel in
    /// pairs from the top a tick apart, Classic 33 ms apart with a lean that
    /// rocks back. Unstaggered, it is M18's second half for Pixel.
    public static DrawerMoment Reveal(DrawerMoment moment, IReadOnlyList<int> topDown, bool staggered = true, double delayMs = 0, OnScreen? from = null)
    {
        for (var i = 0; i < topDown.Count; i++)
        {
            var index = topDown[i];
            var now = from?.Invoke(index);
            var x = now?.X ?? Off;
            if (moment.Pixel)
            {
                var start = Tick(delayMs) + (staggered ? i / 2 : 0);
                moment.Set(index, BotProp.X, new Steps(x, start, Remaining(x, 0, In3)));
            }
            else
            {
                var delay = delayMs + (staggered ? i * 33 : 0);
                moment.Set(index, BotProp.X, new Tween(x, 0, delay, 333, Pop));
                moment.Set(index, BotProp.Lean, new Tween(now?.Lean ?? -10, 0, delay, 333, Pop));
                moment.Set(index, BotProp.Opacity, new Tween(now?.Opacity ?? 0, 1, delay, 83, Linear));
            }
        }
        return moment;
    }

    /// When Reveal()'s all-agents card opens: Pixel on the tick the last bot
    /// lands, Classic at 250 ms.
    public static double CardAfterReveal(bool pixel, int bots) =>
        pixel ? (Math.Max(0, bots - 1) / 2 + 3) * TickMs : 250;

    /// M1 hide: every bot leaves toward the edge. Pixel all at once in two
    /// ticks (30 % of the way, then gone); Classic bottom to top, 17 ms
    /// apart, fading as it goes. Unstaggered, it is M18's first half.
    public static DrawerMoment Hide(DrawerMoment moment, IReadOnlyList<int> topDown, bool staggered = true, OnScreen? from = null)
    {
        for (var i = 0; i < topDown.Count; i++)
        {
            var index = topDown[i];
            var now = from?.Invoke(index) ?? BotPose.Rest;
            if (moment.Pixel)
            {
                moment.Set(index, BotProp.X, new Steps(now.X, 0, now.X + Cell((Off - now.X) * 0.3), Off));
            }
            else
            {
                var delay = staggered ? (topDown.Count - 1 - i) * 17 : 0;
                moment.Set(index, BotProp.X, new Tween(now.X, Off, delay, 167, In));
                moment.Set(index, BotProp.Opacity, new Tween(now.Opacity, 0, delay + 84, 83, Linear));
            }
        }
        return moment;
    }

    /// The length of M18's first half, after which the window moves.
    public static double EdgeHideMs(bool pixel) => pixel ? 2 * TickMs : 167;

    /// M2: bots leave, make room, close the gap and enter. dy is how far each
    /// staying bot is drawn from its new rest as the moment starts.
    public static DrawerMoment Roster(DrawerMoment moment, IReadOnlyList<int> leaving, IReadOnlyDictionary<int, float> dy,
        IReadOnlyList<int> entering, OnScreen? from = null)
    {
        foreach (var index in leaving)
        {
            var now = from?.Invoke(index) ?? BotPose.Rest;
            if (moment.Pixel)
            {
                moment.Set(index, BotProp.X, new Steps(now.X, 0, now.X + Cell((Off - now.X) * 0.3), Off));
            }
            else
            {
                moment.Set(index, BotProp.X, new Tween(now.X, Off, 0, 167, In));
                moment.Set(index, BotProp.ScaleX, new Tween(now.ScaleX, 0.8, 0, 167, In));
                moment.Set(index, BotProp.ScaleY, new Tween(now.ScaleY, 0.8, 0, 167, In));
            }
        }
        // Leaving first: the gap closes after the exit. Appearing: room is
        // made first and the newcomer enters as it settles.
        var appearOnly = entering.Count > 0 && leaving.Count == 0;
        var moveStart = leaving.Count > 0 ? (moment.Pixel ? 2 * TickMs : 100) : 0;
        var anyMove = false;
        foreach (var (index, distance) in dy)
        {
            if (Math.Abs(distance) < 0.01) continue;
            anyMove = true;
            if (moment.Pixel) moment.Set(index, BotProp.Y, new Steps(distance, Tick(moveStart), Remaining(distance, 0, In3)));
            else moment.Set(index, BotProp.Y, new Tween(distance, 0, moveStart, appearOnly ? 250 : 333, appearOnly ? Out : SoftPop));
        }
        if (entering.Count > 0)
        {
            var enterAt = moveStart + (anyMove ? (moment.Pixel ? 2 * TickMs : 167) : 0);
            Reveal(moment, entering, staggered: false, delayMs: enterAt);
        }
        return moment;
    }

    /// M6 lift: Pixel steps the bots 2, then 4 DIP off the edge with a hard
    /// shadow; Classic swells them on their base over a floor shadow.
    public static DrawerMoment Lift(DrawerMoment moment, IEnumerable<int> bots, OnScreen? from = null)
    {
        foreach (var index in bots)
        {
            var now = from?.Invoke(index) ?? BotPose.Rest;
            if (moment.Pixel)
            {
                moment.Set(index, BotProp.X, new Steps(now.X, 0, Remaining(now.X, -4, In2)));
                moment.Set(index, BotProp.Shadow, new Steps(now.Shadow, 0, 1, 1));
            }
            else
            {
                moment.Set(index, BotProp.ScaleX, new Tween(now.ScaleX, 1.07, 0, 167, Out));
                moment.Set(index, BotProp.ScaleY, new Tween(now.ScaleY, 1.07, 0, 167, Out));
                moment.Set(index, BotProp.Shadow, new Tween(now.Shadow, 1, 0, 167, Out));
                moment.Set(index, BotProp.ShadowY, new Tween(now.ShadowY, 3, 0, 167, Out));
            }
        }
        return moment;
    }

    /// M6 drop: Pixel steps back with a 2-DIP thud; Classic lands with a
    /// squash that peaks a quarter of the way while its shadow sinks away.
    public static DrawerMoment Drop(DrawerMoment moment, IEnumerable<int> bots, OnScreen? from = null)
    {
        foreach (var index in bots)
        {
            var now = from?.Invoke(index) ?? BotPose.Rest;
            if (moment.Pixel)
            {
                moment.Set(index, BotProp.X, new Steps(now.X, 0, [.. Remaining(now.X, 0, In2), 0]));
                moment.Set(index, BotProp.Y, new Steps(0, 0, 0, 2, 0));
                moment.Set(index, BotProp.Shadow, new Steps(now.Shadow, 0, now.Shadow, now.Shadow, 0));
            }
            else
            {
                moment.Set(index, BotProp.ScaleX, new Keys(new Tween(now.ScaleX, 1.05, 0, 83, Out), new Tween(1.05, 1, 83, 250, Pop)));
                moment.Set(index, BotProp.ScaleY, new Keys(new Tween(now.ScaleY, 0.93, 0, 83, Out), new Tween(0.93, 1, 83, 250, Pop)));
                moment.Set(index, BotProp.Shadow, new Tween(now.Shadow, 0, 0, 167, Linear));
                moment.Set(index, BotProp.ShadowY, new Tween(now.ShadowY, 0, 0, 167, Linear));
            }
        }
        return moment;
    }

    /// M6 refusal, Pixel: the column shakes across, +2, -2, +2 and home.
    public static DrawerMoment Shake(DrawerMoment moment, IEnumerable<int> bots)
    {
        foreach (var index in bots) moment.Set(index, BotProp.X, new Steps(0, 0, 2, -2, 2, 0));
        return moment;
    }

    /// M6 refusal, Classic: the rubber-banded column springs home.
    public static DrawerMoment Spring(DrawerMoment moment, IEnumerable<int> bots, float pulled)
    {
        foreach (var index in bots) moment.Set(index, BotProp.Y, new Tween(pulled, 0, 0, 333, Pop));
        return moment;
    }

    /// Classic's rubber band: a fifth of the pull past the threshold, at most 8 DIP.
    public static float Rubber(double pullDip, double thresholdDip) =>
        (float)(Math.Sign(pullDip) * Math.Min(8, Math.Max(0, Math.Abs(pullDip) - thresholdDip) / 5));
}
