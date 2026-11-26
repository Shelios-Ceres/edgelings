using System.Globalization;
using System.Text;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Codex 额度改读 rollout 里的 token_count.rate_limits。测试数据是按本机真实
/// 结构写的假 rollout（session_meta 在第一行，token_count 事件散在后面）。
public static class CodexRolloutQuotaTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        Run("a normal reading: primary becomes the single weekly window", Normal);
        Run("a null secondary reads as the single-window shape", NullSecondary);
        Run("a window past its reset waits for sync instead of showing the old balance", ResetPassed);
        Run("the newest reading wins across rollouts, by event time", NewestWins);
        Run("no rollouts, an empty folder and a rollout without readings are no data", NoData);
        Run("a truncated first tail line and an unfinished last line are tolerated", PartialLines);
        Run("only the account's own rollouts are used; no match shows nothing", MultiAccount);
        Run("an unknown account takes the newest rollout", UnknownAccount);
        Run("other limit ids are not the main quota", OtherLimitId);
        Run("account ids come out of the login string, never from a token hash", AccountIds);
        Run("two members of one workspace never see each other's quota", SameWorkspaceOtherUser);
        Run("a rollout from before creator_account_id matches", LegacyRolloutMatches);
        Run("other accounts' newer rollouts do not use up the file budget", OtherAccountsDontTakeSlots);
        Run("archived_sessions rollouts are candidates too", ArchivedRoot);
        Run("a resets_at out of range drops that line only", BadResetsAtSkipsTheLine);
        Run("auth.json states: known login, unreadable, API key, absent", AuthStates);
        Run("no data keeps a two-window reading as it was", NoDataKeepsWindowShape);
        Run("worst case: many big files with no readings stay cheap", WorstCaseCost);
        Run("old numbers are not saved as just synced after a no-data refresh", NoDataKeepsTheSavedTime);
        Run("a reading across a 64KB chunk edge or a split multibyte character is found", ChunkBoundaries);
        Run("the no-reading cache is dropped when the file changes", NoReadingCacheInvalidates);
        Run("a fetch cancelled before it starts returns a result", CancelledFetchReturns);
        Run("a no-data reading is not a failure, and keeps the event time as the sync time", StoreBehaviour);
        Console.WriteLine("CodexRolloutQuotaTests GREEN");
    }

    private static string Meta(string account, string? user = null)
    {
        var u = user is null ? "" : $",\"creator_user_id\":\"{user}\"";
        return $$$"""{"timestamp":"2026-11-20T07:59:00.000Z","type":"session_meta","payload":{"id":"s","creator_account_id":"{{{account}}}"{{{u}}},"cwd":"C:/x","base_instructions":"{{{new string('x', 3000)}}}"}}""";
    }

    /// 旧版 rollout：session_meta 里没有 creator_account_id。
    private static string LegacyMeta() =>
        $$$"""{"timestamp":"2026-11-20T07:59:00.000Z","type":"session_meta","payload":{"id":"s","cwd":"C:/x","base_instructions":"{{{new string('x', 3000)}}}"}}""";

    private static AppUsage? Rd(DirectoryInfo root, string? account, string? user = null) =>
        CodexRolloutQuota.Read(new[] { root.FullName }, account is null ? null : new CodexRolloutQuota.Identity(account, user));

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Count(DateTimeOffset at, double used, long resetsAt, string? secondary = null, string limitId = "codex", string plan = "pro")
    {
        var percent = used.ToString(CultureInfo.InvariantCulture);
        return $$$$"""{"timestamp":"{{{{Stamp(at)}}}}","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"{{{{limitId}}}}","limit_name":null,"primary":{"used_percent":{{{{percent}}}},"window_minutes":10080,"resets_at":{{{{resetsAt}}}}},"secondary":{{{{secondary ?? "null"}}}},"credits":{"has_credits":false},"plan_type":"{{{{plan}}}}"}}}""";
    }

    private static string Noise(DateTimeOffset at) =>
        $$$"""{"timestamp":"{{{Stamp(at)}}}","type":"event_msg","payload":{"type":"agent_message","message":"hi"}}""";

    private static DirectoryInfo Root() => Directory.CreateTempSubdirectory("limen-codex-rollouts-");

    private static string Write(DirectoryInfo root, string name, DateTimeOffset modified, params string[] lines)
    {
        var dir = Path.Combine(root.FullName, "2026", "11", "20");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-{name}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private static void With(Action<DirectoryInfo> body)
    {
        var root = Root();
        try { body(root); }
        finally { root.Delete(true); }
    }

    private static void Normal() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "a", Base.AddMinutes(10), Meta("acct-1"), Noise(Base), Count(Base.AddMinutes(1), 12, resets), Noise(Base.AddMinutes(2)), Count(Base.AddMinutes(9), 37.5, resets));
        var usage = Rd(root, "acct-1");
        Expect(usage is not null, "a reading must be found");
        Expect(usage!.FiveHour.UsedPercent, 0.375);
        Expect(usage.FiveHour.Observed, "an explicit percentage is an observation");
        Expect(usage.FiveHour.ResetAt, DateTimeOffset.FromUnixTimeSeconds(resets));
        Expect(usage.FiveHour.PeriodSeconds, 604800.0);
        Expect(usage.Plan, "pro");
        Expect(usage.ObservedAt, Base.AddMinutes(9));
    });

    private static void NullSecondary() => With(root =>
    {
        Write(root, "a", Base, Meta("acct-1"), Count(Base, 10, Base.AddDays(1).ToUnixTimeSeconds()));
        var usage = Rd(root, "acct-1")!;
        Expect(usage.SecondaryMissing, "a null secondary is the single-window shape");
        Expect(usage.Weekly.HasError && !usage.FiveHour.HasError, "only the missing slot carries the sentinel");
        Expect(UsageCachePolicy.CacheableCopy(usage) is not null, "and it can still be cached");

        var secondary = """{"used_percent":80,"window_minutes":300,"resets_at":1795565984}""";
        Write(root, "b", Base.AddMinutes(5), Meta("acct-1"), Count(Base.AddMinutes(5), 10, 1795565984, secondary));
        var both = Rd(root, "acct-1")!;
        Expect(both.Weekly.UsedPercent, 0.8);
        Expect(both.Weekly.PeriodSeconds, 18000.0);
    });

    private static void ResetPassed() => With(root =>
    {
        var resets = Base.AddHours(-1).ToUnixTimeSeconds();
        Write(root, "a", Base, Meta("acct-1"), Count(Base.AddDays(-8), 100, resets));
        var usage = Rd(root, "acct-1")!;
        var shown = usage.FiveHour.At(Base);
        Expect(shown.AwaitingSync, "a window whose reset has passed awaits a new reading");
        Expect(shown.UsedPercent, 0.0);
        // 读数的时间是事件时间，不是此刻：三天前的数不能显示成“刚同步”。
        Expect(UsageCachePolicy.SuccessfulUpdateAt(usage, null, Base.AddDays(3)), Base.AddDays(-8));
    });

    private static void NewestWins() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        // 修改时间较新的文件里，最后一个事件反而更早：以事件时间为准。
        Write(root, "new-file-old-event", Base.AddHours(2), Meta("acct-1"), Count(Base.AddMinutes(1), 10, resets));
        Write(root, "old-file-new-event", Base.AddHours(1), Meta("acct-1"), Count(Base.AddMinutes(50), 55, resets));
        Write(root, "oldest", Base.AddMinutes(30), Meta("acct-1"), Count(Base.AddMinutes(20), 90, resets));
        var usage = Rd(root, "acct-1")!;
        Expect(usage.FiveHour.UsedPercent, 0.55);
    });

    private static void NoData() => With(root =>
    {
        Expect(CodexRolloutQuota.Read(new[] { Path.Combine(root.FullName, "missing") }, new CodexRolloutQuota.Identity("acct-1", null)) is null, "no sessions folder");
        Expect(Rd(root, "acct-1") is null, "an empty sessions folder");
        Write(root, "a", Base, Meta("acct-1"), Noise(Base));
        Expect(Rd(root, "acct-1") is null, "a rollout with no token_count");
        var fetched = AppUsage.NoData;
        Expect(fetched.FiveHour.Error, "no data");
    });

    private static void PartialLines() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        var big = Noise(Base).Replace("\"hi\"", "\"" + new string('y', 400 * 1024) + "\"");
        // 读数在末尾 64KB 块之外，要往前读好几块才找到；末行没写完。
        var lines = new List<string> { Meta("acct-1"), Count(Base, 42, resets), big, Count(Base, 43, resets)[..^25] };
        Write(root, "a", Base, lines.ToArray());
        var usage = Rd(root, "acct-1")!;
        Expect(usage.FiveHour.UsedPercent, 0.42);
    });

    private static void MultiAccount() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "mine-old", Base.AddHours(1), Meta("acct-1"), Count(Base.AddHours(1), 20, resets));
        Write(root, "other-new", Base.AddHours(3), Meta("acct-2"), Count(Base.AddHours(3), 99, resets, plan: "plus"));
        var mine = Rd(root, "acct-1")!;
        Expect(mine.FiveHour.UsedPercent, 0.2);
        Expect(mine.Plan, "pro");
        var other = Rd(root, "acct-2")!;
        Expect(other.FiveHour.UsedPercent, 0.99);
        Expect(Rd(root, "acct-3") is null, "an account with no rollout must not show another's numbers");
    });

    private static void UnknownAccount() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "a", Base.AddHours(1), Meta("acct-1"), Count(Base.AddHours(1), 20, resets));
        Write(root, "b", Base.AddHours(3), Meta("acct-2"), Count(Base.AddHours(3), 99, resets));
        Expect(Rd(root, null)!.FiveHour.UsedPercent, 0.99);
    });

    private static void OtherLimitId() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "a", Base, Meta("acct-1"), Count(Base, 30, resets), Count(Base.AddMinutes(1), 99, resets, limitId: "codex_other"));
        Expect(Rd(root, "acct-1")!.FiveHour.UsedPercent, 0.3);
    });

    private static void AccountIds()
    {
        Expect(CodexCredentials.IdentityOf("account:acct-1|user-9"), new CodexRolloutQuota.Identity("acct-1", "user-9"));
        Expect(CodexCredentials.IdentityOf("account:acct-1"), new CodexRolloutQuota.Identity("acct-1", null));
        Expect(CodexCredentials.IdentityOf("token:ABCDEF") is null, "a token hash is not an identity");
        Expect(CodexCredentials.IdentityOf(null) is null, "no login");
    }

    private static void SameWorkspaceOtherUser() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "alice", Base.AddHours(1), Meta("team", "alice"), Count(Base.AddHours(1), 20, resets));
        Write(root, "bob", Base.AddHours(3), Meta("team", "bob"), Count(Base.AddHours(3), 90, resets));
        Expect(Rd(root, "team", "alice")!.FiveHour.UsedPercent, 0.2);
        Expect(Rd(root, "team", "bob")!.FiveHour.UsedPercent, 0.9);
        Expect(Rd(root, "team", "carol") is null, "a third member of the workspace sees neither");
        // 登录串没有 user 段时只按 account。
        Expect(Rd(root, "team")!.FiveHour.UsedPercent, 0.9);
    });

    private static void LegacyRolloutMatches() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "old", Base, LegacyMeta(), Count(Base, 33, resets));
        Expect(Rd(root, "acct-1", "u")!.FiveHour.UsedPercent, 0.33);
        Write(root, "other", Base.AddHours(1), Meta("acct-2"), Count(Base.AddHours(1), 70, resets));
        Expect(Rd(root, "acct-1")!.FiveHour.UsedPercent, 0.33);
    });

    private static void OtherAccountsDontTakeSlots() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "mine", Base, Meta("acct-1"), Count(Base, 25, resets));
        for (var i = 0; i < CodexRolloutQuota.MaxFiles * 3; i++)
        {
            Write(root, $"other-{i}", Base.AddMinutes(10 + i), Meta("acct-2"), Count(Base.AddMinutes(10 + i), 99, resets));
        }
        Expect(Rd(root, "acct-1")!.FiveHour.UsedPercent, 0.25);
    });

    private static void ArchivedRoot() => With(root =>
    {
        var archived = Path.Combine(root.FullName, "archived");
        var live = Path.Combine(root.FullName, "live");
        Directory.CreateDirectory(archived);
        Directory.CreateDirectory(live);
        var path = Path.Combine(archived, "rollout-x.jsonl");
        File.WriteAllText(path, Meta("acct-1") + "\n" + Count(Base, 61, Base.AddDays(5).ToUnixTimeSeconds()) + "\n");
        var usage = CodexRolloutQuota.Read(new[] { live, archived }, new CodexRolloutQuota.Identity("acct-1", null));
        Expect(usage!.FiveHour.UsedPercent, 0.61);
    });

    private static void BadResetsAtSkipsTheLine() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        Write(root, "a", Base.AddMinutes(2), Meta("acct-1"), Count(Base, 15, resets), Count(Base.AddMinutes(1), 50, 99999999999999));
        Expect(Rd(root, "acct-1")!.FiveHour.UsedPercent, 0.15);
    });

    private static void AuthStates()
    {
        var root = Root();
        try
        {
            var resets = Base.AddDays(5).ToUnixTimeSeconds();
            Write(root, "a", Base, Meta("acct-1"), Count(Base, 15, resets));
            var roots = new[] { root.FullName };
            // 身份已知：按身份。
            Expect(CodexRolloutQuota.Fetch(roots, "account:acct-1|u", true).FiveHour.UsedPercent, 0.15);
            Expect(CodexRolloutQuota.Fetch(roots, "account:acct-9|u", true).IsNoData, "another account has nothing yet");
            // auth.json 在，但读不出账号（API key、暂时读不到）：不猜是谁的。
            Expect(CodexRolloutQuota.Fetch(roots, null, true).IsNoData, "unreadable auth.json shows no data");
            Expect(CodexRolloutQuota.Fetch(roots, "token:ABC", true).IsNoData, "an API-key login shows no data");
            // 完全没有 auth.json：有 rollout 就取最新的，没有就是“未找到 Codex 登录”。
            Expect(CodexRolloutQuota.Fetch(roots, null, false).FiveHour.UsedPercent, 0.15);
            var empty = Root();
            try
            {
                var none = CodexRolloutQuota.Fetch(new[] { empty.FullName }, null, false);
                Expect(none.FiveHour.Error, "no codex auth");
                // 这条错误会让 Codex 卡出现登录入口，和以前一样。
                Expect(AgentIsland.Model.AgentReports.MentionsAuthFailure(none.FiveHour.Error), "the sign-in offer still appears");
            }
            finally { empty.Delete(true); }
        }
        finally { root.Delete(true); }
    }

    private static void NoDataKeepsWindowShape()
    {
        var two = new AppUsage(
            new WindowUsage(0.4, Base.AddHours(2), null, 18000) { Observed = true },
            new WindowUsage(0.2, Base.AddDays(3), null, 604800) { Observed = true }, "pro");
        var merged = UsageCachePolicy.Merged(two, AppUsage.NoData);
        Expect(!merged.SecondaryMissing, "a two-window reading stays two windows");
        Expect(merged.FiveHour.UsedPercent, 0.4);
        Expect(merged.Weekly.UsedPercent, 0.2);
        Expect(!merged.FiveHour.HasError && !merged.Weekly.HasError, "and carries no error");
        // 真正的失败照旧标为保留。
        var failed = UsageCachePolicy.Merged(two, AppUsage.ErrorPair("network drop"));
        Expect(failed.FiveHour.Kept, "a real failure still marks the numbers kept");
    }

    // 最坏情况：40 个文件各约 6MB 都没有读数（普通事件，中间夹一行 600KB 的超长行）。
    // 只读账号匹配的前 MaxFiles 个，每个最多读 4MB 尾部；缓冲区来自池子。
    private static void WorstCaseCost() => With(root =>
    {
        var line = Noise(Base).Replace("\"hi\"", "\"" + new string('z', 900) + "\"");
        var big = Noise(Base).Replace("\"hi\"", "\"" + new string('z', 600 * 1024) + "\"");
        for (var i = 0; i < 40; i++)
        {
            var lines = new List<string> { Meta("acct-1") };
            for (var k = 0; k < 3000; k++) lines.Add(line);
            lines.Add(big);
            lines.AddRange(Enumerable.Repeat(line, 3000));
            Write(root, $"w{i}", Base.AddMinutes(i), lines.ToArray());
        }
        Expect(Rd(root, "acct-1") is null, "no readings anywhere"); // 同时预热池子和 JIT
        // 让每个文件的长度变一下，缓存失效，再量一次真正的读取。
        foreach (var path in Directory.EnumerateFiles(root.FullName, "*.jsonl", SearchOption.AllDirectories)) File.AppendAllText(path, "\n");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Rd(root, "acct-1");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"MEASURE worst case, 40 files x ~6MB, no readings: {clock.ElapsedMilliseconds} ms, {allocated / 1024} KB allocated");
        Expect(allocated < 3 * 1024 * 1024, $"a scan allocates little ({allocated} bytes)");

        before = GC.GetAllocatedBytesForCurrentThread();
        clock.Restart();
        Rd(root, "acct-1");
        Console.WriteLine($"MEASURE unchanged files are skipped: {clock.ElapsedMilliseconds} ms, {(GC.GetAllocatedBytesForCurrentThread() - before) / 1024} KB allocated");
    });

    private static void StoreBehaviour()
    {
        // 没有 rollout 时 Codex 不算失败（不弹 "Codex stale"）；真正的失败才算。
        var store = new UsageStore(restore: false)
        {
            CodexFetcher = _ => Task.FromResult(AppUsage.NoData),
            ClaudeFetcher = _ => Task.FromResult(AppUsage.NoData),
            CodexLogin = () => "account:a",
            ClaudeShown = () => false,
            CodexShown = () => true,
        };
        var saves = new List<bool>();
        store.SaveSnapshot = (_, _, _, fetchedCodex, _) => saves.Add(fetchedCodex);
        void Refresh()
        {
            store.Refresh(fetchClaude: false);
            store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        }
        Refresh();
        Expect(!store.CodexRefreshFailed, "no rollout is a normal state, not a failure");
        Expect(store.CodexUpdatedAt is null, "and nothing was synced");

        store.CodexFetcher = _ => Task.FromResult(AppUsage.ErrorPair("network drop"));
        Refresh();
        Expect(store.CodexRefreshFailed, "a real failure still counts");

        // 读数的时间是 rollout 里的事件时间，不是轮询的时刻。
        var observed = DateTimeOffset.Now.AddDays(-3);
        var reading = new AppUsage(new WindowUsage(0.4, observed.AddDays(5), null, 604800) { Observed = true }, WindowUsage.Unknown, "pro") { ObservedAt = observed };
        store.CodexFetcher = _ => Task.FromResult(reading);
        saves.Clear();
        Refresh();
        Expect(store.CodexUpdatedAt, observed);
        Expect(!store.CodexRefreshFailed, "a good reading clears the failure");
        Expect(saves.Count == 1 && saves[0], "a real reading is saved as fetched");

        // 之后没有新读数：旧数字保留，不报失败，也不当作“刚取到”存盘。
        store.CodexFetcher = _ => Task.FromResult(AppUsage.NoData);
        saves.Clear();
        Refresh();
        Expect(store.Codex.FiveHour.UsedPercent, 0.4);
        Expect(!store.CodexRefreshFailed, "kept numbers with no new reading are not a failure");
        Expect(store.CodexUpdatedAt, observed);
        Expect(saves.Count == 1 && !saves[0], "a no-data refresh is not saved as fetched");
    }

    // 三天前的旧缓存，经过一次 NoData 刷新后，存盘时间不变；真读数才更新。
    private static void NoDataKeepsTheSavedTime()
    {
        var now = DateTimeOffset.Now;
        var threeDays = now.AddDays(-3);
        var old = new AppUsage(new WindowUsage(0.4, now.AddDays(2), null, 604800) { Observed = true }, WindowUsage.Unknown, "pro") { ObservedAt = threeDays };
        var existing = new UsageCacheSnapshot(AppUsage.Empty, old, threeDays, null, threeDays) { CodexLogin = "account:a" };
        // Store 对 NoData 传 fetchedCodex:false（见 StoreBehaviour），存盘时沿用原时间。
        var kept = UsageCachePolicy.SnapshotForSave(AppUsage.Empty, old, existing, now, fetchedClaude: false, fetchedCodex: false, "account:a");
        Expect(kept is null || kept.CodexUpdatedAt == threeDays, "a no-data refresh must not make old numbers look just synced");
        // 取到的数没有事件时间（Claude 式的即时读数）才记为此刻。
        var live = old with { ObservedAt = null };
        var fresh = UsageCachePolicy.SnapshotForSave(AppUsage.Empty, live, existing, now, fetchedClaude: false, fetchedCodex: true, "account:a");
        Expect(fresh!.CodexUpdatedAt, now);
    }

    // 读数行跨在 64KB 块的边界上，或多字节字符被块边界切开：把读数行在文件末尾的位置
    // 逐字节扫过一整个行长，每个位置都必须读到完整、不乱码的读数。
    private static void ChunkBoundaries() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        var plan = "计划计划计划";
        var reading = Count(Base, 44, resets, plan: plan);
        var lineBytes = Encoding.UTF8.GetByteCount(reading) + 1;
        for (var pad = 64 * 1024 - lineBytes - 3; pad <= 64 * 1024 + 3; pad++)
        {
            var filler = "{\"x\":\"" + new string('a', pad - 9) + "\"}";
            Write(root, "edge", Base.AddMinutes(pad), Meta("acct-1"), reading, filler);
            var usage = Rd(root, "acct-1");
            Expect(usage is not null, $"reading lost with {pad} bytes after it");
            Expect(usage!.FiveHour.UsedPercent, 0.44);
            Expect(usage.Plan, plan);
        }
    });

    // “这个文件没有读数”的缓存，文件一变（长度变了，修改时间没变也算）就失效。
    private static void NoReadingCacheInvalidates() => With(root =>
    {
        var resets = Base.AddDays(5).ToUnixTimeSeconds();
        var path = Write(root, "grow", Base, Meta("acct-1"), Noise(Base));
        Expect(Rd(root, "acct-1") is null, "nothing yet");
        Expect(Rd(root, "acct-1") is null, "still nothing, now from the cache");
        File.AppendAllText(path, Count(Base.AddMinutes(1), 77, resets) + "\n");
        File.SetLastWriteTimeUtc(path, Base.UtcDateTime); // 修改时间不变，只有长度变
        Expect(Rd(root, "acct-1")!.FiveHour.UsedPercent, 0.77);
    });

    // 已经取消的取数返回一个结果，不抛异常。
    private static void CancelledFetchReturns()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = UsageFetcher.FetchCodex("account:a", cts.Token).GetAwaiter().GetResult();
        Expect(result is not null, "a cancelled fetch still returns a result");
    }

    private static void Run(string name, Action test) { test(); Console.WriteLine($"PASS codex rollout quota: {name}"); }
}
