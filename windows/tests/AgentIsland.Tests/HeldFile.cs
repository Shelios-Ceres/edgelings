using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// Another program (an antivirus scan, the agent's own CLI) holding a file
/// open without sharing delete, which makes a rename over it fail until the
/// handle goes away. Unix has no such lock (a rename goes through any open
/// handle), so there a swap or a move onto the file is refused instead.
public static class HeldFile
{
    /// Held for `time` from the first write it turns away, not from now: a slow
    /// machine must not see the hold lapse before the code under test gets
    /// there (both CI runners did). A write that never comes ends it after a while.
    public static Task For(string path, TimeSpan time)
    {
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action release;
        if (OperatingSystem.IsWindows())
        {
            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var stopWatching = Watch(path, () => refused.TrySetResult());
            release = () =>
            {
                handle.Dispose();
                stopWatching();
            };
        }
        else
        {
            release = Refuse(path, () => refused.TrySetResult());
        }
        return Task.Run(async () =>
        {
            await Task.WhenAny(refused.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            await Task.Delay(time);
            release();
        });
    }

    /// For a writer that does not go through AtomicReplace (the Codex account
    /// switcher): held from now, with a real handle on Windows only.
    public static Task Timed(string path, TimeSpan time)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Task.Run(async () =>
        {
            await Task.Delay(time);
            handle.Dispose();
        });
    }

    /// Unix: refuses AtomicReplace's swaps and moves onto the file until the returned action runs.
    public static Action Refuse(string path, Action? onRefused = null) =>
        Wrap(path, () =>
        {
            onRefused?.Invoke();
            throw new IOException($"The process cannot access the file '{Path.GetFullPath(path)}' because it is being used by another process.");
        }, null);

    /// Windows: lets the real lock refuse, and reports the first refusal.
    private static Action Watch(string path, Action onRefused) => Wrap(path, null, onRefused);

    private static Action Wrap(string path, Action? before, Action? onFailure)
    {
        var held = Path.GetFullPath(path);
        bool Ours(string target) => string.Equals(Path.GetFullPath(target), held, StringComparison.OrdinalIgnoreCase);
        void Guard(string target, Action inner)
        {
            if (!Ours(target))
            {
                inner();
                return;
            }
            before?.Invoke();
            try
            {
                inner();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                onFailure?.Invoke();
                throw;
            }
        }
        var replace = AtomicReplace.ReplaceFile;
        var move = AtomicReplace.MoveFile;
        Action<string, string, string> wrappedReplace = (source, target, backup) => Guard(target, () => replace(source, target, backup));
        Action<string, string, bool> wrappedMove = (source, target, overwrite) => Guard(target, () => move(source, target, overwrite));
        AtomicReplace.ReplaceFile = wrappedReplace;
        AtomicReplace.MoveFile = wrappedMove;
        return () =>
        {
            if (AtomicReplace.ReplaceFile == wrappedReplace) AtomicReplace.ReplaceFile = replace;
            if (AtomicReplace.MoveFile == wrappedMove) AtomicReplace.MoveFile = move;
        };
    }
}
