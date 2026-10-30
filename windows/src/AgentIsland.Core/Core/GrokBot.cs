namespace AgentIsland.Core;

/// Grok Bot, SpaceXAI's desktop app. The island knows only whether its window
/// is on screen (AgentProcesses.PresenceOnly) and whether it is installed.
/// Its data folders, with the login files beside them, are never read, listed
/// or watched.
public static class GrokBot
{
    public const string ProcessName = "Grok Bot";

    private static volatile bool _seen;

    /// Installed per user or for all users; a file-exists check, nothing read.
    public static bool Installed => IsInstalled(_seen, File.Exists);

    /// A process seen running counts as installed wherever it lives.
    internal static void NoteRunning() => _seen = true;

    internal static bool IsInstalled(bool seenRunning, Func<string, bool> exists) =>
        seenRunning || Executables().Any(exists);

    private static IEnumerable<string> Executables()
    {
        if (OperatingSystem.IsMacOS())
        {
            var bundle = Path.Combine(ProcessName + ".app", "Contents", "MacOS", ProcessName);
            yield return Path.Combine("/Applications", bundle);
            yield return Path.Combine(IslandPaths.Home, "Applications", bundle);
            yield break;
        }
        const string exe = "Grok Bot.exe";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProcessName, exe);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProcessName, exe);
    }
}
