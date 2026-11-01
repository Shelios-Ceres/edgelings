namespace AgentIsland.Core;

/// Which agent name a macOS process answers to. libproc's name is tried
/// first; an agent's desktop app can run under another executable name than
/// its bundle's (Electron apps often do), so the image path says the rest:
/// the file name, the .app bundle the main executable lives in, the official
/// installer's versioned claude binary, and Kimi Code's own folder. Only names
/// and paths are read here, never a command line. The rule that matched is
/// kept for the diagnostic.
internal static class MacAgentNames
{
    internal readonly record struct Match(string Name, string Rule);

    private const string KimiFolder = "/.kimi-code/";
    private const string BundleMain = ".app/Contents/MacOS/";

    /// A name from `wanted` (case-insensitive) for the process, or null when it is none of the agents'.
    internal static Match? Resolve(string processName, string? imagePath, IReadOnlyCollection<string> wanted)
    {
        string? Of(string candidate) => wanted.FirstOrDefault(w => string.Equals(w, candidate, StringComparison.OrdinalIgnoreCase));
        if (Of(processName) is { } direct) return new Match(direct, "name");
        if (imagePath is null) return null;
        if (Of(Path.GetFileNameWithoutExtension(imagePath)) is { } file) return new Match(file, "image file");
        if (BundleName(imagePath) is { } bundle && Of(bundle) is { } app) return new Match(app, "app bundle");
        if (imagePath.Contains("/claude/versions/", StringComparison.Ordinal) && Of("claude") is { } claude) return new Match(claude, "claude versions folder");
        if (imagePath.Contains(KimiFolder, StringComparison.Ordinal) && Of("kimi") is { } kimi) return new Match(kimi, "kimi-code folder");
        return null;
    }

    /// "Kimi Code" for /Applications/Kimi Code.app/Contents/MacOS/anything; null for
    /// a path that is not a bundle's main executable (a helper inside the bundle
    /// is the nested bundle's own, and is not named for the app).
    internal static string? BundleName(string imagePath)
    {
        var at = imagePath.IndexOf(BundleMain, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = imagePath.LastIndexOf('/', at) + 1;
        var name = imagePath[start..at];
        return name.Length == 0 ? null : name;
    }
}
