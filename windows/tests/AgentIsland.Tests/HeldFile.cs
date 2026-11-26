using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// Another program (an antivirus scan, the agent's own CLI) holding a file
/// open without sharing delete, which makes a rename over it fail until the
/// handle goes away. Unix has no such lock (a rename goes through any open
/// handle), so there a swap or a move onto the file is refused instead.
public static class HeldFile
{
    public static Task For(string path, TimeSpan time)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Task.Run(async () =>
            {
                await Task.Delay(time);
                handle.Dispose();
            });
        }
        // The hold is timed from the first write it turns away, not from now: a
        // slow machine must not see it lapse before the code under test gets
        // there (macOS CI did). A write that never comes ends it after a while.
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Refuse(path, () => refused.TrySetResult());
        return Task.Run(async () =>
        {
            await Task.WhenAny(refused.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            await Task.Delay(time);
            release();
        });
    }

    /// Unix: refuses AtomicReplace's swaps and moves onto the file until the returned action runs.
    public static Action Refuse(string path, Action? onRefused = null)
    {
        var held = Path.GetFullPath(path);
        void Check(string target)
        {
            if (!string.Equals(Path.GetFullPath(target), held, StringComparison.Ordinal)) return;
            onRefused?.Invoke();
            throw new IOException($"The process cannot access the file '{target}' because it is being used by another process.");
        }
        var replace = AtomicReplace.ReplaceFile;
        var move = AtomicReplace.MoveFile;
        Action<string, string, string> refusingReplace = (source, target, backup) => { Check(target); replace(source, target, backup); };
        Action<string, string, bool> refusingMove = (source, target, overwrite) => { Check(target); move(source, target, overwrite); };
        AtomicReplace.ReplaceFile = refusingReplace;
        AtomicReplace.MoveFile = refusingMove;
        return () =>
        {
            if (AtomicReplace.ReplaceFile == refusingReplace) AtomicReplace.ReplaceFile = replace;
            if (AtomicReplace.MoveFile == refusingMove) AtomicReplace.MoveFile = move;
        };
    }
}
