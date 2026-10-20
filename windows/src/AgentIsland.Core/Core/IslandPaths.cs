
namespace AgentIsland.Core;

/// Central path resolution. Mirrors the macOS app's data sources, adapted to
/// Windows conventions:
///   ~/.claude/projects                          -> %USERPROFILE%\.claude\projects
///   ~/Library/Application Support/Claude/...    -> %APPDATA%\Claude\claude-code-sessions,
///                                                  since Oct 2026 %LOCALAPPDATA%\Claude-Data\<package>\...
///   ~/.codex/sessions                           -> %USERPROFILE%\.codex\sessions
///   macOS Keychain "Claude Code-credentials"    -> %USERPROFILE%\.claude\.credentials.json
///   ~/Library/Application Support/AgentIsland   -> %APPDATA%\AgentIsland
///   ~/Library/Caches/dev.agentisland...         -> %LOCALAPPDATA%\AgentIsland\cache
public static class IslandPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// Claude Code config roots, in priority order. CLAUDE_CONFIG_DIR
    /// (comma-separated) overrides, matching Claude Code's own behavior.
    public static IReadOnlyList<string> ClaudeConfigRoots { get; } = ResolveClaudeConfigRoots();

    public static IEnumerable<string> ClaudeProjectRoots =>
        ClaudeConfigRoots.Select(root => Path.Combine(root, "projects"));

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeConfigRoots[0], ".credentials.json");

    /// Claude Desktop's own data folders: %APPDATA%\Claude, and the
    /// %LOCALAPPDATA%\Claude-Data\<package> folders its October 2026 update
    /// moved that data to, leaving only a ".user-data-left.<package>" marker
    /// behind. The old folder comes first, so a session found in both is read
    /// from the new one. Looked up on every call: the move can happen while
    /// the app runs.
    public static IReadOnlyList<string> ClaudeDesktopDataRoots => ClaudeDesktopDataRootsUnder(RoamingAppData, LocalAppData);

    internal static IReadOnlyList<string> ClaudeDesktopDataRootsUnder(string roaming, string local)
    {
        var roots = new List<string> { Path.Combine(roaming, "Claude") };
        try
        {
            var moved = Path.Combine(local, "Claude-Data");
            if (Directory.Exists(moved)) roots.AddRange(Directory.EnumerateDirectories(moved).Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return roots;
    }

    /// Claude Desktop's session stores that exist, one per data folder.
    public static IReadOnlyList<string> ClaudeDesktopSessionsRoots =>
        ClaudeDesktopDataRoots.Select(root => Path.Combine(root, "claude-code-sessions")).Where(Directory.Exists).ToList();

    public static string CodexHome
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(env) ? Path.Combine(Home, ".codex") : env;
        }
    }

    public static string CodexSessionsRoot => Path.Combine(CodexHome, "sessions");

    /// `codex archive` moves a rollout here instead of deleting it — the
    /// tokens were still spent, so accounting walks both roots.
    public static string CodexArchivedSessionsRoot => Path.Combine(CodexHome, "archived_sessions");

    public static string CodexSessionIndexFile => Path.Combine(CodexHome, "session_index.jsonl");
    public static string CodexAuthFile => Path.Combine(CodexHome, "auth.json");

    /// Kimi Code (CLI and desktop app share it): one folder per working
    /// directory, one per session, the live event log at agents/main/wire.jsonl.
    public static string KimiSessionsRoot => Path.Combine(Home, ".kimi-code", "sessions");

    /// DeepSeek Harness: one folder per workspace, one per session, the event
    /// log appended as zstd frames to session.v4.jsonl.zstd.
    public static string DeepSeekSessionsRoot => Path.Combine(Home, ".dsh", "sessions");

    public static string AppSupportDir => Path.Combine(RoamingAppData, "AgentIsland");
    public static string SettingsFile => Path.Combine(AppSupportDir, "settings.json");
    public static string CacheDir => Path.Combine(LocalAppData, "AgentIsland", "cache");

    private static IReadOnlyList<string> ResolveClaudeConfigRoots()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var parts = env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) return parts;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".config", "claude"),
        };
    }
}
