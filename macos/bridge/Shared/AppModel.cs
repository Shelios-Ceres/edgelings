using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.Bridge;

/// What the Mac drawer needs from Core, in one place: the Windows drawer's
/// poll, dwell, rest and speech rules (DrawerWindow.Phases and .Speech), kept
/// here so Swift only draws. Swift calls `frame` about twice a second with the
/// facts only it knows (the clock, which bubbles are open, whether the hover
/// card is up) and gets back each character's state, any bubble it should
/// say, and the drawer's layout choices. Text is chosen here, in the language
/// of the character's set (the Pixel set reads English), and a bubble only
/// ever carries a stock line: never a session's name, never anything an agent
/// wrote.
public static class AppModel
{
    private const double MinDwellSeconds = 2;
    private const double SpeechGapSeconds = 20;
    private const double SpeechQuietAtStartSeconds = 8;

    private sealed class Bot
    {
        public required DisplayProvider Provider { get; init; }
        public AgentPhase Phase { get; set; }
        public string? Detail { get; set; }
        public int Running { get; set; }
        public double PhaseSince { get; set; } = double.NegativeInfinity;
        public AgentPresence? Rest { get; set; }
        public double LastSpoke { get; set; } = double.NegativeInfinity;
        public SpeechQueue Speech { get; } = new();
        public bool PhaseLine { get; set; }
        // Bumped whenever the state Swift shows changes, so it need not diff.
        public int Seq { get; set; }
        // The phase just changed to one that has a whole-agent line.
        public bool PendingPhaseLine { get; set; }
        // The phase changed to one that no longer holds the line on screen.
        public bool TakeBack { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly List<Bot> Bots = DisplayProviders.All.Select(p => new Bot { Provider = p }).ToList();
    private static readonly Random Dice = new();
    private static readonly List<SessionEvent> Fresh = new();
    private static bool _started;
    private static IReadOnlyDictionary<TriggerTool, AgentPresence> _presence = new Dictionary<TriggerTool, AgentPresence>();
    private static double _presenceAt = double.NegativeInfinity;
    private static double _clock;

    /// Starts Core's monitors, as the Windows app does at launch. Safe to call twice.
    public static string Start()
    {
        lock (Gate)
        {
            if (_started) return Ok();
            _started = true;
        }
        // The Mac app's hooks are `sh` (nothing is installed until the user flips a switch).
        ClaudeHooks.UseShell = true;
        GrokHooks.UseShell = true;
        CoreLog.Sink = (error, context) => AppLog.Write($"{context}: {error.GetType().Name}");
        Preferences.MigrateLegacyPrefix();
        AppLanguageStore.ApplyAtStartup();
        ActivityMonitor.Shared.SessionEventsRaised += events =>
        {
            lock (Gate) Fresh.AddRange(events);
        };
        ActivityMonitor.Shared.Start();
        UsageStore.Shared.StartAutoRefresh();
        CostStore.Shared.StartAutoRefresh();
        return Ok();
    }

    private static string Ok() => Json.Write(json => json.WriteBoolean("ok", true));

    /// Drawer choices from Swift: {"method":"setDrawer", "side":"left", "offset":0.4, "locked":true,
    /// "pinnedTop":120 | null, "theme":"classic", "speech":false, "fps":60, "saveBattery":true,
    /// "order":["claude",…], "shown":{"id":"grok","on":false}, "look":{"id":"claude","theme":"pixel","look":"clawd"}}.
    public static string SetDrawer(JsonElement request)
    {
        var store = DrawerStore.Shared;
        foreach (var property in request.EnumerateObject())
        {
            switch (property.Name)
            {
                case "side": store.Side = property.Value.GetString() == "left" ? DrawerSide.Left : DrawerSide.Right; break;
                case "offset": store.Offset = property.Value.GetDouble(); break;
                case "locked": store.Locked = property.Value.GetBoolean(); break;
                case "pinnedTop": store.PinnedTop = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetDouble(); break;
                case "theme": store.Theme = property.Value.GetString() == "classic" ? CharacterTheme.Classic : CharacterTheme.Pixel; break;
                case "display": IslandTargetDisplayStore.Shared.Choice = property.Value.GetString() ?? "auto"; break;
                case "speech": store.SpeechBubbles = property.Value.GetBoolean(); break;
                case "fps": store.FrameRate = property.Value.GetInt32(); break;
                case "saveBattery": store.SaveBattery = property.Value.GetBoolean(); break;
                case "order":
                    store.SetOrder(property.Value.EnumerateArray().Select(v => DisplayProviders.Parse(v.GetString())).OfType<DisplayProvider>().ToList());
                    break;
                case "shown":
                    if (DisplayProviders.Parse(property.Value.GetProperty("id").GetString()) is { } who)
                    {
                        ProviderVisibilityStore.Shared.SetShown(who, property.Value.GetProperty("on").GetBoolean());
                    }
                    break;
                case "look":
                    if (DisplayProviders.Parse(property.Value.GetProperty("id").GetString()) is { } owner)
                    {
                        var set = property.Value.GetProperty("theme").GetString() == "classic" ? CharacterTheme.Classic : CharacterTheme.Pixel;
                        store.SetLookId(set, owner, property.Value.GetProperty("look").GetString());
                    }
                    break;
            }
        }
        return Ok();
    }

    private static bool IsPixel => DrawerStore.Shared.Theme == CharacterTheme.Pixel;

    /// The poll plus the speech: one call per half second.
    /// Request: {"now":12.5,"shown":true,"cardOpen":false,"cardBot":"claude"|null,
    ///           "bubbles":["claude"],"dismissed":["claude"]}
    public static string Frame(JsonElement request)
    {
        lock (Gate)
        {
            var now = request.TryGetProperty("now", out var n) && n.TryGetDouble(out var seconds) ? seconds : _clock;
            _clock = now;
            var shown = !request.TryGetProperty("shown", out var s) || s.ValueKind != JsonValueKind.False;
            var cardOpen = request.TryGetProperty("cardOpen", out var c) && c.ValueKind == JsonValueKind.True;
            var cardBot = request.TryGetProperty("cardBot", out var cb) && cb.ValueKind == JsonValueKind.String ? DisplayProviders.Parse(cb.GetString()) : null;
            var open = Names(request, "bubbles");
            foreach (var name in Names(request, "dismissed"))
            {
                if (DisplayProviders.Parse(name) is { } who && Find(who) is { } gone) gone.Speech.Dismissed(now);
            }

            var pixel = IsPixel;
            var theme = DrawerStore.Shared.Theme;
            Poll(now, pixel);

            var visibility = ProviderVisibilityStore.Shared;
            var visible = DrawerVisibility.Characters(Bots.ToDictionary(b => b.Provider, b => visibility.IsShown(b.Provider)));
            var order = DrawerStore.Shared.Order.ToList();
            var waiting = ActivityMonitor.Shared.Waiting.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
            DispatchEvents(now, shown, cardOpen, cardBot, visible);
            var rules = pixel ? SpeechRules.Pixel : SpeechRules.Classic;

            return Json.Write(json =>
            {
                var store = DrawerStore.Shared;
                json.WriteString("theme", pixel ? "pixel" : "classic");
                json.WriteString("side", store.Side == DrawerSide.Right ? "right" : "left");
                json.WriteNumber("offset", store.Offset);
                json.WriteBoolean("locked", store.Locked);
                if (store.PinnedTop is { } pin) json.WriteNumber("pinnedTop", pin);
                json.WriteNumber("fps", store.FrameRate);
                json.WriteBoolean("saveBattery", store.SaveBattery);
                json.WriteBoolean("speechBubbles", store.SpeechBubbles);
                json.WriteBoolean("chinese", L10n.IsChinese);
                json.WriteString("display", IslandTargetDisplayStore.Shared.Choice);
                json.WriteStartArray("order");
                foreach (var provider in order) json.WriteStringValue(provider.RawValue());
                json.WriteEndArray();
                json.WriteStartArray("bots");
                foreach (var bot in Bots)
                {
                    var provider = bot.Provider;
                    var look = LookCatalog.Of(theme, provider);
                    json.WriteStartObject();
                    json.WriteString("id", provider.RawValue());
                    json.WriteString("name", ProviderIdentity.DisplayName(provider));
                    json.WriteString("kind", DrawerLooks.Kind(provider));
                    json.WriteBoolean("visible", visible[provider]);
                    json.WriteString("look", look.Id);
                    json.WriteNumber("swatch", look.Swatch);
                    json.WriteNumber("tempo", DrawerLooks.TempoOf(provider));
                    json.WriteString("phase", bot.Phase.ToString());
                    json.WriteString("entry", DrawerLooks.Entry(bot.Phase, theme));
                    json.WriteString("hold", bot.Phase == AgentPhase.Idle ? "idle" : DrawerLooks.Hold(bot.Phase, theme));
                    json.WriteBoolean("oneShot", DrawerLooks.IsOneShot(DrawerLooks.Entry(bot.Phase, theme), theme));
                    json.WriteBoolean("resting", bot.Rest is not null);
                    json.WriteNumber("running", bot.Running);
                    json.WriteNumber("seq", bot.Seq);
                    if (bot.Detail is { } detail) json.WriteString("detail", detail);
                    WriteSpeech(json, bot, now, shown, cardOpen, cardBot, open.Contains(provider.RawValue()), rules, waiting, visible[provider], pixel);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            });
        }
    }

    private static HashSet<string> Names(JsonElement request, string property)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (request.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.GetString() is { } value) set.Add(value);
            }
        }
        return set;
    }

    private static Bot? Find(DisplayProvider provider) => Bots.FirstOrDefault(b => b.Provider == provider);

    /// The activity of every agent, with a phase held for its dwell unless a
    /// more urgent one outranks it.
    private static void Poll(double now, bool pixel)
    {
        var monitor = ActivityMonitor.Shared;
        var at = DateTimeOffset.UtcNow;
        var jobs = Bots.Select(b =>
        {
            var tool = b.Provider.ToTriggerTool();
            var thread = monitor.ThreadFor(tool);
            return (Bot: b, Tool: tool, State: monitor.StateFor(tool), Path: thread?.TranscriptPath,
                Running: monitor.RunningFor(tool), Prompt: thread?.PendingPrompt);
        }).ToList();
        if (now - _presenceAt >= 1)
        {
            _presence = AgentPresences.Now();
            _presenceAt = now;
        }
        using var english = pixel ? L10n.English() : null;
        var results = new List<(Bot Bot, AgentPhase Phase, string? Detail, int Running)>();
        foreach (var job in jobs)
        {
            var reading = job.Prompt ?? (job.State is ActivityState.Working or ActivityState.NetworkTrouble or ActivityState.NeedsYou
                && job.Path is { } path
                ? SafeClassify(job.Tool, path, at)
                : null);
            var phase = AgentPhases.Combine(job.State, reading, ProviderUsage.For(job.Bot.Provider).IsExhausted(DateTimeOffset.Now));
            var detail = reading is { } r && r.Phase == phase ? r.Detail : null;
            if (phase == AgentPhase.NetworkTrouble && detail is null && monitor.IsOffline) detail = L10n.Tr("No internet connection");
            results.Add((job.Bot, phase, detail, job.Running));
        }
        var frame = new DrawerActivityFrame(results.Select(r => (r.Bot.Provider, r.Phase, r.Detail, r.Running)));
        foreach (var bot in Bots)
        {
            var activity = frame.ForCharacter(bot.Provider);
            bot.Running = activity.Running;
            Offer(bot, activity.Phase, activity.Detail, now, activity.Phase == AgentPhase.Idle ? Away(bot) : null);
        }
    }

    private static PhaseReading? SafeClassify(TriggerTool tool, string path, DateTimeOffset at)
    {
        try { return AgentPhaseClassifier.Classify(tool, path, at); }
        catch { return null; }
    }

    private static void Offer(Bot bot, AgentPhase phase, string? detail, double now, AgentPresence? rest)
    {
        if (phase == bot.Phase)
        {
            bot.Detail = detail;
            if (rest != bot.Rest)
            {
                bot.Rest = rest;
                bot.Seq++;
            }
            return;
        }
        var dwellOver = now - bot.PhaseSince >= MinDwellSeconds;
        var outranks = AgentPhases.Priority(phase) < AgentPhases.Priority(bot.Phase);
        if (!dwellOver && !outranks) return;
        bot.Phase = phase;
        bot.Detail = detail;
        bot.Rest = rest;
        bot.PhaseSince = now;
        bot.Seq++;
        // A session's bubble stays through a change of the overall phase;
        // only a whole-agent line is taken back.
        if (bot.PhaseLine && !BotLines.Speaks(phase)) bot.TakeBack = true;
        if (!BotLines.Speaks(phase)) bot.PhaseLine = false;
        bot.PendingPhaseLine = BotLines.Speaks(phase);
    }

    private static AgentPresence? Away(Bot bot)
    {
        var tools = bot.Provider == DisplayProvider.Grok ? new[] { TriggerTool.Grok, TriggerTool.Cursor } : new[] { bot.Provider.ToTriggerTool() };
        var most = tools.Select(tool => _presence.TryGetValue(tool, out var presence) ? presence : AgentPresence.Open).Min();
        return most == AgentPresence.Open ? null : most;
    }

    /// Fresh session events go to their character's queue unless it may not
    /// speak right now (lines off, drawer hidden, card open, still starting).
    private static void DispatchEvents(double now, bool shown, bool cardOpen, DisplayProvider? cardBot,
        IReadOnlyDictionary<DisplayProvider, bool> visible)
    {
        if (Fresh.Count == 0) return;
        var rules = IsPixel ? SpeechRules.Pixel : SpeechRules.Classic;
        foreach (var item in Fresh)
        {
            var character = item.Tool.ToDisplayProvider().Character();
            if (Find(character) is not { } bot || !MaySpeak(bot, now, shown, cardOpen, cardBot, visible[bot.Provider])) continue;
            bot.Speech.Rules = rules;
            bot.Speech.Add(item);
        }
        Fresh.Clear();
    }

    private static bool MaySpeak(Bot bot, double now, bool shown, bool cardOpen, DisplayProvider? cardBot, bool visible) =>
        DrawerStore.Shared.SpeechBubbles && shown && visible && now >= SpeechQuietAtStartSeconds
        && !(cardOpen && (cardBot is null || cardBot == bot.Provider));

    /// Writes "speech" when this bot should open a bubble now, and "takeBack"
    /// when the bubble it shows is no longer worth showing.
    private static void WriteSpeech(Utf8JsonWriter json, Bot bot, double now, bool shown, bool cardOpen, DisplayProvider? cardBot,
        bool bubbleOpen, SpeechRules rules, IReadOnlySet<string> waiting, bool visible, bool pixel)
    {
        bot.Speech.Rules = rules;
        var takeBack = bot.TakeBack;
        bot.TakeBack = false;
        if (!MaySpeak(bot, now, shown, cardOpen, cardBot, visible))
        {
            bot.Speech.Clear();
            bot.PendingPhaseLine = false;
            if (takeBack) json.WriteBoolean("takeBack", true);
            return;
        }
        if (bot.Speech.Reconcile(waiting) && bubbleOpen && !bot.PhaseLine) takeBack = true;
        if (takeBack) json.WriteBoolean("takeBack", true);

        using var english = pixel ? L10n.English() : null;
        // A whole-agent line (quota, sign-in, network) for a phase that needs you.
        if (bot.PendingPhaseLine)
        {
            bot.PendingPhaseLine = false;
            if (!bot.Speech.Holds(SessionEventKind.Error) && now - bot.LastSpoke >= SpeechGapSeconds
                && BotLines.For(bot.Phase, Dice) is { } line)
            {
                bot.LastSpoke = now;
                bot.PhaseLine = true;
                json.WriteStartObject("speech");
                json.WriteString("line", line);
                json.WriteString("kind", PhaseKind(bot.Phase));
                json.WriteBoolean("phaseLine", true);
                if (!pixel && bot.Phase == AgentPhase.NetworkTrouble) json.WriteString("cue", "error");
                json.WriteEndObject();
                return;
            }
        }
        if (bot.Speech.Next(now) is not { } speech) return;
        string text, kind, cue;
        if (speech.IsSummary)
        {
            text = BotLines.SummaryTitle(speech.Events.Count);
            kind = speech.NeedsAnswer ? "waiting" : PopOf(speech.Events[0].Kind);
            cue = CueOf(speech.Events[0].Kind);
        }
        else
        {
            text = BotLines.For(speech.First, Dice) ?? "";
            kind = PopOf(speech.First.Kind);
            cue = CueOf(speech.First.Kind);
        }
        bot.PhaseLine = false;
        var weight = text.Sum(ch => ch is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯' ? 2 : 1);
        var linger = pixel
            ? (speech.IsSummary ? 7 : 5.5)
            : speech.NeedsAnswer ? Math.Max(8, Math.Clamp(2 + 0.05 * weight, 4.5, 9)) : Math.Clamp(2 + 0.05 * weight, 4.5, 9);
        json.WriteStartObject("speech");
        json.WriteString("line", text);
        json.WriteString("kind", kind);
        json.WriteNumber("linger", linger);
        json.WriteBoolean("phaseLine", false);
        if (cue.Length > 0 && !pixel) json.WriteString("cue", cue);
        if (pixel && kind != "error") json.WriteBoolean("hop", now - bot.PhaseSince >= 1.5);
        json.WriteEndObject();
    }

    private static string PhaseKind(AgentPhase phase) => phase switch
    {
        AgentPhase.Finished => "done",
        AgentPhase.Failed or AgentPhase.NetworkTrouble => "error",
        AgentPhase.RateLimited => "plain",
        _ => "waiting",
    };

    private static string PopOf(SessionEventKind kind) => kind switch
    {
        SessionEventKind.Done => "done",
        SessionEventKind.Error or SessionEventKind.TaskSkipped => "error",
        SessionEventKind.Mail => "plain",
        _ => "waiting",
    };

    private static string CueOf(SessionEventKind kind) => kind switch
    {
        SessionEventKind.Done => "done",
        SessionEventKind.Question => "ask",
        SessionEventKind.Approval => "alert",
        SessionEventKind.Mail => "",
        _ => "error",
    };

    /// The hover card for one character, or for every shown one when id is
    /// null: {"method":"card","id":"claude"|null}.
    public static string Card(JsonElement request)
    {
        lock (Gate)
        {
            var only = request.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? DisplayProviders.Parse(id.GetString()) : null;
            var theme = DrawerStore.Shared.Theme;
            var pixel = theme == CharacterTheme.Pixel;
            var visibility = ProviderVisibilityStore.Shared;
            var visible = DrawerVisibility.Characters(Bots.ToDictionary(b => b.Provider, b => visibility.IsShown(b.Provider)));
            var order = DrawerStore.Shared.Order.ToList();
            var sections = Bots.Where(b => visible[b.Provider] && (only is null || b.Provider == only))
                .OrderBy(b => order.IndexOf(b.Provider))
                .ToList();
            using var english = pixel ? L10n.English() : null;
            return Json.Write(json =>
            {
                json.WriteBoolean("pixel", pixel);
                json.WriteStartArray("cards");
                foreach (var bot in sections) WriteCard(json, bot, theme);
                json.WriteEndArray();
            });
        }
    }

    private static void WriteCard(Utf8JsonWriter json, Bot bot, CharacterTheme theme)
    {
        var waitingEvents = ActivityMonitor.Shared.Waiting.Where(w => w.Tool.ToDisplayProvider().Character() == bot.Provider);
        var waiting = theme == CharacterTheme.Pixel ? StatusWaitingList.Of(waitingEvents) : StatusWaitingList.ForClassic(waitingEvents, DateTimeOffset.UtcNow);
        var card = StatusCards.For(bot.Provider, bot.Phase, bot.Detail, bot.Running) with
        {
            Phase = DrawerLooks.CardPhase(bot.Phase, false, theme),
            Waiting = waiting,
        };
        if (bot.Rest is { } rest) card = card with { Phase = DrawerLooks.CardPhase(bot.Phase, true, theme), Detail = RestDetail(rest, bot.Provider) };

        var look = LookCatalog.Of(theme, bot.Provider);
        json.WriteStartObject();
        json.WriteString("id", bot.Provider.RawValue());
        json.WriteString("name", card.Name);
        json.WriteNumber("swatch", look.Swatch);
        json.WriteString("look", look.Id);
        json.WriteBoolean("defaultLook", look == LookCatalog.For(theme, bot.Provider)[0]);
        json.WriteString("phase", card.Phase);
        if (card.Detail is { } detail) json.WriteString("detail", detail);
        if (card.Plan is { } plan) json.WriteString("plan", plan);
        if (card.Tasks is { } tasks)
        {
            json.WriteStartObject("tasks");
            json.WriteString("label", tasks.Label);
            json.WriteString("text", tasks.Text);
            json.WriteString("number", tasks.Number);
            json.WriteEndObject();
        }
        json.WriteStartArray("meters");
        foreach (var meter in card.Meters)
        {
            json.WriteStartObject();
            json.WriteString("label", meter.CardLabel);
            json.WriteString("value", meter.Value);
            if (meter.Shown is { } shown) json.WriteNumber("shown", shown);
            if (meter.Used is { } used) json.WriteNumber("used", used);
            if (meter.Resets is { } resets) json.WriteString("resets", resets);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        if (card.Tokens is { } tokens)
        {
            json.WriteStartObject("tokens");
            json.WriteString("label", tokens.Label);
            json.WriteString("today", tokens.Today);
            json.WriteString("todayNumber", tokens.TodayNumber);
            json.WriteString("month", tokens.Month);
            json.WriteString("monthNumber", tokens.MonthNumber);
            json.WriteEndObject();
        }
        json.WriteStartArray("note");
        foreach (var phrase in card.Note)
        {
            json.WriteStartObject();
            json.WriteString("text", phrase.Text);
            if (phrase.Lead is { } lead) json.WriteString("lead", lead);
            json.WriteStartArray("numbers");
            foreach (var number in phrase.Numbers) json.WriteStringValue(number);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartObject("waiting");
        json.WriteNumber("more", card.Waiting.More);
        json.WriteStartArray("rows");
        foreach (var row in card.Waiting.Rows)
        {
            json.WriteStartObject();
            json.WriteString("title", row.Title);
            json.WriteString("word", row.Word);
            json.WriteString("kind", row.Kind.ToString().ToLowerInvariant());
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static string RestDetail(AgentPresence rest, DisplayProvider character) => L10n.Tr(rest switch
    {
        AgentPresence.Closed => AgentPresences.ClosedLine(character, GrokBot.Installed),
        AgentPresence.Minimized => "Minimized",
        _ => "In the tray",
    });

    /// A character's fidgets in a look: {"method":"look","id":"claude"} is answered by Swift's own
    /// engine; this only lists the catalog: {"method":"looks","id":"claude","theme":"pixel"}.
    public static string Looks(JsonElement request)
    {
        var provider = request.TryGetProperty("id", out var id) ? DisplayProviders.Parse(id.GetString()) : null;
        var theme = request.TryGetProperty("theme", out var t) && t.GetString() == "classic" ? CharacterTheme.Classic : CharacterTheme.Pixel;
        return Json.Write(json =>
        {
            json.WriteStartArray("looks");
            foreach (var look in LookCatalog.For(theme, provider ?? DisplayProvider.Claude))
            {
                json.WriteStartObject();
                json.WriteString("id", look.Id);
                json.WriteString("name", look.Name);
                json.WriteNumber("swatch", look.Swatch);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        });
    }
}

/// The one JSON writer the bridge's replies share.
internal static class Json
{
    public static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// The app's own log, `crash.log` in its support folder: what failed and
/// where, by name only (a type, a context, a file name of the app's own),
/// never a message that could carry a path or any text of the user's. A
/// repeat of the same line is written once per launch; past 1 MB it starts again.
internal static class AppLog
{
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        lock (Gate)
        {
            if (!Seen.Add(line)) return;
            try
            {
                var folder = IslandPaths.AppSupportDir;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "crash.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, Path.Combine(folder, "crash.old.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
