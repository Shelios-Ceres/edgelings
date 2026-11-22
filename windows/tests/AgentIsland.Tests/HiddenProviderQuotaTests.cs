using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Hidden agents make zero quota requests, read no login files and trigger no
/// account switches; showing one again fetches only that agent.
public static class HiddenProviderQuotaTests
{
    private static AppUsage Usage(double fiveHour) =>
        new(new WindowUsage(fiveHour, null, null) { Observed = true }, new WindowUsage(0.1, null, null) { Observed = true });

    public static void RunAll()
    {
        BothHiddenMakesZeroCalls();
        HiddenClaudeSkipsAnthropicUntilShown();
        HiddenCodexSkipsOpenAiAndLoginRead();
        HidingAllInFlightAbortsRequest();
        HidingOneInFlightDropsItsHalf();
        Console.WriteLine("HiddenProviderQuotaTests GREEN");
    }

    private static void BothHiddenMakesZeroCalls()
    {
        int claude = 0, codex = 0, loginReads = 0, autoSwitches = 0, kicks = 0;
        var store = new UsageStore(restore: false)
        {
            ClaudeShown = () => false,
            CodexShown = () => false,
            ClaudeFetcher = _ => { Interlocked.Increment(ref claude); return Task.FromResult(Usage(0.2)); },
            CodexFetcher = _ => { Interlocked.Increment(ref codex); return Task.FromResult(Usage(0.3)); },
            CodexLogin = () => { Interlocked.Increment(ref loginReads); return "account:a"; },
            AutoSwitch = _ => Interlocked.Increment(ref autoSwitches),
            BeforeRefresh = () => Interlocked.Increment(ref kicks),
        };
        store.Refresh();
        Expect(!store.Loading, "nothing enters Loading when both providers are hidden");
        Expect(claude == 0 && codex == 0 && loginReads == 0 && autoSwitches == 0,
            $"expected zero quota calls, got claude={claude}, codex={codex}, login={loginReads}, autoSwitch={autoSwitches}");
        Expect(kicks == 1, "shown guest stores still receive the poll tick");
        Expect(store.LastAttemptedAt is null && store.CodexAttemptedAt is null, "neither provider records an attempt while hidden");
        Console.WriteLine("PASS hidden Claude and Codex make zero quota, login or auto-switch calls");
    }

    private static void HiddenClaudeSkipsAnthropicUntilShown()
    {
        int claude = 0, codex = 0;
        var claudeShown = false;
        var store = new UsageStore(restore: false)
        {
            ClaudeShown = () => claudeShown,
            CodexShown = () => true,
            ClaudeFetcher = _ => { Interlocked.Increment(ref claude); return Task.FromResult(Usage(0.4)); },
            CodexFetcher = _ => { Interlocked.Increment(ref codex); return Task.FromResult(Usage(0.25)); },
            CodexLogin = () => "account:a",
        };
        store.Refresh();
        Settle(store);
        Expect(claude == 0 && codex == 1, $"only Codex fetched while Claude was hidden, got claude={claude}, codex={codex}");
        Expect(store.LastAttemptedAt is null, "Claude's attempt clock stays untouched while hidden");
        Expect(store.CodexAttemptedAt is not null, "Codex's attempt clock is set");
        Expect(store.Codex.FiveHour.UsedPercent, 0.25);

        claudeShown = true;
        store.Refresh(fetchClaude: true, fetchCodex: false);
        Settle(store);
        Expect(claude == 1 && codex == 1, $"unhiding Claude fetches Claude alone, got claude={claude}, codex={codex}");
        Expect(store.Claude.FiveHour.UsedPercent, 0.4);
        Expect(store.LastAttemptedAt is not null, "Claude's attempt clock is now recorded");
        Console.WriteLine("PASS hidden Claude never polls Anthropic, and showing it fetches Claude alone");
    }

    private static void HiddenCodexSkipsOpenAiAndLoginRead()
    {
        int claude = 0, codex = 0, loginReads = 0, autoSwitches = 0;
        var codexShown = false;
        var store = new UsageStore(restore: false)
        {
            ClaudeShown = () => true,
            CodexShown = () => codexShown,
            ClaudeFetcher = _ => { Interlocked.Increment(ref claude); return Task.FromResult(Usage(0.5)); },
            CodexFetcher = _ => { Interlocked.Increment(ref codex); return Task.FromResult(Usage(1.0)); },
            CodexLogin = () => { Interlocked.Increment(ref loginReads); return "account:a"; },
            AutoSwitch = _ => Interlocked.Increment(ref autoSwitches),
        };
        store.Refresh();
        Settle(store);
        Expect(claude == 1 && codex == 0 && loginReads == 0 && autoSwitches == 0,
            $"hidden Codex stayed completely off, got claude={claude}, codex={codex}, login={loginReads}, auto={autoSwitches}");
        Expect(store.Claude.FiveHour.UsedPercent, 0.5);

        codexShown = true;
        store.Refresh(fetchClaude: false, fetchCodex: true);
        Settle(store);
        Expect(claude == 1 && codex == 1 && loginReads >= 1 && autoSwitches == 1,
            $"showing Codex fetches Codex alone without re-polling Anthropic, got claude={claude}, codex={codex}");
        Console.WriteLine("PASS hidden Codex skips OpenAI, auth.json reads and auto-switching until shown");
    }

    private static void HidingAllInFlightAbortsRequest()
    {
        var shown = true;
        CancellationToken observed = default;
        var gate = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new UsageStore(restore: false)
        {
            ClaudeShown = () => false,
            CodexShown = () => shown,
            CodexFetcher = token => { observed = token; return gate.Task; },
            CodexLogin = () => "account:a",
        };
        store.Refresh();
        WaitFor(() => observed.CanBeCanceled);
        Expect(store.Loading, "request is in flight");

        shown = false;
        store.CancelIfHidden();
        Expect(!store.Loading, "Loading clears as soon as the last in-flight provider is hidden");
        Expect(observed.IsCancellationRequested, "the in-flight HTTP token is cancelled");

        gate.SetResult(Usage(0.8));
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        Thread.Sleep(50);
        Expect(store.Codex == AppUsage.Empty, "a late reply from a cancelled hidden provider is dropped");
        Console.WriteLine("PASS hiding an agent mid-fetch cancels its HTTP token and drops late replies");
    }

    private static void HidingOneInFlightDropsItsHalf()
    {
        var codexShown = true;
        var autoSwitches = 0;
        var gate = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new UsageStore(restore: false)
        {
            ClaudeShown = () => true,
            CodexShown = () => codexShown,
            ClaudeFetcher = _ => Task.FromResult(Usage(0.35)),
            CodexFetcher = _ => gate.Task,
            CodexLogin = () => "account:a",
            AutoSwitch = _ => Interlocked.Increment(ref autoSwitches),
        };
        store.Refresh();
        WaitFor(() => store.Loading);

        codexShown = false;
        store.CancelIfHidden();
        Expect(store.Loading, "Claude is still shown, so its half stays alive");

        gate.SetResult(Usage(1.0));
        Settle(store);
        Expect(store.Claude.FiveHour.UsedPercent, 0.35);
        Expect(store.Codex == AppUsage.Empty, "Codex was hidden before commit, so its result is dropped");
        Expect(autoSwitches == 0, "and hidden Codex does not auto-switch");
        Console.WriteLine("PASS hiding one provider mid-refresh keeps the shown peer and drops the hidden half");
    }

    private static void Settle(UsageStore store)
    {
        store.RefreshTask?.Wait(TimeSpan.FromSeconds(5));
        WaitFor(() => !store.Loading);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new Exception("timed out waiting for UsageStore");
            Thread.Sleep(10);
        }
    }
}
