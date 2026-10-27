using System.Diagnostics;
using System.Numerics;
using AgentIsland.Model;
using AgentIsland.WinUI.Platform;
using AgentIsland.WinUI.Shell;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace AgentIsland.WinUI.Drawer;

// The game loop: stepping the bots, the pointer gaze, the pacing and the drawing.
internal sealed partial class DrawerWindow
{
    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        var started = Stopwatch.GetTimestamp();
        _loopDevice = sender.Device;
        while (_commands.TryDequeue(out var command)) command();
        TakeShows();
        var scene = _scene;
        var reduced = _reducedMotion;
        // Each character steps on its own schedule, with a few ms of vsync
        // tolerance so callbacks landing early are not dropped.
        var now = _clock.Elapsed.TotalSeconds;
        var moment = _moment;
        var momentMs = moment is null ? 0 : (now - moment.Start) * 1000;
        moment?.RunDue(momentMs);
        _due.Clear();
        foreach (var (index, _, _) in scene.Shown)
        {
            if (now >= _slots[index].NextStep - 0.004) _due.Add(index);
        }
        var near = !reduced && _engine is not null && UpdateGaze(scene);
        if (_due.Count > 0) Step(now, near, reduced);
        Pace(scene, near, reduced, moment is not null && moment.Moving(momentMs));
        _stats?.Frame(_due.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// Busy characters step on every vsync (or at the user's ceiling); calm
    /// ones whose eyes follow a nearby pointer run at 60 fps and the rest
    /// breathe at 30. Reduced motion keeps 12 / 6. Pixel bots move in whole
    /// cells, at most 10-15 frames a second, so 30 is plenty for them.
    private void Step(double now, bool near, bool reduced)
    {
        var theme = _loopTheme;
        var pixel = theme == CharacterTheme.Pixel;
        var cap = _activeCap;
        var activeFps = reduced ? 12 : pixel ? (cap == 0 ? 30 : Math.Min(cap, 30)) : cap == 0 ? double.PositiveInfinity : cap;
        var gazeFps = reduced ? 12 : pixel ? 30 : 60;
        var calmFps = reduced ? 6 : 30;
        var dts = new double[_slots.Length];
        Array.Fill(dts, double.NaN);
        foreach (var index in _due)
        {
            var slot = _slots[index];
            var fps = !DrawerLooks.IsCalm(slot.EngineState, theme) ? activeFps : near ? gazeFps : calmFps;
            var interval = 1.0 / fps;
            // Under reduced motion a sleeping character holds still, once its
            // blend into the settled pose is done. A step covers the time since
            // the last one, so a bot stepped at 6 fps keeps its pace; only a
            // longer gap (the loop resting) is cut short.
            dts[index] = DrawerLooks.HoldsStill(slot.EngineState, theme, reduced, _bots[index].Look) && now - slot.Since > 0.5
                ? double.NaN
                : Math.Min(now - slot.LastStep, Math.Max(0.1, 1.5 * interval));
            slot.LastStep = now;
            slot.NextStep = now - slot.NextStep > interval ? now + interval : slot.NextStep + interval;
        }
        if (_engine is null) return;
        var frames = _engine.Tick(dts, BotSize);
        foreach (var index in _due)
        {
            if (frames[index] is not var (engineState, svg)) continue;
            var slot = _slots[index];
            // Self-completing entries (finished, spawning…) fall back to
            // idle; keep playing the agent's phase instead.
            if (engineState == "idle" && slot.Hold != "idle") _engine.SetState(index, slot.Hold, -1);
            slot.EngineState = engineState;
            slot.Svg = svg;
            Fidget(index, slot, now, reduced);
        }
    }

    /// Now and then an idle character does one of its own little things, on
    /// its own clock, and settles back.
    private void Fidget(int index, Slot slot, double now, bool reduced)
    {
        if (slot.Fidget is not null)
        {
            if (now < slot.FidgetUntil) return;
            slot.Fidget = null;
            slot.NextFidget = now + FidgetGap * (0.5 + Random.Shared.NextDouble());
            if (slot.Hold == "idle") _engine!.SetState(index, "idle", -1);
            return;
        }
        if (reduced || slot.Resting || slot.Fidgets.Length == 0 || slot.Hold != "idle" || slot.EngineState != "idle" || now < slot.NextFidget) return;
        slot.Fidget = slot.Fidgets[Random.Shared.Next(slot.Fidgets.Length)];
        slot.FidgetUntil = now + 2.5 + Random.Shared.NextDouble() * 2;
        // Pixel's fidgets are one-shot gestures (a hop in its first 1.4 s), so
        // they play from their start; Classic's loop on short cycles of their own.
        _engine!.SetState(index, slot.Fidget, _loopTheme == CharacterTheme.Pixel ? 0 : -1);
    }

    /// Feeds the pointer to the due characters; true when it is near any.
    private bool UpdateGaze(Scene scene)
    {
        if (!Win32.GetCursorPos(out var cursor)) return false;
        var x = (cursor.X - scene.Left) / scene.Scale;
        var y = (cursor.Y - scene.Top) / scene.Scale;
        var anyNear = false;
        foreach (var (index, left, top) in scene.Shown)
        {
            var dx = x - (left + BotSize / 2);
            var dy = y - (top + BotSize / 2);
            var near = Math.Sqrt(dx * dx + dy * dy) < GazeRange;
            anyNear |= near;
            if (!_due.Contains(index)) continue;
            var slot = _slots[index];
            if (near)
            {
                _engine!.SetGaze(index, left, top, BotSize, BotSize, x, y);
                slot.Gazing = true;
            }
            else if (slot.Gazing)
            {
                _engine!.ClearGaze(index);
                slot.Gazing = false;
            }
        }
        return anyNear;
    }

    /// The loop runs on vsync only while a character is busy or a drawer
    /// moment plays (Pixel's 30-tick clock for pixel moments); otherwise at
    /// the calm rate the characters need.
    private void Pace(Scene scene, bool near, bool reduced, bool moving)
    {
        var theme = _loopTheme;
        var busy = false;
        foreach (var (index, _, _) in scene.Shown)
        {
            busy |= !DrawerLooks.IsCalm(_slots[index].EngineState, theme);
        }
        var pixel = theme == CharacterTheme.Pixel;
        var active = pixel ? (_activeCap == 0 ? 30 : Math.Min(_activeCap, 30)) : _activeCap;
        var fps = moving ? active : busy ? (reduced ? 12 : active) : near ? (pixel ? 30 : 60) : (reduced ? 6 : 30);
        if (fps == _pace) return;
        _pace = fps;
        _queue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (fps == 0)
            {
                _canvas.IsFixedTimeStep = false;
                return;
            }
            _canvas.TargetElapsedTime = TimeSpan.FromSeconds(1.0 / fps);
            _canvas.IsFixedTimeStep = true;
        });
    }

    /// A frame's SVG is rasterised once into a small bitmap at the display's
    /// DPI; every vsync then only blits. Calm characters change a few times
    /// a second, so most frames re-tessellate nothing.
    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var started = Stopwatch.GetTimestamp();
        var ds = args.DrawingSession;
        var time = _clock.Elapsed.TotalSeconds;
        var theme = _loopTheme;
        // Pixel frames stay hard-edged: drawn without antialiasing and blitted
        // whole-pixel, unfiltered, even at scalings like 125 %.
        var pixel = theme == CharacterTheme.Pixel;
        var scale = sender.Dpi / 96f;
        // A drawer moment draws its own bots, each in its pose.
        var moment = _moment;
        var ms = moment is null ? 0 : (time - moment.Start) * 1000;
        var tug = moment is { Tug: true } ? _tug : 0;
        foreach (var (index, x, y) in moment?.Bots ?? _scene.Shown)
        {
            var slot = _slots[index];
            var pose = moment?.Pose(index, ms) ?? BotPose.Rest;
            if (tug != 0) pose = pose with { Y = pose.Y + tug };
            var edge = moment?.Edge ?? 1;
            if (slot.Svg is not { } svg)
            {
                ds.Transform = BotPainter.Transform(x, y, pose, pixel, edge, scale);
                DrawFallback(ds, slot, time, theme);
                ds.Transform = Matrix3x2.Identity;
                continue;
            }
            if (slot.Rendered is null || slot.Rendered.Device != ds.Device || slot.Rendered.Dpi != sender.Dpi)
            {
                slot.Rendered?.Dispose();
                slot.Rendered = new CanvasRenderTarget(ds, (float)BotSize, (float)BotSize, sender.Dpi);
                slot.RenderedSvg = null;
            }
            if (svg != slot.RenderedSvg)
            {
                using (var session = slot.Rendered.CreateDrawingSession())
                {
                    session.Clear(Microsoft.UI.Colors.Transparent);
                    if (pixel) session.Antialiasing = CanvasAntialiasing.Aliased;
                    _svg.Render(svg, session);
                }
                slot.RenderedSvg = svg;
            }
            BotPainter.Draw(ds, slot.Rendered, svg, _svg, x, y, pose, pixel, moment?.Dark ?? false, edge, scale, circle: moment is not null);
        }
        // The last frame is on screen: the UI thread can move on (hide the
        // window, cross the screen, give the circles back).
        if (moment is { Ended: false } && ms >= moment.DurationMs)
        {
            moment.Ended = true;
            _queue.TryEnqueue(() => EndMoment(moment));
        }
        _stats?.Drew(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// Without the engine: a disc in the character's colour with two eyes,
    /// breathing faster while its agent is busy.
    private static void DrawFallback(CanvasDrawingSession ds, Slot slot, double time, CharacterTheme theme)
    {
        var busy = !DrawerLooks.IsCalm(slot.EngineState, theme);
        var pulse = (float)Math.Sin(time * (busy ? 6 : 1.5)) * (busy ? 1.6f : 0.6f);
        var center = new Vector2((float)BotSize / 2, (float)BotSize / 2);
        var radius = (float)BotSize / 2 - 4 + pulse;
        ds.FillCircle(center, radius, Brand.Rgb(slot.Swatch));
        ds.DrawCircle(center, radius, Brand.Rgb(0xFFFFFF, 60), 1);
        var eye = Brand.Rgb(0xFFFFFF);
        ds.FillEllipse(center + new Vector2(-6, -2), 3.2f, 4.2f, eye);
        ds.FillEllipse(center + new Vector2(6, -2), 3.2f, 4.2f, eye);
    }
}
