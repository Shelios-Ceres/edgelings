namespace AgentIsland.Usage;

/// 清理旧版本留在凭证文件旁边的残留副本（`<文件名>.tmp-<32 位十六进制>`、
/// `.<文件名>.tmp-<32 位十六进制>`、`<文件名>.bak-<32 位十六进制>`）。
/// 旧版本写回登录文件时崩溃或被杀会留下这些副本，里面是完整凭证。
/// 现在 Limen 不再写任何登录文件；这里只按文件名模式和修改时间删除，
/// 从不读取内容，也不碰凭证文件本身。
public static class CredentialLeftovers
{
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    /// 启动时调用一次：Claude 的 .credentials.json 和 Grok 的 auth.json 旁边的残留。
    public static void CleanUpAll()
    {
        CleanUp(Core.IslandPaths.ClaudeCredentialsFile);
        CleanUp(Core.IslandPaths.GrokAuthFilePath);
    }

    /// 只删修改时间早于 LeftoverAge 的副本，免得碰到别的程序刚写下的东西。
    internal static void CleanUp(string credentialFile, DateTimeOffset? now = null)
    {
        try
        {
            if (Path.GetDirectoryName(credentialFile) is not { Length: > 0 } directory || !Directory.Exists(directory)) return;
            var name = Path.GetFileName(credentialFile);
            var cutoff = ((now ?? DateTimeOffset.UtcNow) - LeftoverAge).UtcDateTime;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var leaf = Path.GetFileName(file);
                var copy = IsCopy(leaf, name + ".bak-") || IsCopy(leaf, name + ".tmp-") || IsCopy(leaf, "." + name + ".tmp-");
                if (!copy) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                    File.Delete(file);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsCopy(string leaf, string prefix) =>
        leaf.Length == prefix.Length + 32 && leaf.StartsWith(prefix, StringComparison.Ordinal)
        && leaf.AsSpan(prefix.Length).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
