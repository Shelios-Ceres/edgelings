namespace AgentIsland.WinUI.Shell;

/// The motion system both styles share: the same moments (INTERACTIONS.md,
/// M1-M19), each style with its own numbers. Classic eases on cubic Béziers
/// over Fluent's duration ladder; Pixel steps whole 2-DIP cells on a 30-tick
/// clock. A track gives one value at a time since its moment started.
internal static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();

    /// Windows' animation effects are off, or AGENTISLAND_DEBUG_REDUCED_MOTION=1
    /// says to act as if they were.
    public static bool ReducedMotion =>
        !Settings.AnimationsEnabled || Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_REDUCED_MOTION") == "1";

    /// Pixel's clock: 30 ticks a second, the pixel drawer's own rate.
    public const double TickMs = 1000.0 / 30;

    /// Frame k of a pixel phase shows k ticks after it starts.
    public static int Tick(double ms) => (int)Math.Floor(ms / TickMs + 1e-6);

    /// Whole 2-DIP cells: 3 px at 150 %. Halves round up, as in the design's
    /// prototypes (JavaScript's Math.round), so frames match them exactly.
    public static double Cell(double dip) => Math.Floor(dip / 2 + 0.5) * 2;

    /// Classic's curves, as named in the Classic spec.
    public static Func<double, double> Curve(string name) => name switch
    {
        "out" => Out,
        "in" => In,
        "inOut" => InOut,
        "pop" => Pop,
        "softPop" => SoftPop,
        _ => Linear,
    };

    public static readonly Func<double, double> Out = Bezier(0.22, 1, 0.36, 1);
    public static readonly Func<double, double> In = Bezier(0.4, 0, 1, 1);
    public static readonly Func<double, double> InOut = Bezier(0.65, 0, 0.35, 1);
    public static readonly Func<double, double> Pop = Bezier(0.34, 1.56, 0.64, 1);
    public static readonly Func<double, double> SoftPop = Bezier(0.3, 1.3, 0.6, 1);
    public static readonly Func<double, double> Linear = x => Math.Clamp(x, 0, 1);

    /// A cubic Bézier easing solved the way CSS solves it: Newton's method on
    /// x, then y. Control points' y may leave 0-1 for an overshoot.
    public static Func<double, double> Bezier(double x1, double y1, double x2, double y2)
    {
        double cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx;
        double cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by;
        double X(double t) => ((ax * t + bx) * t + cx) * t;
        double Y(double t) => ((ay * t + by) * t + cy) * t;
        double Slope(double t) => (3 * ax * t + 2 * bx) * t + cx;
        return x =>
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            var t = x;
            for (var i = 0; i < 8; i++)
            {
                var error = X(t) - x;
                var slope = Slope(t);
                if (Math.Abs(error) < 1e-5 || Math.Abs(slope) < 1e-6) break;
                t -= error / slope;
            }
            return Y(Math.Clamp(t, 0, 1));
        };
    }

    /// One value over time: where it is ms after its moment started.
    public interface ITrack
    {
        double Value(double ms);

        /// When it stops changing.
        double EndMs { get; }
    }

    /// Classic: from, eased to to over a duration, after a delay.
    public sealed class Tween(double from, double to, double delayMs, double durationMs, Func<double, double> curve) : ITrack
    {
        public double EndMs => delayMs + durationMs;

        public double Value(double ms)
        {
            if (ms <= delayMs) return from;
            var share = durationMs <= 0 ? 1 : (ms - delayMs) / durationMs;
            return share >= 1 ? to : from + (to - from) * curve(share);
        }
    }

    /// Classic: several tweens on one value, one after another (a squash
    /// that peaks partway, say).
    public sealed class Keys(params Tween[] legs) : ITrack
    {
        public double EndMs => legs[^1].EndMs;

        public double Value(double ms)
        {
            foreach (var leg in legs)
            {
                if (ms < leg.EndMs) return leg.Value(ms);
            }
            return legs[^1].Value(ms);
        }
    }

    /// Pixel: frame values held on whole ticks. Before the phase starts it
    /// shows `before`; frame k (from 1) lands on tick startTick + k.
    public sealed class Steps(double before, int startTick, params double[] frames) : ITrack
    {
        public double EndMs => (startTick + frames.Length) * TickMs;

        public double Value(double ms)
        {
            var k = Tick(ms) - startTick;
            if (k <= 0 || frames.Length == 0) return before;
            return frames[Math.Min(k, frames.Length) - 1];
        }
    }

    /// Pixel's remaining-distance curves: the share of the way still to go
    /// after each frame (in2, in3), the last always exactly there.
    public static double[] Remaining(double from, double to, params double[] left)
    {
        var frames = new double[left.Length];
        for (var i = 0; i < left.Length; i++)
        {
            frames[i] = i == left.Length - 1 ? to : to - Cell((to - from) * left[i]);
        }
        return frames;
    }

    public static readonly double[] In2 = { 0.35, 0 };
    public static readonly double[] In3 = { 0.5, 0.15, 0 };
}
