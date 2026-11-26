using AgentIsland.Model;

namespace AgentIsland.Core;

/// 一个 agent 的一个入口：桌面应用或 CLI。哪个入口开着，就用哪个入口的
/// 登录刷新额度。Cursor 按 Grok Build 的桌面客户端算，所以 Grok 有三个入口。
public enum AgentSurface
{
    ClaudeApp,
    ClaudeCode,
    CodexApp,
    CodexCli,
    AntigravityApp,
    AgyCli,
    GrokBot,
    GrokCli,
    Cursor,
    KimiApp,
    KimiCli,
}

/// 按进程表判定每个入口开没开。判定是纯函数（Evaluate），防抖在 Tracker 里。
/// 只读 Windows 的进程表；Mac 上没有读数，Current 一直是空的。
public static class AgentSurfaces
{
    /// 命令行里判定要用的几个布尔值。命令行里可能有 token 或用户的提示词，
    /// 所以解析完整行就丢，不保存、不记录。Read 为 false 表示没读到命令行。
    private static readonly HashSet<string> TakesValue = new(StringComparer.Ordinal)
    {
        "-m", "--model", "-c", "--config", "-p", "--profile", "-C", "--cd", "-s", "--sandbox",
        "-a", "--ask-for-approval", "-i", "--image", "--color", "--local-provider",
    };

    internal readonly record struct CommandTraits(bool Read, bool Type, bool Import, bool Print, bool Exec, bool Daemon, bool Update)
    {
        /// Type：Electron 的辅助进程（--type=）。Import：Antigravity 的 sidecar（--import=）。
        /// Print：无头运行（-p、--print、--prompt）。Exec：codex exec。
        /// Daemon：codex 的 app-server 或 daemon 子命令、--managed-daemon。Update：grok update。
        /// 子命令取第一个不以 - 开头的参数（argv0 之后）。
        internal static CommandTraits Parse(string? commandLine)
        {
            if (commandLine is null) return default;
            bool type = false, import = false, print = false, exec = false, daemon = false, update = false;
            var index = 0;
            var updateSeen = false;
            var afterDashes = false;
            var previous = "";
            void Token(string token)
            {
                var first = index++ == 0;
                var value = previous;
                previous = token;
                if (token.StartsWith("--type=", StringComparison.Ordinal)) type = true;
                else if (token == "--import" || token.StartsWith("--import=", StringComparison.Ordinal)) import = true;
                else if (token is "-p" or "--print" or "--prompt" || token.StartsWith("--prompt=", StringComparison.Ordinal)) print = true;
                else if (token == "--managed-daemon") daemon = true;
                if (first) return;
                if (token == "--") afterDashes = true;
                if (token.StartsWith('-')) return;
                // grok update：第一个不带 - 的参数。
                if (!updateSeen)
                {
                    updateSeen = true;
                    if (string.Equals(token, "update", StringComparison.OrdinalIgnoreCase)) update = true;
                }
                // codex 的子命令：第一个 -- 之前、独立的 exec、e、app-server、daemon。
                // 前面可以有 -m o3、-c k=v 这类带值选项，所以不能只看第一个参数；
                // 带值选项后面的值正好叫这几个词时，是值，不是子命令。
                if (afterDashes || TakesValue.Contains(value)) return;
                switch (token.ToLowerInvariant())
                {
                    case "exec" or "e": exec = true; break;
                    case "app-server" or "daemon": daemon = true; break;
                }
            }
            var current = new System.Text.StringBuilder();
            var quoted = false;
            var started = false;
            void End()
            {
                if (started) Token(current.ToString());
                current.Clear();
                started = false;
            }
            for (var i = 0; i < commandLine.Length; i++)
            {
                var c = commandLine[i];
                if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    started = true;
                    i++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                    started = true;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    End();
                }
                else
                {
                    current.Append(c);
                    started = true;
                }
            }
            End();
            return new CommandTraits(true, type, import, print, exec, daemon, update);
        }
    }

    /// 一轮扫描的结果：判定开着的入口，以及读不到路径或命令行、无法判定的入口。
    internal sealed record Reading(IReadOnlySet<AgentSurface> Open, IReadOnlySet<AgentSurface> Unknown);

    private const string ClaudeAppFolder = @"\WindowsApps\Claude_";
    private const string ClaudeDataFolder = @"\Claude-Data\";
    private const string CodexPackageFolder = @"\WindowsApps\OpenAI.Codex_";
    private const string CodexDaemonFolder = @"\.codex\packages\app-server-daemon\";
    private const string AgyFolder = @"\agy\";
    private const string KimiCodeFolder = ".kimi-code";

    // 进程名可能是哪些入口；同名的靠路径和命令行再分。
    private static readonly Dictionary<string, AgentSurface[]> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = [AgentSurface.ClaudeApp, AgentSurface.ClaudeCode],
        ["ChatGPT"] = [AgentSurface.CodexApp],
        ["codex"] = [AgentSurface.CodexApp, AgentSurface.CodexCli],
        ["Antigravity"] = [AgentSurface.AntigravityApp, AgentSurface.AgyCli],
        ["agy"] = [AgentSurface.AgyCli],
        ["grok"] = [AgentSurface.GrokCli],
        [GrokBot.ProcessName] = [AgentSurface.GrokBot],
        ["cursor"] = [AgentSurface.Cursor],
        ["Kimi Code"] = [AgentSurface.KimiApp],
        ["kimi"] = [AgentSurface.KimiCli],
    };

    /// Windows 上读路径和命令行的名字：判定要用它们。Grok Bot 只看名字。
    internal static bool Wants(string name) => ByName.ContainsKey(name);

    /// 纯函数：进程表里每个入口开没开。调用前应已去掉 Limen 自己起的进程。
    /// 判定缺一个关键条件（路径、或 --type= 这类命令行标志）的进程，证明不了入口开着，
    /// 也证明不了没开；只在没有别的进程证明开着时，才把入口记为“无法判定”。
    /// 不依赖的条件读不到不要紧：只看命令行的入口（grok、cursor、Kimi Code）不要路径，
    /// 只看名字的（agy、Grok Bot）什么都不要。
    internal static Reading Evaluate(IEnumerable<AgentPresences.Running> processes)
    {
        var open = new HashSet<AgentSurface>();
        var unknown = new HashSet<AgentSurface>();
        foreach (var process in processes)
        {
            var (surface, missing) = Classify(process);
            if (surface is { } found) open.Add(found);
            else if (missing && ByName.TryGetValue(process.Name, out var candidates)) unknown.UnionWith(candidates);
            else if (IsClaudeCodeScript(process) && !process.Traits.Print) open.Add(AgentSurface.ClaudeCode);
            // 读不到命令行的 node/bun 可能是 npm 版 Claude Code（比如在管理员终端里跑的），说不准。
            else if (process.Path is null && AgentProcesses.Hosts.Contains(process.Name)) unknown.Add(AgentSurface.ClaudeCode);
        }
        unknown.ExceptWith(open);
        return new Reading(open, unknown);
    }

    // node 或 bun 跑的 Claude Code：Path 里放的是命令行（AgentProcesses.Hosts）。
    private static bool IsClaudeCodeScript(AgentPresences.Running process) =>
        process.Path is not null
        && AgentProcesses.Hosts.Contains(process.Name)
        && AgentProcesses.Known.Any(k => k.Tool == TriggerTool.Claude && k.PathHint is not null && AgentProcesses.Matches(k, process.Name, process.Path));

    private static bool In(string path, string folder) => path.Replace('/', '\\').Contains(folder, StringComparison.OrdinalIgnoreCase);

    // 不用 Path.GetDirectoryName：测试在 Mac 上也跑，那里反斜杠不是分隔符。
    private static bool InAppFolder(string path)
    {
        var folder = path.Replace('/', '\\');
        var cut = folder.LastIndexOf('\\');
        return cut > 0 && folder[..cut].EndsWith(@"\app", StringComparison.OrdinalIgnoreCase);
    }

    /// 一个进程算哪个入口；Missing 为 true 表示判定要的路径或命令行没读到，说不准。
    private static (AgentSurface? Surface, bool Missing) Classify(AgentPresences.Running process)
    {
        const bool Missing = true;
        if (string.Equals(process.Name, GrokBot.ProcessName, StringComparison.OrdinalIgnoreCase)) return (AgentSurface.GrokBot, false);
        var t = process.Traits;
        var path = process.Path;
        var noLine = !t.Read;
        switch (process.Name.ToLowerInvariant())
        {
            case "claude":
                if (path is null || noLine) return (null, Missing);
                if (In(path, ClaudeAppFolder)) return (t.Type ? null : AgentSurface.ClaudeApp, false);
                // 桌面应用自己拉起的 claude-code 在 Claude-Data 下，不是独立的 CLI。
                return (In(path, ClaudeDataFolder) || t.Print ? null : AgentSurface.ClaudeCode, false);
            case "chatgpt":
                if (path is null) return (null, Missing);
                if (!In(path, CodexPackageFolder) || !InAppFolder(path)) return (null, false);
                return noLine ? (null, Missing) : (t.Type ? null : AgentSurface.CodexApp, false);
            case "codex":
                if (path is null) return (null, Missing);
                // 包里 app 目录下的 Codex.exe 是桌面应用主进程；包里别处的 codex（resources 下）是它的辅助进程。
                if (In(path, CodexPackageFolder))
                {
                    if (!InAppFolder(path)) return (null, false);
                    return noLine ? (null, Missing) : (t.Type ? null : AgentSurface.CodexApp, false);
                }
                // .codex 下的守护进程是 CLI 退出后的残留。
                if (In(path, CodexDaemonFolder)) return (null, false);
                return noLine ? (null, Missing) : (t.Daemon || t.Exec ? null : AgentSurface.CodexCli, false);
            case "antigravity":
                if (path is null) return (null, Missing);
                if (In(path, AgyFolder)) return (AgentSurface.AgyCli, false);
                // language_server 用 Antigravity.exe 跑的 sidecar（--import=）有时比父进程活得久。
                return noLine ? (null, Missing) : (t.Type || t.Import ? null : AgentSurface.AntigravityApp, false);
            case "agy":
                return (AgentSurface.AgyCli, false);
            case "grok":
                // 启动时的 grok update 子进程不单独算一个入口。
                return noLine ? (null, Missing) : (t.Update ? null : AgentSurface.GrokCli, false);
            case "cursor":
                return noLine ? (null, Missing) : (t.Type ? null : AgentSurface.Cursor, false);
            case "kimi code":
                return noLine ? (null, Missing) : (t.Type ? null : AgentSurface.KimiApp, false);
            case "kimi":
                if (path is null) return (null, Missing);
                if (!In(path, KimiCodeFolder) || process.Headless) return (null, false);
                return noLine ? (null, Missing) : (t.Print ? null : AgentSurface.KimiCli, false);
            default:
                return (null, false);
        }
    }

    /// 读现在的进程表（node 和 bun 不在 ByName 里，它们的 Path 已经是命令行）。Limen 自己起的进程和它们的子孙已排除；读不了就返回 null。
    internal static Reading? ReadNow()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var table = OwnProcesses.Without(AgentPresences.Processes()).Select(process =>
            {
                if (!Wants(process.Name)) return process;
                var line = AgentProcesses.CommandLineOf(process.Id);
                return process with { Path = AgentPresences.PathOf(process.Id), Traits = CommandTraits.Parse(line) };
            }).ToList();
            return Evaluate(table.Select(p => AgentProcesses.Hosts.Contains(p.Name) ? p with { Traits = CommandTraits.Parse(p.Path) } : p));
        }
        catch
        {
            return null;
        }
    }

    /// 防抖：连续两次扫描结果一致，入口才切换。
    /// - 第一次扫描直接采用，没有可比的上一次。
    /// - 无法判定的入口不下结论：保持上一次的判定，也不打断它正在积累的切换（pending）。
    /// - 第一次扫描就无法判定的入口，先不出现在 Current 里，标为“未知”（Unknown），
    ///   等第一次真正读到再直接采用。
    internal sealed class Tracker
    {
        private readonly object _gate = new();
        private HashSet<AgentSurface> _current = new();
        private HashSet<AgentSurface> _undecided = new();
        private readonly HashSet<AgentSurface> _pending = new();
        private bool _seen;

        public IReadOnlySet<AgentSurface> Current
        {
            get
            {
                lock (_gate) return _current;
            }
        }

        /// 还没有读到过确定结果的入口。
        public IReadOnlySet<AgentSurface> Unknown
        {
            get
            {
                lock (_gate) return _undecided;
            }
        }

        /// 返回这一轮有没有入口切换。集合都是整体替换，读的一方拿到的不会再变。
        public bool Observe(Reading reading)
        {
            lock (_gate)
            {
                var next = new HashSet<AgentSurface>(_current);
                var undecided = new HashSet<AgentSurface>(_undecided);
                foreach (var surface in Enum.GetValues<AgentSurface>())
                {
                    var unknown = reading.Unknown.Contains(surface);
                    var raw = reading.Open.Contains(surface);
                    if (!_seen)
                    {
                        if (unknown) undecided.Add(surface);
                        else if (raw) next.Add(surface);
                    }
                    else if (unknown)
                    {
                        continue;
                    }
                    else if (undecided.Remove(surface))
                    {
                        if (raw) next.Add(surface);
                    }
                    else if (raw == _current.Contains(surface)) _pending.Remove(surface);
                    else if (_pending.Remove(surface))
                    {
                        if (raw) next.Add(surface);
                        else next.Remove(surface);
                    }
                    else _pending.Add(surface);
                }
                _seen = true;
                var changed = !next.SetEquals(_current);
                _current = next;
                _undecided = undecided;
                return changed;
            }
        }
    }

    internal static Tracker Shared { get; } = new();

    /// 现在开着的入口（已防抖）。还没有读数、或在 Mac 上，是空的。
    public static IReadOnlySet<AgentSurface> Current => Shared.Current;

    /// 还没读到过确定结果的入口（第一次扫描就读不到的）。
    public static IReadOnlySet<AgentSurface> Undecided => Shared.Unknown;

    public static bool IsOpen(AgentSurface surface) => Current.Contains(surface);
}
