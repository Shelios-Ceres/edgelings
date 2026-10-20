using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Dispatching;

namespace AgentIsland.WinUI.Drawer;

// What the characters say: bubbles for a phase or a session, and the cues and hops that go with them.
internal sealed partial class DrawerWindow
{
    /// A line in a bubble when the agent needs you; a line that no longer
    /// holds (the agent went back to work) is taken back.
    private void Speak(Bot bot, AgentPhase phase, double now)
    {
        if (!BotLines.Speaks(phase))
        {
            // A session's bubble stays through a change of the overall phase;
            // only a whole-agent line is taken back.
            if (bot.PhaseLine) bot.Bubble?.Hide(animated: true);
            return;
        }
        if (!DrawerStore.Shared.SpeechBubbles || !_shown || !bot.Visible) return;
        if (now < SpeechQuietAtStartSeconds || now - bot.LastSpoke < SpeechGapSeconds) return;
        if (_card.IsOpen && (_cardBot is null || _cardBot == bot)) return;
        var pixel = _theme == CharacterTheme.Pixel;
        string? line;
        using (pixel ? L10n.English() : null) line = BotLines.For(phase, _random);
        if (line is null) return;
        bot.LastSpoke = now;
        bot.PhaseLine = true;
        Say(bot, line, SpeechBubble.KindOf(phase));
        if (!pixel && phase == AgentPhase.NetworkTrouble) PlayCue(bot, "error");
    }

    private void Say(Bot bot, string line, LineKind kind, TimeSpan? linger = null)
    {
        if (bot.Bubble is null)
        {
            var bubble = bot.Bubble = new SpeechBubble(_root);
            bubble.Closed += () => bot.Speech.Dismissed(_clock.Elapsed.TotalSeconds);
        }
        var swatch = Brand.Readable(SwatchOf(bot), Brand.IsDark());
        bot.Bubble.Say(line, swatch, _theme == CharacterTheme.Pixel, DrawerStore.Shared.Side == DrawerSide.Right,
            bot.Bounds, WorkAreaDip(), _reducedMotion, kind, linger);
    }

    /// Whether a character may speak now: lines on, its bubble room free and
    /// its card shut, past the first seconds, when what already waits is not news.
    private bool MaySpeak(Bot bot, double now) =>
        DrawerStore.Shared.SpeechBubbles && _shown && bot.Visible && now >= SpeechQuietAtStartSeconds
        && !(_card.IsOpen && (_cardBot is null || _cardBot == bot));

    /// A scan found something new in a session: its agent's character says so,
    /// even while the agent's overall phase stays Working.
    private void OnSessionEvents(IReadOnlyList<SessionEvent> events)
    {
        if (_closed) return;
        var now = _clock.Elapsed.TotalSeconds;
        foreach (var item in events)
        {
            var character = item.Tool.ToDisplayProvider().Character();
            DebugTrace.Write($"session event {item.Kind} {item.Key} \"{item.Title}\"");
            if (_bots.FirstOrDefault(b => b.Provider == character) is not { } bot || !MaySpeak(bot, now)) continue;
            bot.Speech.Rules = _theme == CharacterTheme.Pixel ? SpeechRules.Pixel : SpeechRules.Classic;
            bot.Speech.Add(item);
        }
        PumpSpeech(now);
    }

    /// Takes back what the sessions have moved on from and pops the next
    /// bubble once a bot is free.
    private void PumpSpeech(double now)
    {
        var waiting = ActivityMonitor.Shared.Waiting.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var bot in _bots)
        {
            bot.Speech.Rules = _theme == CharacterTheme.Pixel ? SpeechRules.Pixel : SpeechRules.Classic;
            if (!MaySpeak(bot, now))
            {
                bot.Speech.Clear();
                continue;
            }
            if (bot.Speech.Reconcile(waiting) && bot.Bubble is { IsOpen: true } open && !bot.PhaseLine) open.Hide(animated: true);
            if (bot.Speech.Next(now) is { } speech) SaySpeech(bot, speech, now);
        }
    }

    private void SaySpeech(Bot bot, Speech speech, double now)
    {
        var pixel = _theme == CharacterTheme.Pixel;
        string line;
        LineKind kind;
        string cue;
        // A bubble says only a stock line (never a session's name, never the
        // agent's own words); several waiting at once say how many.
        using (pixel ? L10n.English() : null)
        {
            if (speech.IsSummary)
            {
                line = BotLines.SummaryTitle(speech.Events.Count);
                kind = speech.NeedsAnswer ? LineKind.Waiting : PopOf(speech.Events[0].Kind);
                cue = CueOf(speech.Events[0].Kind);
            }
            else
            {
                var item = speech.First;
                line = BotLines.For(item, _random) ?? "";
                kind = PopOf(item.Kind);
                cue = CueOf(item.Kind);
            }
        }
        bot.PhaseLine = false;
        // Pixel: 5.5 s (4.5 s and a second for the heading), a summary 7 s.
        // Classic: by length, and a request at least eight seconds.
        var weight = Weight(line);
        var linger = pixel
            ? TimeSpan.FromSeconds(speech.IsSummary ? 7 : 5.5)
            : TimeSpan.FromSeconds(speech.NeedsAnswer ? Math.Max(8, Math.Clamp(2 + 0.05 * weight, 4.5, 9)) : Math.Clamp(2 + 0.05 * weight, 4.5, 9));
        Say(bot, line, kind, linger);
        if (pixel)
        {
            if (kind != LineKind.Error && now - bot.PhaseSince >= 1.5) HopCharacter(bot);
        }
        else
        {
            PlayCue(bot, cue);
        }
    }

    /// The pop each kind of event gets.
    private static LineKind PopOf(SessionEventKind kind) => kind switch
    {
        SessionEventKind.Done => LineKind.Done,
        SessionEventKind.Error or SessionEventKind.TaskSkipped => LineKind.Error,
        _ => LineKind.Waiting,
    };

    /// Classic's one-shot cue for an event (its engine's `cue`).
    private static string CueOf(SessionEventKind kind) => kind switch
    {
        SessionEventKind.Done => "done",
        SessionEventKind.Question => "ask",
        SessionEventKind.Approval => "alert",
        _ => "error",
    };

    /// How long a text is for the reader: a character counts one, a CJK one two.
    private static double Weight(string text) =>
        text.Sum(c => c is >= '\u2E80' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uFF00' and <= '\uFFEF' ? 2 : 1);

    /// Plays Classic's cue as a bubble pops, unless the bot's state already
    /// shows that (a lone session's change of state carries the motion).
    /// Reduced motion keeps the two emotes, which are information.
    private void PlayCue(Bot bot, string cue)
    {
        if (_reducedMotion && cue is not ("ask" or "alert")) return;
        var index = bot.Index;
        _commands.Enqueue(() =>
        {
            var shows = (cue, _slots[index].EngineState) is ("done", "finished") or ("ask", "asking") or ("alert", "approving") or ("error", "failed");
            if (shows) return;
            _engine?.Cue(index, cue);
            _slots[index].NextStep = 0;
        });
    }

    /// Pixel: the character hops once, as a click would make it, as the
    /// bubble lands; its state stays the agent's overall one. Not when the
    /// phase just changed (the entry is the gesture), nor under reduced motion.
    private void HopCharacter(Bot bot)
    {
        if (_reducedMotion) return;
        bot.Hop ??= CreateHop(bot);
        bot.Hop.Stop();
        bot.Hop.Start();
    }

    private DispatcherQueueTimer CreateHop(Bot bot)
    {
        var timer = _queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(2 * Motion.TickMs);
        timer.Tick += (_, _) =>
        {
            var index = bot.Index;
            _commands.Enqueue(() =>
            {
                if (DrawerLooks.HoldsStill(_slots[index].EngineState, _theme, _reducedMotion, _bots[index].Look)) return;
                _engine?.Poke(index);
                _slots[index].NextStep = 0;
            });
        };
        return timer;
    }

    /// A locked drawer turns a drag down out loud, with the way out; it is an
    /// answer to the drag, so it speaks even with character lines off.
    private void SayLocked(Bot bot)
    {
        _card.Hide();
        _cardBot = null;
        string line;
        using (_theme == CharacterTheme.Pixel ? L10n.English() : null) line = L10n.Tr("Locked in place.\nRight-click to unlock.");
        Say(bot, line, LineKind.Plain);
    }

    private void Hush()
    {
        foreach (var bot in _bots) bot.Bubble?.Hide(animated: false);
    }
}
