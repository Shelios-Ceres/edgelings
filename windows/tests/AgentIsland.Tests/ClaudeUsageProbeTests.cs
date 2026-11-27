using System.Diagnostics;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 假的 claude：测试自己的 exe 按 LIMEN_FAKE_CLAUDE 指定的模式，按 stream-json 控制协议回 JSON。
/// Program.Main 一开头就把它分流出来。
internal static class FakeClaude
{
    public const string ModeVariable = "LIMEN_FAKE_CLAUDE";
    public const string DumpVariable = "FAKE_CLAUDE_DUMP";

    private const string Limits =
        "{\"session\":{},\"subscription_type\":\"max\",\"rate_limits_available\":true,\"rate_limits\":"
        + "{\"five_hour\":{\"utilization\":47,\"resets_at\":\"2026-11-26T05:09:59.928113+00:00\"},"
        + "\"seven_day\":{\"utilization\":56,\"resets_at\":\"2026-11-30T09:59:59.928132+00:00\"}},\"behaviors\":null}";

    /// 读假 claude 写的记录：允许对方同时在写、删；读不了就当还没有。
    public static string[] ReadLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static int Run(string mode, string[] args)
    {
        var dump = Environment.GetEnvironmentVariable(DumpVariable);
        void Note(string line)
        {
            if (dump is null) return;
            // 测试那边同时在读：追加时允许别人读、写、删。
            using var stream = new FileStream(dump, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
        Note("pid=" + Environment.ProcessId);
        Note("args=" + string.Join(" ", args));
        Note("cwd=" + Environment.CurrentDirectory);
        Note("env=" + string.Join(",", Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.OrdinalIgnoreCase)));
        Note("entrypoint=" + Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT"));
        Note("browser=" + Environment.GetEnvironmentVariable("BROWSER"));
        if (mode == "spawn")
        {
            try
            {
                using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "hostname.exe"))
                { UseShellExecute = false, CreateNoWindow = true });
                child?.WaitForExit(2000);
                Note("spawn=ok");
            }
            catch
            {
                Note("spawn=fail");
            }
        }
        string Reply(string id, string payload) =>
            "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + id + "\",\"response\":" + payload + "}}";
        while (Console.In.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var id = document.RootElement.GetProperty("request_id").GetString()!;
            var subtype = document.RootElement.GetProperty("request").GetProperty("subtype").GetString();
            if (subtype == "initialize")
            {
                if (mode == "errinit")
                {
                    Console.WriteLine("{\"type\":\"control_response\",\"response\":{\"subtype\":\"error\",\"request_id\":\"" + id + "\",\"error\":\"x\"}}");
                    return 1;
                }
                if (mode == "noise")
                {
                    Console.WriteLine("not json at all");
                    Console.WriteLine(Reply("other-9", "{}"));
                    Console.WriteLine("{\"type\":\"system\",\"subtype\":\"init\"}");
                }
                Console.WriteLine(Reply(id, mode == "noaccount" ? "{\"account\":{\"apiKeySource\":\"ANTHROPIC_API_KEY\",\"apiProvider\":\"x\",\"tokenSource\":\"none\"}}" : "{\"account\":{\"apiProvider\":\"x\",\"email\":\"<redacted>\",\"subscriptionType\":\"max\"}}"));
            }
            else if (subtype == "get_usage")
            {
                if (mode == "hang") Thread.Sleep(Timeout.Infinite);
                // 第一次起的挂住，之后起的正常回复：测被取消的那次不能把结果给后来者。
                if (mode == "hangonce" && dump is not null && ReadLines(dump).Count(l => l.StartsWith("pid=")) == 1)
                {
                    Thread.Sleep(Timeout.Infinite);
                }
                if (mode == "slowok") Thread.Sleep(1000);
                Console.WriteLine(Reply(id, mode switch
                {
                    "unavailable" or "noaccount" => "{\"session\":{},\"subscription_type\":null,\"rate_limits_available\":false,\"rate_limits\":null}",
                    "null" => "{\"session\":{},\"subscription_type\":null,\"rate_limits_available\":true,\"rate_limits\":null}",
                    "garbage" => "{\"session\":{}}",
                    _ => Limits,
                }));
            }
        }
        if (mode == "slowexit") Thread.Sleep(60_000);
        return 0;
    }
}

/// Windows 上的 Claude 额度探测：解析、环境、门控是纯逻辑；进程相关的用上面的假 claude，
/// 不碰真实的 claude 和登录。
public static class ClaudeUsageProbeTests
{
    public static void RunAll()
    {
        Run("a reply with both windows becomes the quota, with the plan", () => Parsed(Payload("true", Limits)));
        Run("rate_limits_available false is a login without subscription quota, not a sign-in prompt", NoSubscription);
        Run("available but rate_limits null is no reading this time, not a sign-in prompt", NoReading);
        Run("a reply with neither window, or neither field, is a failure to parse", Unparsable);
        Run("the environment is the whitelist plus the forced variables, nothing of Limen's own", Environment_);
        Run("the probe's messages never read as auth or rate-limit to the activity monitor", MessagesAreNotAuthStates);
        Run("the probe's messages are translated for display", MessagesAreTranslated);
        Run("Claude counts as open when nothing has been read yet or a surface is undecided", SurfaceGate);
        Run("the store skips Claude when it is closed or while the terminal sign-in runs", StoreGate);
        Run("a probe in flight when the terminal sign-in starts is dropped", StoreDropsInFlight);
        Run("a finished sign-in asks through the fetcher, past the gate", ReauthFinishUsesFetcher);
        Run("at most one probe per poll cycle: the tick on the interval goes, a manual refresh inside it does not", OnePerCycle);
        Run("a wake or a network recovery probes by its own two-minute floor, not the cycle", EventsAreNotTheCycle);
        Run("each message leaves the last numbers on the card, marked as kept", MessagesKeepTheSnapshot);
        Run("no subscription quota clears the old numbers to a dash", NoSubscriptionClears);
        Run("a manual refresh probes again outside the cycle, but not within sixty seconds", ManualRefreshFloor);
        Run("starting the terminal sign-in cancels the probe only when the probe is what runs", ReauthCancelIsGated);
        Run("nothing is probed before the first process scan; Claude open then gets one", StartupWaitsForReading);
        Run("a cancelled or skipped probe does not use up the cycle", CancelledProbeKeepsTheCycle);
        Run("a clock set back does not block the probe", ClockSetBack);
        Run("no subscription and no account clear the card and the saved snapshot, and are no failure", NoSubscriptionDetails);
        Run("a sign-in that finishes with no subscription clears the card and the saved snapshot", ReauthFinishNoSubscription);
        Run("clearing the saved snapshot keeps Codex's part", ClearSavedKeepsCodex);
        Run("the native claude.exe is preferred; a .ps1 is not a claude", ChoosesNative);

        Run("fake claude: the quota is read, the arguments and environment are exactly the plan's", RealRun);
        Run("fake claude: unavailable, null and garbage replies", Replies);
        Run("fake claude: noise lines and other request ids are skipped", Noise);
        Run("fake claude: an error reply to initialize fails at once", ErrorInit);
        Run("fake claude: no reply kills the whole tree at the budget and unregisters", Hung);
        Run("fake claude: a slow exit after the reply is killed and the result stands", SlowExit);
        Run("fake claude: a missing claude is the install message", NotFound);
        Run("fake claude: the single-process job refuses the child's own children, a .cmd shim still runs", SingleProcess);
        Run("fake claude: two probes at once run one process", SingleFlight);
        Run("fake claude: a cancelled token ends the probe", Cancelled);
        Run("fake claude: a cancelled probe finishes by itself and the next one is skipped meanwhile", CancelledRunNotShared);
        Run("fake claude: a token already cancelled starts nothing", CancelledBeforeStart);
        OfflineKeepsCredentials(shim: false);
        OfflineKeepsCredentials(shim: true);
        Console.WriteLine("ClaudeUsageProbeTests GREEN");
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine($"PASS Claude probe: {name}");
    }

    private const string Limits =
        "{\"five_hour\":{\"utilization\":47,\"resets_at\":\"2026-11-26T05:09:59+00:00\"},"
        + "\"seven_day\":{\"utilization\":56,\"resets_at\":\"2026-11-30T09:59:59+00:00\"}}";

    private static string Payload(string available, string limits, string plan = "\"max\"") =>
        "{\"subscription_type\":" + plan + ",\"rate_limits_available\":" + available + ",\"rate_limits\":" + limits + "}";

    private static AppUsage Interpret(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeUsageProbe.Interpret(document.RootElement);
    }

    private static void Parsed(string json)
    {
        var usage = Interpret(json);
        Expect(!usage.FiveHour.HasError && !usage.Weekly.HasError, "no error on either window");
        Expect(Math.Abs(usage.FiveHour.UsedPercent - 0.47) < 1e-9, "five hours 47%");
        Expect(Math.Abs(usage.Weekly.UsedPercent - 0.56) < 1e-9, "week 56%");
        Expect(usage.FiveHour.ResetAt is not null && usage.Weekly.ResetAt is not null, "both resets read");
        Expect(usage.Plan, "max");
    }

    private static void NoSubscription()
    {
        var usage = Interpret(Payload("false", "null", "null"));
        Expect(usage.FiveHour.Error, ClaudeUsageProbe.NoSubscriptionMessage);
        using (var document = JsonDocument.Parse(Payload("false", "null", "null")))
        {
            Expect(ClaudeUsageProbe.Interpret(document.RootElement, hasAccount: false).FiveHour.Error, ClaudeUsageProbe.NoAccountMessage);
        }
        Expect(usage.Weekly.Error, ClaudeUsageProbe.NoSubscriptionMessage);
    }

    private static void NoReading()
    {
        var usage = Interpret(Payload("true", "null", "null"));
        Expect(usage.FiveHour.Error, ClaudeUsageProbe.NoReadingMessage);
    }

    private static void Unparsable()
    {
        Expect(Interpret(Payload("true", "{}")).FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
        Expect(Interpret("{}").FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
        // 只有一个窗口也算读到了：另一个窗口留给界面按 no data 处理。
        var one = Interpret(Payload("true", "{\"five_hour\":{\"utilization\":10,\"resets_at\":null}}"));
        Expect(!one.FiveHour.HasError, "the window that is there is read");
    }

    private static void Environment_()
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = @"C:\Windows", ["PATH"] = "p", ["USERPROFILE"] = "u", ["CLAUDE_CONFIG_DIR"] = "cfg",
            ["ANTHROPIC_API_KEY"] = "secret", ["CLAUDE_CODE_OAUTH_TOKEN"] = "secret", ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli", ["LIMEN_SOMETHING"] = "x", ["HTTPS_PROXY"] = "http://proxy",
        };
        // 代理和证书路径：大小写两种写法都要在白名单里（本测试的字典不分大小写，这里按名字逐个核对）。
        var listed = new[]
        {
            "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
            "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE",
            "ALL_PROXY", "all_proxy", "HOME", "CLAUDE_CODE_GIT_BASH_PATH",
        };
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in listed) exact[name] = "v";
        var passed = ClaudeUsageProbe.BuildEnvironment(name => exact.GetValueOrDefault(name)).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in listed) Expect(passed.Contains(name), name + " is on the whitelist");
        var environment = ClaudeUsageProbe.BuildEnvironment(name => parent.GetValueOrDefault(name));
        var names = environment.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var kept in new[] { "SystemRoot", "PATH", "USERPROFILE", "CLAUDE_CONFIG_DIR", "HTTPS_PROXY" })
            Expect(names.Contains(kept), kept + " is passed on");
        foreach (var dropped in new[] { "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDECODE", "LIMEN_SOMETHING" })
            Expect(!names.Contains(dropped), dropped + " is not");
        string Of(string name) => environment.Last(e => e.Name == name).Value;
        Expect(Of("CLAUDE_CODE_ENTRYPOINT"), "sdk-cli");
        foreach (var (name, value) in new[]
                 {
                     ("DISABLE_AUTOUPDATER", "1"), ("DISABLE_TELEMETRY", "1"), ("DISABLE_ERROR_REPORTING", "1"),
                     ("ENABLE_CLAUDEAI_MCP_SERVERS", "false"), ("CLAUDE_CODE_AUTO_CONNECT_IDE", "false"),
                     ("CLAUDE_CODE_IDE_SKIP_AUTO_INSTALL", "1"),
                 })
        {
            Expect(Of(name), value);
        }
        Expect(Of("BROWSER").EndsWith("hostname.exe", StringComparison.OrdinalIgnoreCase), "BROWSER is a program that changes nothing");
    }

    private static void MessagesAreNotAuthStates()
    {
        foreach (var message in AllMessages())
        {
            var lower = message.ToLowerInvariant();
            foreach (var word in new[] { "auth", "login", "rate limit", "rate_limit" })
                Expect(!lower.Contains(word), message + " does not contain " + word);
        }
    }

    private static string[] AllMessages() =>
    [
        ClaudeUsageProbe.NoSubscriptionMessage, ClaudeUsageProbe.NoAccountMessage, ClaudeUsageProbe.NotInstalledMessage,
        ClaudeUsageProbe.NoReadingMessage, ClaudeUsageProbe.NoResponseMessage,
    ];

    private static void MessagesAreTranslated()
    {
        foreach (var message in AllMessages())
        {
            var shown = ErrorDisplay.Localize(message);
            Expect(!L10n.IsChinese || shown != message, message + " has a Chinese text when the UI is Chinese");
        }
        Expect(ClaudeUsageProbe.NoSubscriptionMessage != ClaudeUsageProbe.NotInstalledMessage, "distinct constants");
    }

    private static void SurfaceGate()
    {
        var tracker = new AgentSurfaces.Tracker();
        var firsts = 0;
        tracker.FirstObserved += () => firsts++;
        Expect(!AgentSurfaces.ClaudeMayBeOpen(tracker, TimeSpan.FromSeconds(5)), "no reading yet: not probed at start");
        Expect(AgentSurfaces.ClaudeMayBeOpen(tracker, AgentSurfaces.NoReadingGrace), "still none after a minute counts as open");
        tracker.Observe(new AgentSurfaces.Reading(new HashSet<AgentSurface>(), new HashSet<AgentSurface>()));
        tracker.Observe(new AgentSurfaces.Reading(new HashSet<AgentSurface>(), new HashSet<AgentSurface>()));
        Expect(firsts, 1);
        Expect(!AgentSurfaces.ClaudeMayBeOpen(tracker, TimeSpan.FromMinutes(5)), "read, and everything is closed");
        var codexOnly = new AgentSurfaces.Tracker();
        codexOnly.Observe(new AgentSurfaces.Reading(new HashSet<AgentSurface> { AgentSurface.CodexCli }, new HashSet<AgentSurface>()));
        Expect(!AgentSurfaces.ClaudeMayBeOpen(codexOnly, TimeSpan.Zero), "another agent's surface does not open Claude");
        foreach (var surface in new[] { AgentSurface.ClaudeApp, AgentSurface.ClaudeCode })
        {
            var open = new AgentSurfaces.Tracker();
            open.Observe(new AgentSurfaces.Reading(new HashSet<AgentSurface> { surface }, new HashSet<AgentSurface>()));
            Expect(AgentSurfaces.ClaudeMayBeOpen(open, TimeSpan.Zero), surface + " open");
            var undecided = new AgentSurfaces.Tracker();
            undecided.Observe(new AgentSurfaces.Reading(new HashSet<AgentSurface>(), new HashSet<AgentSurface> { surface }));
            Expect(AgentSurfaces.ClaudeMayBeOpen(undecided, TimeSpan.Zero), surface + " undecided counts as open");
        }
    }

    private sealed class StoreRig
    {
        public int Claude;
        public volatile TaskCompletionSource? Hang;
        public CancellationToken LastToken;
        public int Cleared;
        public bool Open = true;
        public DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        public Func<AppUsage> Reply = () => new AppUsage(new WindowUsage(0.5, null, null) { Observed = true }, new WindowUsage(0.6, null, null) { Observed = true });
        public readonly UsageStore Store;

        public StoreRig()
        {
            Store = new UsageStore(restore: false)
            {
                ClaudeFetcher = async token =>
                {
                    LastToken = token;
                    Interlocked.Increment(ref Claude);
                    if (Hang is { } hang) await hang.Task;
                    return Reply();
                },
                CodexFetcher = _ => Task.FromResult(AppUsage.ErrorPair("http 429")),
                CodexLogin = () => "account:a",
                ClaudeGateEnabled = true,
                ClaudeSurfaceOpen = () => Open,
                ClaudeProbeInterval = () => TimeSpan.FromMinutes(5),
                CodexShown = () => false,
                ClearSavedClaude = () => Cleared++,
                Clock = () => Now,
            };
            Store.Delay = (_, _) => new TaskCompletionSource().Task;
        }

        public void Settle() => Store.RefreshTask?.Wait(TimeSpan.FromSeconds(10));
    }

    private static void StoreGate()
    {
        var rig = new StoreRig();
        rig.Open = false;
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 0);
        rig.Open = true;
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Now += TimeSpan.FromMinutes(10);
        rig.Store.ClaudeReauthInProgress = true;
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Store.ClaudeReauthInProgress = false;
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 2);

        var ungated = new StoreRig { Open = false };
        ungated.Store.ClaudeGateEnabled = false;
        ungated.Store.Refresh();
        ungated.Settle();
        Expect(ungated.Claude, 1);
    }

    private static void StoreDropsInFlight()
    {
        var rig = new StoreRig { Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        rig.Store.Refresh();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (rig.Claude == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
        rig.Store.ClaudeReauthInProgress = true;
        rig.Hang.SetResult();
        rig.Settle();
        Expect(rig.Store.Claude.FiveHour.Error, "no data");
        Expect(rig.Store.ClaudeUpdatedAt is null, "nothing was recorded as an update");
    }

    private static void ReauthFinishUsesFetcher()
    {
        var rig = new StoreRig { Open = false };
        rig.Store.ClaudeReauthInProgress = true;
        rig.Store.FinishClaudeReauth().GetAwaiter().GetResult();
        Expect(rig.Claude, 1);
        Expect(!rig.Store.ClaudeReauthInProgress, "the wait is over");
        Expect(Math.Abs(rig.Store.Claude.FiveHour.UsedPercent - 0.5) < 1e-9, "the reading is shown");
    }

    private static void OnePerCycle()
    {
        var rig = new StoreRig();
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Now += TimeSpan.FromSeconds(30);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Now += TimeSpan.FromMinutes(3);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Now = new DateTimeOffset(2026, 11, 26, 12, 5, 0, TimeSpan.Zero);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 2);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 2);
        rig.Now += TimeSpan.FromMinutes(5);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 3);
    }

    private static void EventsAreNotTheCycle()
    {
        var rig = new StoreRig();
        rig.Store.Refresh();
        rig.Settle();
        rig.Now += TimeSpan.FromSeconds(90);
        rig.Store.HandleWake();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Now += TimeSpan.FromMinutes(1);
        rig.Store.HandleWake();
        Thread.Sleep(50);
        rig.Settle();
        Expect(rig.Claude, 2);
        rig.Now += TimeSpan.FromSeconds(10);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 2);
    }

    private static void MessagesKeepTheSnapshot()
    {
        foreach (var message in AllMessages().Where(m => m != ClaudeUsageProbe.NoSubscriptionMessage && m != ClaudeUsageProbe.NoAccountMessage))
        {
            var rig = new StoreRig();
            rig.Store.Refresh();
            rig.Settle();
            var at = rig.Store.ClaudeUpdatedAt;
            Expect(at is not null, "the first reading is recorded");
            rig.Reply = () => AppUsage.ErrorPair(message);
            rig.Now += TimeSpan.FromMinutes(10);
            rig.Store.Refresh();
            rig.Settle();
            var shown = rig.Store.Claude;
            Expect(Math.Abs(shown.FiveHour.UsedPercent - 0.5) < 1e-9 && shown.FiveHour.Kept, message + ": the numbers stay, marked kept");
            Expect(shown.FiveHour.Error, message);
            Expect(rig.Store.ClaudeUpdatedAt, at);
        }
    }

    private static void NoSubscriptionClears()
    {
        var rig = new StoreRig();
        rig.Store.Refresh();
        rig.Settle();
        Expect(Math.Abs(rig.Store.Claude.FiveHour.UsedPercent - 0.5) < 1e-9, "numbers first");
        rig.Reply = () => AppUsage.ErrorPair(ClaudeUsageProbe.NoSubscriptionMessage);
        rig.Now += TimeSpan.FromMinutes(10);
        rig.Store.Refresh();
        rig.Settle();
        var shown = rig.Store.Claude;
        Expect(shown.FiveHour.HasError && !shown.FiveHour.Kept && shown.FiveHour.UsedPercent == 0, "five hours: no numbers, not kept");
        Expect(shown.Weekly.HasError && !shown.Weekly.Kept, "week: the same");
        Expect(shown.FiveHour.Error, ClaudeUsageProbe.NoSubscriptionMessage);
        Expect(rig.Store.ClaudeUpdatedAt is null, "no update time for numbers that are gone");
        // 后来又取到了：数字回来。
        rig.Reply = () => new AppUsage(new WindowUsage(0.2, null, null) { Observed = true }, new WindowUsage(0.3, null, null) { Observed = true });
        rig.Now += TimeSpan.FromMinutes(10);
        rig.Store.Refresh();
        rig.Settle();
        Expect(Math.Abs(rig.Store.Claude.FiveHour.UsedPercent - 0.2) < 1e-9, "numbers come back");
    }

    private static void ManualRefreshFloor()
    {
        var rig = new StoreRig();
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Now += TimeSpan.FromSeconds(30);
        rig.Store.Refresh(fetchClaude: true, fetchCodex: false, manual: true);
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Now += TimeSpan.FromSeconds(31);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 1);
        rig.Store.Refresh(fetchClaude: true, fetchCodex: false, manual: true);
        rig.Settle();
        Expect(rig.Claude, 2);
        rig.Now += TimeSpan.FromSeconds(61);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 2);
        rig.Store.Refresh(fetchClaude: true, fetchCodex: false, manual: true);
        rig.Settle();
        Expect(rig.Claude, 3);
    }

    /// 可选的集成测试：真 claude、隔离的 CLAUDE_CONFIG_DIR、假的过期登录、连不上的代理。
    /// 固定“网络不通时 claude 不改凭证文件”。设了 LIMEN_CLAUDE_PROBE_IT=1 且找得到 claude 才跑，
    /// 否则只打印 SKIP，不算 PASS。绝不碰真实登录：配置目录是临时目录，里面只有假 token。
    private static void OfflineKeepsCredentials(bool shim)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP Claude probe integration: needs Windows");
            return;
        }
        if (Environment.GetEnvironmentVariable("LIMEN_CLAUDE_PROBE_IT") != "1")
        {
            Console.WriteLine("SKIP Claude probe integration: set LIMEN_CLAUDE_PROBE_IT=1 to run it");
            return;
        }
        if (Trigger.CLILocator.PathFor(TriggerTool.Claude) is not { } real)
        {
            Console.WriteLine("SKIP Claude probe integration: no claude found");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "limen-probe-it-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(directory, "config");
        Directory.CreateDirectory(config);
        var credentials = Path.Combine(config, ".credentials.json");
        File.WriteAllText(credentials,
            "{\"claudeAiOauth\":{\"accessToken\":\"FAKE-access\",\"refreshToken\":\"FAKE-refresh\",\"expiresAt\":1000,"
            + "\"scopes\":[\"user:inference\",\"user:profile\"],\"subscriptionType\":\"max\"}}");
        var before = File.ReadAllBytes(credentials);
        var baseline = Process.GetProcesses().Select(p => p.Id).ToHashSet();
        var browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "iexplore" };
        var seen = new HashSet<string>();
        using var stop = new CancellationTokenSource();
        var watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var process in Process.GetProcesses())
                {
                    if (!baseline.Contains(process.Id) && browsers.Contains(process.ProcessName)) seen.Add(process.ProcessName);
                    process.Dispose();
                }
                Thread.Sleep(50);
            }
        });
        try
        {
            var launcher = real;
            if (shim)
            {
                launcher = Path.Combine(directory, "claude.cmd");
                File.WriteAllText(launcher, "@echo off\r\n\"" + real + "\" %*\r\n");
            }
            ClaudeUsageProbe.Locate = () => launcher;
            ClaudeUsageProbe.WorkDirectory = () => Path.Combine(directory, "work");
            ClaudeUsageProbe.ExtraEnvironment = new Dictionary<string, string>
            {
                ["CLAUDE_CONFIG_DIR"] = config, ["HTTPS_PROXY"] = "http://127.0.0.1:9", ["HTTP_PROXY"] = "http://127.0.0.1:9",
            };
            var usage = Fetch();
            Expect(usage.FiveHour.Error, ClaudeUsageProbe.NoReadingMessage);
        }
        finally
        {
            stop.Cancel();
            watcher.Wait(TimeSpan.FromSeconds(5));
            ClaudeUsageProbe.Locate = static () => null;
            ClaudeUsageProbe.ExtraEnvironment = new Dictionary<string, string>();
            ClaudeUsageProbe.WorkDirectory = () => IslandPaths.CacheDir;
        }
        var after = File.ReadAllBytes(credentials);
        try { Directory.Delete(directory, recursive: true); } catch { }
        Expect(after.SequenceEqual(before), "offline: the credentials file is byte for byte what it was");
        Expect(seen.Count, 0);
        Console.WriteLine("PASS Claude probe integration: offline leaves the credentials file alone, no browser"
            + (shim ? " (behind a .cmd shim)" : ""));
    }

    private static void ReauthCancelIsGated()
    {
        foreach (var gated in new[] { true, false })
        {
            var rig = new StoreRig { Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            rig.Store.ClaudeGateEnabled = gated;
            rig.Store.Refresh();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (rig.Claude == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
            rig.Store.BeginClaudeReauth();
            Expect(rig.LastToken.IsCancellationRequested, gated ? true : false);
            rig.Hang.SetResult();
            rig.Settle();
            if (!gated)
            {
                // 门控关着（非 Windows）：取数没被打断，结果照常显示。
                Expect(Math.Abs(rig.Store.Claude.FiveHour.UsedPercent - 0.5) < 1e-9, "the fetch in flight is shown");
            }
        }
    }

    private static void StartupWaitsForReading()
    {
        var rig = new StoreRig { Open = false };
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 0);
        rig.Open = true;
        rig.Store.OnClaudeSurfacesRead();
        rig.Settle();
        Expect(rig.Claude, 1);
        var mac = new StoreRig();
        mac.Store.ClaudeGateEnabled = false;
        mac.Store.OnClaudeSurfacesRead();
        mac.Settle();
        Expect(mac.Claude, 0);
    }

    private static void CancelledProbeKeepsTheCycle()
    {
        // 这一次没有探测（Skipped）：不当结果，也不占名额。
        var skipped = new StoreRig();
        skipped.Reply = () => ClaudeUsageProbe.Skipped;
        skipped.Store.Refresh();
        skipped.Settle();
        Expect(skipped.Store.ClaudeUpdatedAt is null && skipped.Store.Claude.FiveHour.Error == "no data", "nothing shown for a skipped probe");
        skipped.Reply = () => new AppUsage(new WindowUsage(0.5, null, null) { Observed = true }, new WindowUsage(0.6, null, null) { Observed = true });
        skipped.Store.Refresh();
        skipped.Settle();
        Expect(skipped.Claude, 2);
        Expect(Math.Abs(skipped.Store.Claude.FiveHour.UsedPercent - 0.5) < 1e-9, "the next one is shown");
    }

    private static void ClockSetBack()
    {
        var rig = new StoreRig();
        rig.Store.Refresh();
        rig.Settle();
        rig.Now -= TimeSpan.FromHours(1);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude, 2);
    }

    /// 页脚的提示只对显示着的 agent 出：测试里没检测到 Claude，要先把它设成显示。
    private static IDisposable ClaudeShownInFooter()
    {
        var visibility = AgentIsland.Model.ProviderVisibilityStore.Shared;
        var was = visibility.IsShown(AgentIsland.Model.DisplayProvider.Claude);
        visibility.SetShown(AgentIsland.Model.DisplayProvider.Claude, true);
        return new Restore(() => visibility.SetShown(AgentIsland.Model.DisplayProvider.Claude, was));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static void NoSubscriptionDetails()
    {
        using var shown = ClaudeShownInFooter();
        foreach (var message in new[] { ClaudeUsageProbe.NoSubscriptionMessage, ClaudeUsageProbe.NoAccountMessage })
        {
            var rig = new StoreRig();
            rig.Store.Refresh();
            rig.Settle();
            rig.Reply = () => AppUsage.ErrorPair(message);
            rig.Now += TimeSpan.FromMinutes(10);
            rig.Store.Refresh();
            rig.Settle();
            Expect(rig.Store.Claude.FiveHour.HasError && !rig.Store.Claude.FiveHour.Kept, message + ": numbers cleared");
            Expect(rig.Cleared, 1);
            Expect(rig.Store.RefreshWarning is null, message + ": no \"stale\" in the footer");
        }
        // 没装 Claude Code：保留快照，但也不是取数失败。
        var absent = new StoreRig();
        absent.Store.Refresh();
        absent.Settle();
        absent.Reply = () => AppUsage.ErrorPair(ClaudeUsageProbe.NotInstalledMessage);
        absent.Now += TimeSpan.FromMinutes(10);
        absent.Store.Refresh();
        absent.Settle();
        Expect(absent.Store.Claude.FiveHour.Kept, "kept");
        Expect(absent.Cleared, 0);
        Expect(absent.Store.RefreshWarning is null, "not installed is no failure");
        // 真失败还是要提示。
        var failing = new StoreRig();
        failing.Reply = () => AppUsage.ErrorPair(ClaudeUsageProbe.NoResponseMessage);
        failing.Store.Refresh();
        failing.Settle();
        Expect(failing.Store.RefreshWarning is not null, "a real failure still shows in the footer (so the nulls above mean something)");
    }

    private static void ReauthFinishNoSubscription()
    {
        using var shown = ClaudeShownInFooter();
        var rig = new StoreRig();
        rig.Store.Refresh();
        rig.Settle();
        Expect(Math.Abs(rig.Store.Claude.FiveHour.UsedPercent - 0.5) < 1e-9, "numbers first");
        rig.Reply = () => AppUsage.ErrorPair(ClaudeUsageProbe.NoSubscriptionMessage);
        rig.Store.ClaudeReauthInProgress = true;
        rig.Store.FinishClaudeReauth().GetAwaiter().GetResult();
        Expect(rig.Store.Claude.FiveHour.HasError && !rig.Store.Claude.FiveHour.Kept, "cleared");
        Expect(rig.Cleared, 1);
        Expect(rig.Store.RefreshWarning is null, "no stale footer");
    }

    private static void ClearSavedKeepsCodex()
    {
        var now = DateTimeOffset.Now;
        WindowUsage Window(double used) => new(used, now.AddHours(2), null) { Observed = true };
        Preferences.Set(UsageStore.CacheKey, new UsageCacheSnapshot(
            new AppUsage(Window(0.5), Window(0.6)), new AppUsage(Window(0.1), Window(0.2)), now));
        Expect(UsageStore.LoadCachedSnapshot()?.Claude.FiveHour.UsedPercent is 0.5, "saved with Claude numbers");
        UsageStore.ClearCachedClaude();
        var after = UsageStore.LoadCachedSnapshot();
        Expect(after is not null, "the snapshot is still there");
        Expect(after!.Claude.IsErrorOnly || after.Claude == AppUsage.Empty, "Claude's part is gone");
        Expect(Math.Abs(after.Codex.FiveHour.UsedPercent - 0.1) < 1e-9 && Math.Abs(after.Codex.Weekly.UsedPercent - 0.2) < 1e-9, "Codex's part is kept");
        Preferences.Remove(UsageStore.CacheKey);
    }

    private static void ChoosesNative()
    {
        // 探测只在 Windows 上跑；路径按 Windows 的分隔符拼，Mac 上不成立。
        if (!OperatingSystem.IsWindows()) return;
        bool Exists(string path) => path.EndsWith(@".local\bin\claude.exe", StringComparison.OrdinalIgnoreCase);
        Expect(ClaudeUsageProbe.ChooseClaude(@"C:\n\claude.cmd", @"C:\Users\x", Exists), @"C:\Users\x\.local\bin\claude.exe");
        Expect(ClaudeUsageProbe.ChooseClaude(@"C:\n\claude.cmd", @"C:\Users\x", _ => false), @"C:\n\claude.cmd");
        Expect(ClaudeUsageProbe.ChooseClaude(@"C:\n\claude.ps1", @"C:\Users\x", _ => false) is null, "a .ps1 is not a claude");
        Expect(ClaudeUsageProbe.ChooseClaude(null, @"C:\Users\x", _ => false) is null, "none");
    }

    private static void CancelledBeforeStart()
    {
        if (Skip()) return;
        using var fake = new Fake("ok");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Expect(Fetch(cts.Token).FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
        Expect(fake.Lines().Length, 0);
    }

    // MARK: - 假 claude

    private sealed class Fake : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "limen-fake-claude-" + Guid.NewGuid().ToString("N"));
        public readonly string Dump;
        public readonly string Exe;
        public string Shim => Path.Combine(Directory, "claude.cmd");

        public Fake(string mode, bool shim = false, TimeSpan? budget = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Dump = Path.Combine(Directory, "dump.txt");
            Exe = FakeExe();
            File.WriteAllText(Shim, "@echo off\r\n\"" + Exe + "\" %*\r\n");
            ClaudeUsageProbe.Locate = () => shim ? Shim : Exe;
            ClaudeUsageProbe.WorkDirectory = () => Path.Combine(Directory, "work");
            ClaudeUsageProbe.ExtraEnvironment = new Dictionary<string, string>
            {
                [FakeClaude.ModeVariable] = mode,
                [FakeClaude.DumpVariable] = Dump,
            };
            ClaudeUsageProbe.Budget = budget ?? TimeSpan.FromSeconds(20);
            ClaudeUsageProbe.LimitToOneProcess = true;
        }

        public string[] Lines() => FakeClaude.ReadLines(Dump);

        public string? Value(string key) => Lines().LastOrDefault(l => l.StartsWith(key + "="))?[(key.Length + 1)..];

        public int? Pid => int.TryParse(Value("pid"), out var pid) ? pid : null;

        public static bool Alive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            ClaudeUsageProbe.Locate = static () => null;
            ClaudeUsageProbe.ExtraEnvironment = new Dictionary<string, string>();
            ClaudeUsageProbe.Budget = TimeSpan.FromSeconds(20);
            // 等探测收尾、假进程真正退出，再删目录。
            ClaudeUsageProbe.Idle().Wait(TimeSpan.FromSeconds(30));
            if (Pid is { } pid)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (Alive(pid) && DateTime.UtcNow < deadline) Thread.Sleep(20);
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { System.IO.Directory.Delete(Directory, recursive: true); return; }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }
    }

    /// 测试自己的 exe。用 dotnet 直接跑 dll 时 ProcessPath 是 dotnet.exe，改用旁边的 apphost。
    private static string FakeExe()
    {
        var path = Environment.ProcessPath!;
        if (!Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return path;
        return Path.Combine(AppContext.BaseDirectory, "AgentIsland.Tests.exe");
    }

    private static bool Skip() => !OperatingSystem.IsWindows();

    private static AppUsage Fetch(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ClaudeUsageProbe.Fetch(ct).GetAwaiter().GetResult();
    }

    private static void RealRun()
    {
        if (Skip()) return;
        using var fake = new Fake("ok");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "secret");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "secret");
        try
        {
            var usage = Fetch();
            Expect(Math.Abs(usage.FiveHour.UsedPercent - 0.47) < 1e-9 && Math.Abs(usage.Weekly.UsedPercent - 0.56) < 1e-9, "the quota is read");
            Expect(usage.Plan, "max");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        }
        var args = fake.Value("args")!;
        foreach (var part in new[]
                 {
                     "-p", "--output-format stream-json", "--verbose", "--input-format stream-json", "--no-session-persistence",
                     "--setting-sources user", "--strict-mcp-config", "claude-probe-settings.json",
                 })
        {
            Expect(args.Contains(part), "argument " + part);
        }
        Expect(File.ReadAllText(Path.Combine(fake.Directory, "work", "claude-probe-settings.json")), "{\"disableAllHooks\":true}");
        Expect(fake.Value("cwd")!.TrimEnd('\\'), Path.Combine(fake.Directory, "work"));
        Expect(fake.Value("entrypoint"), "sdk-cli");
        Expect(fake.Value("browser")!.EndsWith("hostname.exe", StringComparison.OrdinalIgnoreCase), "BROWSER set");
        var names = fake.Value("env")!.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        Expect(!names.Contains("ANTHROPIC_API_KEY") && !names.Contains("CLAUDE_CODE_OAUTH_TOKEN"), "Limen's own credentials variables are not passed on");
        Expect(!names.Any(n => n.StartsWith("LIMEN", StringComparison.OrdinalIgnoreCase) && n != FakeClaude.ModeVariable), "nothing else of Limen's");
        Expect(OwnProcesses.Snapshot().Count, 0);
    }

    private static void Replies()
    {
        if (Skip()) return;
        using (new Fake("unavailable")) Expect(Fetch().FiveHour.Error, ClaudeUsageProbe.NoSubscriptionMessage);
        using (new Fake("noaccount")) Expect(Fetch().FiveHour.Error, ClaudeUsageProbe.NoAccountMessage);
        using (new Fake("null")) Expect(Fetch().FiveHour.Error, ClaudeUsageProbe.NoReadingMessage);
        using (new Fake("garbage")) Expect(Fetch().FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
    }

    private static void Noise()
    {
        if (Skip()) return;
        using var fake = new Fake("noise");
        Expect(Math.Abs(Fetch().FiveHour.UsedPercent - 0.47) < 1e-9, "the quota is still read");
    }

    private static void ErrorInit()
    {
        if (Skip()) return;
        using var fake = new Fake("errinit");
        var clock = Stopwatch.StartNew();
        Expect(Fetch().FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
        Expect(clock.Elapsed < TimeSpan.FromSeconds(10), "does not wait out the budget");
    }

    private static void Hung()
    {
        if (Skip()) return;
        foreach (var shim in new[] { false, true })
        {
            using var fake = new Fake("hang", shim, TimeSpan.FromSeconds(3));
            if (!OperatingSystem.IsWindows()) return;
            var task = ClaudeUsageProbe.Fetch();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (OwnProcesses.Snapshot().Count == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
            Expect(OwnProcesses.Snapshot().Count >= 1, "the probe is registered while it runs");
            var usage = task.GetAwaiter().GetResult();
            Expect(usage.FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
            Expect(OwnProcesses.Snapshot().Count, 0);
            var pid = fake.Pid ?? throw new Exception("the fake never started");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Fake.Alive(pid) && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Expect(!Fake.Alive(pid), (shim ? "behind cmd.exe" : "direct") + ": the process is gone");
        }
    }

    private static void SlowExit()
    {
        if (Skip()) return;
        using var fake = new Fake("slowexit", budget: TimeSpan.FromSeconds(3));
        var clock = Stopwatch.StartNew();
        var usage = Fetch();
        Expect(Math.Abs(usage.FiveHour.UsedPercent - 0.47) < 1e-9, "the result stands");
        Expect(clock.Elapsed < TimeSpan.FromSeconds(10), "and does not wait a minute for the exit");
        var pid = fake.Pid!.Value;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Fake.Alive(pid) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Expect(!Fake.Alive(pid), "the slow process is killed");
    }

    private static void NotFound()
    {
        if (Skip()) return;
        using var fake = new Fake("ok");
        ClaudeUsageProbe.Locate = static () => null;
        Expect(Fetch().FiveHour.Error, ClaudeUsageProbe.NotInstalledMessage);
        ClaudeUsageProbe.Locate = () => Path.Combine(fake.Directory, "gone.exe");
        Expect(Fetch().FiveHour.Error, ClaudeUsageProbe.NotInstalledMessage);
        Expect(!File.Exists(fake.Dump), "nothing was started");
    }

    private static void SingleProcess()
    {
        if (Skip()) return;
        using (var direct = new Fake("spawn"))
        {
            var usage = Fetch();
            Expect(direct.Value("spawn"), "fail");
            Expect(Math.Abs(usage.FiveHour.UsedPercent - 0.47) < 1e-9, "and the quota is still read");
        }
        using (var shim = new Fake("spawn", shim: true))
        {
            var usage = Fetch();
            Expect(shim.Value("spawn"), "ok");
            Expect(Math.Abs(usage.FiveHour.UsedPercent - 0.47) < 1e-9, "a .cmd shim is run through cmd.exe");
        }
    }

    private static void SingleFlight()
    {
        if (Skip()) return;
        using var fake = new Fake("slowok");
        if (!OperatingSystem.IsWindows()) return;
        var first = ClaudeUsageProbe.Fetch();
        var second = ClaudeUsageProbe.Fetch();
        Expect(ReferenceEquals(first, second), "the second asks for the one already running");
        first.GetAwaiter().GetResult();
        Expect(fake.Lines().Count(l => l.StartsWith("pid=")), 1);
        Fetch();
        Expect(fake.Lines().Count(l => l.StartsWith("pid=")), 2);
    }

    private static void CancelledRunNotShared()
    {
        if (Skip()) return;
        using var fake = new Fake("hangonce", budget: TimeSpan.FromSeconds(4));
        if (!OperatingSystem.IsWindows()) return;
        using var first = new CancellationTokenSource();
        var a = ClaudeUsageProbe.Fetch(first.Token);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!fake.Lines().Any(l => l.StartsWith("pid=")) && DateTime.UtcNow < until) Thread.Sleep(10);
        first.Cancel();
        Expect(a.GetAwaiter().GetResult().FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
        var pid = fake.Pid!.Value;
        Expect(Fake.Alive(pid), "cancelling does not kill the probe: it finishes by itself");
        Expect(ReferenceEquals(ClaudeUsageProbe.Fetch().GetAwaiter().GetResult(), ClaudeUsageProbe.Skipped), "meanwhile the next one is skipped");
        Expect(fake.Lines().Count(l => l.StartsWith("pid=")), 1);
        ClaudeUsageProbe.Idle().Wait(TimeSpan.FromSeconds(15));
        Expect(!Fake.Alive(pid), "past the budget the tree is killed");
        Expect(OwnProcesses.Snapshot().Count, 0);
        Expect(Math.Abs(Fetch().FiveHour.UsedPercent - 0.47) < 1e-9, "afterwards a probe runs again");
    }

    private static void Cancelled()
    {
        if (Skip()) return;
        using var fake = new Fake("hang", budget: TimeSpan.FromSeconds(3));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var clock = Stopwatch.StartNew();
        Expect(Fetch(cts.Token).FiveHour.Error, ClaudeUsageProbe.NoResponseMessage);
        Expect(clock.Elapsed < TimeSpan.FromSeconds(2.5), "the caller is released at once, not at the end of the budget");
        ClaudeUsageProbe.Idle().Wait(TimeSpan.FromSeconds(15));
        Expect(OwnProcesses.Snapshot().Count, 0);
        Expect(!Fake.Alive(fake.Pid!.Value), "the process is gone at the end of the budget");
    }
}
