using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Localization;

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

    /// Tile-sized caption for a transport failure that outlasted its retries
    /// (macOS shortError); anything else keeps its own message.
    private static string ShortError(Exception error, CancellationToken ct) =>
        Http.IsTransient(error, ct) ? L10n.Tr("network drop") : error.Message;

    // MARK: - Claude

    /// Anthropic doesn't ship a usage endpoint for end users — Claude Code
    /// itself talks to api.anthropic.com/api/oauth/usage with a beta header
    /// and a User-Agent that identifies as the CLI. We replicate that.
    public static async Task<AppUsage> FetchClaude(CancellationToken ct = default)
    {
        // Nothing here may throw: a fetch that faults kills the refresh
        // task before it can clear UsageStore.Loading, freezing usage (and
        // auto-resume) until an app relaunch. Credential file IO races with
        // the CLI rewriting the file are a real occurrence, not a theory.
        try
        {
            var resolution = await ClaudeCredentials.ResolveUsage((token, plan) => FetchClaudeUsage(token, plan, ct));
            return resolution switch
            {
                ClaudeCredentials.Resolution.Usage usage => usage.Value,
                ClaudeCredentials.Resolution.ReauthRequired reauth => AppUsage.ErrorPair(reauth.Message),
                ClaudeCredentials.Resolution.Failed failed => AppUsage.ErrorPair(failed.Message),
                _ => AppUsage.ErrorPair("bad response"),
            };
        }
        catch (Exception error)
        {
            return AppUsage.ErrorPair(error.Message);
        }
    }

    private static async Task<ClaudeCredentials.ProbeOutcome> FetchClaudeUsage(string token, string? plan, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.SendRetryingTransient(() =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                // Anthropic gates this endpoint on a CLI User-Agent. Without it
                // the request 401s even with a valid token.
                request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.121");
                return request;
            }, ct);
            var status = (int)response.StatusCode;
            if (status == 401) return new ClaudeCredentials.ProbeOutcome.Unauthorized();
            if (status == 403) return new ClaudeCredentials.ProbeOutcome.ScopeInsufficient();
            if (status == 429) return new ClaudeCredentials.ProbeOutcome.RateLimited();
            if (status != 200) return new ClaudeCredentials.ProbeOutcome.OtherError($"HTTP {status}");

            // The endpoint also returns 200 with a rate_limit_error body
            // sometimes; don't trust the status code alone.
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var root = doc.RootElement;
            if (Jsonl.GetObject(root, "error") is { } error
                && Jsonl.GetString(error, "type") == "rate_limit_error")
            {
                return new ClaudeCredentials.ProbeOutcome.RateLimited();
            }
            return new ClaudeCredentials.ProbeOutcome.Success(new AppUsage(
                ParseClaudeWindow(Jsonl.GetObject(root, "five_hour"), periodSeconds: 5 * 3600),
                ParseClaudeWindow(Jsonl.GetObject(root, "seven_day"), periodSeconds: 7 * 86400),
                plan));
        }
        catch (Exception error)
        {
            return new ClaudeCredentials.ProbeOutcome.OtherError(ShortError(error, ct));
        }
    }

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
