using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Claude 登录文件的只读一面。Limen 不刷新登录，也不写任何登录文件：
/// 额度由官方 claude 自己报（见 ClaudeUsageProbe）。这里只剩三件事：
/// 认出“需要重新登录”的错误文案、描述登录在哪里（给诊断看，不含 token）、
/// 给终端登录的轮询提供一个本地变化信号。
/// macOS 上登录存在登录钥匙串里；Windows 上 Claude Code 把同样的 claudeAiOauth JSON
/// 存在 %USERPROFILE%\.claude\.credentials.json。
public static class ClaudeCredentials
{
    // MARK: - Credentials file

    /// Test seam: another credentials file.
    internal static string? CredentialsFileOverride;

    private static string CredentialsFile => CredentialsFileOverride ?? IslandPaths.ClaudeCredentialsFile;

    /// Test seam: the keychain to read, or null for the real one on a Mac.
    /// The real login keychain is used only on macOS, with no file override
    /// and no root override: a test or a bench never reaches it.
    internal static ClaudeKeychain? KeychainOverride;

    private static ClaudeKeychain? Keychain =>
        KeychainOverride ?? (OperatingSystem.IsMacOS() && CredentialsFileOverride is null && IslandPaths.RootOverride is null ? ClaudeKeychain.Default : null);

    /// Where Claude's login is kept and when its access token runs out: a
    /// description for a diagnostic, never a token. Source is "keychain",
    /// "file" or "none".
    public sealed record LoginStatus(string Source, string? Plan, double? MinutesLeft);

    public static LoginStatus Describe()
    {
        double? MinutesLeft(double expiresAtMs) =>
            expiresAtMs > 0 ? Math.Round((DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMs) - DateTimeOffset.UtcNow).TotalMinutes) : null;
        if (Keychain is { } keychain && keychain.Read() is { } item) return new LoginStatus("keychain", item.SubscriptionType, MinutesLeft(item.ExpiresAt));
        try
        {
            if (!File.Exists(CredentialsFile)) return new LoginStatus("none", null, null);
            using var stream = SharedFile.Open(CredentialsFile);
            using var doc = JsonDocument.Parse(stream);
            if (Jsonl.GetObject(doc.RootElement, "claudeAiOauth") is not { } oauth) return new LoginStatus("none", null, null);
            return new LoginStatus("file", Jsonl.GetString(oauth, "subscriptionType"), MinutesLeft(Jsonl.GetDouble(oauth, "expiresAt") ?? 0));
        }
        catch
        {
            return new LoginStatus("none", null, null);
        }
    }

    /// Cheap local change signal used by the re-auth poll — file mtime here,
    /// keychain `mdat` on macOS.
    public static string? CredentialsModificationStamp()
    {
        if (Keychain is { } keychain && keychain.ModificationStamp() is { } stamp) return stamp;
        try
        {
            var path = CredentialsFile;
            if (!File.Exists(path)) return null;
            return File.GetLastWriteTimeUtc(path).Ticks.ToString();
        }
        catch
        {
            return null;
        }
    }
}
