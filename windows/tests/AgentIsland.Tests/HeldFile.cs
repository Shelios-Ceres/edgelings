using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// Another program (an antivirus scan, the agent's own CLI) holding a file
/// open without sharing delete, which makes a rename over it fail until the
/// handle goes away. Unix has no such lock (a rename goes through any open
/// handle), so there the swap onto the file is refused for the same time.
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
        var held = Path.GetFullPath(path);
        var inner = AtomicReplace.ReplaceFile;
        Action<string, string, string> refusing = (source, target, backup) =>
        {
            if (string.Equals(Path.GetFullPath(target), held, StringComparison.Ordinal))
                throw new IOException($"The process cannot access the file '{target}' because it is being used by another process.");
            inner(source, target, backup);
        };
        AtomicReplace.ReplaceFile = refusing;
        return Task.Run(async () =>
        {
            await Task.Delay(time);
            if (AtomicReplace.ReplaceFile == refusing) AtomicReplace.ReplaceFile = inner;
        });
    }
}
