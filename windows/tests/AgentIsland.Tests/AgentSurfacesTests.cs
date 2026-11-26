using AgentIsland.Core;
using AgentIsland.Model;
using static AgentIsland.Core.AgentPresences;
using static AgentIsland.Core.AgentSurfaces;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 入口判定：每个 agent 的桌面应用和 CLI 哪个开着，防抖，和 Limen 自己起的
/// 进程（登记表）的排除。进程表和命令行都是合成的，形状取自 2026-11-26 在本机看到的。
public static class AgentSurfacesTests
{
    private const string Home = @"C:\Users\me";
    private const string ClaudeApp = @"C:\Program Files\WindowsApps\Claude_6.2_x64__abc\app\Claude.exe";
    private const string ClaudeEmbedded = Home + @"\AppData\Local\Claude-Data\x\claude-code\2.1.330\h\claude.exe";
    private const string ClaudeCli = Home + @"\.local\bin\claude.exe";
    private const string CodexPackage = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1_x64__abc\app\";
    private const string CodexCli = Home + @"\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe";
    private const string CodexDaemon = Home + @"\.codex\packages\app-server-daemon\0.1\codex.exe";
    private const string AntigravityApp = Home + @"\AppData\Local\Programs\antigravity\Antigravity.exe";
    private const string Agy = Home + @"\AppData\Local\agy\bin\agy.exe";
    private const string Grok = Home + @"\.grok\bin\grok.exe";

    private static CommandTraits Line(string? text = "") => CommandTraits.Parse(text);

    private static Running P(int id, int parent, string name, string? path, string? line = "", DateTime? started = null) =>
        new(id, parent, name, path) { Traits = Line(line), Started = started };

    private static readonly DateTime Epoch = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime T(double seconds) => Epoch.AddSeconds(seconds);

    private static IReadOnlySet<AgentSurface> Open(params Running[] processes) => Evaluate(processes).Open;

    private static void Is(IReadOnlySet<AgentSurface> actual, params AgentSurface[] expected)
    {
        if (!actual.SetEquals(expected)) throw new Exception($"expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
    }

    private static Reading Seen(params AgentSurface[] open) => new(open.ToHashSet(), new HashSet<AgentSurface>());

    private static Reading Unsure(params AgentSurface[] unknown) => new(new HashSet<AgentSurface>(), unknown.ToHashSet());

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("命令行：--type=、--import=、-p 各算一个布尔值", () =>
            {
                Expect(Line(@"""C:\a\Claude.exe"" --type=gpu-process --x").Type, "type");
                Expect(Line(@"Antigravity.exe --import=file:///c:/x/register_loader.js").Import, "import");
                Expect(Line(@"claude -p --output-format stream-json").Print, "-p");
                Expect(Line(@"kimi --prompt=hi").Print, "--prompt=");
                Expect(!Line(@"claude -px").Print, "带别的字符的参数不是 -p");
                Expect(!Line(null).Read && Line("x").Read, "读不到命令行和读到了要分得清");
            }),
            ("命令行：子命令：grok 取第一个不带 - 的参数，codex 看第一个 -- 之前的独立参数", () =>
            {
                Expect(Line(@"codex.exe exec ""fix it""").Exec, "exec");
                Expect(Line(@"codex.exe app-server --managed-daemon").Daemon, "app-server");
                Expect(Line(@"codex.exe daemon pid-update-loop").Daemon, "daemon");
                Expect(Line(@"grok.exe update --trigger=auto_background").Update, "update");
                Expect(Line(@"codex.exe ""exec""").Exec, "引号里的子命令照样是子命令");
                Expect(Line(@"codex.exe -m o3 exec ""fix it""").Exec, "带值选项在前，exec 照样是子命令");
                Expect(Line(@"codex.exe -c k=v app-server").Daemon, "-c k=v 在前，app-server 照样是子命令");
                Expect(Line(@"codex.exe --model o3 e hi").Exec, "e 是 exec 的别名");
                Expect(!Line(@"codex.exe -m exec").Exec, "带值选项的值叫 exec，是值，不是子命令");
                Expect(!Line(@"codex.exe -c daemon").Daemon, "-c 的值叫 daemon，也是值");
                Expect(!Line(@"codex.exe -- exec").Exec, "-- 之后都是参数");
                Expect(!Line(@"codex.exe ""explain exec""").Exec, "一个参数里含 exec 不算");
                Expect(!Line(@"""C:\Program Files\x\codex.exe""").Exec, "带空格的 argv0 不是子命令");
                Expect(!Line(@"grok.exe chat update").Update, "grok 的 update 只看第一个不带 - 的参数");
            }),
            ("Claude：桌面应用主进程算应用；辅助进程和 Claude-Data 下的不算", () =>
            {
                Is(Open(P(1, 0, "Claude", ClaudeApp)), AgentSurface.ClaudeApp);
                Is(Open(P(1, 0, "Claude", ClaudeApp, "Claude.exe --type=renderer"), P(2, 1, "claude", ClaudeEmbedded)));
            }),
            ("Claude：独立 CLI 算 CLI，-p 无头运行不算", () =>
            {
                Is(Open(P(1, 0, "claude", ClaudeCli, "claude")), AgentSurface.ClaudeCode);
                Is(Open(P(1, 0, "claude", ClaudeCli, "claude -p --output-format stream-json")));
            }),
            ("Claude：node 跑的 npm 版 Claude Code 算 CLI", () =>
            {
                var line = @"""C:\Program Files\nodejs\node.exe"" ""C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js""";
                Is(Evaluate(new[] { new Running(1, 0, "node", line) { Traits = Line(line) } }).Open, AgentSurface.ClaudeCode);
                Is(Evaluate(new[] { new Running(1, 0, "node", "node.exe server.js") { Traits = Line("node.exe server.js") } }).Open);
            }),
            ("Codex：桌面应用是包里的 ChatGPT 或 Codex 主进程，extension-host 和包里的 codex 单独不算", () =>
            {
                Is(Open(P(1, 0, "Codex", CodexPackage + "Codex.exe")), AgentSurface.CodexApp);
                Is(Open(P(1, 0, "ChatGPT", CodexPackage + "ChatGPT.exe")), AgentSurface.CodexApp);
                Is(Open(P(1, 0, "ChatGPT", CodexPackage + "ChatGPT.exe", "ChatGPT.exe --type=gpu-process")));
                Is(Open(P(1, 0, "extension-host", CodexPackage + @"resources\plugins\extension-host.exe")));
                Is(Open(P(1, 0, "codex", CodexPackage + @"resources\codex.exe")));
            }),
            ("Codex：CLI 本体算 CLI；守护进程残留和 exec 不算", () =>
            {
                Is(Open(P(1, 0, "codex", CodexCli, "codex")), AgentSurface.CodexCli);
                Is(Open(P(1, 0, "codex", CodexDaemon, "codex.exe app-server --managed-daemon")));
                // 不在守护进程目录下、但跑的是 app-server（比如用户自己 `codex app-server`）：命令行这一道照样挡住。
                Is(Open(P(1, 0, "codex", CodexCli, "codex app-server")));
                // 在守护进程目录下、命令行读不出子命令：目录这一道照样挡住。
                Is(Open(P(1, 0, "codex", CodexDaemon, "codex")));
                Is(Open(P(1, 0, "codex", CodexDaemon, "codex.exe daemon pid-update-loop")));
                Is(Open(P(1, 0, "codex", CodexCli, "codex exec hi")));
            }),
            ("Antigravity：主进程算应用，sidecar 和辅助进程不算", () =>
            {
                Is(Open(P(1, 0, "Antigravity", AntigravityApp, "Antigravity.exe")), AgentSurface.AntigravityApp);
                Is(Open(P(1, 0, "Antigravity", AntigravityApp, "Antigravity.exe --import=file:///c:/x/sidecar_sdk/register_loader.js")));
                Is(Open(P(1, 0, "Antigravity", AntigravityApp, "Antigravity.exe --type=utility")));
                Is(Open(P(1, 0, "language_server", Home + @"\AppData\Local\Programs\antigravity\resources\bin\language_server.exe")));
            }),
            ("Antigravity：agy CLI 算 CLI，和应用分开", () =>
            {
                Is(Open(P(1, 0, "agy", Agy, "agy")), AgentSurface.AgyCli);
                Is(Open(P(1, 0, "Antigravity", Home + @"\AppData\Local\agy\bin\antigravity.exe", "x")), AgentSurface.AgyCli);
                Is(Open(P(1, 0, "agy", Agy), P(2, 0, "Antigravity", AntigravityApp)), AgentSurface.AgyCli, AgentSurface.AntigravityApp);
            }),
            ("Grok：Grok Bot、grok CLI、Cursor 各是一个入口；grok update 子进程不单独算", () =>
            {
                Is(Open(new Running(1, 0, "Grok Bot")), AgentSurface.GrokBot);
                Is(Open(P(1, 0, "grok", Grok, "grok")), AgentSurface.GrokCli);
                Is(Open(P(1, 0, "cursor", @"C:\Program Files\cursor\Cursor.exe", "Cursor.exe")), AgentSurface.Cursor);
                Is(Open(P(1, 0, "cursor", @"C:\Program Files\cursor\Cursor.exe", "Cursor.exe --type=renderer")));
                Is(Open(P(2, 1, "grok", Grok, "grok.exe update --trigger=auto_background")));
                Is(Open(P(1, 0, "grok", Grok, "grok"), P(2, 1, "grok", Grok, "grok.exe update --trigger=auto_background")), AgentSurface.GrokCli);
            }),
            ("Kimi：Kimi Code 桌面应用和 .kimi-code 下的 CLI；-p 无头运行不算", () =>
            {
                Is(Open(P(1, 0, "Kimi Code", Home + @"\AppData\Local\Kimi Code\Kimi Code.exe", "Kimi Code.exe")), AgentSurface.KimiApp);
                Is(Open(P(1, 0, "kimi", Home + @"\.kimi-code\bin\kimi.exe", "kimi")), AgentSurface.KimiCli);
                Is(Open(P(1, 0, "kimi", Home + @"\.kimi-code\bin\kimi.exe", "kimi -p hi")));
                Is(Open(P(1, 0, "kimi", @"C:\Program Files\Kimi\kimi.exe", "kimi")));
            }),
            ("无法判定：缺判定要的关键条件，且没有别的进程证明开着", () =>
            {
                Is(Evaluate(new[] { new Running(1, 0, "claude", null) { Traits = Line() } }).Unknown, AgentSurface.ClaudeApp, AgentSurface.ClaudeCode);
                Is(Evaluate(new[] { new Running(1, 0, "claude", ClaudeApp) }).Unknown, AgentSurface.ClaudeApp, AgentSurface.ClaudeCode);
                Is(Evaluate(new[] { new Running(1, 0, "claude", null) { Traits = Line() }, P(2, 0, "claude", ClaudeApp) }).Unknown, AgentSurface.ClaudeCode);
                Is(Evaluate(new[] { new Running(1, 0, "grok", Grok) }).Unknown, AgentSurface.GrokCli);
                // 读不到命令行的 node/bun 可能是 npm 版 Claude Code（管理员终端里跑的），记为说不准，不当成关着。
                Is(Evaluate(new[] { new Running(1, 0, "node", null) }).Unknown, AgentSurface.ClaudeCode);
                Is(Evaluate(new[] { new Running(1, 0, "bun", null) }).Open);
                var npmLine = @"node C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js";
                Is(Evaluate(new[] { new Running(1, 0, "node", null), new Running(2, 0, "node", npmLine) { Traits = Line(npmLine) } }).Unknown);
                Is(Evaluate(new[] { new Running(1, 0, "codex", CodexPackage + @"resources\codex.exe") }).Unknown);
            }),
            ("无法判定：只靠命令行的入口读不到路径，照常判定", () =>
            {
                var none = (string?)null;
                var of = Evaluate(new[]
                {
                    P(1, 0, "grok", none, "grok"), P(2, 0, "cursor", none, "Cursor.exe"),
                    P(3, 0, "Kimi Code", none, "Kimi Code.exe"), new Running(4, 0, "agy", none),
                });
                Is(of.Open, AgentSurface.GrokCli, AgentSurface.Cursor, AgentSurface.KimiApp, AgentSurface.AgyCli);
                Is(of.Unknown);
                Is(Evaluate(new[] { P(1, 0, "cursor", none, "Cursor.exe --type=gpu-process") }).Open);
            }),
            ("防抖：连续两次一致才切换", () =>
            {
                var tracker = new Tracker();
                tracker.Observe(Seen());
                Expect(!tracker.Observe(Seen(AgentSurface.GrokCli)), "第一次看到，还不切换");
                Is(tracker.Current);
                Expect(tracker.Observe(Seen(AgentSurface.GrokCli)), "第二次一致，切换");
                Is(tracker.Current, AgentSurface.GrokCli);
                tracker.Observe(Seen());
                Is(tracker.Current, AgentSurface.GrokCli);
                tracker.Observe(Seen(AgentSurface.GrokCli));
                tracker.Observe(Seen());
                Is(tracker.Current, AgentSurface.GrokCli);
                Expect(tracker.Observe(Seen()), "连续两次没有，关掉");
                Is(tracker.Current);
            }),
            ("防抖：第一次扫描直接采用；各入口互不影响", () =>
            {
                var tracker = new Tracker();
                tracker.Observe(Seen(AgentSurface.ClaudeApp));
                Is(tracker.Current, AgentSurface.ClaudeApp);
                tracker.Observe(Seen(AgentSurface.ClaudeApp, AgentSurface.CodexCli));
                tracker.Observe(Seen(AgentSurface.ClaudeApp, AgentSurface.CodexCli));
                Is(tracker.Current, AgentSurface.ClaudeApp, AgentSurface.CodexCli);
            }),
            ("无法判定：保持上一次的判定，也不算一次变化", () =>
            {
                var tracker = new Tracker();
                tracker.Observe(Seen(AgentSurface.ClaudeCode));
                for (var i = 0; i < 3; i++) Expect(!tracker.Observe(Unsure(AgentSurface.ClaudeCode)), "读不到不算变化");
                Is(tracker.Current, AgentSurface.ClaudeCode);
            }),
            ("无法判定：不打断正在积累的切换", () =>
            {
                var tracker = new Tracker();
                tracker.Observe(Seen(AgentSurface.ClaudeCode));
                tracker.Observe(Seen());
                tracker.Observe(Unsure(AgentSurface.ClaudeCode));
                Is(tracker.Current, AgentSurface.ClaudeCode);
                Expect(tracker.Observe(Seen()), "中间夹一次读不到，第二次没有照样切换");
                Is(tracker.Current);
            }),
            ("首轮无法判定：不当成关着，标为未知，读到后直接采用", () =>
            {
                var tracker = new Tracker();
                tracker.Observe(Unsure(AgentSurface.ClaudeApp));
                Is(tracker.Current);
                Is(tracker.Unknown, AgentSurface.ClaudeApp);
                tracker.Observe(Unsure(AgentSurface.ClaudeApp));
                Is(tracker.Current);
                Is(tracker.Unknown, AgentSurface.ClaudeApp);
                Expect(tracker.Observe(Seen(AgentSurface.ClaudeApp)), "第一次真正读到，直接采用，不用等第二次");
                Is(tracker.Current, AgentSurface.ClaudeApp);
                Is(tracker.Unknown);
                var closed = new Tracker();
                closed.Observe(Unsure(AgentSurface.CodexCli));
                closed.Observe(Seen());
                Is(closed.Unknown);
                Is(closed.Current);
            }),
            ("登记表：登记的进程和它的子孙都被排除，别的进程不受影响", () =>
            {
                var table = new[]
                {
                    new Running(1, 0, "explorer") { Started = T(-500) }, new Running(10, 1, "claude") { Started = T(0) },
                    new Running(11, 10, "node") { Started = T(1) }, new Running(12, 11, "conhost") { Started = T(2) },
                    new Running(20, 1, "claude") { Started = T(3) },
                };
                var gone = OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = T(0) }, table);
                if (!gone.SetEquals(new[] { 10, 11, 12 })) throw new Exception($"got [{string.Join(",", gone)}]");
                Expect(OwnProcesses.Excluded(new Dictionary<int, DateTime?>(), table).Count, 0);
            }),
            ("登记表：ParentId 指向登记 PID、但创建时间更早的进程不算子孙（PID 被复用）", () =>
            {
                // userinit 早退出了，explorer 的 ParentId 还指着它原来的 PID；探测进程恰好拿到这个 PID。
                var table = new[]
                {
                    new Running(10, 1, "svc") { Started = T(0) }, new Running(30, 10, "explorer") { Started = T(-1000) },
                    new Running(31, 30, "claude") { Started = T(-900) },
                    new Running(11, 10, "child") { Started = T(1) }, new Running(12, 11, "grandchild") { Started = T(2) },
                    new Running(13, 11, "older") { Started = T(0.5) },
                };
                var gone = OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = T(0) }, table);
                if (!gone.SetEquals(new[] { 10, 11, 12 })) throw new Exception($"got [{string.Join(",", gone)}]");
                Expect(!gone.Contains(30) && !gone.Contains(31), "explorer 和它的子进程不能被排除");
            }),
            ("登记表：创建时间读不到的不算子孙；只能按需去读的用 startOf", () =>
            {
                var table = new[] { new Running(11, 10, "child"), new Running(12, 11, "grandchild") };
                Expect(OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = T(0) }, table).Count, 1);
                Expect(OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = null }, table, _ => T(5)).Count, 1);
                var gone = OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = T(0) }, table, id => id == 11 ? T(1) : null);
                if (!gone.SetEquals(new[] { 10, 11 })) throw new Exception($"got [{string.Join(",", gone)}]");
                Expect(OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = T(0) }, table, _ => T(1)).Count, 3);
            }),
            ("登记表：父子成环不会死循环", () =>
            {
                var table = new[] { new Running(10, 11, "a") { Started = T(1) }, new Running(11, 10, "b") { Started = T(2) } };
                Expect(OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = T(0) }, table).Count, 2);
            }),
            ("登记表：配对注销，重复登记同一 PID 要注销两次", () =>
            {
                const int pid = 987001;
                var first = OwnProcesses.Register(pid, T(0));
                var second = OwnProcesses.Register(pid, T(0));
                Expect(OwnProcesses.Contains(pid), "登记后在表里");
                first.Dispose();
                first.Dispose();
                Expect(OwnProcesses.Contains(pid), "重复 Dispose 同一个登记只注销一次");
                second.Dispose();
                Expect(!OwnProcesses.Contains(pid), "注销完就不在了");
            }),
            ("登记表：Without 把探测进程从进程表里拿掉，入口判定看不到它", () =>
            {
                var table = new[]
                {
                    P(10, 1, "claude", ClaudeCli, "claude", T(0)), P(11, 10, "claude", ClaudeCli, "claude", T(1)),
                    P(20, 1, "grok", Grok, "grok", T(-5)),
                };
                using (OwnProcesses.Register(10, T(0)))
                {
                    Is(Evaluate(OwnProcesses.Without(table)).Open, AgentSurface.GrokCli);
                }
                Is(Evaluate(OwnProcesses.Without(table)).Open, AgentSurface.ClaudeCode, AgentSurface.GrokCli);
            }),
            ("登记表：探测进程不让 Claude 变成开着（Presence）", () =>
            {
                var table = new[]
                {
                    new Running(1, 0, "explorer") { Started = T(-9) }, new Running(10, 1, "claude") { Started = T(0) },
                    new Running(11, 10, "claude") { Started = T(1) },
                };
                using (OwnProcesses.Register(10, T(0)))
                {
                    Expect(Of(OwnProcesses.Without(table), new AppWindow[0])[TriggerTool.Claude], AgentPresence.Closed);
                }
                Expect(Of(OwnProcesses.Without(table), new AppWindow[0])[TriggerTool.Claude] != AgentPresence.Closed, "不登记就算开着");
            }),
            ("Gone：登记的进程和它的子孙不算 agent 还活着", () =>
            {
                var running = new[] { (10, "claude", (string?)null), (11, "claude", (string?)null), (20, "grok", (string?)null) };
                var gone = AgentProcesses.Gone(running, new HashSet<int> { 10, 11 });
                Expect(gone.Contains(TriggerTool.Claude), "只剩探测进程：Claude 算没了");
                Expect(!gone.Contains(TriggerTool.Grok), "别人的进程照常");
                Expect(!AgentProcesses.Gone(running, new HashSet<int>()).Contains(TriggerTool.Claude), "不排除就算活着");
                var table = new[] { new Running(10, 1, "claude") { Started = T(0) }, new Running(11, 10, "claude") { Started = T(1) }, new Running(20, 1, "grok") { Started = T(-1) } };
                var own = OwnProcesses.Excluded(new Dictionary<int, DateTime?> { [10] = T(0) }, table);
                Expect(AgentProcesses.Gone(running, own).Contains(TriggerTool.Claude), "排除集合是登记进程加子孙");
            }),
            ("WindowsJobProcess.Run：子进程起来就登记，结束就注销", () =>
            {
                if (!OperatingSystem.IsWindows()) return;
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = "/d /c echo ok",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var during = -1;
                var result = AgentIsland.Usage.WindowsJobProcess.RunInSingleProcessJob(start, TimeSpan.FromSeconds(5), launch =>
                {
                    var ok = launch();
                    during = OwnProcesses.Snapshot().Count;
                    return ok;
                });
                Expect(result, "ok");
                Expect(during, 1);
                Expect(OwnProcesses.Snapshot().Count, 0);
            }),
        };
        foreach (var (name, test) in tests)
        {
            test();
            System.Console.WriteLine($"PASS surfaces: {name}");
        }
        System.Console.WriteLine("AgentSurfacesTests GREEN");
    }
}
