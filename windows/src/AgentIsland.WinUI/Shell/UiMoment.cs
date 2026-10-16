using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Shell;

/// One popup moment on the UI thread (the hover card, a speech bubble): each
/// rendered frame, `apply` sets every moving property for the time since the
/// trigger, until the moment's length. Pixel moments round that time down to
/// whole ticks of their 30-tick clock inside `apply`, so a slow frame skips
/// ahead and never stretches a moment. Nothing runs between moments.
internal sealed class UiMoment
{
    private readonly Stopwatch _clock = new();
    private readonly double _durationMs;
    private readonly Action<double> _apply;
    private readonly Action? _done;
    private bool _running;
    private bool _landed;
    private bool _stopped;
    private double _startMs;

    // Moments waiting for the hand clock, in the order they began.
    private static readonly List<UiMoment> Pending = new();

    /// AGENTISLAND_DEBUG_POPUP_FILM: moments wait to be stepped by hand.
    internal static bool Manual { get; set; }

    /// The films' hand clock: a moment begun by hand starts at its reading.
    internal static double ManualMs { get; private set; }

    /// The moment that began last, for the film to step.
    internal static UiMoment? Latest { get; private set; }

    private UiMoment(double durationMs, Action<double> apply, Action? done)
    {
        _durationMs = durationMs;
        _apply = apply;
        _done = done;
    }

    public double DurationMs => _durationMs;

    /// Applies the first frame now and the rest as frames render; `done`
    /// runs after the last unless the moment is stopped first.
    public static UiMoment Play(double durationMs, Action<double> apply, Action? done = null)
    {
        var moment = new UiMoment(durationMs, apply, done);
        Latest = moment;
        apply(0);
        if (durationMs <= 0)
        {
            moment.Land();
            return moment;
        }
        if (Manual)
        {
            moment._startMs = ManualMs;
            Pending.Add(moment);
            return moment;
        }
        moment._running = true;
        moment._clock.Start();
        CompositionTarget.Rendering += moment.OnRendering;
        return moment;
    }

    /// Leaves every property where this frame put it, for a moment that
    /// takes over from the frame on screen.
    public void Stop()
    {
        _stopped = true;
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// The film's hand on the clock: sets the frame for ms, and lands the
    /// moment when ms reaches its end.
    internal void Step(double ms)
    {
        if (_landed || _stopped) return;
        _apply(Math.Min(ms, _durationMs));
        if (ms >= _durationMs) Land();
    }

    /// The hand clock moves to ms: every moment it holds takes its frame for
    /// that reading, and moments they begin start from it.
    internal static void Advance(double ms)
    {
        ManualMs = ms;
        foreach (var moment in Pending.ToList()) moment.Step(ms - moment._startMs);
        Pending.RemoveAll(moment => moment._landed || moment._stopped);
    }

    /// Whether any moment begun by hand has yet to land.
    internal static bool AnyPending => Pending.Exists(moment => !moment._landed && !moment._stopped);

    /// Lands every moment the hand clock holds, and those they begin.
    internal static void Finish()
    {
        for (var i = 0; i < 20 && AnyPending; i++) Advance(ManualMs + 5000);
    }

    internal static void ResetClock()
    {
        ManualMs = 0;
        Pending.Clear();
    }

    private void OnRendering(object? sender, object e)
    {
        var ms = _clock.Elapsed.TotalMilliseconds;
        if (ms < _durationMs)
        {
            _apply(ms);
            return;
        }
        Stop();
        _apply(_durationMs);
        Land();
    }

    /// `done` runs once, when the moment lands.
    private void Land()
    {
        if (_landed) return;
        _landed = true;
        _done?.Invoke();
    }
}
