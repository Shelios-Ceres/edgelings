using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 唤醒和网络恢复以前会取消正在进行的刷新并立刻重来；现在要等自上次刷新
/// 起满两分钟。抓取器和时钟都是脚本化的，不碰网络。
public static class UsageEventFloorTests
{
    public static void RunAll()
    {
        Run("a wake and a network recovery within two minutes of a refresh start none", EventsInsideTheFloor);
        Run("repeated wakes within two minutes refresh once", RepeatedWakes);
        Run("after two minutes a wake refreshes, and a manual refresh is never held back", AfterTheFloor);
        Run("events held back by the floor are made up for once when it ends", MadeUpOnce);
        Run("a refresh that runs meanwhile makes the make-up unnecessary", MakeUpCancelledByOtherRefresh);
        Run("a refresh of only Codex leaves the make-up to fetch Claude", PartialRefreshKeepsTheMakeUp);
        Run("stopping the store cancels the make-up", StopCancelsTheMakeUp);
        Run("a request stuck in flight is superseded by the make-up when the floor ends", MakeUpWithARequestStuck);
        Run("Codex auto-switching is off until the user turns it on", AutoSwitchDefaultsOff);
        Console.WriteLine("UsageEventFloorTests GREEN");
    }

    private sealed class Rig
    {
        public int Claude;
        public int Codex;
        /// While set, a Claude fetch started now stays in flight until it is completed.
        public volatile TaskCompletionSource? HangClaude;
        public DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        public readonly UsageStore Store;

        public Rig()
        {
            Store = new UsageStore(restore: false)
            {
                ClaudeFetcher = async _ =>
                {
                    Interlocked.Increment(ref Claude);
                    if (HangClaude is { } hang) await hang.Task;
                    return AppUsage.ErrorPair("rate limited");
                },
                CodexFetcher = _ => { Interlocked.Increment(ref Codex); return Task.FromResult(AppUsage.ErrorPair("http 429")); },
                CodexLogin = () => "account:a",
                Clock = () => Now,
            };
            UseManualDelay();
        }

        public readonly List<TaskCompletionSource> Waits = new();

        public void UseManualDelay() => Store.Delay = (_, _) =>
        {
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Waits) Waits.Add(wait);
            return wait.Task;
        };

        public int WaitCount() { lock (Waits) return Waits.Count; }

        public void EndWaits() { lock (Waits) foreach (var wait in Waits) wait.TrySetResult(); }

        public static void Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        }

        public void Settle() => Store.RefreshTask?.Wait(TimeSpan.FromSeconds(10));
    }

    private static void EventsInsideTheFloor()
    {
        var rig = new Rig();
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude == 1 && rig.Codex == 1, "the first refresh asks each provider once");
        rig.Now += TimeSpan.FromSeconds(90);
        rig.Store.HandleWake();
        rig.Store.HandleNetworkRecovered().GetAwaiter().GetResult();
        rig.Settle();
        Expect(rig.Claude == 1 && rig.Codex == 1, $"neither event asks again, got {rig.Claude} and {rig.Codex}");
    }

    private static void RepeatedWakes()
    {
        var rig = new Rig();
        for (var wake = 0; wake < 4; wake++)
        {
            rig.Store.HandleWake();
            rig.Settle();
            rig.Now += TimeSpan.FromSeconds(20);
        }
        Expect(rig.Claude == 1 && rig.Codex == 1, $"four wakes in eighty seconds refresh once, got {rig.Claude} and {rig.Codex}");
    }

    private static void AfterTheFloor()
    {
        var rig = new Rig();
        rig.Store.Refresh();
        rig.Settle();
        rig.Now += UsageStore.EventRefreshFloor + TimeSpan.FromSeconds(1);
        rig.Store.HandleWake();
        rig.Settle();
        Expect(rig.Claude == 2 && rig.Codex == 2, "a wake past the floor refreshes");
        rig.Now += TimeSpan.FromSeconds(5);
        rig.Store.HandleWake();
        rig.Settle();
        Expect(rig.Claude == 2, "and the floor starts over from that refresh");
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude == 3 && rig.Codex == 3, "a manual refresh inside the floor still goes through");
    }

    private static void MadeUpOnce()
    {
        var rig = new Rig();
        rig.Store.Refresh();
        rig.Settle();
        rig.Now += TimeSpan.FromSeconds(30);
        rig.Store.HandleWake();
        rig.Now += TimeSpan.FromSeconds(10);
        rig.Store.HandleNetworkRecovered().GetAwaiter().GetResult();
        rig.Store.HandleWake();
        Rig.Until(() => rig.WaitCount() >= 1);
        Thread.Sleep(100);
        Expect(rig.Claude == 1 && rig.WaitCount() == 1, $"held back and one make-up waiting, got {rig.Claude} refreshes and {rig.WaitCount()} waits");
        rig.Now += TimeSpan.FromMinutes(2);
        rig.EndWaits();
        Rig.Until(() => rig.Claude == 2);
        rig.Settle();
        Thread.Sleep(100);
        Expect(rig.Claude == 2 && rig.Codex == 2, $"the floor ending refreshes exactly once, got {rig.Claude} and {rig.Codex}");
    }

    private static void MakeUpCancelledByOtherRefresh()
    {
        var rig = new Rig();
        rig.Store.Refresh();
        rig.Settle();
        rig.Now += TimeSpan.FromSeconds(30);
        rig.Store.HandleWake();
        Rig.Until(() => rig.WaitCount() >= 1);
        rig.Store.Refresh();
        rig.Settle();
        Expect(rig.Claude == 2, "the manual refresh ran");
        rig.Now += TimeSpan.FromMinutes(2);
        rig.EndWaits();
        Thread.Sleep(200);
        Expect(rig.Claude == 2 && rig.Codex == 2, $"the make-up did not add one, got {rig.Claude} and {rig.Codex}");
    }

    private static void PartialRefreshKeepsTheMakeUp()
    {
        var rig = new Rig();
        rig.Store.Refresh();
        rig.Settle();
        rig.Now += TimeSpan.FromSeconds(30);
        rig.Store.HandleWake();
        Rig.Until(() => rig.WaitCount() >= 1);
        rig.Store.Refresh(fetchClaude: false, fetchCodex: true);
        rig.Settle();
        Expect(rig.Claude == 1 && rig.Codex == 2, "only Codex was refreshed");
        rig.Now += TimeSpan.FromMinutes(2);
        rig.EndWaits();
        Rig.Until(() => rig.Claude == 2);
        rig.Settle();
        Expect(rig.Claude == 2, $"the make-up still fetched Claude, got {rig.Claude}");
    }

    private static void StopCancelsTheMakeUp()
    {
        var rig = new Rig();
        rig.Store.Refresh();
        rig.Settle();
        rig.Now += TimeSpan.FromSeconds(30);
        rig.Store.HandleWake();
        Rig.Until(() => rig.WaitCount() >= 1);
        rig.Store.StopAutoRefresh();
        rig.Now += TimeSpan.FromMinutes(2);
        rig.EndWaits();
        Thread.Sleep(200);
        Expect(rig.Claude == 1 && rig.Codex == 1, $"a stopped store makes nothing up, got {rig.Claude} and {rig.Codex}");
    }

    private static void MakeUpWithARequestStuck()
    {
        var rig = new Rig();
        rig.HangClaude = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.Refresh();
        Rig.Until(() => rig.Claude == 1);
        rig.Now += TimeSpan.FromSeconds(30);
        rig.Store.HandleWake();
        Rig.Until(() => rig.WaitCount() >= 1);
        Expect(rig.Claude == 1, "the stuck request is left alone inside the floor");
        rig.HangClaude = null;
        rig.Now += TimeSpan.FromMinutes(2);
        rig.EndWaits();
        Rig.Until(() => rig.Claude == 2);
        Expect(rig.Claude == 2, $"when the floor ends the make-up supersedes the stuck request, got {rig.Claude}");
        rig.Settle();
    }

    private static void AutoSwitchDefaultsOff()
    {
        Expect(!CodexAccountSwitcher.AutoSwitchEnabled, "a fresh install does not rotate logins");
    }

    private static void Run(string name, Action test) { test(); Console.WriteLine($"PASS usage event floor: {name}"); }
}
