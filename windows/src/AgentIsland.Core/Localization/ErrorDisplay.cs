using AgentIsland.Usage;

namespace AgentIsland.Localization;

/// Provider/network error strings are internal contract identifiers — the
/// activity monitor pattern-matches on the English text — so they stay
/// English in the stores and get localized only at display time.
public static class ErrorDisplay
{
    public static string Localize(string error)
    {
        if (!L10n.IsChinese) return error;
        return error switch
        {
            "auth required — run claude" => "需要登录——运行 claude",
            "re-login: claude /login" => "请重新登录：claude /login",
            // The keychain token 401'd AND the refresh grant failed. Without
            // its own sentinel the caption stayed on the cold-start "auth
            // required" text forever, so a revoked refresh token was
            // indistinguishable from never having signed in (macOS #22).
            "token refresh failed — sign in again" => "登录续期失败 — 请重新认证",
            // 探测 Claude 额度的几种结果（ClaudeUsageProbe）。都不是“需要登录”。
            ClaudeUsageProbe.NoSubscriptionMessage => "当前登录没有订阅额度",
            ClaudeUsageProbe.NoAccountMessage => "Claude Code 里没有登录账号（或在用 API key）",
            ClaudeUsageProbe.NotInstalledMessage => "装 Claude Code 并登录一次就能看到额度",
            ClaudeUsageProbe.NoReadingMessage => "这次没取到额度，请在 Claude Code 里重新登录",
            ClaudeUsageProbe.NoResponseMessage => "Claude Code 没有返回额度（可能需要更新）",
            "no codex auth" => "未找到 Codex 登录",
            "rate limited" => "已限流",
            "parse error" => "解析失败",
            "bad response" => "响应异常",
            _ when error.StartsWith("http ", StringComparison.OrdinalIgnoreCase) => "HTTP 错误 " + error[5..],
            _ => error,
        };
    }
}
