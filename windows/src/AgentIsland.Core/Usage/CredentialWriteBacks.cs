namespace AgentIsland.Usage;

/// Re-writes a rotated credential pair that the store would not take, on a
/// short back-off of its own instead of waiting for the next quota poll: the
/// program holding the file is usually Claude Code or the grok CLI itself, and
/// while the file holds the replaced token it will try to refresh with it.
public static class CredentialWriteBacks
{
    /// Test seam: the waits between tries. Empty means no loop at all.
    internal static TimeSpan[] Backoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

    private static readonly object Gate = new();
    // The loop that is current for each key; a newer one retires the older.
    private static readonly Dictionary<string, long> Loops = new();
    private static long _next;

    /// Runs `attempt` after each wait until it returns true (nothing left to
    /// write); `exhausted` runs if the waits run out first, and only while this
    /// loop is still the current one for `key`. Starting a loop for a key
    /// retires the one before it, so a pair held later gets the whole schedule.
    internal static Task Retry(string provider, string key, Func<bool> attempt, Action exhausted)
    {
        var waits = Backoff.ToArray();
        if (waits.Length == 0) return Task.CompletedTask;
        long id;
        lock (Gate) Loops[key] = id = ++_next;
        return Task.Run(async () =>
        {
            foreach (var wait in waits)
            {
                await Task.Delay(wait);
                lock (Gate)
                {
                    if (!Loops.TryGetValue(key, out var current) || current != id) return;
                }
                var done = false;
                try { done = attempt(); } catch { }
                if (done) return;
            }
            lock (Gate)
            {
                // Checked and acted on together: a loop retired a moment ago
                // must not mark the pair that replaced the one it was for.
                if (!Loops.TryGetValue(key, out var current) || current != id) return;
                CredentialRefreshLog.Note(provider, "write-back retries exhausted");
                exhausted();
            }
        });
    }

    /// Retires every loop, for tests that reset the state under them.
    internal static void ResetForTests()
    {
        lock (Gate) Loops.Clear();
    }

    /// One last try for everything still held, for a normal exit or a sign-out
    /// or shutdown. The two providers run side by side, each given its own
    /// three seconds at most, so a file that stays locked cannot hold the app open.
    public static void FlushAll()
    {
        try
        {
            var claude = Task.Run(ClaudeCredentials.FlushPendingWriteBack);
            var grok = Task.Run(GrokUsageFetcher.FlushPendingWriteBacks);
            Task.WaitAll(new[] { claude, grok }, TimeSpan.FromSeconds(3));
        }
        catch
        {
        }
    }

    /// Removes what a crash left next to the credential files.
    public static void CleanUpLeftovers()
    {
        AtomicReplace.CleanUpLeftovers(Core.IslandPaths.ClaudeCredentialsFile);
        AtomicReplace.CleanUpLeftovers(GrokAuthFile.DefaultPath);
    }
}
