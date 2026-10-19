using System.Numerics;
using AgentIsland.Core;
using AgentIsland.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using AgentIsland.WinUI.Shell;
using Windows.Foundation;
using Windows.UI;
using static AgentIsland.WinUI.Shell.Motion;

namespace AgentIsland.WinUI.Drawer;

/// AGENTISLAND_DEBUG_DRAWER_FILM=<dir>: the drawer's moments frame by frame,
/// and sheets of its bots in every state, rendered off-screen at 150 % on
/// plain light and dark ground for design QA. The moments step on their own
/// clock here, Pixel tick by tick and Classic every 33 ms, with the same
/// moves and painter as the live drawer, so each frame is exact; the bots
/// hold the frames they had when the film was asked for.
internal sealed partial class DrawerWindow
{
    private const float FilmDpi = 144;
    // A narrow screen, so M18 shows both edges.
    private const float StageWidth = 120;
    private const float StageHeight = 480;
    private const float WindowWidth = (float)(InnerInset + BotSize + EdgeInset);

    /// One frame of a film: the window in stage DIPs and its bots, posed.
    private sealed record Shot(Rect Window, float Edge, (int Index, float X, float Y, BotPose Pose)[] Bots);

    /// AGENTISLAND_DEBUG_DRAWER_MOMENTS=1: every drawer moment live, one
    /// after another, each traced (AGENTISLAND_DEBUG_TRACE) as it starts
    /// and ends. It changes the edge and the shown agents, and puts them
    /// back.
    public async void DebugMoments()
    {
        async Task Step(string name, Action action, int waitMs)
        {
            DebugTrace.Write($"drawer moments: {name}");
            action();
            await Task.Delay(waitMs);
        }
        var store = DrawerStore.Shared;
        var side = store.Side;
        await Step("hide", ToggleDeliberately, 800);
        await Step("show", ToggleDeliberately, 1200);
        await Step("edge", () => store.Side = side == DrawerSide.Right ? DrawerSide.Left : DrawerSide.Right, 1500);
        await Step("edge back", () => store.Side = side, 1500);
        if (_bots.Where(b => b.Visible).Skip(1).FirstOrDefault() is { } second)
        {
            await Step($"leave {second.Provider}", () => ProviderVisibilityStore.Shared.SetShown(second.Provider, false), 1200);
            await Step($"appear {second.Provider}", () => ProviderVisibilityStore.Shared.SetShown(second.Provider, true), 1200);
        }
        await Step("lift", () =>
        {
            _dragging = true;
            if (_reducedMotion) return;
            var shown = _scene.Shown;
            Play(DrawerMoves.Lift(NewMoment(shown, store.Side, holds: true), TopDown(shown), OnScreenNow()));
        }, 700);
        await Step("drop", EndPress, 900);
        _dragging = false;
        await Step("refuse", () =>
        {
            _refused = true;
            Refuse(30);
        }, 400);
        await Step("let go", EndPress, 900);
        _refused = false;
        await Step("hide for reveal", ToggleDeliberately, 800);
        await Step("reveal", Reveal, 2500);
        DebugTrace.Write("drawer moments: done");
    }

    /// AGENTISLAND_DEBUG_MENU_FILM=<dir>: the drawer menu's open, a submenu's
    /// and, in Classic, its close (M7), stepped by hand and printed popup by
    /// popup into a strip each.
    public async void DebugMenuFilm(string directory)
    {
        Directory.CreateDirectory(directory);
        var name = (_theme == CharacterTheme.Pixel ? "pixel" : "classic") + "-" + (Brand.IsDark() ? "dark" : "light");
        UiMoment.Manual = true;
        try
        {
            var pixel = _theme == CharacterTheme.Pixel;
            var Next = PopupPrint.NextMoment;
            var before = UiMoment.Latest;
            DebugMenu();
            if (await Next(before) is not { } open) return;
            await PopupPrint.FilmAsync(Path.Combine(directory, $"m7-open-{name}.png"), open, pixel);
            if (MenuMotion.OpenPanels().SelectMany(panel => panel.Items).OfType<MenuFlyoutSubItem>().FirstOrDefault() is { } sub)
            {
                before = UiMoment.Latest;
                (FrameworkElementAutomationPeer.CreatePeerForElement(sub).GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider)?.Expand();
                if (await Next(before) is { } submenu) await PopupPrint.FilmAsync(Path.Combine(directory, $"m7-submenu-{name}.png"), submenu, pixel);
            }
            if (_theme != CharacterTheme.Pixel && MenuMotion.Menu is { } menu)
            {
                before = UiMoment.Latest;
                menu.Hide();
                if (await Next(before) is { } close) await PopupPrint.FilmAsync(Path.Combine(directory, $"m7-close-{name}.png"), close, pixel);
            }
        }
        catch (Exception error)
        {
            DebugTrace.Write("menu film failed: " + error);
        }
        finally
        {
            UiMoment.Manual = false;
        }
        DebugTrace.Write("menu film saved in " + directory);
    }

    public void DebugFilm(string directory) => _commands.Enqueue(() =>
    {
        try
        {
            Film(directory);
        }
        catch (Exception error)
        {
            CrashLog.Write(error, "Drawer film");
        }
    });

    /// On the game loop's thread, which owns the engine and the slots.
    private void Film(string directory)
    {
        if (_loopDevice is not { } device || _engine is not { } engine) return;
        Directory.CreateDirectory(directory);
        var pixel = _loopTheme == CharacterTheme.Pixel;
        var style = pixel ? "pixel" : "classic";
        var order = DrawerStore.Shared.Order.Select(p => _bots.First(b => b.Provider == p).Index).ToList();
        var svgs = Frames(engine);
        var films = new List<(string Name, double Ms, Func<double, Shot> Shot)>();

        var column = Column(order, StageWidth - WindowWidth);
        var all = column.Bots;
        Shot Still(Rect window, float edge, (int Index, float X, float Y)[] bots, DrawerMoment moment, double ms) =>
            new(window, edge, bots.Select(b => (b.Index, b.X, b.Y, moment.Pose(b.Index, ms))).ToArray());
        DrawerMoment New((int, float, float)[] bots, float edge, bool dark) =>
            new() { Bots = bots, Start = 0, Pixel = pixel, Dark = dark, Edge = edge };

        foreach (var dark in new[] { false, true })
        {
            films.Clear();
            var reveal = DrawerMoves.Reveal(New(all, 1, dark), order);
            films.Add(("m1-reveal", reveal.DurationMs, ms => Still(column.Window, 1, all, reveal, ms)));
            var hide = DrawerMoves.Hide(New(all, 1, dark), order);
            films.Add(("m1-hide", hide.DurationMs, ms => Still(column.Window, 1, all, hide, ms)));

            // M2: the fourth bot leaves a column of seven, then comes back.
            var gone = order[3];
            var rest = order.Where(i => i != gone).ToList();
            var small = Column(rest, StageWidth - WindowWidth);
            films.Add(Roster("m2-leave", column, small, New, dark));
            films.Add(Roster("m2-appear", small, column, New, dark));

            var lift = DrawerMoves.Lift(New(all, 1, dark), order);
            films.Add(("m6-lift", lift.DurationMs, ms => Still(column.Window, 1, all, lift, ms)));
            var lifted = lift.OnScreenAt(lift.DurationMs);
            var drop = DrawerMoves.Drop(New(all, 1, dark), order, lifted);
            films.Add(("m6-drop", drop.DurationMs, ms => Still(column.Window, 1, all, drop, ms)));
            if (pixel)
            {
                var shake = DrawerMoves.Shake(New(all, 1, dark), order);
                films.Add(("m6-refuse", shake.DurationMs, ms => Still(column.Window, 1, all, shake, ms)));
            }
            else
            {
                // The pointer pulls 60 DIP down over 200 ms, then lets go.
                const double pullMs = 200;
                var spring = DrawerMoves.Spring(New(all, 1, dark), order, DrawerMoves.Rubber(60, DragThreshold));
                films.Add(("m6-refuse", pullMs + spring.DurationMs, ms =>
                {
                    if (ms >= pullMs) return Still(column.Window, 1, all, spring, ms - pullMs);
                    var tug = DrawerMoves.Rubber(60 * ms / pullMs, DragThreshold);
                    return new Shot(column.Window, 1, all.Select(b => (b.Index, b.X, b.Y, BotPose.Rest with { Y = tug })).ToArray());
                }));
            }

            // M18: out by the right edge, in from the left.
            var away = DrawerMoves.Hide(New(all, 1, dark), order, staggered: false);
            var left = Column(order, 0);
            var back = DrawerMoves.Reveal(New(left.Bots, -1, dark), order, staggered: !pixel);
            var turn = DrawerMoves.EdgeHideMs(pixel);
            films.Add(("m18-edge", turn + back.DurationMs, ms => ms < turn
                ? Still(column.Window, 1, all, away, ms)
                : Still(left.Window, -1, left.Bots, back, ms - turn)));

            foreach (var (name, ms, shot) in films)
            {
                Strip(device, Path.Combine(directory, $"{name}-{style}-{(dark ? "dark" : "light")}.png"),
                    $"{name} · {style} · {(dark ? "dark" : "light")} · 150 %", pixel, dark, ms, shot, svgs);
            }
        }
        Sheet(device, engine, directory, style, pixel, order);
        DebugTrace.Write($"drawer film saved in {directory}");
    }

    /// Every bot's current frame, hidden ones too, without moving time on.
    private Dictionary<int, string> Frames(ICharacterEngine engine)
    {
        var frames = engine.Tick(Enumerable.Repeat(0.0, _slots.Length).ToArray(), BotSize);
        var svgs = new Dictionary<int, string>();
        for (var i = 0; i < frames.Length; i++)
        {
            if (frames[i] is var (_, svg)) svgs[i] = svg;
        }
        return svgs;
    }

    /// A drawer of these bots, docked at x, centred on the stage as an
    /// unlocked drawer is.
    private static (Rect Window, (int Index, float X, float Y)[] Bots) Column(IReadOnlyList<int> bots, float x)
    {
        var height = (float)(VerticalPadding * 2 + bots.Count * BotSize + Math.Max(0, bots.Count - 1) * Gap);
        var top = MathF.Round((StageHeight - height) / 2);
        var placed = bots.Select((index, i) => (index, (float)EdgeInset, (float)(VerticalPadding + i * (BotSize + Gap)))).ToArray();
        return (new Rect(x, top, WindowWidth, height), placed);
    }

    /// M2 from one column to the other, as ChangeRoster plays it: the window
    /// covers both while the bots move, then settles on the new one.
    private (string Name, double Ms, Func<double, Shot> Shot) Roster(string name,
        (Rect Window, (int Index, float X, float Y)[] Bots) from, (Rect Window, (int Index, float X, float Y)[] Bots) to,
        Func<(int, float, float)[], float, bool, DrawerMoment> create, bool dark)
    {
        var union = new Rect(from.Window.X, Math.Min(from.Window.Y, to.Window.Y), WindowWidth,
            Math.Max(from.Window.Bottom, to.Window.Bottom) - Math.Min(from.Window.Y, to.Window.Y));
        var shift = (float)(to.Window.Y - union.Y);
        var oldTop = from.Bots.ToDictionary(b => b.Index, b => (float)(from.Window.Y + b.Y));
        var staying = to.Bots.Select(b => (b.Index, b.X, b.Y + shift)).ToArray();
        var leaving = from.Bots.Where(b => to.Bots.All(t => t.Index != b.Index)).Select(b => (b.Index, b.X, (float)(oldTop[b.Index] - union.Y))).ToArray();
        var entering = to.Bots.Where(b => !oldTop.ContainsKey(b.Index)).Select(b => b.Index).ToList();
        var dy = staying.Where(b => oldTop.ContainsKey(b.Index))
            .ToDictionary(b => b.Index, b => oldTop[b.Index] - (float)(union.Y + b.Item3));
        var bots = staying.Concat(leaving).ToArray();
        var moment = DrawerMoves.Roster(create(bots, 1, dark), leaving.Select(b => b.Index).ToList(), dy, entering);
        // One more frame after the moment: the window settles on the new column.
        var settle = moment.DurationMs + (moment.Pixel ? TickMs : 1000.0 / 30);
        return (name, settle, ms => ms < settle
            ? new Shot(union, 1, bots.Select(b => (b.Index, b.Item2, b.Item3, moment.Pose(b.Index, ms))).ToArray())
            : new Shot(to.Window, 1, to.Bots.Select(b => (b.Index, b.X, b.Y, BotPose.Rest)).ToArray()));
    }

    /// A film's frames side by side, ten to a row: Pixel every tick, Classic
    /// every 33 ms, from the trigger to one frame past the end.
    private void Strip(CanvasDevice device, string path, string title, bool pixel, bool dark, double durationMs,
        Func<double, Shot> shot, Dictionary<int, string> svgs)
    {
        var step = pixel ? TickMs : 1000.0 / 30;
        var count = (int)Math.Ceiling(durationMs / step - 1e-6) + 2;
        const int perRow = 10;
        const float gap = 6, label = 18, header = 26;
        var rows = (count + perRow - 1) / perRow;
        var width = Math.Min(count, perRow) * (StageWidth + gap) + gap;
        var height = header + rows * (StageHeight + label + gap) + gap;
        using var target = new CanvasRenderTarget(device, width, height, FilmDpi);
        var rasters = new Dictionary<string, CanvasRenderTarget>();
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 0x80, 0x80, 0x80));
            var text = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 12 };
            ds.DrawText(title, gap, 5, Color.FromArgb(255, 255, 255, 255), text);
            for (var k = 0; k < count; k++)
            {
                // Pixel frames sit mid-tick, clear of the tick edges.
                var ms = pixel ? k * step + step / 2 : k * step;
                var x = gap + k % perRow * (StageWidth + gap);
                var y = header + k / perRow * (StageHeight + label + gap);
                DrawShot(ds, new Vector2(x, y), shot(ms), pixel, dark, svgs, rasters, device);
                ds.DrawText(pixel ? $"t={k}" : $"{ms:F0} ms", x + 2, y + StageHeight + 1, Color.FromArgb(255, 255, 255, 255), text);
            }
        }
        target.SaveAsync(path).AsTask().GetAwaiter().GetResult();
        foreach (var raster in rasters.Values) raster.Dispose();
    }

    private static (Color Ground, Color Ink) Ground(bool dark) => dark
        ? (Color.FromArgb(255, 0x1E, 0x1F, 0x24), Color.FromArgb(255, 0xF3, 0xF4, 0xF6))
        : (Color.FromArgb(255, 0xF2, 0xF2, 0xF0), Color.FromArgb(255, 0x17, 0x19, 0x1F));

    /// The stage: plain ground, the window's outline, and its bots clipped to it.
    private void DrawShot(CanvasDrawingSession ds, Vector2 at, Shot shot, bool pixel, bool dark, Dictionary<int, string> svgs,
        Dictionary<string, CanvasRenderTarget> rasters, CanvasDevice device)
    {
        var (ground, ink) = Ground(dark);
        ds.FillRectangle(at.X, at.Y, StageWidth, StageHeight, ground);
        var window = new Rect(at.X + shot.Window.X, at.Y + shot.Window.Y, shot.Window.Width, shot.Window.Height);
        using (var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash })
        {
            ds.DrawRectangle(window, Color.FromArgb(70, ink.R, ink.G, ink.B), 1 / 1.5f, dash);
        }
        using (ds.CreateLayer(1, window))
        {
            foreach (var (index, x, y, pose) in shot.Bots)
            {
                var svg = svgs.GetValueOrDefault(index);
                if (svg is null) continue;
                if (!rasters.TryGetValue(svg, out var raster))
                {
                    raster = new CanvasRenderTarget(device, (float)BotSize, (float)BotSize, FilmDpi);
                    using (var session = raster.CreateDrawingSession())
                    {
                        session.Clear(Microsoft.UI.Colors.Transparent);
                        if (pixel) session.Antialiasing = CanvasAntialiasing.Aliased;
                        _svg.Render(svg, session);
                    }
                    rasters[svg] = raster;
                }
                BotPainter.Draw(ds, raster, svg, _svg, (float)window.X + x, (float)window.Y + y, pose, pixel, dark, shot.Edge, FilmDpi / 96, circle: true);
            }
        }
    }

    /// Every bot in every state a phase plays (resting too, where the set
    /// draws it), in each of its fidgets, and poked while it rests: each a
    /// drawer at three moments (0.5, 0.9 and 1.3 s in; resting's slow loop at
    /// its settled start, a breath in and its slow blink; the poke's wake and
    /// the eyes closing again), on light then dark ground.
    private void Sheet(CanvasDevice device, ICharacterEngine engine, string directory, string style, bool pixel, IReadOnlyList<int> order)
    {
        var column = Column(order, (StageWidth - WindowWidth) / 2);
        var takes = new List<(string Name, Func<int, string> State, double[] Times, double PokeAt)>();
        var states = Enum.GetValues<AgentPhase>()
            .SelectMany(phase => new[] { DrawerLooks.Entry(phase, _loopTheme), DrawerLooks.Hold(phase, _loopTheme) })
            .Append(Resting)
            .Distinct()
            .Where(engine.Draws);
        foreach (var state in states)
            takes.Add((state, _ => state, state == Resting ? new[] { 0.5, 2.5, 5.0 } : new[] { 0.5, 0.9, 1.3 }, double.NaN));
        for (var k = 0; k < 3; k++)
        {
            var nth = k;
            string? Fidget(int index) => engine.Fidgets(_bots.First(b => b.Index == index).Provider).ElementAtOrDefault(nth);
            if (order.Any(index => Fidget(index) is not null))
                takes.Add(($"fidget-{k + 1}", index => Fidget(index) ?? "idle", new[] { 0.5, 0.9, 1.3 }, double.NaN));
        }
        if (engine.Draws(Resting)) takes.Add(("resting-poke", _ => Resting, new[] { 0.8, 1.5, 3.5 }, 0.5));
        if (engine.Draws("sleeping")) takes.Add(("sleeping-poke", _ => "sleeping", new[] { 0.8, 1.5, 3.5 }, 0.5));
        // Reduced motion: the blend into the settled pose, then still, and a poke does nothing.
        // Classic's `sleeping` keeps moving under reduced motion, so only Pixel's gets a take.
        if (engine.Draws(Resting)) takes.Add(("resting-reduced", _ => Resting, new[] { 0.8, 1.5, 3.5 }, 0.5));
        if (pixel && engine.Draws("sleeping")) takes.Add(("sleeping-reduced", _ => "sleeping", new[] { 0.8, 1.5, 3.5 }, 0.5));
        foreach (var (name, stateOf, times, pokeAt) in takes)
        {
            foreach (var index in order) engine.SetState(index, stateOf(index), 0);
            var shots = new List<Dictionary<int, string>>();
            var clock = 0.0;
            var poked = double.IsNaN(pokeAt);
            var still = name.EndsWith("-reduced");
            var dts = Enumerable.Repeat(1 / 30.0, _slots.Length).ToArray();
            foreach (var time in times)
            {
                while (clock < time - 1e-6)
                {
                    if (!poked && clock >= pokeAt - 1e-6)
                    {
                        if (!still) foreach (var index in order) engine.Poke(index);
                        poked = true;
                    }
                    if (still && clock >= 0.5) Array.Fill(dts, double.NaN);
                    engine.Tick(dts, BotSize);
                    clock += 1 / 30.0;
                }
                shots.Add(Frames(engine));
            }
            const float gap = 6, header = 26;
            var width = gap + times.Length * 2 * (StageWidth + gap);
            using var target = new CanvasRenderTarget(device, width, header + StageHeight + gap, FilmDpi);
            var rasters = new Dictionary<string, CanvasRenderTarget>();
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(255, 0x80, 0x80, 0x80));
                var text = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 12 };
                var poke = double.IsNaN(pokeAt) ? "" : $", poked at {pokeAt:0.0} s";
                ds.DrawText($"{name} · {style} · at {string.Join(", ", times.Select(t => $"{t:0.0} s"))}{poke}, light then dark · 150 %", gap, 5,
                    Color.FromArgb(255, 255, 255, 255), text);
                var k = 0;
                foreach (var dark in new[] { false, true })
                {
                    foreach (var svgs in shots)
                    {
                        var shot = new Shot(column.Window, 1, column.Bots.Select(b => (b.Index, b.X, b.Y, BotPose.Rest)).ToArray());
                        DrawShot(ds, new Vector2(gap + k++ * (StageWidth + gap), header), shot, pixel, dark, svgs, rasters, device);
                    }
                }
            }
            target.SaveAsync(Path.Combine(directory, $"sheet-{name}-{style}.png")).AsTask().GetAwaiter().GetResult();
            foreach (var raster in rasters.Values) raster.Dispose();
        }
        // Back to what each agent is doing.
        foreach (var index in order)
        {
            var slot = _slots[index];
            engine.SetState(index, slot.Hold, -1);
            slot.NextStep = 0;
        }
    }
}
