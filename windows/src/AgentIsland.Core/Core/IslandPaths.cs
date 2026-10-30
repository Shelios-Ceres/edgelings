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
///
/// On macOS the same names resolve to the Mac's own folders: Edgelings keeps
/// settings in ~/Library/Application Support/Edgelings and caches in
/// ~/Library/Caches/Edgelings, and Application Support stands in for both
/// AppData roots (.NET would answer ~/.config and ~/.local/share).
///
/// RootOverride (EDGELINGS_ROOT in the environment) moves the whole home to
/// <root>/home, with every app and agent folder beneath it. The agents' own
/// overrides (CODEX_HOME, GROK_HOME, KIMI_CODE_HOME, CLAUDE_CONFIG_DIR) still
/// win when set, so a test or bench that sets the root clears them first.
/// Tests and benches set the root so they never touch the real folders; the
/// macOS app can pass it through the bridge.
public static class IslandPaths
{
    private static string? _root = NormalizeRoot(Environment.GetEnvironmentVariable("EDGELINGS_ROOT"));

    /// Where a test or a bench keeps its whole world; null means the real one.
    public static string? RootOverride
    {
        get => _root;
        set => _root = NormalizeRoot(value);
    }

    private static string? NormalizeRoot(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());

    public static string Home => _root is { } root ? Path.Combine(root, "home") : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string RoamingAppData =>
        OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : _root is not null ? Path.Combine(Home, "AppData", "Roaming")
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string LocalAppData =>
        OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : _root is not null ? Path.Combine(Home, "AppData", "Local")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// Claude Code config roots, in priority order. CLAUDE_CONFIG_DIR
    /// (comma-separated) overrides, matching Claude Code's own behavior.
    public static IReadOnlyList<string> ClaudeConfigRoots => ResolveClaudeConfigRoots();

    public static IEnumerable<string> ClaudeProjectRoots =>
        ClaudeConfigRoots.Select(root => Path.Combine(root, "projects"));

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeConfigRoots[0], ".credentials.json");

    /// Claude Desktop's own data folders: %APPDATA%\Claude, and the
    /// %LOCALAPPDATA%\Claude-Data\<package> folders its October 2026 update
    /// moved that data to, leaving only a ".user-data-left.<package>" marker
    /// behind. The old folder comes first, so a session found in both is read
    /// from the new one. Looked up on every call: the move can happen while
    /// the app runs.
    public static IReadOnlyList<string> ClaudeDesktopDataRoots =>
        // The move to Claude-Data is Windows' (a packaged app); macOS keeps ~/Library/Application Support/Claude.
        OperatingSystem.IsMacOS() ? new[] { Path.Combine(RoamingAppData, "Claude") } : ClaudeDesktopDataRootsUnder(RoamingAppData, LocalAppData);

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

    /// The grok CLI's home: GROK_HOME, else ~/.grok. Its sessions, auth.json
    /// and hooks all live under it. A leading ~ is expanded here: Windows
    /// shells never do it, and the variable travels inside dotfiles copied
    /// over from a Mac.
    public static string GrokHome => GrokHomeFrom(Environment.GetEnvironmentVariable("GROK_HOME"));

    internal static string GrokHomeFrom(string? custom)
    {
        if (string.IsNullOrWhiteSpace(custom)) return Path.Combine(Home, ".grok");
        var trimmed = custom.Trim();
        if (trimmed == "~") return Home;
        return trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith(@"~\", StringComparison.Ordinal)
            ? Path.Combine(Home, trimmed[2..])
            : trimmed;
    }

    /// Grok CLI: one folder per percent-encoded working directory, one per
    /// session, the live event stream at updates.jsonl.
    public static string GrokSessionsRoot => Path.Combine(GrokHome, "sessions");

    /// Kimi Code (CLI and desktop app share it): one folder per working
    /// directory, one per session, the live event log at agents/main/wire.jsonl.
    public static string KimiSessionsRoot => Path.Combine(KimiHome, "sessions");

    /// Kimi Code's data folder: KIMI_CODE_HOME, else ~/.kimi-code. Sessions,
    /// config.toml, credentials, the memory plugin's folder and the mailbox
    /// all live under it.
    public static string KimiHome
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            return string.IsNullOrWhiteSpace(env) ? Path.Combine(Home, ".kimi-code") : env.Trim();
        }
    }

    /// DeepSeek Harness: one folder per workspace, one per session, the event
    /// log appended as zstd frames to session.v4.jsonl.zstd.
    public static string DeepSeekSessionsRoot => Path.Combine(Home, ".dsh", "sessions");

    /// Settings and the crash log. Before the rename to Edgelings these were
    /// under "AgentIsland"; AppDataMigration copies them across once.
    public static string AppSupportDir => Path.Combine(RoamingAppData, "Edgelings");
    public static string SettingsFile => Path.Combine(AppSupportDir, "settings.json");

    /// Caches: %LOCALAPPDATA%\Edgelings\cache, ~/Library/Caches/Edgelings on macOS.
    public static string CacheDir => CacheDirOverride ?? (OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Caches", "Edgelings")
        : Path.Combine(LocalAppData, "Edgelings", "cache"));

    /// Another cache folder while the real home is read: the diagnostic reads
    /// the user's agent data but must not write into the app's own caches.
    public static string? CacheDirOverride { get; set; }

    /// Cursor's conversation and account store (VS Code's globalStorage).
    public static string CursorStateDb => Path.Combine(RoamingAppData, "Cursor", "User", "globalStorage", "state.vscdb");

    /// Where the Claude and Grok hooks keep their signal folders. On Windows
    /// that is the Agent Island folder, where installs made before the rename
    /// point; the Mac app never had one, so it uses its own.
    public static string HooksDir => OperatingSystem.IsMacOS() ? AppSupportDir : LegacyAppSupportDir;

    /// Agent Island's folders, left in place by the migration. The Claude and
    /// Grok hooks' signal folders and Grok's hook script stay in the first:
    /// installs made before the rename point at them, and the rename edits
    /// neither agent's config.
    public static string LegacyAppSupportDir => Path.Combine(RoamingAppData, "AgentIsland");
    public static string LegacyCacheDir => Path.Combine(LocalAppData, "AgentIsland", "cache");

    private static IReadOnlyList<string> ResolveClaudeConfigRoots()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var parts = env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) return parts;
        }
        var home = Home;
        return new[]
        {
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".config", "claude"),
        };
    }
}
