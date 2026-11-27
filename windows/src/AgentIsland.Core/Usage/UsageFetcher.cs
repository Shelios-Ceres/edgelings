using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

public static class UsageFetcher
{
    // MARK: - Codex

    /// Codex 的额度来自它自己写进 rollout 的 token_count.rate_limits
    /// （见 CodexRolloutQuota）：不联网、不读 token。login 是调用方已经拿到的
    /// CodexCredentials.CurrentLogin() 的结果，这里不再读 auth.json。
    /// 读 rollout 放到线程池上，不让同时发起的 Claude 请求等它。
    public static Task<AppUsage> FetchCodex(string? login, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            // 已经取消：和旧行为一样返回一个结果（调用方自己会丢弃），不抛异常。
            if (ct.IsCancellationRequested) return AppUsage.ErrorPair("cancelled");
            try
            {
                return CodexRolloutQuota.Fetch(
                    new[] { IslandPaths.CodexSessionsRoot, IslandPaths.CodexArchivedSessionsRoot },
                    login, CodexCredentials.AuthFileExists());
            }
            catch (Exception error)
            {
                return AppUsage.ErrorPair(error.Message);
            }
        });

    /// 调用方没有现成登录（macOS bridge）时自己取一次。
    public static Task<AppUsage> FetchCodex(CancellationToken ct = default) =>
        FetchCodex(CodexCredentials.CurrentLogin(), ct);

    // MARK: - Claude

    /// Claude 的额度由官方 claude 自己报（ClaudeUsageProbe），这里只留解析窗口的那一段。
    internal static WindowUsage ParseClaudeWindow(JsonElement? obj, double periodSeconds)
    {
        if (obj is not { } window) return WindowUsage.Unknown;
        // Anthropic returns `utilization` as a percentage in [0, 100], not a
        // normalized fraction. Always divide by 100; clamp below.
        if ((Jsonl.GetDouble(window, "utilization") ?? Jsonl.GetDouble(window, "used_percent")) is not { } raw)
            return WindowUsage.Unknown;
        var normalized = raw / 100.0;
        DateTimeOffset? resetAt = null;
        if (Jsonl.GetDouble(window, "resets_at") is { } epoch)
        {
            resetAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000));
        }
        else if (Jsonl.GetString(window, "resets_at") is { } iso)
        {
            resetAt = Jsonl.ParseIso8601(iso);
        }
        return new WindowUsage(Math.Min(1, Math.Max(0, normalized)), resetAt, null, periodSeconds) { Observed = true };
    }
}
