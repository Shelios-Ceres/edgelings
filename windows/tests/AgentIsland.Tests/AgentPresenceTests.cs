using AgentIsland.Core;
using static AgentIsland.Core.AgentPresences;

namespace AgentIsland.Tests;

/// Pins how much of an agent's app is on screen: an open window (its own or
/// its host's; a covered one counts), only minimized ones, none (the tray),
/// or no process. Synthetic process trees, shaped like the ones on this machine on
/// 2026-10-17.
public static class AgentPresenceTests
{
    private static readonly Running Explorer = new(1, 0, "explorer");

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("no process: closed", () => Expect(TriggerTool.Claude, AgentPresence.Closed, new[] { Explorer }, new AppWindow[0])),
            ("its own window open: open", () => Expect(TriggerTool.Claude, AgentPresence.Open,
                new[] { Explorer, new Running(10, 1, "claude") }, new[] { new AppWindow(10, false) })),
            ("its own window minimized: minimized", () => Expect(TriggerTool.Claude, AgentPresence.Minimized,
                new[] { Explorer, new Running(10, 1, "claude") }, new[] { new AppWindow(10, true) })),
            ("one window minimized and one open: open", () => Expect(TriggerTool.Cursor, AgentPresence.Open,
                new[] { Explorer, new Running(10, 1, "Cursor") }, new[] { new AppWindow(10, true), new AppWindow(10, false) })),
            ("Claude Code inside the Claude app shows through it", () => Expect(TriggerTool.Claude, AgentPresence.Open,
                new[] { Explorer, new Running(10, 1, "claude"), new Running(11, 10, "claude") }, new[] { new AppWindow(10, false) })),
            ("a CLI in a terminal shows through the terminal", () => Expect(TriggerTool.Grok, AgentPresence.Open,
                Terminal("grok"), new[] { new AppWindow(18, false) })),
            ("a CLI in a minimized terminal is minimized", () => Expect(TriggerTool.Grok, AgentPresence.Minimized,
                Terminal("grok"), new[] { new AppWindow(18, true) })),
            ("Codex's CLI in the minimized Codex app is minimized", () => Expect(TriggerTool.Codex, AgentPresence.Minimized,
                new[] { new Running(28, 0, "sihost"), new Running(29, 28, "ChatGPT"), new Running(30, 29, "codex") },
                new[] { new AppWindow(29, true) })),
            ("a classic console's window is its host's", () => Expect(TriggerTool.Claude, AgentPresence.Open,
                Console("claude"), new[] { new AppWindow(41, false) })),
            ("the shell's own windows don't count", () => Expect(TriggerTool.Claude, AgentPresence.Hidden,
                Console("claude"), new[] { new AppWindow(1, false) })),
            ("an app with no window shown is in the tray", () => Expect(TriggerTool.DeepSeek, AgentPresence.Hidden,
                new[] { Explorer, new Running(60, 1, "DeepSeek Harness"), new Running(61, 60, "DeepSeek Harness") }, new AppWindow[0])),
            ("the Kimi chat app isn't Kimi Code", () => Expect(TriggerTool.Kimi, AgentPresence.Closed,
                new[] { Explorer, new Running(70, 1, "Kimi", @"C:\Program Files\Kimi\Kimi.exe") }, new[] { new AppWindow(70, false) })),
            ("Kimi Code's CLI is told apart by its path", () => Expect(TriggerTool.Kimi, AgentPresence.Open,
                new[] { Explorer, new Running(70, 1, "kimi", @"C:\Users\me\.kimi-code\bin\kimi.exe") }, new[] { new AppWindow(70, false) })),
        };
        foreach (var (name, test) in tests)
        {
            test();
            System.Console.WriteLine($"PASS presence: {name}");
        }
        System.Console.WriteLine("AgentPresenceTests GREEN");
    }

    /// A CLI in Windows Terminal: the shell is the terminal's child.
    private static Running[] Terminal(string cli) =>
        new[] { Explorer, new Running(18, 1, "WindowsTerminal"), new Running(19, 18, "pwsh"), new Running(20, 19, cli) };

    /// A CLI in a classic console: its window is conhost's, the shell's child.
    private static Running[] Console(string cli) =>
        new[] { Explorer, new Running(39, 1, "cmd"), new Running(40, 39, cli), new Running(41, 39, "conhost") };

    private static void Expect(TriggerTool tool, AgentPresence expected, IReadOnlyList<Running> processes, IReadOnlyList<AppWindow> windows)
    {
        var actual = Of(processes, windows)[tool];
        if (actual != expected) throw new Exception($"{tool}: expected {expected}, got {actual}");
    }
}
