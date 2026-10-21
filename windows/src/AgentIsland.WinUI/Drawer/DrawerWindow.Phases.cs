using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.WinUI.Drawer;

// What each character is doing: the poll, its phase, its state and why it rests.
internal sealed partial class DrawerWindow
{
    private async void Poll()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var monitor = ActivityMonitor.Shared;
            // Settings can show any provider, including one whose character is hidden.
            var jobs = _bots
                .Select(b =>
                {
                    var thread = monitor.ThreadFor(b.Tool);
                    return (Bot: b, Tool: b.Tool, State: monitor.StateFor(b.Tool), Path: thread?.TranscriptPath,
                        Running: monitor.RunningFor(b.Tool), Prompt: thread?.PendingPrompt);
                })
                .ToList();
            // Tail reads stay off the UI thread. Working sessions are read for
            // how they work, network trouble for a prompt that may be open, and
            // finished turns for whether they ended on an error. The pixel
            // panels read in English, details included.
            var english = _theme == CharacterTheme.Pixel;
            var lookAround = _clock.Elapsed.TotalSeconds - _presenceAt >= 1;
            var (readings, presence) = await Task.Run(() =>
            {
                using (english ? L10n.English() : null)
                {
                    return (jobs
                        .Select(job => job.Prompt ?? (job.State is ActivityState.Working or ActivityState.NetworkTrouble or ActivityState.NeedsYou
                            && job.Path is { } path
                            ? AgentPhaseClassifier.Classify(job.Tool, path, now)
                            : null))
                        .ToList(), lookAround ? AgentPresences.Now() : null);
                }
            });
            if (_closed) return;
            var seconds = _clock.Elapsed.TotalSeconds;
            if (presence is not null)
            {
                _presence = presence;
                _presenceAt = seconds;
            }
            var recounted = false;
            var results = new List<(Bot Bot, AgentPhase Phase, string? Detail, int Running)>();
            for (var i = 0; i < jobs.Count; i++)
            {
                var (bot, tool, state, _, running, _) = jobs[i];
                var reading = readings[i];
                var phase = AgentPhases.Combine(state, reading, QuotaExhausted(tool.ToDisplayProvider()));
                var detail = reading is { } r && r.Phase == phase ? r.Detail : null;
                if (phase == AgentPhase.NetworkTrouble && detail is null && monitor.IsOffline)
                {
                    using (english ? L10n.English() : null) detail = L10n.Tr("No internet connection");
                }
                results.Add((bot, phase, detail, running));
            }
            _activity = new DrawerActivityFrame(results.Select(r => (r.Bot.Provider, r.Phase, r.Detail, r.Running)));
            foreach (var bot in _bots)
            {
                var activity = _activity.ForCharacter(bot.Provider);
                recounted |= bot.Running != activity.Running && (_cardBot is null || _cardBot == bot);
                bot.Running = activity.Running;
                Offer(bot, activity.Phase, activity.Detail, seconds, activity.Phase == AgentPhase.Idle ? Away(bot) : null);
            }
            // A task can start or end without the shown phase changing.
            if (recounted) RefreshCard();
            PumpSpeech(seconds);
        }
        catch (Exception error)
        {
            CrashLog.Write(error, "Drawer poll");
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    /// The activity monitor only overlays usage for some providers, so the
    /// drawer checks every character's quota itself.
    private static bool QuotaExhausted(DisplayProvider provider) =>
        ProviderUsage.For(provider).IsExhausted(DateTimeOffset.Now);

    /// A shown phase stays at least MinDwellSeconds so quick tool bursts do
    /// not flicker, unless the new phase outranks it (asking, errors).
    private void Offer(Bot bot, AgentPhase phase, string? detail, double now, AgentPresence? rest)
    {
        if (phase == bot.Phase)
        {
            var changed = detail != bot.Detail;
            bot.Detail = detail;
            if (rest != bot.Rest)
            {
                // Its agent's app went out of sight or came back.
                bot.Rest = rest;
                DebugTrace.Write($"rest {bot.Provider} {rest?.ToString() ?? "over"}");
                Show(bot, "idle", "idle");
                changed = true;
            }
            if (changed) RefreshCardFor(bot.Provider);
            return;
        }
        var dwellOver = now - bot.PhaseSince >= MinDwellSeconds;
        var outranks = AgentPhases.Priority(phase) < AgentPhases.Priority(bot.Phase);
        if (!dwellOver && !outranks) return;
        bot.Phase = phase;
        bot.Detail = detail;
        if (rest != bot.Rest) DebugTrace.Write($"rest {bot.Provider} {rest?.ToString() ?? "over"}");
        bot.Rest = rest;
        bot.PhaseSince = now;
        Speak(bot, phase, now);
        var theme = _theme;
        Show(bot, DrawerLooks.Entry(phase, theme), phase == AgentPhase.Idle ? "idle" : DrawerLooks.Hold(phase, theme));
        RefreshCardFor(bot.Provider);
    }

    /// Puts a character into a state on the game loop. A resting one takes
    /// its set's resting state, or in a set without one idles, unfidgeting.
    private void Show(Bot bot, string entry, string hold)
    {
        var index = bot.Index;
        var theme = _theme;
        var resting = bot.Rest is not null;
        _commands.Enqueue(() =>
        {
            var slot = _slots[index];
            if (resting && _engine?.Draws(Resting) == true) entry = hold = Resting;
            slot.Hold = hold;
            slot.Resting = resting;
            slot.Since = _clock.Elapsed.TotalSeconds;
            slot.Fidget = null;
            slot.NextFidget = slot.Since + FidgetGap * (0.5 + Random.Shared.NextDouble());
            // Reduced motion rests in the settled pose (the loop's start), held still.
            if (_engine is not null) _engine.SetState(index, entry, DrawerLooks.IsOneShot(entry, theme) || DrawerLooks.HoldsStill(entry, theme, _reducedMotion, bot.Look) ? 0 : -1);
            else slot.EngineState = hold;
            slot.NextStep = 0;
        });
    }

    private const string Resting = "resting";

    private static readonly bool DebugRest = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_REST") == "1";

    /// Why an idle character rests: every app of its agent (Grok's with
    /// Cursor's) closed or out of sight; null while any is on screen.
    /// AGENTISLAND_DEBUG_REST=1 rests every idle character.
    private AgentPresence? Away(Bot bot)
    {
        if (DebugRest) return AgentPresence.Minimized;
        var tools = bot.Provider == DisplayProvider.Grok ? new[] { TriggerTool.Grok, TriggerTool.Cursor } : new[] { bot.Tool };
        var most = tools.Select(tool => _presence.TryGetValue(tool, out var presence) ? presence : AgentPresence.Open).Min();
        return most == AgentPresence.Open ? null : most;
    }

    /// Why a character rests, in words; Grok's shared character names its apps.
    public static string RestDetail(AgentPresence rest, DisplayProvider character) => L10n.Tr(rest switch
    {
        AgentPresence.Closed => AgentPresences.ClosedLine(character, GrokBot.Installed),
        AgentPresence.Minimized => "Minimized",
        _ => "In the tray",
    });
}
