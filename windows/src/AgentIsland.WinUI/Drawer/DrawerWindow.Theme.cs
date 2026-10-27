using AgentIsland.Core;
using AgentIsland.Model;

namespace AgentIsland.WinUI.Drawer;

// The character set: building its engine and swapping it or the agents' looks live.
internal sealed partial class DrawerWindow
{
    /// The theme's engine, or null when V8 cannot run it; the drawer then
    /// draws its fallback discs.
    private ICharacterEngine? CreateEngine(CharacterTheme theme)
    {
        ICharacterEngine? engine = null;
        try
        {
            engine = V8CharacterEngine.Create(theme, V8CharacterEngine.Directory);
        }
        catch (Exception error)
        {
            CrashLog.Write(error, theme == CharacterTheme.Pixel ? "Pixel engine" : "Classic engine");
        }
        return engine;
    }

    private static void AddBot(ICharacterEngine? engine, Bot bot)
    {
        if (engine is null)
        {
            bot.Look = null;
            return;
        }
        bot.Look = LookCatalog.Of(engine.Theme, bot.Provider).Id;
        var index = engine.AddBot(bot.Provider, DrawerLooks.TempoOf(bot.Provider), Random.Shared.Next(1, 1_000_000), bot.Look);
        if (index != bot.Index) throw new InvalidOperationException($"engine index {index} for drawer bot {bot.Index}");
    }

    /// Swaps the character set live: a new engine gets every bot, resting in
    /// its agent's current phase, and the game loop takes it over.
    private void ApplyTheme()
    {
        var theme = DrawerStore.Shared.Theme;
        if (theme == _theme) return;
        _theme = theme;
        var engine = CreateEngine(theme);
        var rest = new List<(int Index, string Hold, bool Resting, uint Swatch, string[] Fidgets)>();
        foreach (var bot in _bots)
        {
            AddBot(engine, bot);
            var resting = bot.Rest is not null;
            var hold = resting && engine?.Draws(Resting) == true ? Resting
                : bot.Phase == AgentPhase.Idle ? "idle" : DrawerLooks.Hold(bot.Phase, theme);
            rest.Add((bot.Index, hold, resting, SwatchOf(bot), engine?.Fidgets(bot.Provider, bot.Look) ?? Array.Empty<string>()));
            // The new set starts from each bot's current state, so an older state still on its way is moot.
            Volatile.Write(ref _shows[bot.Index], null);
        }
        // A switch the loop has yet to take (it rests while the drawer is
        // hidden) is superseded: its engine goes now, not when the loop wakes.
        var handoff = new Handoff(engine);
        Interlocked.Exchange(ref _handoff, handoff)?.Engine?.Dispose();
        _commands.Enqueue(() =>
        {
            if (Interlocked.CompareExchange(ref _handoff, null, handoff) != handoff) return;
            var old = _engine;
            _engine = engine;
            _loopTheme = theme;
            foreach (var (index, hold, resting, swatch, fidgets) in rest)
            {
                var slot = _slots[index];
                slot.Hold = slot.EngineState = hold;
                slot.Resting = resting;
                slot.Since = _clock.Elapsed.TotalSeconds;
                slot.Swatch = swatch;
                slot.Fidgets = fidgets;
                slot.Fidget = null;
                slot.Gazing = false;
                slot.Svg = null;
                slot.NextStep = 0;
                engine?.SetState(index, hold, DrawerLooks.HoldsStill(hold, theme, _reducedMotion, _bots[index].Look) ? 0 : -1);
            }
            old?.Dispose();
        });
        Hush();
        RefreshCard();
    }

    /// Dresses each character in the look its agent now wears. The engine
    /// swaps the bot in place, so it keeps its seed, tempo and state. Pixel's
    /// bot then hops once (not under reduced motion), as for a poke; Classic's
    /// turns over like a coin (M20).
    private void ApplyLooks()
    {
        if (_engine is null) return;
        var theme = _theme;
        foreach (var bot in _bots)
        {
            var look = LookCatalog.Of(theme, bot.Provider).Id;
            if (look == bot.Look) continue;
            bot.Look = look;
            var index = bot.Index;
            var provider = bot.Provider;
            var swatch = SwatchOf(bot);
            void Swap()
            {
                if (_loopTheme != theme || _engine is not { } engine) return;
                var slot = _slots[index];
                engine.SetLook(index, look);
                slot.Swatch = swatch;
                slot.Fidgets = engine.Fidgets(provider, look);
                slot.Fidget = null;
                slot.Gazing = false;
                slot.Svg = null;
                slot.NextStep = 0;
                // A bot held still steps into its new look's settled pose first.
                slot.Since = _clock.Elapsed.TotalSeconds;
                var holdsStill = DrawerLooks.HoldsStill(slot.Hold, theme, _reducedMotion, look);
                engine.SetState(index, slot.Hold, holdsStill ? 0 : -1);
                if (theme == CharacterTheme.Pixel && !holdsStill && !_reducedMotion) engine.Poke(index);
            }
            // M20: Classic's bot turns like a coin, or fades under reduced motion, and its
            // new look swaps in at the middle. Only a visible bot, and one moment at a time.
            if (theme == CharacterTheme.Classic && _shown && bot.Visible && _moment is null)
                Play(DrawerMoves.Flip(NewMoment(_scene.Shown, DrawerStore.Shared.Side), index, Swap, _reducedMotion));
            else _commands.Enqueue(Swap);
        }
        RefreshCard();
    }
}
