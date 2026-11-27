using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Windows 上 Claude 额度的读法：起一个 `claude`，走 stream-json 控制协议，发 initialize 和
/// get_usage 两条请求，不发任何对话，不碰 token、不自己刷新登录（4b 步骤 3，方案见
/// notes/quota-by-entry-plan-v2.md 第 2、8、9、12 节）。
///
/// 探测进程：
/// - 放进 Job Object（KILL_ON_JOB_CLOSE），超时或结束都杀整棵进程树；
/// - 用 OwnProcesses 登记，不会被判成“Claude 开着”；
/// - 环境用白名单构造，不继承 Limen 自己的环境；
/// - 不得打开浏览器：BROWSER 指向一个立刻退出的程序，ActiveProcessLimit=1 时连子进程都起不来。
///
/// 取舍（用户 11/26 定）：登录已经失效时，由 Claude Code 自己清掉，Limen 不读凭证文件。
/// - 刷新被服务器明确拒绝时，claude 会清空 .credentials.json 里的 token。这是 Claude Code
///   自己的行为，用户自己打开 Claude Code 也会这样；
/// - 只有 Claude 开着时才探测；
/// - Limen 不读凭证文件，所以不预先判断 token 有没有过期；
/// - 网络不通时 claude 不会改动这个文件（可选的集成测试固定了这一点）。
///
/// 结果都落在 AppUsage 上，说明文字是英文标识，显示时由 ErrorDisplay 翻译。这些文字
/// 不能含 auth、login、rate limited：ActivityMonitor 会按子串把它们判成“需要登录”或“限流”。
public static class ClaudeUsageProbe
{
    /// 有账号，但这个账号没有订阅额度。不是需要登录。
    public const string NoSubscriptionMessage = "This account has no subscription quota";

    /// Claude Code 里没有登录的账号（或在用 API key）。不是需要登录。
    public const string NoAccountMessage = "Claude Code has no signed-in account (or an API key is in use)";

    /// 找不到 claude。不是需要登录。
    public const string NotInstalledMessage = "Install Claude Code and sign in once to see quota";

    /// Mac 上 Claude 额度暂未启用：不是故障，也不要求重新登录。
    public const string MacNotEnabledMessage = "Claude quota is not enabled on Mac yet";

    /// 额度接口可用但这次没取到（多半是 token 过期而 Claude Code 自己刷新失败）。
    public const string NoReadingMessage = "No quota this time; sign in again from Claude Code";

    /// 超时、进程起不来、回复解析失败。
    public const string NoResponseMessage = "Claude Code returned no quota (it may need an update)";

    /// 这一次没有探测（上一个被取消的还在自己收尾）。不是结果，调用方当作没取。
    public static readonly AppUsage Skipped = AppUsage.ErrorPair("probe skipped");

    /// 拿到额度后，等进程自己退出最多这么久；再不退就杀整棵树，结果照常返回。
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private const string SettingsFileName = "claude-probe-settings.json";
    private const string SettingsContent = "{\"disableAllHooks\":true}";

    // 来自 notes/tools/claude-probe/probe.mjs 的 KEEP_ENV，外加 CLAUDE_CONFIG_DIR：
    // Limen 自己读 Claude 的登录和会话时认它（IslandPaths），探测要读同一个账号。
    private static readonly string[] KeptEnvironment =
    [
        "SystemRoot", "windir", "PATH", "PATHEXT", "ComSpec", "SystemDrive", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "ProgramData", "ProgramFiles",
        "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "OS",
        "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "COMPUTERNAME", "USERNAME", "USERDOMAIN",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
        "ALL_PROXY", "all_proxy", "HOME", "CLAUDE_CODE_GIT_BASH_PATH",
        // 证书路径，不是凭证：企业代理或自签证书环境里 claude 要靠它们联网。
        "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE",
        "CLAUDE_CONFIG_DIR",
    ];

    private static readonly (string Name, string Value)[] ForcedEnvironment =
    [
        ("DISABLE_AUTOUPDATER", "1"),
        ("CLAUDE_CODE_ENTRYPOINT", "sdk-cli"),
        ("DISABLE_TELEMETRY", "1"),
        ("DISABLE_ERROR_REPORTING", "1"),
        ("ENABLE_CLAUDEAI_MCP_SERVERS", "false"),
        ("CLAUDE_CODE_AUTO_CONNECT_IDE", "false"),
        ("CLAUDE_CODE_IDE_SKIP_AUTO_INSTALL", "1"),
    ];

    private const string InitializeRequest =
        "{\"type\":\"control_request\",\"request_id\":\"init-1\",\"request\":{\"subtype\":\"initialize\",\"hooks\":null}}";

    private const string UsageRequest =
        "{\"type\":\"control_request\",\"request_id\":\"usage-1\",\"request\":{\"subtype\":\"get_usage\",\"skip_behaviors\":true}}";

    // MARK: - Test seams

    /// claude 的位置；找不到返回 null。
    internal static Func<string?> Locate { get; set; } =
        () => ChooseClaude(Trigger.CLILocator.PathFor(TriggerTool.Claude), IslandPaths.Home, File.Exists);

    /// 优先用原生的 ~\.local\bin\claude.exe（npm 版和原生版都装了时，.cmd 垫片要多起 cmd 和 node，
    /// 也不能限制进程数）。.ps1 起不了，当作找不到。
    internal static string? ChooseClaude(string? located, string home, Func<string, bool> exists)
    {
        var native = Path.Combine(home, ".local", "bin", "claude.exe");
        if (exists(native)) return native;
        if (located is null || located.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return null;
        return located;
    }

    /// 工作目录和 settings 文件所在的目录。
    internal static Func<string> WorkDirectory { get; set; } = () => IslandPaths.CacheDir;

    /// 追加进探测进程环境的变量（测试用：隔离的 CLAUDE_CONFIG_DIR、假 claude 的模式开关）。
    internal static IReadOnlyDictionary<string, string> ExtraEnvironment { get; set; } = new Dictionary<string, string>();

    /// 直接起的 .exe 用 ActiveProcessLimit=1；.cmd 一定要 cmd.exe 再起子进程，不限。
    internal static bool LimitToOneProcess { get; set; } = true;

    /// 总时限：initialize、get_usage 和等进程退出共用。
    internal static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(20);

    private static readonly object Gate = new();
    private static Task? _busy;
    private static Task<AppUsage>? _shared;
    private static CancellationToken _runningToken;

    /// 取一次 Claude 额度。同一时间只跑一个：
    /// - 上一个还在跑、调用方没取消：这一次接着等它的结果；
    /// - 上一个被取消了（调用方换代了），它在剩下的预算内自己收尾，不杀，免得正好卡在
    ///   “刷新请求已发出、新 token 还没写回”的瞬间把登录弄丢；这一次直接跳过，返回 Skipped。
    /// 什么情况都不抛异常，落成 AppUsage 上的说明文字。
    [SupportedOSPlatform("windows")]
    public static Task<AppUsage> Fetch(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_busy is { IsCompleted: false } busy)
            {
                if (_runningToken.IsCancellationRequested) return Task.FromResult(Skipped);
                if (_shared is { IsCompleted: false } shared) return shared;
                busy.Wait(TimeSpan.FromSeconds(1)); // 结果刚出来、进程正在退出：等它收尾。
            }
            var result = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runningToken = ct;
            _shared = result.Task;
            _busy = Task.Run(() => RunGuarded(ct, result), CancellationToken.None);
            return result.Task;
        }
    }

    /// 探测进程（包括被取消后的收尾）都结束了。测试用。
    internal static Task Idle()
    {
        lock (Gate) return _busy ?? Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void RunGuarded(CancellationToken ct, TaskCompletionSource<AppUsage> result)
    {
        try
        {
            result.TrySetResult(Run(ct, result));
        }
        catch
        {
            result.TrySetResult(AppUsage.ErrorPair(NoResponseMessage));
        }
    }

    [SupportedOSPlatform("windows")]
    private static AppUsage Run(CancellationToken ct, TaskCompletionSource<AppUsage> result)
    {
        if (ct.IsCancellationRequested) return AppUsage.ErrorPair(NoResponseMessage);
        if (Locate() is not { Length: > 0 } path || !File.Exists(path)) return AppUsage.ErrorPair(NotInstalledMessage);
        var directory = WorkDirectory();
        string settings;
        try
        {
            Directory.CreateDirectory(directory);
            settings = Path.Combine(directory, SettingsFileName);
            if (!File.Exists(settings) || File.ReadAllText(settings) != SettingsContent) File.WriteAllText(settings, SettingsContent);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return AppUsage.ErrorPair(NoResponseMessage);
        }

        var start = StartInfo(path, directory, settings);
        var singleProcess = LimitToOneProcess && !IsShim(path);
        using var conversation = WindowsJobProcess.Conversation.Start(start, singleProcess);
        if (conversation is null) return AppUsage.ErrorPair(NoResponseMessage);

        var clock = Stopwatch.StartNew();
        var budget = Budget;
        var failed = AppUsage.ErrorPair(NoResponseMessage);

        // 被取消：不再等结果，进程在剩下的预算内自己结束，超出才由 Dispose 关 Job 杀整棵树。
        AppUsage Abandon()
        {
            result.TrySetResult(failed);
            conversation.CloseInput();
            var rest = budget - clock.Elapsed;
            if (rest > TimeSpan.Zero) conversation.WaitForExit(rest);
            return failed;
        }

        if (!conversation.WriteLine(InitializeRequest)) return failed;
        if (Await(conversation, "init-1", budget, clock, ct) is not { } init)
        {
            return ct.IsCancellationRequested ? Abandon() : failed;
        }
        bool hasAccount;
        using (init)
        {
            if (!Succeeded(init.RootElement)) return failed;
            hasAccount = Jsonl.GetObject(init.RootElement, "response") is { } initReply
                && Jsonl.GetObject(initReply, "response") is { } initPayload
                && HasSignedInAccount(initPayload);
        }
        if (!conversation.WriteLine(UsageRequest)) return failed;
        if (Await(conversation, "usage-1", budget, clock, ct) is not { } usage)
        {
            return ct.IsCancellationRequested ? Abandon() : failed;
        }
        AppUsage reading;
        using (usage)
        {
            reading = Succeeded(usage.RootElement) && Jsonl.GetObject(usage.RootElement, "response") is { } response
                && Jsonl.GetObject(response, "response") is { } payload
                ? Interpret(payload, hasAccount)
                : failed;
        }
        if (ct.IsCancellationRequested) return Abandon();
        // 关掉 stdin，让它自己退；退得慢就由 Dispose 关 Job 杀整棵树，结果不受影响。
        conversation.CloseInput();
        var left = budget - clock.Elapsed;
        conversation.WaitForExit(left < ExitGrace ? left : ExitGrace, ct);
        return reading;
    }

    /// 读到 request_id 对上的 control_response 为止；其他行忽略。EOF、超时、取消返回 null。
    [SupportedOSPlatform("windows")]
    private static JsonDocument? Await(WindowsJobProcess.Conversation conversation, string requestId,
        TimeSpan budget, Stopwatch clock, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var left = budget - clock.Elapsed;
            if (left <= TimeSpan.Zero) return null;
            if (conversation.ReadLine(left, out var line, ct) != WindowsJobProcess.LineResult.Line) return null;
            var document = Jsonl.TryParseLine(line!);
            if (document is null) continue;
            var root = document.RootElement;
            if (Jsonl.GetString(root, "type") == "control_response"
                && Jsonl.GetObject(root, "response") is { } response
                && Jsonl.GetString(response, "request_id") == requestId)
            {
                return document;
            }
            document.Dispose();
        }
        return null;
    }

    /// initialize 回复里的 account 对象：没登录、用 API key、登录被清掉时都在，只有登录了才带 email 字段
    /// （隔离目录和真实登录实测）。只看 email 这个字段名在不在，不读任何内容。
    private static bool HasSignedInAccount(JsonElement payload) =>
        Jsonl.GetObject(payload, "account") is { } account && account.TryGetProperty("email", out _);

    private static bool Succeeded(JsonElement message) =>
        Jsonl.GetObject(message, "response") is { } response && Jsonl.GetString(response, "subtype") == "success";

    /// get_usage 的回复内容（control_response.response.response）落成额度。
    /// - rate_limits 是对象：读 five_hour 和 seven_day，两个窗口都读不出来算解析失败；
    /// - rate_limits 为空且 rate_limits_available 为 false：initialize 回复的 account 里有登录后
    ///   才有的字段就是“这个账号没有订阅额度”，否则是“没有登录的账号（或在用 API key）”；只看字段名，不读内容；
    /// - rate_limits 为空而 rate_limits_available 为 true：这次没取到；
    /// - 其他：解析失败。
    internal static AppUsage Interpret(JsonElement payload, bool hasAccount = true)
    {
        var available = Jsonl.GetBool(payload, "rate_limits_available");
        if (Jsonl.GetObject(payload, "rate_limits") is not { } limits)
        {
            return available switch
            {
                false => AppUsage.ErrorPair(hasAccount ? NoSubscriptionMessage : NoAccountMessage),
                true => AppUsage.ErrorPair(NoReadingMessage),
                _ => AppUsage.ErrorPair(NoResponseMessage),
            };
        }
        var fiveHour = UsageFetcher.ParseClaudeWindow(Jsonl.GetObject(limits, "five_hour"), periodSeconds: 5 * 3600);
        var weekly = UsageFetcher.ParseClaudeWindow(Jsonl.GetObject(limits, "seven_day"), periodSeconds: 7 * 86400);
        if (fiveHour.HasError && weekly.HasError) return AppUsage.ErrorPair(NoResponseMessage);
        return new AppUsage(fiveHour, weekly, Jsonl.GetString(payload, "subscription_type"));
    }

    // MARK: - 启动参数和环境

    private static bool IsShim(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo StartInfo(string path, string directory, string settings)
    {
        var arguments = "-p --output-format stream-json --verbose --input-format stream-json"
            + " --no-session-persistence --setting-sources user --settings \"" + settings + "\" --strict-mcp-config";
        var start = new ProcessStartInfo
        {
            // npm 装的 claude 是 .cmd 垫片，CreateProcess 不能直接起它。
            FileName = IsShim(path) ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : path,
            Arguments = IsShim(path) ? "/d /s /c \"\"" + path + "\" " + arguments + "\"" : arguments,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        foreach (var (name, value) in BuildEnvironment(Environment.GetEnvironmentVariable)) start.Environment[name] = value;
        return start;
    }

    /// 白名单环境。read 取当前进程的变量，测试可以换成假的。
    internal static List<(string Name, string Value)> BuildEnvironment(Func<string, string?> read)
    {
        var environment = new List<(string, string)>();
        foreach (var name in KeptEnvironment)
        {
            if (read(name) is { } value) environment.Add((name, value));
        }
        environment.AddRange(ForcedEnvironment);
        environment.Add(("BROWSER", NoBrowser()));
        foreach (var (name, value) in ExtraEnvironment) environment.Add((name, value));
        return environment;
    }

    /// 什么都不做、带任何参数都立刻退出且退出码是 0 的系统程序：claude 要开浏览器时落在这里。
    /// hostname.exe 什么都不改，带网址时退 1。Claude Code 打开网址只看退出码、失败也不回退到别的打开方式
    /// （本机原生版实测），所以不用一个退 0 的程序；attrib.exe 之类本身能改文件的程序不用。
    private static string NoBrowser() => Path.Combine(Environment.SystemDirectory, "hostname.exe");
}
