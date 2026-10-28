using AgentIsland.Core;

namespace AgentIsland.Model;

/// What one bubble says: a single session's event, or, when enough were
/// waiting for the bot's turn to speak, all of them at once.
public sealed record Speech(IReadOnlyList<SessionEvent> Events)
{
    public bool IsSummary => Events.Count > 1;
    public SessionEvent First => Events[0];

    /// Whether this speaks for a question or a permission prompt, which pops
    /// as a request and sits ahead of the rest.
    public bool NeedsAnswer => Events.Any(e => e.Kind is SessionEventKind.Question or SessionEventKind.Approval);
}

/// How a style spaces and merges what a bot has to say. The two styles
/// answered separately (Phase 3, D1): Pixel says one thing at a time and
/// merges from three; Classic merges from two and lets a later bubble replace
/// one that has shown long enough.
public sealed record SpeechRules(
    int MergeAt,
    double GapSeconds,
    double ReplaceAfterSeconds,
    bool NewerReplacesOlder)
{
    public static readonly SpeechRules Pixel = new(3, 0.5, 0, false);
    public static readonly SpeechRules Classic = new(2, 0, 2.5, true);
}

/// One bot's turn to speak. Sessions of its agent raise events at any time; a
/// bot says one thing at a time, so they wait here. Approvals and questions
/// go first, everything else in the order it came. An event whose session has
/// moved on is dropped from the queue, or taken back from the bubble.
public sealed class SpeechQueue
{
    private readonly List<SessionEvent> _queued = new();
    private double _readyAt;
    private double _shownAt;

    public SpeechRules Rules { get; set; } = SpeechRules.Pixel;

    /// What is on screen now, or null.
    public Speech? Shown { get; private set; }

    public int Count => _queued.Count;

    /// Whether an event of this kind is on screen or waiting its turn.
    public bool Holds(SessionEventKind kind) =>
        _queued.Any(e => e.Kind == kind) || Shown?.Events.Any(e => e.Kind == kind) == true;

    public bool Add(SessionEvent item)
    {
        if (_queued.Any(e => e.Key == item.Key) || Shown?.Events.Any(e => e.Key == item.Key) == true) return false;
        var key = item.SessionKey;
        if (Rules.NewerReplacesOlder) _queued.RemoveAll(e => e.SessionKey == key);
        _queued.Add(item);
        return true;
    }

    /// The next bubble, once the bot is free: nothing showing and the gap
    /// over, or what shows has been up long enough to be replaced. Null while
    /// it must wait, or nothing does.
    public Speech? Next(double now)
    {
        if (_queued.Count == 0) return null;
        if (Shown is not null)
        {
            if (Rules.ReplaceAfterSeconds <= 0 || now - _shownAt < Rules.ReplaceAfterSeconds) return null;
        }
        else if (now < _readyAt)
        {
            return null;
        }

        var ordered = _queued
            .Select((e, i) => (Event: e, Index: i))
            .OrderBy(x => Urgency(x.Event))
            .ThenBy(x => x.Index)
            .Select(x => x.Event)
            .ToList();
        var say = ordered.Count >= Rules.MergeAt ? ordered : new List<SessionEvent> { ordered[0] };
        foreach (var item in say) _queued.Remove(item);
        _shownAt = now;
        return Shown = new Speech(say);
    }

    /// Approvals and questions first, then everything else in the order it came.
    private static int Urgency(SessionEvent item) =>
        item.Kind is SessionEventKind.Question or SessionEventKind.Approval ? 0 : 1;

    /// The bubble is gone, by its own time or taken back.
    public void Dismissed(double now)
    {
        Shown = null;
        _readyAt = now + Rules.GapSeconds;
    }

    /// Drops what the sessions have moved on from. Returns true when what is
    /// showing is no longer worth showing, so the caller takes the bubble back.
    public bool Reconcile(IReadOnlySet<string> waitingKeys)
    {
        _queued.RemoveAll(Gone);
        return Shown is { } shown && shown.Events.All(Gone);

        bool Gone(SessionEvent e) => e.Kind != SessionEventKind.TaskSkipped && !waitingKeys.Contains(e.Key);
    }

    /// Everything the card's list holds is no longer news.
    public void Clear()
    {
        _queued.Clear();
    }
}
