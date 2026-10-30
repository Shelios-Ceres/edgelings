using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

public static class CodexCredentials
{
    public static DateTimeOffset? AuthModificationStamp()
    {
        try
        {
            var path = IslandPaths.CodexAuthFile;
            if (!File.Exists(path)) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch
        {
            return null;
        }
    }

    /// Which login auth.json holds right now: its account and user, as the
    /// account switcher tells logins apart (two members of one Team
    /// workspace share the account id), else a hash of its access token
    /// (never the token itself). Only compared, never shown or logged. Null
    /// when there is no readable login.
    public static string? CurrentLogin()
    {
        try
        {
            var path = IslandPaths.CodexAuthFile;
            if (!File.Exists(path)) return null;
            using var stream = SharedFile.Open(path);
            using var document = JsonDocument.Parse(stream);
            return LoginIn(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    internal static string? LoginIn(JsonElement root)
    {
        if (CodexAccountSwitcher.IdentityIn(root) is { } identity)
        {
            return "account:" + identity.AccountId + (identity.UserId is { } user ? "|" + user : "");
        }
        if (Jsonl.GetObject(root, "tokens") is not { } tokens) return null;
        if (Jsonl.GetString(tokens, "access_token") is not { Length: > 0 } token) return null;
        return "token:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool SpawnReauth()
    {
        if (Trigger.CLILocator.Locate("codex") is not { } cli) return false;
        return TerminalLauncher.RunVisible(cli, "login", "Edgelings — Codex login");
    }
}
