using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Model;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The work-state groups (Think, Do, Team, and Busy for Kimi and Antigravity)
/// against the two engines the app ships, and the rotation on a fake clock:
/// every look is in its table, every state it names exists in its engine, and
/// the rotation turns at the agreed times.
public static class StateGroupsTests
{
    private sealed record Engine(HashSet<string> Phases, Dictionary<string, double> Cycles);

    private static readonly CharacterTheme[] Themes = { CharacterTheme.Pixel, CharacterTheme.Classic };

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("every look of both sets is in its table, and the table has no other", EveryLookHasAnEntry),
            ("every state a table names exists in its engine, and cycles", EveryStateExistsInItsEngine),
            ("Kimi and Antigravity are Busy, the rest Think and Do; Team follows the table", GroupShapes),
            ("phases fall into their groups, the attention states into none", PhasesFallIntoGroups),
            ("the second slot: a tool's own state, else the look's pick; Pixel and Classic differ when it equals main", SecondSlot),
            ("rotation: main first, the second at 12 s, then 8 s and 5 s in turn, on cycle boundaries", RotationTimes),
            ("rotation: a phase change inside a group never restarts the motion", PhaseChangeInsideGroup),
            ("rotation: a tool arriving in main shows next turn, in the second at the next boundary", ToolArrives),
            ("rotation: re-entering a group starts at main with a fresh 12 s; the dwell holds a quick exit", ReentryAndDwell),
            ("rotation: reduced motion plays main only", ReducedMotion),
            ("rotation: Busy for Kimi and Antigravity leads with work, thinking second, a tool in its place", BusyRotation),
            ("rotation: the tempo stretches the cycle, and segments stay at least four seconds", MinimumSegment),
            ("rotation: a ten-minute walk of every look stays inside its engine's states", EveryLookWalks),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
    }

    private static void EveryLookHasAnEntry()
    {
        foreach (var theme in Themes)
        {
            var wanted = new HashSet<string>();
            foreach (var provider in DisplayProviders.All)
            {
                var kind = DrawerLooks.Kind(provider);
                foreach (var look in LookCatalog.For(theme, provider))
                {
                    Expect(StateGroups.For(theme, kind, look.Id) is not null, $"{theme} {kind}/{look.Id} has no entry");
                    wanted.Add(StateGroups.Key(theme, kind, look.Id));
                }
            }
            var extra = StateGroups.Keys(theme).Where(key => !wanted.Contains(key)).ToList();
            Expect(extra.Count == 0, $"{theme} table has entries for no look: {string.Join(", ", extra)}");
            Expect(StateGroups.For(theme, "claude", "no-such-look") is null, $"{theme}: an unknown look has no groups");
        }
    }

    private static void EveryStateExistsInItsEngine()
    {
        foreach (var theme in Themes)
        {
            var engine = ProbeEngine(theme);
            foreach (var key in StateGroups.Keys(theme))
            {
                var parts = key.Split('/');
                var groups = StateGroups.For(theme, parts[0], parts[1])!;
                foreach (var group in Enum.GetValues<WorkGroup>())
                {
                    if (groups.Of(group) is not { } pair) continue;
                    foreach (var state in new[] { pair.Main, pair.Second })
                    {
                        Expect(engine.Phases.Contains(state), $"{theme} {key} {group}: '{state}' is not a state of the engine");
                        Expect(engine.Cycles.TryGetValue(CycleKey(theme, key, state), out var cycle) && cycle > 0, $"{theme} {key}: '{state}' has no cycle");
                    }
                }
            }
            foreach (var phase in Enum.GetValues<AgentPhase>())
            {
                var state = DrawerLooks.Entry(phase, theme);
                Expect(engine.Phases.Contains(state), $"{theme}: {phase} enters '{state}', which the engine lacks");
            }
        }
    }

    private static void GroupShapes()
    {
        foreach (var theme in Themes)
        {
            foreach (var key in StateGroups.Keys(theme))
            {
                var kind = key.Split('/')[0];
                var groups = StateGroups.For(theme, kind, key.Split('/')[1])!;
                if (kind is "kimi" or "antigravity")
                    Expect(groups.Busy is not null && groups.Think is null && groups.Do is null, $"{theme} {key}: Busy and no Think or Do");
                else
                    Expect(groups.Busy is null && groups.Think is not null && groups.Do is not null, $"{theme} {key}: Think and Do and no Busy");
                if (kind == "kimi") Expect(groups.Team is not null, $"{theme} {key}: Kimi keeps Team");
                if (kind is not "kimi" and not "antigravity") Expect(groups.Team is not null, $"{theme} {key}: Team");
            }
        }
        Expect(StateGroups.GroupOf(CharacterTheme.Classic, "antigravity", "paper-plane", AgentPhase.Spawning) == WorkGroup.Team,
            "Classic's Antigravity keeps Team");
        Expect(StateGroups.GroupOf(CharacterTheme.Pixel, "antigravity", "balloon", AgentPhase.Spawning) is null,
            "Pixel's Antigravity has no Team: spawning plays its own state");
    }

    private static void PhasesFallIntoGroups()
    {
        foreach (var theme in Themes)
        {
            var (kind, look, busyKind, busyLook) = theme == CharacterTheme.Pixel ? ("codex", "notebook", "kimi", "rabbit") : ("codex", "keycap", "kimi", "moon-rabbit");
            foreach (var phase in new[] { AgentPhase.Thinking, AgentPhase.Streaming, AgentPhase.Compacting })
            {
                Expect(StateGroups.GroupOf(theme, kind, look, phase) == WorkGroup.Think, $"{theme} {phase} is Think");
                Expect(StateGroups.GroupOf(theme, busyKind, busyLook, phase) == WorkGroup.Busy, $"{theme} {phase} is Busy for Kimi");
            }
            foreach (var phase in new[] { AgentPhase.Reading, AgentPhase.Searching, AgentPhase.WebSearching, AgentPhase.Writing, AgentPhase.Running, AgentPhase.Controlling, AgentPhase.Working })
            {
                Expect(StateGroups.GroupOf(theme, kind, look, phase) == WorkGroup.Do, $"{theme} {phase} is Do");
                Expect(StateGroups.GroupOf(theme, busyKind, busyLook, phase) == WorkGroup.Busy, $"{theme} {phase} is Busy for Kimi");
            }
            foreach (var phase in new[] { AgentPhase.Spawning, AgentPhase.WaitingOnAgents, AgentPhase.Sending })
            {
                Expect(StateGroups.GroupOf(theme, kind, look, phase) == WorkGroup.Team, $"{theme} {phase} is Team");
                Expect(StateGroups.GroupOf(theme, busyKind, busyLook, phase) == WorkGroup.Team, $"{theme} {phase} is Team for Kimi");
            }
            foreach (var phase in new[] { AgentPhase.Idle, AgentPhase.Asking, AgentPhase.Approving, AgentPhase.Finished, AgentPhase.Failed,
                         AgentPhase.NetworkTrouble, AgentPhase.RateLimited, AgentPhase.AuthRequired })
            {
                Expect(StateGroups.GroupOf(theme, kind, look, phase) is null && StateGroups.GroupOf(theme, busyKind, busyLook, phase) is null,
                    $"{theme} {phase} plays its own state");
            }
        }
        // Pixel draws Cursor as Grok.
        Expect(StateGroups.GroupOf(CharacterTheme.Pixel, "cursor", "cat", AgentPhase.Running) == WorkGroup.Do, "Pixel's Cursor wears Grok's table");
        Expect(StateGroups.GroupOf(CharacterTheme.Classic, "cursor", "orbit-ring", AgentPhase.Running) == WorkGroup.Do, "Classic's Cursor has its own entries");
    }

    private static void SecondSlot()
    {
        // Pixel Clawd, Think: main reading; Thinking takes the look's pick, Streaming its own state.
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "claude", "clawd", WorkGroup.Think, AgentPhase.Thinking)), "reading,thinking");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "claude", "clawd", WorkGroup.Think, AgentPhase.Streaming)), "reading,streaming");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "claude", "clawd", WorkGroup.Think, AgentPhase.Compacting)), "reading,compacting");
        // Do: main is the signature motion; a known tool is the second, an unknown one the pick.
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "claude", "clawd", WorkGroup.Do, AgentPhase.Working)), "working,running");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "claude", "clawd", WorkGroup.Do, AgentPhase.Reading)), "working,reading");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "claude", "clawd", WorkGroup.Do, AgentPhase.Controlling)), "working,controlling");
        // Pixel: the tool's state equal to main leaves one slot; Classic turns to the pick.
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "codex", "notebook", WorkGroup.Do, AgentPhase.Writing)), "writing");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Classic, "codex", "keycap", WorkGroup.Do, AgentPhase.Running)), "running,writing");
        // Team: Pixel's whale spouts when the phase gives no state of its own.
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "deepseek", "whale", WorkGroup.Team, AgentPhase.Spawning)), "spawning,sending");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Pixel, "deepseek", "whale", WorkGroup.Team, AgentPhase.WaitingOnAgents)), "spawning,orbit");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Classic, "claude", "succulent", WorkGroup.Team, AgentPhase.WaitingOnAgents)), "orbit,spawning");
        Expect(string.Join(",", StateGroups.Slots(CharacterTheme.Classic, "claude", "succulent", WorkGroup.Team, AgentPhase.Sending)), "orbit,sending");
    }

    private static GroupRotation Rotation(CharacterTheme theme, string kind, string look, double cycle = 3) =>
        new(theme, kind, look, _ => cycle);

    // Steps a rotation once a second and records every change of state with its time.
    private static List<(double At, string State)> Trace(GroupRotation rotation, double seconds, Func<double, AgentPhase> phaseAt, bool reduced = false, double step = 0.25)
    {
        var log = new List<(double, string)>();
        string? last = null;
        for (var t = 0.0; t <= seconds + 1e-9; t += step)
        {
            var state = rotation.State(t, phaseAt(t), reduced);
            if (state != last) log.Add((t, state));
            last = state;
        }
        return log;
    }

    private static string Show(List<(double At, string State)> trace) => string.Join(" ", trace.Select(e => $"{e.At:0.##}:{e.State}"));

    private static void RotationTimes()
    {
        // Pixel codex/notebook, Think, constant 3 s cycles: main to 12 s, the second 5 s (rounded up to 6), main 8 s (to 9).
        var trace = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook"), 40, _ => AgentPhase.Thinking);
        Expect(Show(trace), "0:thinking 12:streaming 18:thinking 27:streaming 33:thinking");
        // A cycle that does not divide the time: segments end on the next boundary.
        trace = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook", 2.5), 40, _ => AgentPhase.Thinking);
        Expect(Show(trace), "0:thinking 12.5:streaming 17.5:thinking 27.5:streaming 32.5:thinking");
        // Classic's Think, the same numbers.
        trace = Trace(Rotation(CharacterTheme.Classic, "codex", "keycap"), 20, _ => AgentPhase.Thinking);
        Expect(Show(trace), "0:thinking 12:streaming 18:thinking");
    }

    private static void PhaseChangeInsideGroup()
    {
        // Thinking, then Streaming at 5 s, both Think: the motion does not restart, and the new phase fills the second slot.
        var trace = Trace(Rotation(CharacterTheme.Pixel, "claude", "clawd"), 20, t => t < 5 ? AgentPhase.Thinking : AgentPhase.Streaming);
        Expect(Show(trace), "0:reading 12:streaming 18:reading");
        // A phase change across groups (Think to Do) starts the new group at its main.
        trace = Trace(Rotation(CharacterTheme.Pixel, "claude", "clawd"), 8, t => t < 5 ? AgentPhase.Thinking : AgentPhase.Running);
        Expect(Show(trace), "0:reading 5:working");
    }

    private static void ToolArrives()
    {
        // Do on Clawd: Working at first (second = running), Reading from 10 s: main until 12 s, then reading, not running.
        var trace = Trace(Rotation(CharacterTheme.Pixel, "claude", "clawd"), 20, t => t < 10 ? AgentPhase.Working : AgentPhase.Reading);
        Expect(Show(trace), "0:working 12:reading 18:working");
        // A tool arriving during the second slot switches at that state's next boundary, no sooner than four seconds in
        // (1 s cycles: the segment began at 12, the tool came at 14.3).
        trace = Trace(Rotation(CharacterTheme.Pixel, "claude", "clawd", 1), 24, t => t < 14.3 ? AgentPhase.Working : AgentPhase.Searching);
        Expect(Show(trace), "0:working 12:running 16:searching 21:working");
        // Classic: a tool that is main falls back to the look's pick.
        trace = Trace(Rotation(CharacterTheme.Classic, "codex", "keycap"), 20, _ => AgentPhase.Running);
        Expect(Show(trace), "0:running 12:writing 18:running");
    }

    private static void ReentryAndDwell()
    {
        var rotation = Rotation(CharacterTheme.Pixel, "codex", "notebook");
        // Think for 15 s (the second is playing), Finished for 6 s, Think again: main, and the 12 s count starts afresh.
        var trace = Trace(rotation, 45, t => t < 15 ? AgentPhase.Thinking : t < 21 ? AgentPhase.Finished : AgentPhase.Thinking);
        Expect(Show(trace), "0:thinking 12:streaming 15:finished 21:thinking 33:streaming 39:thinking");
        // Leaving a group inside the dwell waits it out.
        var quick = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook"), 5, t => t < 1 ? AgentPhase.Thinking : AgentPhase.Finished);
        Expect(Show(quick), "0:thinking 2:finished");
        // Entering one from outside has no dwell.
        var enter = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook"), 5, t => t < 1 ? AgentPhase.Finished : AgentPhase.Thinking);
        Expect(Show(enter), "0:finished 1:thinking");
    }

    private static void ReducedMotion()
    {
        var trace = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook"), 60, _ => AgentPhase.Thinking, reduced: true);
        Expect(Show(trace), "0:thinking");
        var tool = Trace(Rotation(CharacterTheme.Pixel, "claude", "clawd"), 30, t => t < 15 ? AgentPhase.Working : AgentPhase.Reading, reduced: true);
        Expect(Show(tool), "0:working");
        // Switching it on mid-group stops the rotation at main.
        var rotation = Rotation(CharacterTheme.Pixel, "codex", "notebook");
        var log = new List<string>();
        for (var t = 0.0; t < 40; t += 0.25) log.Add(rotation.State(t, AgentPhase.Thinking, reducedMotion: t >= 14));
        Expect(log[(int)(13.0 / 0.25)], "streaming"); // 13 s: the second is playing
        Expect(log[^1], "thinking");
    }

    private static void BusyRotation()
    {
        foreach (var (theme, kind, look, main) in new[]
                 {
                     (CharacterTheme.Pixel, "kimi", "rabbit", "working"), (CharacterTheme.Pixel, "antigravity", "rainbow", "streaming"),
                     (CharacterTheme.Classic, "kimi", "moon-rabbit", "running"), (CharacterTheme.Classic, "antigravity", "paper-plane", "writing"),
                 })
        {
            // Thinking for 40 s: work first, then thinking in the second slot.
            var trace = Trace(Rotation(theme, kind, look), 40, _ => AgentPhase.Thinking);
            Expect(Show(trace), $"0:{main} 12:thinking 18:{main} 27:thinking 33:{main}");
            // A known tool takes the second slot in place of thinking.
            trace = Trace(Rotation(theme, kind, look), 20, t => t < 3 ? AgentPhase.Thinking : AgentPhase.Reading);
            Expect(Show(trace), $"0:{main} 12:reading 18:{main}");
            // Think and Do share one group: no restart between them.
            trace = Trace(Rotation(theme, kind, look), 11, t => t < 5 ? AgentPhase.Thinking : AgentPhase.Searching);
            Expect(Show(trace), $"0:{main}");
        }
        // Kimi's Team is a group of its own.
        var team = Trace(Rotation(CharacterTheme.Pixel, "kimi", "rabbit"), 8, t => t < 4 ? AgentPhase.Thinking : AgentPhase.Spawning);
        Expect(Show(team), "0:working 4:spawning");
    }

    private static void MinimumSegment()
    {
        // 1 s cycles: the second lasts its 5 s, the minimum 4 s never binds.
        var trace = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook", 1), 30, _ => AgentPhase.Thinking);
        Expect(Show(trace), "0:thinking 12:streaming 17:thinking 25:streaming 30:thinking");
        // A cycle longer than a segment holds the whole cycle: 6 s.
        trace = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook", 6), 40, _ => AgentPhase.Thinking);
        Expect(Show(trace), "0:thinking 12:streaming 18:thinking 30:streaming 36:thinking");
        // A tempo of 2 halves the engine's 3 s cycle to 1.5 s of the clock: 5 s rounds up to 6, 8 s to 9.
        trace = Trace(Rotation(CharacterTheme.Pixel, "codex", "notebook", 1.5), 30, _ => AgentPhase.Thinking);
        Expect(Show(trace), "0:thinking 12:streaming 18:thinking 27:streaming");
    }

    private static void EveryLookWalks()
    {
        foreach (var theme in Themes)
        {
            var engine = ProbeEngine(theme);
            var phases = Enum.GetValues<AgentPhase>();
            var rng = new Random(11);
            foreach (var key in StateGroups.Keys(theme))
            {
                var parts = key.Split('/');
                var rotation = new GroupRotation(theme, parts[0], parts[1], state => engine.Cycles[CycleKey(theme, key, state)]);
                var phase = AgentPhase.Thinking;
                var nextChange = 0.0;
                for (var t = 0.0; t < 600; t += 0.5)
                {
                    if (t >= nextChange)
                    {
                        phase = phases[rng.Next(phases.Length)];
                        nextChange = t + rng.Next(1, 40);
                    }
                    var state = rotation.State(t, phase);
                    Expect(engine.Phases.Contains(state), $"{theme} {key}: played '{state}' for {phase} at {t} s");
                }
            }
        }
    }

    private static string CycleKey(CharacterTheme theme, string key, string state) => theme == CharacterTheme.Pixel ? $"{key}/{state}" : state;

    /// The engine's own PHASES and cycleOf for every state, as the drawer's host module reads them.
    private static Engine ProbeEngine(CharacterTheme theme)
    {
        var file = theme == CharacterTheme.Pixel ? "characters.mjs" : "classic.mjs";
        var directory = Path.GetDirectoryName(Source("Drawer", "Characters", file))!;
        using var v8 = new V8ScriptEngine();
        v8.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
        v8.Execute(new DocumentInfo(new Uri(Path.Combine(directory, "probe-groups.mjs"))) { Category = ModuleCategory.Standard }, $$"""
            import * as lib from './{{file}}';
            const cycles = {};
            for (const kind of lib.KINDS) for (const { id } of lib.LOOKS[kind])
              for (const state of lib.PHASES) cycles[{{(theme == CharacterTheme.Pixel ? "`${kind}/${id}/${state}`" : "state")}}] = {{(theme == CharacterTheme.Pixel ? "lib.cycleOf(kind, state, id)" : "lib.cycleOf(state)")}};
            globalThis.probe = JSON.stringify({ phases: lib.PHASES, cycles });
            """);
        using var json = JsonDocument.Parse((string)v8.Script.probe);
        var cycles = json.RootElement.GetProperty("cycles").EnumerateObject().ToDictionary(c => c.Name, c => c.Value.GetDouble());
        return new(json.RootElement.GetProperty("phases").EnumerateArray().Select(p => p.GetString()!).ToHashSet(), cycles);
    }

    /// A source file of the WinUI app, found from the test binaries up to the repo.
    private static string Source(params string[] path)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(new[] { dir.FullName, "src", "AgentIsland.WinUI" }.Concat(path).ToArray());
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException(string.Join("/", path));
    }
}
