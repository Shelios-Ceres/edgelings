using System.Text;
using System.Text.Json;
using AgentIsland.Localization;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

public static class CursorQuotaStateTests
{
    public static void RunAll()
    {
        Run("new account failure cannot inherit old account quota", ChangedAccount);
        Run("same account failures retain its own quota without renewing time", SameAccount);
        Run("logout and removed database clear cached quota and identity", SignedOut);
        Run("legacy unbound quota is invalidated when identity is read", LegacyCache);
        Run("saved quota is bound to the user across a JSON round trip", CacheRoundTrip);
        Run("quota parser requires a finite reported percent including explicit zero", ParsePercent);
        Run("read failure has a localized explanation", LocalizedReadFailure);
        Console.WriteLine("CursorQuotaStateTests GREEN");
    }

    private static CursorCredentials.Snapshot Login(string user, string token = "dummy-token") =>
        new(new CursorCredentials.Session(token, user), "ultra", user + "@example.invalid");

    private static CursorUsageStore.Cached Cache(string? user = "account-a") => new()
    {
        Snapshot = new CursorUsageSnapshot(0.62, DateTimeOffset.Now.AddDays(3), "pro"),
        UpdatedAt = DateTimeOffset.Now.AddMinutes(-10),
        UserId = user,
    };

    private static void ChangedAccount()
    {
        var writes = new List<CursorUsageStore.Cached?>();
        var store = new CursorUsageStore(Cache(), writes.Add);
        store.Apply(new CursorUsageFetcher.Outcome.Failed("network timeout"), Login("account-b"));
        Expect(store.Snapshot is null && store.LastUpdated is null, "account A's numbers and sync time cannot label account B");
        Expect(store.AccountEmail == "account-b@example.invalid" && store.PlanBadge == "ULTRA", "current readable identity must not inherit the old plan");
        Expect(writes.Count == 1 && writes[0] is null, "old account quota must not return after restart");
        Expect(store.Window.HasError && !store.Window.Observed && !store.Window.Kept, "new account failure is unknown, not a retained zero");
        var fresh = new CursorUsageSnapshot(0, null, "ultra");
        store.Apply(new CursorUsageFetcher.Outcome.Success(fresh), Login("account-b"));
        Expect(store.Snapshot == fresh && store.Window.Observed && !store.Window.HasError, "successful account B zero resumes a known balance");
        Expect(writes[^1]?.UserId == "account-b", "the new quota is saved against B's stable user ID");
    }

    private static void SameAccount()
    {
        var cached = Cache();
        var writes = new List<CursorUsageStore.Cached?>();
        var store = new CursorUsageStore(cached, writes.Add);
        store.Apply(new CursorUsageFetcher.Outcome.Failed("network timeout"), Login("account-a", "rotated-token"));
        Expect(store.Snapshot == cached.Snapshot && store.LastUpdated == cached.UpdatedAt, "same user retains its observed balance and timestamp across token rotation");
        Expect(store.Window.Kept && store.Window.Observed, "same user's failure is visibly retained data");
        store.ApplyReadFailure();
        Expect(store.Snapshot == cached.Snapshot && store.AccountEmail == "account-a@example.invalid", "a database read failure cannot replace the established identity");
        store.Apply(new CursorUsageFetcher.Outcome.ReauthRequired(), Login("account-a"));
        Expect(store.Snapshot == cached.Snapshot && writes.Count == 0, "same-user 401 retains last good data without saving a new timestamp");
    }

    private static void SignedOut()
    {
        foreach (var missingDatabase in new[] { false, true })
        {
            var writes = new List<CursorUsageStore.Cached?>();
            var store = new CursorUsageStore(Cache(), writes.Add);
            store.Apply(new CursorUsageFetcher.Outcome.Failed("timeout"), Login("account-a"));
            if (missingDatabase) store.Apply(new CursorUsageFetcher.Outcome.NotInstalled(), null);
            else store.Apply(new CursorUsageFetcher.Outcome.ReauthRequired(), new CursorCredentials.Snapshot(null, "pro", "old@example.invalid"));
            Expect(store.Snapshot is null && store.LastUpdated is null && store.AccountEmail is null && store.PlanBadge is null,
                "logout/missing database clears all prior quota identity, even if local email rows survive");
            Expect(writes.Count == 1 && writes[0] is null, "logout removes persisted quota");
            Expect(!store.Window.Observed && store.Window.HasError, "missing quota is unknown even when no network error exists");
        }
    }

    private static void LegacyCache()
    {
        foreach (var readFailure in new[] { false, true })
        {
            var writes = new List<CursorUsageStore.Cached?>();
            var store = new CursorUsageStore(Cache(user: null), writes.Add);
            Expect(store.AccountEmail is null, "legacy cache is initially unattributed");
            if (readFailure) store.ApplyReadFailure();
            else store.Apply(new CursorUsageFetcher.Outcome.Failed("timeout"), Login("account-a"));
            Expect(store.Snapshot is null && store.LastUpdated is null, "unknown-owner cache is not assigned to a newly read user");
            Expect(writes.Count == 1 && writes[0] is null, "unbound legacy data is invalidated once ownership needs checking");
        }
    }

    private static void CacheRoundTrip()
    {
        CursorUsageStore.Cached? saved = null;
        var store = new CursorUsageStore(null, value => saved = value);
        store.Apply(new CursorUsageFetcher.Outcome.Success(new CursorUsageSnapshot(0, null, "pro")), Login("account-a"));
        var json = JsonSerializer.Serialize(saved);
        Expect(!json.Contains("dummy-token", StringComparison.Ordinal), "persist only stable identity, never credentials");
        var restored = JsonSerializer.Deserialize<CursorUsageStore.Cached>(json)!;
        var restarted = new CursorUsageStore(restored, _ => { });
        restarted.Apply(new CursorUsageFetcher.Outcome.Failed("timeout"), Login("account-a"));
        Expect(restarted.Snapshot?.UsedPercent == 0 && restarted.Window.Kept, "a real zero survives a restart and same-user failure");
        restarted.Apply(new CursorUsageFetcher.Outcome.Failed("timeout"), Login("account-b"));
        Expect(restarted.Snapshot is null, "persisted ownership also prevents account B from inheriting zero");
    }

    private static void ParsePercent()
    {
        static CursorUsageSnapshot? Parse(string json) => CursorUsageFetcher.ParseUsage(Encoding.UTF8.GetBytes(json));
        foreach (var json in new[]
        {
            "{}", "{\"planUsage\":{}}", "{\"planUsage\":{\"totalPercentUsed\":null}}",
            "{\"planUsage\":{\"totalPercentUsed\":\"NaN\"}}",
            "{\"planUsage\":{\"totalPercentUsed\":\"Infinity\"}}",
            "{\"planUsage\":{\"totalPercentUsed\":1e999}}",
            "{\"planUsage\":{\"totalPercentUsed\":\"NaN\",\"autoPercentUsed\":50}}",
            "{\"planUsage\":{\"totalPercentUsed\":{}}}",
        }) Expect(Parse(json) is null, "invalid/missing percentages must be a failed fetch: " + json);
        Expect(Parse("{\"planUsage\":{\"totalPercentUsed\":0}}")?.UsedPercent == 0, "explicit numeric zero remains valid");
        Expect(Parse("{\"planUsage\":{\"totalPercentUsed\":\"0\"}}")?.UsedPercent == 0, "Connect's string zero remains valid");
        Expect(Parse("{\"planUsage\":{\"autoPercentUsed\":30,\"apiPercentUsed\":55}}")?.UsedPercent == 0.55, "reported halves remain supported when total is omitted");
        Expect(Parse("{\"planUsage\":{\"autoPercentUsed\":0}}")?.UsedPercent == 0, "a reported zero half is distinguishable from both halves missing");
    }

    private static void LocalizedReadFailure()
    {
        var previous = L10n.Current;
        try
        {
            L10n.Current = L10n.Language.SimplifiedChinese;
            var store = new CursorUsageStore(null, _ => { });
            store.ApplyReadFailure();
            Expect(store.ErrorCaption == "暂时无法读取 Cursor 数据", "database error uses the active language");
        }
        finally { L10n.Current = previous; }
    }

    private static void Run(string name, Action test) { test(); Console.WriteLine($"PASS Cursor quota: {name}"); }
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
}
