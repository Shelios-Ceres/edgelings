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
        if (IdentityIn(root) is { } identity)
        {
            return "account:" + identity.AccountId + (identity.UserId is { } user ? "|" + user : "");
        }
        if (Jsonl.GetObject(root, "tokens") is not { } tokens) return null;
        if (Jsonl.GetString(tokens, "access_token") is not { Length: > 0 } token) return null;
        return "token:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    internal sealed record Identity(string AccountId, string? UserId);

    /// auth.json 里的账号和用户（只读，不写）：account_id 之外还看 id_token / access_token 里的
    /// chatgpt_account_id、chatgpt_user_id；同一个 Team workspace 的成员 account 相同、user 不同。
    internal static Identity? IdentityIn(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object) return null;
            var account = StringIn(tokens, "account_id");
            string? user = null, subject = null;
            foreach (var key in new[] { "id_token", "access_token" })
            {
                if (StringIn(tokens, key)?.Split('.') is not { Length: 3 } parts) continue;
                try
                {
                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                    payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                    using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                    subject ??= StringIn(claims.RootElement, "sub");
                    if (claims.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth))
                    {
                        account ??= StringIn(auth, "chatgpt_account_id");
                        user ??= StringIn(auth, "chatgpt_user_id");
                    }
                }
                catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { }
            }
            // Email alone is not an identity: one user may have several workspaces.
            return account is null ? null : new Identity(account, user ?? subject);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private static string? StringIn(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    /// CurrentLogin 返回值里的账号和用户，用来对上 rollout 的 creator_account_id 和
    /// creator_user_id（同一个 Team workspace 的成员 account 相同、user 不同）。
    /// 登录只有 token 哈希、或没有登录时为 null；登录串没有 user 段时 User 为 null。
    internal static CodexRolloutQuota.Identity? IdentityOf(string? login)
    {
        const string Prefix = "account:";
        if (login is null || !login.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = login[Prefix.Length..];
        var bar = rest.IndexOf('|');
        var account = bar >= 0 ? rest[..bar] : rest;
        if (account.Length == 0) return null;
        var user = bar >= 0 && bar + 1 < rest.Length ? rest[(bar + 1)..] : null;
        return new CodexRolloutQuota.Identity(account, user);
    }

    /// auth.json 在不在（只看文件在不在，不读内容）。
    internal static bool AuthFileExists()
    {
        try { return File.Exists(IslandPaths.CodexAuthFile); }
        catch { return false; }
    }

    public static bool SpawnReauth()
    {
        if (Trigger.CLILocator.Locate("codex") is not { } cli) return false;
        return TerminalLauncher.RunVisible(cli, "login", "Limen — Codex login");
    }
}
