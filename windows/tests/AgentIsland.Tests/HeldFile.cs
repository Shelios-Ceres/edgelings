namespace AgentIsland.Tests;

/// Another program (an antivirus scan, the agent's own CLI) holding a file
/// open without sharing delete, which makes a rename over it fail until the
/// handle goes away.
public static class HeldFile
{
    public static Task For(string path, TimeSpan time)
    {
        var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Task.Run(async () =>
        {
            await Task.Delay(time);
            handle.Dispose();
        });
    }
}
