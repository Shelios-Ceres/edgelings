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
        Console.WriteLine("PASS a login is told by its account id, else by a hash of its token");
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
