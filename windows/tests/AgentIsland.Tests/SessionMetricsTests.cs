using System.Globalization;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 悬浮卡主对话的缓存率和 token 速度（第 8 批）：各家日志的公式、按请求去重、不显示的情况、取整、5 秒节流。
/// 日志都是几行的假文件，写在临时目录里；时间是固定的，节流用假时钟，不 sleep。
public static class SessionMetricsTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "limen-session-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = L10n.Current;
        L10n.Current = L10n.Language.English;
        try
        {
            var tests = new (string Name, Action<string> Test)[]
            {
                ("Claude: cache is hits over everything read, a request's lines count once, speed is output over its own time", ClaudeFormula),
                ("Claude: a request starts at its api-request, else at the last user record, never at an attachment", ClaudeStart),
                ("Claude: side chains and synthetic replies are not the conversation", ClaudeSkipped),
                ("Claude: a missing cache field is no cache rate, a zero one is 0", ClaudeNoCacheField),
                ("Claude: only the last five requests make the cache rate and the last three long replies the speed", Windows),
                ("Codex: cached over input, token_usage_record by response id, started after the last tool output", CodexFormula),
                ("Codex: an old log with only token_count gives a cache rate and no speed", CodexLegacy),
                ("Kimi: cache read over all input, time from llm.request to usage.record", KimiFormula),
                ("fewer than two requests show nothing", TooFewRequests),
                ("speed hides after thirty seconds without output, the cache rate stays, and under 1 tok/s is not shown", SpeedHides),
                ("the cache rate rounds down, and 100 is only all hits", RoundsDown),
                ("speed text is whole tok/s, and 1.2k from a thousand", SpeedText),
                ("a log is read from its tail, then only what is appended; a torn last line waits", IncrementalRead),
                ("a rewritten shorter log is read afresh", Rewritten),
                ("the store recomputes a session at most every five seconds", Throttle),
                ("a system clock set backwards does not freeze the readings", ClockRewind),
                ("lines of one message without a requestId are counted once", NoRequestId),
                ("only working sessions of Claude, Codex and Kimi are read", WorkingOnly),
                ("card rows carry metrics on running main rows only, and the key holds presence, not numbers", RowsCarryMetrics),
            };
            foreach (var (name, test) in tests)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
                test(dir);
                Console.WriteLine("PASS session metrics: " + name);
            }
        }
        finally
        {
            L10n.Current = previous;
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("SessionMetricsTests GREEN");
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static string Iso(double seconds) =>
        T0.AddSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Write(string dir, params string[] lines)
    {
        var path = Path.Combine(dir, "session.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static void Append(string path, params string[] lines) => File.AppendAllText(path, string.Join("\n", lines) + "\n");

    // MARK: - Claude lines

    private static string Api(double at) => $$"""{"type":"api-request","timestamp":"{{Iso(at)}}","querySource":"sdk"}""";

    private static string UserAt(double at) => $$"""{"type":"user","timestamp":"{{Iso(at)}}","message":{"role":"user","content":"x"}""" + "}";

    private static string Attachment(double at) => $$"""{"type":"attachment","timestamp":"{{Iso(at)}}"}""";

    private static string Asst(string request, double at, long input, long output, long read, long create,
        string? stop = "tool_use", bool sidechain = false, string model = "claude-opus-5-5", bool cacheFields = true)
    {
        var cache = cacheFields ? $$""","cache_read_input_tokens":{{read}},"cache_creation_input_tokens":{{create}}""" : "";
        var stopReason = stop is null ? "null" : $"\"{stop}\"";
        var head = $$"""{"type":"assistant","timestamp":"{{Iso(at)}}","requestId":"req_{{request}}","isSidechain":{{(sidechain ? "true" : "false")}},"message":{"id":"msg_{{request}}","model":"{{model}}","stop_reason":{{stopReason}},"usage":{"input_tokens":{{input}},"output_tokens":{{output}}""";
        return head + cache + "}}}";
    }

    private static SessionMetrics? Claude(string path) => new SessionMetricsFile(path, () => new ClaudeMetricsParser()).Update();

    /// 三个请求：命中 80/100、190/200、270/300；耗时 2、3、2 秒，输出 100、300、200。
    /// 第三个请求写成三行（内容块各一行），只算一次。
    private static string[] ThreeRequests() => new[]
    {
        UserAt(0), Api(0.5), Asst("1", 1.5, 10, 100, 80, 10), Asst("1", 2.5, 10, 100, 80, 10),
        UserAt(2.8), Attachment(3), Api(3.5), Asst("2", 6.5, 5, 300, 190, 5),
        UserAt(6.8), Api(7.5), Asst("3", 8, 10, 200, 270, 20), Asst("3", 8.5, 10, 200, 270, 20), Asst("3", 9.5, 10, 200, 270, 20),
    };

    private static void ClaudeFormula(string dir)
    {
        var metrics = Claude(Write(dir, ThreeRequests()))!;
        Expect(metrics.Requests, 3);
        // 命中 540 / 全部 600 = 90%，同一请求的三行不重复算。
        Expect(metrics.CachePercent, 90);
        // 请求 1 从 0.5 到 2.5 共 2 秒 100 个 → 50；请求 2 从 3.5 到 6.5 共 3 秒 300 个 → 100；请求 3 从 7.5 到 9.5 共 2 秒 200 个 → 100。
        Expect(Math.Abs(metrics.Speed!.Value - (50 + 100 + 100) / 3.0) < 0.001, "speed is the mean of each reply's own speed");
        Expect(metrics.LastOutputAt, T0.AddSeconds(9.5));
    }

    private static void ClaudeStart(string dir)
    {
        // 没有 api-request：取前面最近一条 user；夹在中间的 attachment（写在请求之后）不算起点。
        var path = Write(dir,
            UserAt(0), Attachment(1.9), Asst("1", 2, 1, 100, 90, 9),
            UserAt(10), Attachment(11.9), Asst("2", 12, 1, 100, 90, 9),
            UserAt(20), Attachment(21.9), Asst("3", 22, 1, 100, 90, 9));
        var metrics = Claude(path)!;
        // 每次 user 到回复结束都是 2 秒，100 个 token → 50 tok/s；起点要是取了 attachment，会算成 1000 tok/s。
        Expect(Math.Abs(metrics.Speed!.Value - 50) < 0.001, "started at the user record");
        // 一个请求 attachment 之前没有任何起点：只算缓存率。
        var none = Claude(Write(Path.Combine(dir), Attachment(0), Asst("a", 1, 1, 100, 90, 9), Attachment(4), Asst("b", 5, 1, 100, 90, 9)))!;
        Expect(none.Speed is null, "no start, no speed");
        Expect(none.CachePercent, 90);
        // 重试：只取成功那一次 api-request。
        var retried = Claude(Write(dir, Api(0), Api(5), Asst("1", 7, 1, 100, 90, 9), Api(8), Asst("2", 10, 1, 100, 90, 9)))!;
        Expect(Math.Abs(retried.Speed!.Value - (50 + 50) / 2.0) < 0.001, "the last api-request starts the reply");
    }

    private static void ClaudeSkipped(string dir)
    {
        var path = Write(dir,
            Api(0), Asst("1", 2, 1, 100, 90, 9),
            Api(3), Asst("side", 4, 1, 100, 0, 0, sidechain: true),
            Api(5), Asst("syn", 6, 1, 100, 0, 0, model: "<synthetic>"),
            Api(7), Asst("2", 9, 1, 100, 90, 9));
        var metrics = Claude(path)!;
        Expect(metrics.Requests, 2);
        Expect(metrics.CachePercent, 90);
    }

    private static void ClaudeNoCacheField(string dir)
    {
        var none = Claude(Write(dir, Api(0), Asst("1", 2, 1, 100, 0, 0, cacheFields: false), Api(3), Asst("2", 5, 1, 100, 0, 0, cacheFields: false)))!;
        Expect(none.CachePercent is null, "no cache field, no cache rate");
        Expect(none.Speed is not null, "speed still shows");
        // 字段在、值是 0：缓存率是 0%，不是“没有”。
        var zero = Claude(Write(dir, Api(0), Asst("1", 2, 100, 100, 0, 0), Api(3), Asst("2", 5, 100, 100, 0, 0)))!;
        Expect(zero.CachePercent, 0);
    }

    private static void Windows(string dir)
    {
        // 八个请求：前三个缓存全没中、输出短；后五个中了 80%；输出长的请求速度依次 10、20、40、80，只取最后三个 20、40、80。
        var lines = new List<string>();
        double at = 0;
        void Request(string id, long input, long read, long output, double seconds)
        {
            lines.Add(Api(at));
            at += seconds;
            lines.Add(Asst(id, at, input, output, read, 0));
            at += 1;
        }
        Request("1", 100, 0, 10, 1);
        Request("2", 100, 0, 10, 1);
        Request("3", 100, 0, 10, 1);
        Request("4", 20, 80, 100, 10);  // 10 tok/s
        Request("5", 20, 80, 200, 10);  // 20
        Request("6", 20, 80, 400, 10);  // 40
        Request("7", 20, 80, 800, 10);  // 80
        Request("8", 20, 80, 49, 1);    // 输出不到 50：不算速度
        var metrics = Claude(Write(dir, lines.ToArray()))!;
        // 最近 5 次：4..8，命中 400 / 500。
        Expect(metrics.CachePercent, 80);
        Expect(Math.Abs(metrics.Speed!.Value - (20 + 40 + 80) / 3.0) < 0.001, "the last three replies of 50 tokens or more");
    }

    // MARK: - Codex lines

    private static string Rollout(double at, string type, string payload) => $$"""{"timestamp":"{{Iso(at)}}","type":"{{type}}","payload":""" + payload + "}";

    private static string Record(double at, string id, long input, long cached, long output) =>
        Rollout(at, "token_usage_record", $$"""{"response_id":"{{id}}","usage":{"input_tokens":{{input}},"cached_input_tokens":{{cached}},"output_tokens":{{output}}""" + "}}");

    private static string Count(double at, long input, long cached, long output, long running) =>
        Rollout(at, "event_msg", $$"""{"type":"token_count","info":{"total_token_usage":{"total_tokens":{{running}}""" + $$"""},"last_token_usage":{"input_tokens":{{input}},"cached_input_tokens":{{cached}},"output_tokens":{{output}}""" + "}}}");

    private static SessionMetrics? Codex(string path) => new SessionMetricsFile(path, () => new CodexMetricsParser()).Update();

    private static void CodexFormula(string dir)
    {
        var path = Write(dir,
            Rollout(0, "event_msg", """{"type":"task_started"}"""),
            Rollout(0.1, "response_item", """{"type":"message","role":"user"}"""),
            Rollout(3, "response_item", """{"type":"reasoning"}"""),
            Record(5, "r1", 1000, 900, 200),
            Rollout(6, "response_item", """{"type":"custom_tool_call_output"}"""),
            // token_count 和 token_usage_record 写同一个请求：有记录时只用记录，不叠加。
            Count(6.003, 1000, 900, 200, 1200),
            Rollout(12, "response_item", """{"type":"message","role":"assistant"}"""),
            Record(16, "r2", 2000, 1900, 400),
            // 同一个 response_id 再写一遍。
            Record(16.5, "r2", 2000, 1900, 400));
        var metrics = Codex(path)!;
        Expect(metrics.Requests, 2);
        // 命中 2800 / input 3000 = 93.3% → 93。
        Expect(metrics.CachePercent, 93);
        // r1：从回合开始后的用户消息（0.1）到 5 → 4.9 秒 200 个；r2：从工具输出（6）到 16 → 10 秒 400 个 = 40。
        Expect(Math.Abs(metrics.Speed!.Value - (200 / 4.9 + 40) / 2) < 0.001, "each reply from its own start");
        Expect(metrics.LastOutputAt, T0.AddSeconds(16));
    }

    private static void CodexLegacy(string dir)
    {
        var metrics = Codex(Write(dir,
            Count(5, 1000, 900, 200, 1200),
            // 累计值没变：重发，不是新请求。
            Count(5.5, 1000, 900, 200, 1200),
            Count(15, 2000, 1900, 400, 3600)))!;
        Expect(metrics.Requests, 2);
        Expect(metrics.CachePercent, 93);
        Expect(metrics.Speed is null, "a token_count says nothing of when the reply began");
    }

    // MARK: - Kimi lines

    private static string KimiLine(double at, string type, string rest = "") =>
        $$"""{"type":"{{type}}","agentId":"main","time":{{T0.AddSeconds(at).ToUnixTimeMilliseconds()}}""" + rest + "}";

    private static string KimiUsage(double at, long other, long output, long read, long create) =>
        KimiLine(at, "usage.record", $$""","usage":{"inputOther":{{other}},"output":{{output}},"inputCacheRead":{{read}},"inputCacheCreation":{{create}}""" + "}");

    private static void KimiFormula(string dir)
    {
        var metrics = new SessionMetricsFile(Write(dir,
            KimiLine(0, "llm.request"), KimiUsage(4, 100, 200, 800, 100),
            KimiLine(5, "llm.request"), KimiUsage(7, 50, 300, 900, 50)), () => new KimiMetricsParser()).Update()!;
        // 命中 1700 / (1000 + 1000) = 85%。
        Expect(metrics.CachePercent, 85);
        // 200 / 4 = 50，300 / 2 = 150。
        Expect(Math.Abs(metrics.Speed!.Value - 100) < 0.001, "from llm.request to usage.record");
        Expect(metrics.LastOutputAt, T0.AddSeconds(7));
    }

    // MARK: - what shows

    private static RequestSample Sample(int n, long hit, long total, long output, double seconds, bool cache = true) =>
        new("k" + n, T0.AddSeconds(n * 100), T0.AddSeconds(n * 100 + seconds), hit, total, output, cache, true);

    private static void TooFewRequests(string dir)
    {
        var one = SessionMetrics.Compute(new[] { Sample(1, 90, 100, 100, 2) }, T0.AddSeconds(102));
        Expect(one.Shown(T0.AddSeconds(103)) is null, "one request is too little");
        var two = SessionMetrics.Compute(new[] { Sample(1, 90, 100, 100, 2), Sample(2, 90, 100, 100, 2) }, T0.AddSeconds(202));
        Expect(two.Shown(T0.AddSeconds(203)) is not null, "two will do");
        Expect(Claude(Write(dir, Api(0)))?.Shown(T0) is null, "a log without a usage line");
    }

    private static void SpeedHides(string dir)
    {
        var samples = new[] { Sample(1, 90, 100, 100, 2), Sample(2, 90, 100, 100, 2) };
        var last = T0.AddSeconds(202);
        var metrics = SessionMetrics.Compute(samples, last);
        Expect(metrics.Shown(last.AddSeconds(30))!.Speed is not null, "thirty seconds is still fresh");
        var stale = metrics.Shown(last.AddSeconds(31))!;
        Expect(stale.Speed is null, "no output for thirty seconds");
        Expect(stale.CachePercent, 90);
        // 慢到 1 tok/s 以下：两项里只剩缓存率；都没有就整组隐藏。
        var slow = SessionMetrics.Compute(new[] { Sample(1, 90, 100, 50, 100), Sample(2, 90, 100, 50, 100) }, last);
        Expect(slow.Speed!.Value < 1 && slow.Shown(last)!.Speed is null, "under 1 tok/s");
        var noCache = SessionMetrics.Compute(new[] { Sample(1, 0, 100, 50, 100, cache: false), Sample(2, 0, 100, 50, 100, cache: false) }, last);
        Expect(noCache.Shown(last) is null, "neither to show");
    }

    private static void RoundsDown(string dir)
    {
        // 996 / 1000 = 99.6% 向下取整是 99。
        Expect(SessionMetrics.Compute(new[] { Sample(1, 498, 500, 10, 1), Sample(2, 498, 500, 10, 1) }, null).CachePercent, 99);
        Expect(SessionMetrics.Compute(new[] { Sample(1, 500, 500, 10, 1), Sample(2, 500, 500, 10, 1) }, null).CachePercent, 100);
        Expect(SessionMetrics.Compute(new[] { Sample(1, 1, 1000, 10, 1), Sample(2, 0, 1000, 10, 1) }, null).CachePercent, 0);
        // 分母是 0：没有缓存率。
        Expect(SessionMetrics.Compute(new[] { Sample(1, 0, 0, 10, 1), Sample(2, 0, 0, 10, 1) }, null).CachePercent is null, "empty denominator");
    }

    private static void SpeedText(string dir)
    {
        Expect(StatusMetrics.SpeedText(41.4), "41");
        Expect(StatusMetrics.SpeedText(41.5), "42");
        Expect(StatusMetrics.SpeedText(999.4), "999");
        Expect(StatusMetrics.SpeedText(1000), "1.0k");
        Expect(StatusMetrics.SpeedText(1234), "1.2k");
        Expect(StatusMetrics.SpeedText(1299), "1.2k");
        Expect(StatusMetrics.SpeedText(999.6), "1.0k");
    }

    // MARK: - reading

    private static void IncrementalRead(string dir)
    {
        // 开头一大段旧日志（缓存全没中）不该被读：尾部的请求够了就不往前翻。
        var old = new List<string>();
        for (var i = 0; i < 4000; i++) old.Add(Asst("old" + i, i, 100, 10, 0, 0) + new string(' ', 800));
        var path = Write(dir, old.ToArray());
        // 尾部五个请求：p1、p2 各命中 90/100，加 ThreeRequests 里的三个（540/600）。
        Append(path, Api(0), Asst("p1", 2, 10, 100, 90, 0), Api(3), Asst("p2", 5, 10, 100, 90, 0));
        Append(path, ThreeRequests().Select(line => line.Replace("req_", "req_new")).ToArray());
        var file = new SessionMetricsFile(path, () => new ClaudeMetricsParser());
        Expect(file.Update()!.CachePercent, 90);
        // 追加一个请求（命中 100/100），最后一行写了一半：写完整之前不算。
        var next = Asst("4", 15, 0, 100, 100, 0);
        File.AppendAllText(path, Api(11.5) + Environment.NewLine + next[..40]);
        Expect(file.Update()!.CachePercent, 90);
        File.AppendAllText(path, next[40..] + Environment.NewLine);
        // 最近 5 次：p2（90/100）、请求 1、2、3（540/600）、请求 4（100/100）→ 730/800 = 91.25%。
        Expect(file.Update()!.CachePercent, 91);
        // 读不了的文件没有读数，不抛。
        Expect(new SessionMetricsFile(Path.Combine(dir, "missing.jsonl"), () => new ClaudeMetricsParser()).Update() is null, "missing file");
    }

    private static void Rewritten(string dir)
    {
        var path = Write(dir, ThreeRequests());
        var file = new SessionMetricsFile(path, () => new ClaudeMetricsParser());
        Expect(file.Update()!.CachePercent, 90);
        Write(dir, Api(0), Asst("z1", 2, 100, 100, 0, 0), Api(3), Asst("z2", 5, 100, 100, 0, 0));
        var again = file.Update()!;
        Expect(again.Requests, 2);
        Expect(again.CachePercent, 0);
    }

    // MARK: - the store

    private static ScannedSession Session(string id, string? path, ActivityState status = ActivityState.Working, TriggerTool tool = TriggerTool.Claude) =>
        new(tool, id, "/work", "Refactor the card", T0, status, path, null, SessionLaunchTarget.Cli);

    private static void Throttle(string dir)
    {
        var clock = TimeSpan.Zero;
        var store = new SessionMetricsStore(() => clock);
        var path = Write(dir, ThreeRequests());
        var session = Session("a", path);
        Expect(store.Refresh(new[] { session }), "the first read is a change");
        Expect(store.Snapshot()[session.Id].CachePercent, 90);
        // 追加一个全不命中的请求（命中 0/1000），两秒后问：还是旧的。
        Append(path, Api(12), Asst("4", 14, 1000, 100, 0, 0), Api(15), Asst("5", 17, 1000, 100, 0, 0));
        clock = TimeSpan.FromSeconds(2);
        Expect(!store.Refresh(new[] { session }), "within five seconds nothing is read");
        Expect(store.Snapshot()[session.Id].CachePercent, 90);
        clock = TimeSpan.FromSeconds(5);
        Expect(store.Refresh(new[] { session }), "five seconds on, it is read again");
        // 最近 5 次：请求 2（190/200）、3（270/300）、4（0/1000）、5（0/1000），和请求 1（80/100）→ 540/2600 = 20%。
        Expect(store.Snapshot()[session.Id].CachePercent, 20);
        // 又过 5 秒没有新内容：读了，读数没变，不算变化。
        clock = TimeSpan.FromSeconds(10);
        Expect(!store.Refresh(new[] { session }), "nothing new");
    }

    private static void ClockRewind(string dir)
    {
        var clock = TimeSpan.FromMinutes(10);
        var store = new SessionMetricsStore(() => clock);
        var path = Write(dir, ThreeRequests());
        var session = Session("a", path);
        store.Refresh(new[] { session });
        Append(path, Api(12), Asst("4", 14, 1000, 100, 0, 0), Api(15), Asst("5", 17, 1000, 100, 0, 0));
        clock = TimeSpan.FromMinutes(1);
        Expect(store.Refresh(new[] { session }), "the clock went back: it is still read");
        Expect(store.Snapshot()[session.Id].CachePercent, 20);
    }

    private static void NoRequestId(string dir)
    {
        string Old(string line) => line.Replace("\"requestId\":\"req_a\",", "");
        var path = Write(dir, Api(0), Old(Asst("a", 2, 100, 50, 100, 0, stop: null)), Old(Asst("a", 3, 100, 100, 100, 0, stop: "end_turn")));
        Expect(Claude(path)!.Requests, 1);
    }

    private static void WorkingOnly(string dir)
    {
        var store = new SessionMetricsStore(() => TimeSpan.Zero);
        var path = Write(dir, ThreeRequests());
        var sessions = new[]
        {
            Session("work", path),
            Session("waiting", path, ActivityState.NeedsYou),
            Session("idle", path, ActivityState.Idle),
            Session("grok", path, tool: TriggerTool.Grok),
            Session("nopath", null),
            Session("zst", path + ".zst"),
        };
        store.Refresh(sessions);
        Expect(string.Join(",", store.Snapshot().Keys), "claude:work");
        // 会话不在跑了，读数也撤掉。
        Expect(store.Refresh(new[] { Session("work", path, ActivityState.NeedsYou) }), "dropped");
        Expect(store.Snapshot().Count, 0);
    }

    // MARK: - the card's rows

    private static void RowsCarryMetrics(string dir)
    {
        var now = T0.AddSeconds(10);
        var shown = new SessionMetrics(92, 41.4, 5, now.AddSeconds(-1));
        var metrics = new Dictionary<string, SessionMetrics> { ["claude:a"] = shown, ["claude:b"] = shown, ["claude:c"] = shown };
        var running = Session("a", "/work/a.jsonl") with { Modified = now };
        var waiting = Session("b", "/work/b.jsonl", ActivityState.NeedsYou) with { Modified = now.AddMinutes(-1) };
        var rows = StatusTaskRows.Build(new[] { running, waiting }, new Dictionary<string, PhaseReading>(), now, pixel: false, metrics);
        var a = rows.Single(row => row.Id == "claude:a");
        Expect(a.Metrics, new StatusMetrics(92, "41"));
        Expect(rows.Single(row => row.Id == "claude:b").Metrics is null, "a conversation that waits on you shows none");
        // key 里没有数字：读数变了不重建；有没有缓存率、有没有速度变了才重建。
        string Key(SessionMetrics m) => StatusTaskRows.Key(StatusTaskRows.Build(new[] { running }, new Dictionary<string, PhaseReading>(), now, false,
            new Dictionary<string, SessionMetrics> { ["claude:a"] = m }));
        Expect(Key(shown), Key(new SessionMetrics(91, 77, 9, now)));
        Expect(Key(shown) != Key(new SessionMetrics(91, null, 5, now)), "the speed went");
        Expect(Key(shown) != Key(new SessionMetrics(null, 41, 5, now)), "the cache rate went");
        // 太久没有新输出：速度撤掉，缓存率留着；一项都没有时这一行没有指标。
        var stale = StatusTaskRows.Build(new[] { running }, new Dictionary<string, PhaseReading>(), now.AddSeconds(60), false, metrics);
        Expect(stale[0].Metrics, new StatusMetrics(92, null));
        var empty = StatusTaskRows.Build(new[] { running }, new Dictionary<string, PhaseReading>(), now, false,
            new Dictionary<string, SessionMetrics> { ["claude:a"] = new SessionMetrics(null, null, 5, now) });
        Expect(empty[0].Metrics is null, "nothing to show, no group");
    }
}
