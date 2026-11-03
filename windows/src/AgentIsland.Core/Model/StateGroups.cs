using AgentIsland.Core;

namespace AgentIsland.Model;

/// The three groups the bots' work motions are folded into (Think, Do, Team),
/// and Busy for the agents whose transcripts cannot tell thinking from doing
/// (Kimi, Antigravity): Think and Do merged. A phase outside every group
/// (the attention and rest states) plays its own state, as before.
public enum WorkGroup { Think, Do, Team, Busy }

/// A look's two motions in a group: the main one and the second it turns to.
public sealed record GroupPair(string Main, string Second);

/// What a look plays in each group it has. A group a look lacks leaves its
/// phases to their own states.
public sealed record LookGroups(GroupPair? Think, GroupPair? Do, GroupPair? Team, GroupPair? Busy)
{
    public GroupPair? Of(WorkGroup group) => group switch
    {
        WorkGroup.Think => Think,
        WorkGroup.Do => Do,
        WorkGroup.Team => Team,
        _ => Busy,
    };
}

/// How a rotation is timed, in seconds. The numbers are the design chats'
/// shared ones (2026-11-02): a group held for FirstMain turns to its second
/// motion, then main and second alternate; a segment ends at the playing
/// state's next cycle boundary and lasts at least MinSegment.
public sealed record GroupTimings(double FirstMain, double Main, double Second, double MinSegment, double MinDwell);

/// Which state each phase plays in a group, per character set and look
/// (Model/StateGroups.Data.cs, generated from the design chats' tables), and
/// the rule for choosing the second motion. The engines are untouched: every
/// name is a state they already have.
public static partial class StateGroups
{
    public static readonly GroupTimings Timings = new(12, 8, 5, 4, 2);

    private static readonly AgentPhase[] ThinkPhases = { AgentPhase.Thinking, AgentPhase.Streaming, AgentPhase.Compacting };
    private static readonly AgentPhase[] DoPhases =
    {
        AgentPhase.Reading, AgentPhase.Searching, AgentPhase.WebSearching, AgentPhase.Writing,
        AgentPhase.Running, AgentPhase.Controlling, AgentPhase.Working,
    };
    private static readonly AgentPhase[] TeamPhases = { AgentPhase.Spawning, AgentPhase.WaitingOnAgents, AgentPhase.Sending };

    /// The look's key in a set's table. Pixel draws Cursor as Grok.
    internal static string Key(CharacterTheme theme, string kind, string look) =>
        (theme == CharacterTheme.Pixel && kind == "cursor" ? "grok" : kind) + "/" + look;

    private static Dictionary<string, LookGroups> Looks(CharacterTheme theme) => theme == CharacterTheme.Pixel ? PixelLooks : ClassicLooks;

    private static Dictionary<WorkGroup, string[]> Tools(CharacterTheme theme) => theme == CharacterTheme.Pixel ? PixelTools : ClassicTools;

    /// Every kind/look key a set's table has.
    public static IEnumerable<string> Keys(CharacterTheme theme) => Looks(theme).Keys;

    /// A look's groups, or null for a look the table does not know.
    public static LookGroups? For(CharacterTheme theme, string kind, string look) =>
        Looks(theme).TryGetValue(Key(theme, kind, look), out var groups) ? groups : null;

    /// The group a phase plays in for a look, or null when it plays its own state.
    public static WorkGroup? GroupOf(CharacterTheme theme, string kind, string look, AgentPhase phase)
    {
        if (For(theme, kind, look) is not { } groups) return null;
        if (groups.Busy is not null && (ThinkPhases.Contains(phase) || DoPhases.Contains(phase))) return WorkGroup.Busy;
        if (groups.Think is not null && ThinkPhases.Contains(phase)) return WorkGroup.Think;
        if (groups.Do is not null && DoPhases.Contains(phase)) return WorkGroup.Do;
        if (groups.Team is not null && TeamPhases.Contains(phase)) return WorkGroup.Team;
        return null;
    }

    /// The motions a group plays for a phase, in turn: main, then the second
    /// when it differs. The second is the phase's own state when the set
    /// gives the group one (a tool's motion), else the look's pick. Pixel
    /// plays main alone when the phase's own state is main; Classic then
    /// turns to the look's pick.
    public static IReadOnlyList<string> Slots(CharacterTheme theme, string kind, string look, WorkGroup group, AgentPhase phase)
    {
        var pair = For(theme, kind, look)?.Of(group) ?? throw new ArgumentException($"{theme} {kind}/{look} has no {group} group");
        var own = DrawerLooks.Entry(phase, theme);
        var second = Tools(theme)[group].Contains(own) ? own : pair.Second;
        if (theme == CharacterTheme.Classic && second == pair.Main) second = pair.Second;
        return second == pair.Main ? new[] { pair.Main } : new[] { pair.Main, second };
    }
}

/// One bot's rotation through its look's work groups: feed it the phase it
/// shows and the clock, and it says which state to play. The clock and the
/// cycle lengths are passed in, so the host's frame loop, a film and a test on
/// a fake clock all run the same code.
public sealed class GroupRotation
{
    private readonly CharacterTheme _theme;
    private readonly string _kind;
    private readonly string _look;
    private readonly Func<string, double> _cycleSeconds;
    private WorkGroup? _group;
    private AgentPhase? _phase, _pending;
    private IReadOnlyList<string> _slots = Array.Empty<string>();
    private double _groupAt, _segmentAt, _playAt, _endAt;
    private double? _swapAt;
    private int _segment, _slot;
    private string? _playing;
    private bool _reduced;

    /// cycleSeconds says how long a state's loop lasts on this bot's clock
    /// (the engine's cycleOf, divided by the bot's tempo).
    public GroupRotation(CharacterTheme theme, string kind, string look, Func<string, double> cycleSeconds)
    {
        _theme = theme;
        _kind = kind;
        _look = look;
        _cycleSeconds = cycleSeconds;
    }

    /// The group the shown phase plays in, or null for its own state.
    public WorkGroup? Group => _group;

    /// The phase left every group at once (an attention or rest state took
    /// over); the next group is entered afresh, with no dwell to wait out.
    public void Leave()
    {
        _group = null;
        _phase = null;
        _pending = null;
    }

    /// The state to play now. Outside a group it is the phase's own entry
    /// state; a caller that sets a rest pose apart (finished, failed) keeps doing so.
    public string State(double now, AgentPhase phase, bool reducedMotion = false)
    {
        var t = StateGroups.Timings;
        _reduced = reducedMotion;
        if (_phase != phase && _pending != phase) _pending = phase;
        if (_pending is { } next)
        {
            var nextGroup = StateGroups.GroupOf(_theme, _kind, _look, next);
            if (nextGroup != _group && _group is not null && now - _groupAt < t.MinDwell)
            {
                // The group just began: wait out the dwell before leaving it.
            }
            else
            {
                _phase = next;
                _pending = null;
                if (nextGroup != _group)
                {
                    _group = nextGroup;
                    _groupAt = now;
                    _segment = 0;
                    if (_group is { } entered)
                    {
                        _slots = Plan(entered, next);
                        Start(now, 0);
                    }
                }
                else if (_group is { } held)
                {
                    _slots = Plan(held, next);
                    // A new tool while the second slot plays: the motion swaps at the playing
                    // state's next cycle boundary, inside the same segment.
                    if (_slot > 0) _swapAt = SlotPlays(_slot) || _slot >= _slots.Count ? null : NextBoundary(now);
                }
            }
        }
        if (_group is null || _phase is not { } shown) return DrawerLooks.Entry(_phase ?? phase, _theme);
        if (_slots.Count > 0 && (_slots.Count > 1) == reducedMotion) _slots = Plan(_group.Value, shown);
        if (_swapAt is { } swapAt && now >= swapAt && swapAt < _endAt)
        {
            _swapAt = null;
            if (_slot < _slots.Count && !SlotPlays(_slot))
            {
                _playing = _slots[_slot];
                _playAt = now;
                // The segment keeps its end, rounded up to the new state's cycle.
                var cycle = Math.Max(0.1, _cycleSeconds(_playing));
                var nominal = _segmentAt + Math.Max(t.Second, t.MinSegment);
                _endAt = _playAt + Math.Max(1, Math.Ceiling((nominal - _playAt) / cycle - 1e-9)) * cycle;
            }
        }
        if (now >= _endAt)
        {
            // Main follows every second segment, whatever tool arrived meanwhile.
            _segment++;
            Start(now, _slots.Count < 2 ? 0 : _slot == 0 ? 1 : 0);
        }
        return _playing!;
    }

    private IReadOnlyList<string> Plan(WorkGroup group, AgentPhase phase)
    {
        var slots = StateGroups.Slots(_theme, _kind, _look, group, phase);
        return _reduced ? new[] { slots[0] } : slots;
    }

    private bool SlotPlays(int slot) => slot < _slots.Count && _slots[slot] == _playing;

    private void Start(double now, int slot)
    {
        var t = StateGroups.Timings;
        _slot = slot;
        _playing = _slots[slot];
        _segmentAt = _playAt = now;
        _swapAt = null;
        var length = _segment == 0 ? t.FirstMain : slot == 0 ? t.Main : t.Second;
        _endAt = BoundaryAfter(length);
    }

    // The first cycle boundary of the playing state at or after `from` seconds
    // into the segment (a segment starts its state at phase 0), and no sooner than MinSegment.
    private double BoundaryAfter(double from)
    {
        var cycle = Math.Max(0.1, _cycleSeconds(_playing!));
        return _segmentAt + Math.Ceiling(Math.Max(from, StateGroups.Timings.MinSegment) / cycle - 1e-9) * cycle;
    }

    // The playing state's next cycle boundary at or after now (it began at phase 0 at _playAt).
    private double NextBoundary(double now)
    {
        var cycle = Math.Max(0.1, _cycleSeconds(_playing!));
        return _playAt + Math.Ceiling((now - _playAt) / cycle - 1e-9) * cycle;
    }
}
