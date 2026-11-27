namespace AgentIsland.Cost;

/// Hardcoded price table (USD per million tokens), refreshed 2026-11-22 from
/// the first-party entries in LiteLLM's model_prices_and_context_window.json,
/// with Moonshot's K3.1 Pro, K3 and K2.7 Code rows read from its pricing
/// page (platform.kimi.ai/docs/pricing/chat) and Google's Gemini rows read
/// from ai.google.dev/gemini-api/docs/pricing (Standard tier). DeepSeek's rows
/// are its peak rates (api-docs.deepseek.com/quick_start/pricing); outside its
/// peak hours it bills half. Unknown models price at $0 (ccusage parity) and
/// are surfaced so the UI can warn about unpriced spend. Moonshot and DeepSeek
/// bill cache writes as ordinary input, so their creation rate is the input rate.
public static class Pricing
{
    private sealed record Price(double Input, double Output, double CacheCreation, double CacheRead, double? CacheCreation1h = null);

    // Anthropic bills 5-minute ephemeral cache writes at 1.25x base input and
    // 1-hour ephemeral cache writes at 2x base input across its catalog.
    private static Price Anthropic(double input, double output, double cacheRead) =>
        new(input, output, input * 1.25, cacheRead, input * 2.0);

    private static readonly Dictionary<string, Price> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Anthropic
        ["claude-fable-5-2"] = Anthropic(8, 40, 0.2),
        ["claude-fable-5-1"] = Anthropic(10, 50, 0.25),
        ["claude-fable-5"] = Anthropic(10, 50, 1.0),
        ["claude-mythos-5-2"] = Anthropic(8, 40, 0.2),
        ["claude-mythos-5-1"] = Anthropic(10, 50, 0.25),
        ["claude-mythos-5"] = Anthropic(10, 50, 1.0),
        ["claude-opus-5-5"] = Anthropic(4, 20, 0.2),
        ["claude-opus-5"] = Anthropic(5, 25, 0.5),
        ["claude-opus-4-8"] = Anthropic(5, 25, 0.5),
        ["claude-opus-4-7"] = Anthropic(5, 25, 0.5),
        ["claude-opus-4-6"] = Anthropic(5, 25, 0.5),
        ["claude-opus-4-5"] = Anthropic(5, 25, 0.5),
        ["claude-opus-4-1"] = Anthropic(15, 75, 1.5),
        ["claude-sonnet-5-5"] = Anthropic(2, 10, 0.1),
        ["claude-sonnet-5"] = Anthropic(2, 10, 0.2),
        ["claude-haiku-5-5"] = Anthropic(0.1, 0.5, 0.01),
        ["claude-sonnet-4-6"] = Anthropic(3, 15, 0.3),
        ["claude-sonnet-4-5"] = Anthropic(3, 15, 0.3),
        ["claude-haiku-4-5"] = Anthropic(1, 5, 0.1),
        // OpenAI
        ["gpt-6.2-astra"] = new(8, 40, 10, 0.4),
        ["gpt-6.2-sol"] = new(2, 10, 2.5, 0.1),
        ["gpt-6.1-sol"] = new(2, 10, 2.5, 0.1),
        ["gpt-6.1-terra"] = new(1, 5, 1.25, 0.05),
        ["gpt-6-astra"] = new(10, 50, 12.5, 1.0),
        ["gpt-6-sol"] = new(2, 10, 2.5, 0.2),
        ["gpt-6-luna"] = new(0.1, 0.5, 0.125, 0.01),
        ["gpt-5.6"] = new(4, 20, 5, 0.4),
        ["gpt-5.6-sol"] = new(4, 20, 5, 0.4),
        ["gpt-5.6-terra"] = new(2, 12, 2.5, 0.2),
        ["gpt-5.6-luna"] = new(0.2, 1.2, 0.25, 0.02),
        ["gpt-5.5"] = new(5, 30, 5, 0.5),
        ["gpt-5.4"] = new(2.5, 15, 2.5, 0.25),
        ["gpt-5.2"] = new(1.75, 14, 1.75, 0.175),
        ["gpt-5.1"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5.3-codex"] = new(1.75, 14, 1.75, 0.175),
        ["gpt-5.3-codex-spark"] = new(1.75, 14, 1.75, 0.175),
        ["gpt-5.2-codex"] = new(1.75, 14, 1.75, 0.175),
        ["gpt-5.1-codex"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5.1-codex-max"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5.1-codex-mini"] = new(0.25, 2, 0.25, 0.025),
        ["gpt-5-codex"] = new(1.25, 10, 1.25, 0.125),
        // Codex's Guardian auto-review model is included free on Codex accounts
        // and does not consume quota; keep it known at $0 rather than unpriced.
        ["codex-auto-review"] = new(0, 0, 0, 0),
        ["gpt-5.4-mini"] = new(0.75, 4.5, 0.75, 0.075),
        ["gpt-5.4-nano"] = new(0.2, 1.25, 0.2, 0.02),
        ["gpt-5-mini"] = new(0.25, 2, 0.25, 0.025),
        ["gpt-5-nano"] = new(0.05, 0.4, 0.05, 0.005),
        ["gpt-5.5-pro"] = new(30, 180, 30, 3),
        ["gpt-5.4-pro"] = new(30, 180, 30, 3),
        ["gpt-5.2-pro"] = new(21, 168, 21, 0),
        ["gpt-5-pro"] = new(15, 120, 15, 0),
        // Moonshot — Kimi Code's plan aliases for K3 (both context sizes), K3.1 Pro
        // and K2.7 Code.
        ["kimi-k3.1-pro"] = new(2, 10, 2, 0.2),
        ["kimi-code/k3.1-pro"] = new(2, 10, 2, 0.2),
        ["kimi-k3.1-pro-highspeed"] = new(4, 20, 4, 0.2),
        ["kimi-code/k3.1-pro-highspeed"] = new(4, 20, 4, 0.2),
        ["kimi-k3"] = new(3, 15, 3, 0.3),
        ["kimi-code/k3"] = new(3, 15, 3, 0.3),
        ["kimi-code/k3-256k"] = new(3, 15, 3, 0.3),
        ["kimi-k2.7-code"] = new(0.95, 4, 0.95, 0.19),
        ["kimi-k2.7-code-highspeed"] = new(1.9, 8, 1.9, 0.38),
        ["kimi-code/kimi-for-coding-highspeed"] = new(1.9, 8, 1.9, 0.38),
        // K2.8 Preview: Moonshot publishes no price, so this is Tencent TokenHub's
        // ($1 in, $4 out, $0.25 cache hit, via LiteLLM), as the user chose.
        ["kimi-code/kimi-for-coding"] = new(1, 4, 1, 0.25),
        ["kimi-k2.6"] = new(0.95, 4, 0.95, 0.16),
        // The model ids Kimi's desktop app (Kimi Work) writes to its logs. k3d1 is K3.1 Pro,
        // k3 is K3, k2d8-preview is K2.8 Preview (the user's price above) and k2d6 is K2.6.
        // Any other model it writes has no known price and stays tokens only.
        ["k3d1-agent"] = new(2, 10, 2, 0.2),
        ["k3-agent"] = new(3, 15, 3, 0.3),
        ["k2d8-preview"] = new(1, 4, 1, 0.25),
        ["k2d6-agent"] = new(0.95, 4, 0.95, 0.16),
        // DeepSeek
        ["deepseek-v4-pro"] = new(1.32, 3.96, 1.32, 0.044),
        ["deepseek-v4-flash"] = new(0.3, 1.2, 0.3, 0.006),
        ["deepseek-flash"] = new(0.3, 1.2, 0.3, 0.006),
        // Google Gemini (models without a scheduled price change; implicit caching
        // has no write charge, so creation is the input rate).
        ["gemini-4-neon"] = new(3, 15, 3, 0.15),
        ["gemini-4-hydrogen"] = new(1.5, 7.5, 1.5, 0.075),
        // Antigravity Orchestrator internal routing aliases: Neon planner and Hydrogen worker.
        ["gemini-pro-orchestrator-neon"] = new(3, 15, 3, 0.15),
        ["gemini-flash-worker-agy"] = new(1.5, 7.5, 1.5, 0.075),
        ["gemini-3.5-flash-lite"] = new(0.3, 2.5, 0.3, 0.03),
        ["gemini-3.1-flash-lite"] = new(0.25, 1.5, 0.25, 0.025),
    };

    // Models whose price changes on a date: Google announced these rises ahead
    // of time. Each list starts at MinValue and the last row whose date has
    // passed applies, by the event's own time. Rates change at midnight
    // Pacific time, which is UTC-8 on both dates. Gemini's implicit caching
    // has no write charge, so creation is the input rate.
    private static readonly Dictionary<string, (DateTimeOffset From, Price Price)[]> Dated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-3.8-flash"] = new[]
        {
            (DateTimeOffset.MinValue, new Price(0.75, 3.75, 0.75, 0.075)),
            (MidnightPacific(2027, 1, 1), new Price(1.5, 7.5, 1.5, 0.15)),
        },
        ["gemini-4-argon"] = new[]
        {
            (DateTimeOffset.MinValue, new Price(2, 10, 2, 0.1)),
            (MidnightPacific(2026, 12, 1), new Price(4, 20, 4, 0.2)),
        },
        // Antigravity Orchestrator (Argon) planner alias.
        ["gemini-pro-orchestrator-g1-pro"] = new[]
        {
            (DateTimeOffset.MinValue, new Price(2, 10, 2, 0.1)),
            (MidnightPacific(2026, 12, 1), new Price(4, 20, 4, 0.2)),
        },
        ["gemini-4-carbon"] = new[]
        {
            (DateTimeOffset.MinValue, new Price(2, 10, 2, 0.1)),
            (MidnightPacific(2026, 12, 1), new Price(4, 20, 4, 0.2)),
        },
    };

    // DeepSeek's peak hours are 01:00-04:00 and 06:00-10:00 UTC, Monday to
    // Friday; the rest of the week, weekends included, bills at half on all
    // four rates. Chinese public holidays are not modelled and price as peak.
    private static readonly HashSet<string> DeepSeekModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "deepseek-v4-pro", "deepseek-v4-flash", "deepseek-flash",
    };

    private static bool IsDeepSeekPeak(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return utc.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && utc.Hour is (>= 1 and < 4) or (>= 6 and < 10);
    }

    private static DateTimeOffset MidnightPacific(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.FromHours(-8));

    private static bool TryPrice(TokenEvent tokenEvent, out Price price)
    {
        var model = CanonicalModelName(tokenEvent.PricedAs ?? tokenEvent.Model);
        if (Dated.TryGetValue(model, out var rows))
        {
            price = rows.Last(row => row.From <= tokenEvent.Timestamp).Price;
            return true;
        }
        if (!Table.TryGetValue(model, out price!)) return false;
        if (DeepSeekModels.Contains(model) && !IsDeepSeekPeak(tokenEvent.Timestamp))
        {
            price = new Price(price.Input / 2, price.Output / 2, price.CacheCreation / 2, price.CacheRead / 2);
        }
        return true;
    }

    public static double Cost(TokenEvent tokenEvent)
    {
        if (!TryPrice(tokenEvent, out var price)) return 0;
        var cc1h = Math.Clamp(tokenEvent.CacheCreation1hTokens, 0, tokenEvent.CacheCreationTokens);
        var cc5m = tokenEvent.CacheCreationTokens - cc1h;
        var cc1hRate = price.CacheCreation1h ?? price.CacheCreation;
        return (tokenEvent.InputTokens * price.Input
                + tokenEvent.OutputTokens * price.Output
                + cc5m * price.CacheCreation
                + cc1h * cc1hRate
                + tokenEvent.CacheReadTokens * price.CacheRead) / 1_000_000.0;
    }

    /// 这条调用有没有价格：日志自带金额（Grok），或者价格表里查得到它的计价名。
    /// 没有价格的调用 Cost 记 0，界面写“未定价”，不进合计金额。
    public static bool HasPrice(TokenEvent tokenEvent) =>
        tokenEvent.SelfReportedCostUSD is not null || TryPrice(tokenEvent, out _);

    /// 价格表最后一次核对的日期，Usage 页写在金额旁边。
    public static readonly DateOnly TableDate = new(2026, 11, 22);

    public static bool IsKnown(string model) =>
        Table.ContainsKey(CanonicalModelName(model)) || Dated.ContainsKey(CanonicalModelName(model));

    /// A model its maker gives away (Codex's auto-review): known, and every rate is zero.
    public static bool IsFree(string model) =>
        Table.TryGetValue(CanonicalModelName(model), out var price)
        && price is { Input: 0, Output: 0, CacheCreation: 0, CacheRead: 0, CacheCreation1h: null or 0 };

    /// A log's model id trimmed to the longest price key that equals it or
    /// is a prefix of it ending at a dash ("gemini-4-argon-trial" prices as
    /// "gemini-4-argon"). An id no key reaches stays as it is.
    public static string Family(string model)
    {
        string? best = null;
        foreach (var key in Table.Keys.Concat(Dated.Keys))
        {
            if (best is not null && key.Length <= best.Length) continue;
            if (model.Equals(key, StringComparison.OrdinalIgnoreCase)
                || model.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase)) best = key;
        }
        return best ?? model;
    }

    /// Date-suffixed model ids ("claude-haiku-4-5-20251001") strip to their
    /// base form: the suffix is a dash followed by exactly 8 digits.
    public static string CanonicalModelName(string model)
    {
        if (model.Length > 9)
        {
            var suffix = model[^9..];
            if (suffix[0] == '-' && suffix[1..].All(char.IsAsciiDigit))
            {
                return model[..^9];
            }
        }
        return model;
    }
}
