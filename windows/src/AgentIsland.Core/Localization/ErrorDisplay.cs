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
            "no codex auth" => "未找到 Codex 登录",
            "auth expired — codex login" => "登录已过期——运行 codex login",
            "rate limited" => "已限流",
            "parse error" => "解析失败",
            "bad response" => "响应异常",
            _ when error.StartsWith("http ", StringComparison.OrdinalIgnoreCase) => "HTTP 错误 " + error[5..],
            _ => error,
        };
    }
}
