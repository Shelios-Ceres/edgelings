using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using static AgentIsland.Core.AgentPresences;

namespace AgentIsland.Tests;

/// Pins how much of an agent's app is on screen: an open window (its own or
/// its host's; a covered one counts), only minimized ones, none (the tray),
/// or no process. Synthetic process trees, shaped like the ones on this machine on
/// 2026-10-17.
public static class AgentPresenceTests
{
    private static readonly Running Explorer = new(1, 0, "explorer");

    // How npm's claude.cmd and claude.ps1 shims, and bun, start Claude Code.
    private const string NpmClaude = @"""C:\Program Files\nodejs\node.exe"" ""C:\Users\me\AppData\Roaming\npm\\node_modules\@anthropic-ai\claude-code\cli.js""";
    private const string PowerShellShimClaude = @"""C:\Program Files\nodejs\node.exe"" C:\Users\me\AppData\Roaming\npm/node_modules/@anthropic-ai/claude-code/cli.js";
    private const string BunClaude = @"C:\Users\me\.bun\bin\bun.exe C:\Users\me\.bun\install\global\node_modules\@anthropic-ai\claude-code\cli.js";

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
            ("Grok Bot's open window keeps Grok open", () => Expect(TriggerTool.Grok, AgentPresence.Open,
                GrokBotTree(), new[] { new AppWindow(80, false) })),
            ("Grok Bot's window minimized: Grok minimized", () => Expect(TriggerTool.Grok, AgentPresence.Minimized,
                GrokBotTree(), new[] { new AppWindow(80, true) })),
            ("Grok Bot in the tray: Grok in the tray", () => Expect(TriggerTool.Grok, AgentPresence.Hidden,
                GrokBotTree(), new AppWindow[0])),
            ("Grok Bot's helper processes show through the main one", () => Expect(TriggerTool.Grok, AgentPresence.Open,
                new[] { Explorer, new Running(81, 80, "Grok Bot"), new Running(80, 1, "Grok Bot") }, new[] { new AppWindow(80, false) })),
            ("with no Grok Bot, Grok is as before", () => Expect(TriggerTool.Grok, AgentPresence.Closed,
                new[] { Explorer }, new[] { new AppWindow(80, false) })),
            ("the grok CLI in a minimized terminal still counts beside Grok Bot in the tray", () => Expect(TriggerTool.Grok, AgentPresence.Minimized,
                Terminal("grok").Concat(new[] { new Running(80, 1, "Grok Bot") }).ToList(), new[] { new AppWindow(18, true) })),
            ("Grok Bot isn't Cursor's or Claude's", () =>
            {
                var of = Of(GrokBotTree(), new[] { new AppWindow(80, false) });
                if (of[TriggerTool.Cursor] != AgentPresence.Closed || of[TriggerTool.Claude] != AgentPresence.Closed) throw new Exception("Grok Bot leaked into another agent");
            }),
            ("only Grok Bot running still leaves the grok CLI gone", () =>
            {
                var gone = AgentProcesses.Gone(new[] { ("Grok Bot", (string?)null) });
                if (!gone.Contains(TriggerTool.Grok)) throw new Exception("Grok Bot kept a dead CLI turn alive");
            }),
            ("Grok's closed line names Grok Bot only where it is installed", () =>
            {
                if (ClosedLine(DisplayProvider.Grok, false) != "Grok and Cursor closed") throw new Exception("no Grok Bot");
                if (ClosedLine(DisplayProvider.Grok, true) != "Grok, Grok\u00A0Bot and Cursor closed") throw new Exception("with Grok Bot");
                if (ClosedLine(DisplayProvider.Claude, true) != "App closed") throw new Exception("another character");
            }),
            ("a card keeps Grok Bot's name on one line, in both languages", () =>
            {
                var previous = L10n.Current;
                try
                {
                    foreach (var (language, line) in new[] { (L10n.Language.English, "Grok, Grok\u00A0Bot and Cursor closed"), (L10n.Language.SimplifiedChinese, "Grok、Grok\u00A0Bot 和 Cursor 都已关闭") })
                    {
                        L10n.Current = language;
                        var shown = L10n.Tr(ClosedLine(DisplayProvider.Grok, true));
                        if (shown != line) throw new Exception(shown);
                        if (!shown.Split(' ').Any(word => word.EndsWith("Grok\u00A0Bot", StringComparison.Ordinal))) throw new Exception("split: " + shown);
                    }
                }
                finally { L10n.Current = previous; }
            }),
            ("Grok Bot counts as installed by its exe or a running process", () =>
            {
                if (GrokBot.IsInstalled(false, _ => false)) throw new Exception("nothing there");
                if (!GrokBot.IsInstalled(true, _ => false)) throw new Exception("running");
                var bundle = OperatingSystem.IsMacOS() ? "Grok Bot.app/Contents/MacOS/Grok Bot" : "Grok Bot/Grok Bot.exe";
                if (!GrokBot.IsInstalled(false, path => path.Replace('\\', '/').EndsWith(bundle, StringComparison.OrdinalIgnoreCase))) throw new Exception("exe");
            }),
            ("Claude Code installed with npm runs as node and is not gone", () =>
            {
                foreach (var line in new[] { NpmClaude, PowerShellShimClaude, BunClaude })
                {
                    var host = line.Contains("bun.exe", StringComparison.Ordinal) ? "bun" : "node";
                    if (AgentProcesses.Gone(new[] { (host, (string?)line) }).Contains(TriggerTool.Claude)) throw new Exception("idled: " + line);
                }
            }),
            ("another node script is not Claude Code", () =>
            {
                if (!AgentProcesses.Gone(new[] { ("node", (string?)@"node D:\src\site\server.js") }).Contains(TriggerTool.Claude)) throw new Exception("any node counted");
            }),
            ("a node whose command line can't be read is never idled on a guess", () =>
            {
                if (AgentProcesses.Gone(new[] { ("node", (string?)null) }).Contains(TriggerTool.Claude)) throw new Exception("idled on a guess");
            }),
            ("npm's Claude Code in a terminal shows through the terminal", () => Expect(TriggerTool.Claude, AgentPresence.Open,
                new[] { Explorer, new Running(18, 1, "WindowsTerminal"), new Running(19, 18, "pwsh"), new Running(20, 19, "node", NpmClaude) },
                new[] { new AppWindow(18, false) })),
            ("another node script doesn't keep Claude open", () => Expect(TriggerTool.Claude, AgentPresence.Closed,
                new[] { Explorer, new Running(18, 1, "WindowsTerminal"), new Running(20, 18, "node", @"node D:\src\site\server.js") },
                new[] { new AppWindow(18, false) })),
            ("this process's command line reads back", () =>
            {
                var line = AgentProcesses.CommandLineOf(Environment.ProcessId);
                if (line is null || !line.Contains("AgentIsland.Tests", StringComparison.OrdinalIgnoreCase)) throw new Exception($"read {line ?? "nothing"}");
            }),
            ("a CLI handed off to Windows Terminal shows through the terminal", () => Expect(TriggerTool.Grok, AgentPresence.Open,
                Handoff("grok"), new[] { new AppWindow(61, false) }, ConsolePrograms)),
            ("a CLI handed off to a minimized Windows Terminal is minimized", () => Expect(TriggerTool.Grok, AgentPresence.Minimized,
                Handoff("grok"), new[] { new AppWindow(61, true) }, ConsolePrograms)),
            ("a handed-off CLI with no terminal window is in the tray", () => Expect(TriggerTool.Grok, AgentPresence.Hidden,
                Handoff("grok"), new AppWindow[0], ConsolePrograms)),
            // The same trees, shaped as a Mac has them: launchd above everything, a terminal app, login and the shell.
            ("on a Mac a CLI in Terminal shows through the terminal's window", () => Expect(TriggerTool.Claude, AgentPresence.Open,
                MacTerminal("claude"), new[] { new AppWindow(200, false) })),
            ("on a Mac a CLI in a terminal whose windows are all off screen is minimized", () => Expect(TriggerTool.Claude, AgentPresence.Minimized,
                MacTerminal("claude"), new[] { new AppWindow(200, true) })),
            ("on a Mac a CLI whose terminal shows no window is in the tray", () => Expect(TriggerTool.Claude, AgentPresence.Hidden,
                MacTerminal("claude"), new AppWindow[0])),
            ("on a Mac the system's own windows (launchd, the Dock) don't count", () => Expect(TriggerTool.Claude, AgentPresence.Hidden,
                MacTerminal("claude"), new[] { new AppWindow(1, false), new AppWindow(150, false) })),
            ("on a Mac the Claude app is a process named Claude", () => Expect(TriggerTool.Claude, AgentPresence.Open,
                new[] { new Running(1, 0, "launchd"), new Running(300, 1, "Claude") }, new[] { new AppWindow(300, false) })),
            ("on a Mac npm's Claude Code runs as node and is told by its argument path", () =>
            {
                var line = "node /Users/me/.nvm/versions/node/v22.1.0/lib/node_modules/@anthropic-ai/claude-code/cli.js";
                if (AgentProcesses.Gone(new[] { ("node", (string?)line) }).Contains(TriggerTool.Claude)) throw new Exception("idled: " + line);
            }),
            ("on a Mac Kimi Code's CLI is told apart by its path", () => Expect(TriggerTool.Kimi, AgentPresence.Open,
                new[] { new Running(1, 0, "launchd"), new Running(70, 1, "kimi", "/Users/me/.kimi-code/bin/kimi") }, new[] { new AppWindow(70, false) })),
            ("the Kimi chat app on a Mac isn't Kimi Code", () => Expect(TriggerTool.Kimi, AgentPresence.Closed,
                new[] { new Running(1, 0, "launchd"), new Running(70, 1, "Kimi", "/Applications/Kimi.app/Contents/MacOS/Kimi") }, new[] { new AppWindow(70, false) })),
            ("KERN_PROCARGS2 reads as the arguments, and the environment is never touched", () =>
            {
                var bytes = new List<byte>(BitConverter.GetBytes(3));
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes("/usr/local/bin/node\0\0\0"));
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes("node\0/x/@anthropic-ai/claude-code/cli.js\0--flag\0"));
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes("SECRET_TOKEN=abc\0HOME=/Users/me\0"));
                var line = ProcessArguments.Parse(bytes.ToArray(), bytes.Count);
                if (line != "node /x/@anthropic-ai/claude-code/cli.js --flag") throw new Exception("read " + line);
                if (ProcessArguments.Parse(Array.Empty<byte>(), 0) is not null || ProcessArguments.Parse(BitConverter.GetBytes(0), 4) is not null) throw new Exception("empty");
                var cut = bytes.Take(34).ToArray();
                if (ProcessArguments.Parse(cut, cut.Length) is not { } partial || !partial.StartsWith("node", StringComparison.Ordinal)) throw new Exception("a cut-short buffer reads what it holds");
            }),
            ("a window report is used while fresh and ignored when stale", () =>
            {
                var now = DateTimeOffset.UtcNow;
                MacWindowReport.Report(new[] { new AppWindow(5, true) }, now);
                if (MacWindowReport.Current(now.AddSeconds(5)) is not { Count: 1 }) throw new Exception("fresh");
                if (MacWindowReport.Current(now + MacWindowReport.MaxAge + TimeSpan.FromSeconds(1)) is not null) throw new Exception("stale");
                MacWindowReport.Forget();
                if (MacWindowReport.Current(now) is not null) throw new Exception("forgotten");
            }),
            ("on a Mac the live process table lists this process, with its arguments", () =>
            {
                if (OperatingSystem.IsMacOS()) LiveMacTable();
            }),
            ("a CLI under a desktop app in the tray stays in the tray beside a terminal", () => Expect(TriggerTool.Claude, AgentPresence.Hidden,
                new[] { Explorer, new Running(10, 1, "claude"), new Running(11, 10, "claude"), new Running(61, 1, "WindowsTerminal") },
                new[] { new AppWindow(61, false) }, id => id == 11)),
            ("a desktop app in the tray stays in the tray beside a terminal", () => Expect(TriggerTool.DeepSeek, AgentPresence.Hidden,
                new[] { Explorer, new Running(60, 1, "DeepSeek Harness"), new Running(61, 1, "WindowsTerminal") },
                new[] { new AppWindow(61, false) }, ConsolePrograms)),
            ("the PE header tells a console program from a desktop one", () =>
            {
                if (!OperatingSystem.IsWindows()) return;
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (!IsConsoleImage(Path.Combine(system, "cmd.exe"))) throw new Exception("cmd.exe is a console program");
                if (IsConsoleImage(Path.Combine(system, "notepad.exe"))) throw new Exception("notepad.exe is not");
                if (IsConsoleImage(Path.Combine(system, "no-such-program.exe"))) throw new Exception("a missing file is not");
            }),
        };
        foreach (var (name, test) in tests)
        {
            test();
            System.Console.WriteLine($"PASS presence: {name}");
        }
        System.Console.WriteLine("AgentPresenceTests GREEN");
    }

    /// Grok Bot's main process and an Electron helper, under the shell.
    private static Running[] GrokBotTree() =>
        new[] { Explorer, new Running(80, 1, "Grok Bot"), new Running(81, 80, "Grok Bot") };

    /// A CLI in Windows Terminal: the shell is the terminal's child.
    private static Running[] Terminal(string cli) =>
        new[] { Explorer, new Running(18, 1, "WindowsTerminal"), new Running(19, 18, "pwsh"), new Running(20, 19, cli) };

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void LiveMacTable()
    {
        var entries = MacProcesses.Snapshot();
        var me = entries.FirstOrDefault(e => e.Id == Environment.ProcessId) ?? throw new Exception("this process is not in the table");
        if (me.ParentId <= 0 || me.Name.Length == 0 || string.IsNullOrEmpty(me.Path)) throw new Exception($"incomplete: {me.ParentId} {me.Name}");
        var arguments = MacProcesses.CommandLine(Environment.ProcessId);
        if (arguments is null || !arguments.Contains("AgentIsland.Tests", StringComparison.Ordinal)) throw new Exception("arguments: " + arguments);
        if (!AgentPresences.Now().ContainsKey(TriggerTool.Claude)) throw new Exception("presence reads every agent");
    }

    /// A CLI in Terminal on a Mac: launchd, the Dock beside it, Terminal (which owns the windows), login and the shell.
    private static Running[] MacTerminal(string cli) =>
        new[]
        {
            new Running(1, 0, "launchd"), new Running(150, 1, "Dock"), new Running(200, 1, "Terminal"),
            new Running(201, 200, "login"), new Running(202, 201, "zsh"), new Running(203, 202, cli),
        };

    /// A CLI in a classic console: its window is conhost's, the shell's child.
    private static Running[] Console(string cli) =>
        new[] { Explorer, new Running(39, 1, "cmd"), new Running(40, 39, cli), new Running(41, 39, "conhost") };

    /// A CLI started from the Start menu with Windows Terminal as the default
    /// terminal: the shell is Explorer's child and keeps a windowless console
    /// host; the tab lives in a terminal the system started.
    private static Running[] Handoff(string cli) =>
        new[]
        {
            Explorer, new Running(50, 1, "powershell"), new Running(51, 50, cli), new Running(52, 50, "conhost"),
            new Running(59, 0, "svchost"), new Running(61, 59, "WindowsTerminal"), new Running(62, 61, "OpenConsole"),
        };

    private static bool ConsolePrograms(int id) => id is 50 or 51 or 52 or 62;

    private static void Expect(TriggerTool tool, AgentPresence expected, IReadOnlyList<Running> processes, IReadOnlyList<AppWindow> windows,
        Func<int, bool>? isConsole = null)
    {
        var actual = Of(processes, windows, isConsole)[tool];
        if (actual != expected) throw new Exception($"{tool}: expected {expected}, got {actual}");
    }
}
