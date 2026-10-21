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

    /// A line's words, as a card sets them: split at spaces, except that
    /// "Grok Bot" is one word, so a line that must break never splits its name.
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == "Bot" && words.Count > 0 && words[^1].EndsWith("Grok", StringComparison.Ordinal)) words[^1] += " Bot";
            else words.Add(word);
        }
        return words;
    }

    internal static bool IsInstalled(bool seenRunning, Func<string, bool> exists) =>
        seenRunning || Executables().Any(exists);

    private static IEnumerable<string> Executables()
    {
        const string exe = "Grok Bot.exe";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProcessName, exe);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProcessName, exe);
    }
}
