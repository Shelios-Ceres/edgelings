using System.Text.Json;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// A Codex account switch while a refresh is in flight: the old request must
/// not land its account's numbers on the new one, and a result for a login
/// that has changed is dropped without an auto-switch. Claude's half of the
/// same refresh still commits.
public static class CodexSwitchRefreshTests
{
    private static AppUsage Usage(double fiveHour) =>
        new(new WindowUsage(fiveHour, null, null) { Observed = true }, new WindowUsage(0.1, null, null) { Observed = true });

    public static void RunAll()
    {
        LoginIdentity();
        SwitchStartsANewRefresh();
        ChangedLoginIsDropped();
        UnchangedLoginCommits();
        NewLoginFailureShowsTheError();
        CachedNumbersKeepTheirLogin();
        Console.WriteLine("CodexSwitchRefreshTests GREEN");
    }

    private static string? Login(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CodexCredentials.LoginIn(document.RootElement);
    }

    private static void LoginIdentity()
    {
        Expect(Login("""{"tokens":{"account_id":"acct-1","access_token":"t1"}}"""), "account:acct-1");
        Expect(Login("""{"tokens":{"account_id":"acct-1","access_token":"t2"}}"""), "account:acct-1");
        var fallback = Login("""{"tokens":{"access_token":"secret-token-1"}}""");
        Expect(fallback is not null && !fallback.Contains("secret-token-1"), "the access token itself is never kept");
        Expect(fallback != Login("""{"tokens":{"access_token":"secret-token-2"}}"""), "another token is another login");
        Expect(Login("""{"tokens":{}}""") is null, "no login");
        // Two members of one Team workspace share the account id.
        var alice = Login(Member("team-1", "user-alice", "t1"));
        Expect(alice != Login(Member("team-1", "user-bob", "t1")), "another member of the same workspace is another login");
        Expect(alice == Login(Member("team-1", "user-alice", "t2")), "a refreshed token is the same login");
        Console.WriteLine("PASS a login is told by its account and user, else by a hash of its token");
    }

    private static string Member(string account, string user, string generation)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = user,
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = account, chatgpt_user_id = user },
            ["generation"] = generation,
        });
        var jwt = "e30." + Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fake";
        return JsonSerializer.Serialize(new { tokens = new { account_id = account, id_token = jwt, access_token = "FAKE-" + generation } });
    }

    private static UsageStore Store(Func<CancellationToken, Task<AppUsage>> codex, Func<string?> login, List<string> log)
    {
        var store = new UsageStore(restore: false)
        {
            CodexFetcher = codex,
            ClaudeFetcher = _ => Task.FromResult(Usage(0.3)),
            CodexLogin = login,
        };
        store.AutoSwitch = _ => log.Add("autoswitch");
        return store;
    }

    private static void SwitchStartsANewRefresh()
    {
        var log = new List<string>();
        var gates = new List<(CancellationToken Token, TaskCompletionSource<AppUsage> Result)>();
        var store = Store(token =>
        {
            var result = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates) gates.Add((token, result));
            return result.Task;
        }, () => "account:a", log);

        store.Refresh();
        WaitFor(() => { lock (gates) return gates.Count == 1; });
        store.Refresh();
        Expect(store.Loading, "a plain refresh while one is in flight is still ignored");
        Thread.Sleep(50);
        lock (gates) Expect(gates.Count, 1);

        store.RefreshAfterCodexSwitch();
        WaitFor(() => { lock (gates) return gates.Count == 2; });
        Expect(gates[0].Token.IsCancellationRequested, "the old request is cancelled");
        Expect(!gates[1].Token.IsCancellationRequested, "the new one runs");

        // The old account's numbers arrive late: they are dropped.
        gates[0].Result.SetResult(Usage(0.9));
        Thread.Sleep(100);
        Expect(store.Codex == AppUsage.Empty, "the old request's result is not shown");
        Expect(store.Loading, "and does not end the new refresh");

        gates[1].Result.SetResult(Usage(0.2));
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        WaitFor(() => !store.Loading);
        Expect(store.Codex.FiveHour.UsedPercent, 0.2);
        Console.WriteLine("PASS a switch during a refresh cancels it and starts a new one");
    }

    private static void ChangedLoginIsDropped()
    {
        var log = new List<string>();
        var login = "account:a";
        var store = Store(_ => Task.FromResult(Usage(1.0)), () => login, log);
        // The login changes after the fetch started, before its result lands.
        store.CodexFetcher = _ =>
        {
            login = "account:b";
            return Task.FromResult(Usage(1.0));
        };
        store.Refresh();
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        WaitFor(() => !store.Loading);
        Expect(store.Codex == AppUsage.Empty, "Codex's half is dropped");
        Expect(store.Claude.FiveHour.UsedPercent, 0.3);
        Expect(log.Count, 0);
        Console.WriteLine("PASS a result for a login that has changed is dropped and does not auto-switch");
    }

    private static void UnchangedLoginCommits()
    {
        var log = new List<string>();
        var store = Store(_ => Task.FromResult(Usage(1.0)), () => "account:a", log);
        store.Refresh();
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        WaitFor(() => !store.Loading);
        Expect(store.Codex.FiveHour.UsedPercent, 1.0);
        Expect(log.Count, 1);
        Console.WriteLine("PASS a result for the same login commits and may auto-switch");
    }

    private static void NewLoginFailureShowsTheError()
    {
        var log = new List<string>();
        var login = "account:a";
        string? savedFor = null;
        var store = Store(_ => Task.FromResult(Usage(1.0)), () => login, log);
        store.SaveSnapshot = (_, _, _, _, codexLogin) => savedFor = codexLogin;
        RefreshAndWait(store);
        Expect(store.Codex.FiveHour.UsedPercent, 1.0);
        Expect(savedFor, "account:a");

        // Same login, failed fetch: its own numbers are kept.
        store.CodexFetcher = _ => Task.FromResult(AppUsage.ErrorPair("network drop"));
        RefreshAndWait(store);
        Expect(store.Codex.FiveHour.UsedPercent == 1.0 && store.Codex.FiveHour.Kept, "a login's own numbers survive its failed fetch");

        // Auto-switch activated account b, whose parked token has expired.
        login = "account:b";
        store.CodexFetcher = _ => Task.FromResult(AppUsage.ErrorPair("auth expired — codex login"));
        RefreshAndWait(store);
        Expect(store.Codex.FiveHour.UsedPercent == 0 && !store.Codex.FiveHour.Kept,
            "account a's 100% is not shown under account b's login");
        Expect(store.Codex.FiveHour.Error, "auth expired — codex login");
        Expect(store.CodexUpdatedAt is null, "and a's sync time is not b's");
        Console.WriteLine("PASS after a switch, a failed first fetch shows its error, not the previous account's numbers");
    }

    private static void CachedNumbersKeepTheirLogin()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var saved = UsageCachePolicy.SnapshotForSave(Usage(0.3), Usage(0.9), null, now, codexLogin: "account:a")!;
        var restored = UsageCachePolicy.RestoredSnapshot(
            JsonSerializer.Deserialize<UsageCacheSnapshot>(JsonSerializer.Serialize(saved))!, now.AddMinutes(1), UsageCachePolicy.KeepFor)!;
        Expect(restored.CodexLogin, "account:a");
        var claudeOnly = UsageCachePolicy.SnapshotForSave(Usage(0.4), AppUsage.Empty, restored, now.AddMinutes(2), fetchedCodex: false, codexLogin: "account:b")!;
        Expect(claudeOnly.CodexLogin, "account:a");
        Expect(UsageCachePolicy.SameCodexLogin("account:a", "account:a"), "the same login owns its numbers");
        Expect(!UsageCachePolicy.SameCodexLogin("account:a", "account:b"), "another login does not inherit them");
        Expect(UsageCachePolicy.SameCodexLogin(null, "account:b"), "a snapshot from before logins were saved stays usable");
        Expect(UsageCachePolicy.SameCodexLogin("account:a", null), "an unreadable auth.json is not another login");
        Console.WriteLine("PASS cached Codex numbers carry the login they were fetched for");
    }

    private static void RefreshAndWait(UsageStore store)
    {
        store.Refresh();
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        WaitFor(() => !store.Loading);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new Exception("timed out waiting for the store");
            Thread.Sleep(10);
        }
    }
}
