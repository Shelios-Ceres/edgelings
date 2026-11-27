using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

internal static class RuntimeBoundaryTests
{
    public static void RunAll()
    {
        VerifyServerIdentity();
        VerifyVisibility();
        VerifySerialMonitor();
        Console.WriteLine("RuntimeBoundaryTests GREEN");
    }

    private static void VerifyServerIdentity()
    {
        var owner = AntigravityLanguageServer.ResolvePortOwner;
        var started = AntigravityLanguageServer.StartTime;
        var call = AntigravityLanguageServer.CallRpc;
        try
        {
            var stamp = DateTime.UtcNow;
            var server = new AntigravityLiveCascades.Server(1234, 44, stamp, "fixture");
            var calls = 0;
            AntigravityLanguageServer.CallRpc = (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult<AntigravityLanguageServer.Reply?>(new(200, []));
            };
            AntigravityLanguageServer.StartTime = _ => stamp;
            AntigravityLanguageServer.ResolvePortOwner = _ => 45;
            server.Call("probe", "{}").GetAwaiter().GetResult();
            AntigravityLanguageServer.ResolvePortOwner = _ => 44;
            AntigravityLanguageServer.StartTime = _ => stamp.AddTicks(1);
            server.Call("probe", "{}").GetAwaiter().GetResult();
            Expect(calls, 0);
            AntigravityLanguageServer.StartTime = _ => stamp;
            server.Call("probe", "{}").GetAwaiter().GetResult();
            Expect(calls, 1);
        }
        finally
        {
            AntigravityLanguageServer.ResolvePortOwner = owner;
            AntigravityLanguageServer.StartTime = started;
            AntigravityLanguageServer.CallRpc = call;
        }
    }

    private sealed class Guest : GuestUsageStore
    {
        public int Applied;
        public Task Begin(Func<CancellationToken, Task<int>> fetch)
        {
            Expect(TryStart(TimeSpan.Zero), true);
            return RunVisibleFetch(fetch, _ => -1, () => true, value => { Applied = value; Loading = false; });
        }
        public bool Kick(TimeSpan floor) => TryStart(floor);
        public Task Begin2(Func<CancellationToken, Task<int>> fetch) =>
            RunVisibleFetch(fetch, _ => -1, () => true, value => { Applied = value; Loading = false; });
    }

    private static void VerifyVisibility()
    {
        var guest = new Guest();
        var blocked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = guest.Begin(ct => { observed.SetResult(ct); return blocked.Task; });
        var token = observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        guest.CancelPending();
        Expect(token.IsCancellationRequested, true);
        guest.Begin(_ => Task.FromResult(2)).GetAwaiter().GetResult();
        blocked.SetResult(1);
        old.GetAwaiter().GetResult();
        Expect(guest.Applied, 2);
        // Hide, show, kick: a cancelled attempt gives up its floor, so the
        // store fetches again at once.
        var floored = new Guest();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        Expect(floored.Kick(TimeSpan.FromMinutes(2)), true);
        var first = floored.Begin2(ct => { seen.SetResult(ct); return pending.Task; });
        seen.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        floored.CancelPending();
        Expect(floored.Kick(TimeSpan.FromMinutes(2)), true);
        pending.SetResult(0);
        first.GetAwaiter().GetResult();
        var store = DrawerStore.Shared;
        store.SetShown(DisplayProvider.Antigravity, false);
        var path = Path.Combine(IslandPaths.Home, ".gemini", "antigravity", "brain", "fixture",
            ".system_generated", "logs", "transcript_full.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        var list = AntigravityLanguageServer.ListAntigravityProcessIds;
        int lists = 0;
        AntigravityLanguageServer.ListAntigravityProcessIds = () => { lists++; return []; };
        try
        {
            var future = DateTimeOffset.UtcNow.AddDays(1);
            Expect(SessionScanner.ScanAntigravity(future, new Dictionary<string, DateTimeOffset>()).Count > 0,
                "hidden sessions remain in history");
            Expect(lists, 0);
            store.SetShown(DisplayProvider.Antigravity, true);
            SessionScanner.ScanAntigravity(future.AddMinutes(6), new Dictionary<string, DateTimeOffset>());
            Expect(lists > 0, "shown provider consults live state");
        }
        finally
        {
            AntigravityLanguageServer.ListAntigravityProcessIds = list;
            store.SetShown(DisplayProvider.Antigravity, false);
        }
        var resumed = false;
        Expect(!store.WhileVisible(DisplayProvider.Antigravity, () => resumed = true) && !resumed, "hide precludes CLI resume");
    }

    // Run last in the console runner: switch from its inline mode to the exact
    // scheduler installed by the Mac bridge.
    private static void VerifySerialMonitor()
    {
        UiThread.UseSerialQueue();
        int inside = 0, calls = 0, overlaps = 0;
        void CheckOne()
        {
            if (Interlocked.Increment(ref inside) != 1) Interlocked.Increment(ref overlaps);
            Thread.Yield();
            calls++;
            Interlocked.Decrement(ref inside);
        }
        var work = Enumerable.Range(0, 64).SelectMany(_ => new[]
        {
            UiThread.InvokeAsync(CheckOne),
            Task.Factory.StartNew(CheckOne, CancellationToken.None, TaskCreationOptions.None, UiThread.Scheduler),
            Task.Run(() => UiThread.RunSerial(() => { CheckOne(); return true; })),
        }).ToArray();
        Expect(Task.WaitAll(work, TimeSpan.FromSeconds(10)), true);
        var posted = new TaskCompletionSource();
        UiThread.Post(() => { CheckOne(); posted.SetResult(); });
        Expect(posted.Task.Wait(TimeSpan.FromSeconds(5)), true);
        Expect(calls, 193);
        Expect(overlaps, 0);
    }
}
