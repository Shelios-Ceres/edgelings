using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// One Kimi Code quota window: share used (0-1) and when it refills.
public sealed record KimiQuotaWindow(double UsedPercent, DateTimeOffset? ResetAt);

/// Kimi Code's plan quotas — the 5-hour and weekly windows when the plan has
/// them, the monthly total (shared with the Kimi chat app) and Kimi Code's
/// part of it — plus the membership level and the booster wallet balance.
public sealed record KimiUsageSnapshot(
    KimiQuotaWindow? FiveHour,
    KimiQuotaWindow? Weekly,
    KimiQuotaWindow? Month,
    KimiQuotaWindow? MonthCode,
    string? Plan,
    long? BoosterCents,
    string? BoosterCurrency);

/// Reads Kimi Code's plan usage the way Kimi Code itself does. The running
/// app's local server is asked first (127.0.0.1, its own server.token): it
/// refreshes its own sign-in before calling Kimi. Without it, the stored
/// access token is used read-only while it is still valid. This never
/// refreshes Kimi's tokens itself — Kimi Code signs out when a refresh token
/// it holds is spent behind its back.
public static class KimiUsageFetcher
{
    public abstract record Outcome
    {
        public sealed record Success(KimiUsageSnapshot Snapshot) : Outcome;
        public sealed record SignInStale : Outcome;
        public sealed record Failed(string Message) : Outcome;
        public sealed record NotInstalled : Outcome;
    }

    private static readonly TimeSpan HeartbeatFresh = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private static string Home => Path.Combine(IslandPaths.Home, ".kimi-code");

    public static async Task<Outcome> Fetch()
    {
        if (!Directory.Exists(Home)) return new Outcome.NotInstalled();
        string? failure = null;
        if (LocalServer() is { } server)
        {
            try
            {
                return new Outcome.Success(await FromLocalServer(server.BaseUrl, server.Token));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                failure = error.Message;
            }
        }
        if (StoredAccessToken() is { } token)
        {
            try
            {
                return new Outcome.Success(await FromApi(ApiBase(), token));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                failure = error.Message;
            }
        }
        return failure is null ? new Outcome.SignInStale() : new Outcome.Failed(failure);
    }

    // MARK: - Local server

    /// The newest live instance Kimi Code registered — its desktop app runs
    /// one while open, beating its heartbeat every 15 s. Only a loopback
    /// host is accepted: the server token never leaves this machine.
    internal static (string BaseUrl, string Token)? LocalServer()
    {
        try
        {
            var tokenPath = Path.Combine(Home, "server.token");
            var instances = Path.Combine(Home, "server", "instances");
            if (!File.Exists(tokenPath) || !Directory.Exists(instances)) return null;
            var token = File.ReadAllText(tokenPath).Trim();
            if (token.Length == 0) return null;
            var newest = new DirectoryInfo(instances).EnumerateFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(newest.FullName));
            if (Jsonl.GetLong(doc.RootElement, "pid") is { } pid && !ProcessAlive(pid)) return null;
            return ServerBaseUrl(doc.RootElement, DateTimeOffset.UtcNow) is { } baseUrl ? (baseUrl, token) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// Kimi Code's own liveness rule: a registration whose process is gone
    /// is stale, however fresh its heartbeat.
    private static bool ProcessAlive(long pid)
    {
        if (pid is <= 0 or > int.MaxValue) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// What changes when Kimi Code signs in again: its CLI writes a fresh
    /// access token (they last 15 minutes) and its desktop app registers a
    /// new server when it opens. The registration's name, not its write
    /// time: the heartbeat rewrites the file every 15 s. Null without Kimi
    /// Code.
    public static string? SignInStamp()
    {
        try
        {
            if (!Directory.Exists(Home)) return null;
            var credentials = new FileInfo(Path.Combine(Home, "credentials", "kimi-code.json"));
            var instances = new DirectoryInfo(Path.Combine(Home, "server", "instances"));
            var server = instances.Exists
                ? instances.EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.Name
                : null;
            return $"{(credentials.Exists ? credentials.LastWriteTimeUtc.Ticks : 0)}|{server}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The instance's API root, or null unless it is loopback and alive.
    internal static string? ServerBaseUrl(JsonElement instance, DateTimeOffset now)
    {
        var host = Jsonl.GetString(instance, "host");
        var port = Jsonl.GetLong(instance, "port") ?? 0;
        var heartbeat = Jsonl.GetDouble(instance, "heartbeat_at") ?? 0;
        if (host is not ("127.0.0.1" or "localhost" or "::1") || port is <= 0 or > 65535) return null;
        if (heartbeat is <= 0 or > 253_402_300_799_999) return null;
        if (now - DateTimeOffset.FromUnixTimeMilliseconds((long)heartbeat) > HeartbeatFresh) return null;
        var authority = host == "::1" ? "[::1]" : host;
        return $"http://{authority}:{port}/api/v1";
    }

    private static async Task<KimiUsageSnapshot> FromLocalServer(string baseUrl, string token)
    {
        using var usageDoc = await Get($"{baseUrl}/oauth/usage", token);
        var usage = Envelope(usageDoc.RootElement);
        if (Jsonl.GetString(usage, "kind") != "ok" || Jsonl.GetObject(usage, "quota") is not { } quota)
        {
            throw new InvalidDataException(Jsonl.GetString(usage, "message") ?? "usage unavailable");
        }
        string? plan = null;
        try
        {
            using var infoDoc = await Get($"{baseUrl}/oauth/userinfo", token);
            var info = Envelope(infoDoc.RootElement);
            if (Jsonl.GetObject(info, "userInfo") is { } user) plan = Jsonl.GetString(user, "userLevelName");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            // The tier is a badge; the quotas still stand without it.
        }
        return FromLocalQuota(quota, plan);
    }

    /// The local server's parsed quota: camelCase windows and the booster
    /// wallet already converted to cents.
    internal static KimiUsageSnapshot FromLocalQuota(JsonElement quota, string? plan)
    {
        var usages = Jsonl.GetObject(quota, "usages");
        var booster = Jsonl.GetObject(quota, "extraUsage");
        return new KimiUsageSnapshot(
            Window(usages, "limit5h", "usedRatio", "resetAt"),
            Window(usages, "limit7d", "usedRatio", "resetAt"),
            Window(usages, "monthTotal", "usedRatio", "resetAt"),
            Window(usages, "monthCode", "usedRatio", "resetAt"),
            plan,
            booster is { } b ? Jsonl.GetLong(b, "balanceCents") : null,
            booster is { } c ? Jsonl.GetString(c, "currency") : null);
    }

    /// The local server wraps results as { code: 0, data: ... }.
    private static JsonElement Envelope(JsonElement root)
    {
        if (Jsonl.GetLong(root, "code") is not 0 || Jsonl.GetObject(root, "data") is not { } data)
        {
            throw new InvalidDataException(Jsonl.GetString(root, "msg") ?? "local server error");
        }
        return data;
    }

    // MARK: - Kimi API with the stored token

    /// The stored access token while it has a minute left; null otherwise.
    private static string? StoredAccessToken()
    {
        try
        {
            var path = Path.Combine(Home, "credentials", "kimi-code.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var token = Jsonl.GetString(doc.RootElement, "access_token");
            var expires = Jsonl.GetDouble(doc.RootElement, "expires_at") ?? 0;
            var expiresAt = expires > 1e11
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)expires)
                : DateTimeOffset.FromUnixTimeSeconds((long)expires);
            return token is { Length: > 0 } && expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1) ? token : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// Kimi Code's managed base URL from its config; only Kimi's own two
    /// hosts are accepted, so the token is never sent anywhere else.
    private static string ApiBase()
    {
        try
        {
            var config = File.ReadAllText(Path.Combine(Home, "config.toml"));
            var match = Regex.Match(config, @"base_url\s*=\s*""(https://api\.kimi\.(?:com|ai)/coding/v1)/?""");
            if (match.Success) return match.Groups[1].Value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return "https://api.kimi.com/coding/v1";
    }

    private static async Task<KimiUsageSnapshot> FromApi(string baseUrl, string token)
    {
        using var usageDoc = await Get($"{baseUrl}/usages", token);
        string? plan = null;
        try
        {
            using var me = await Get($"{baseUrl}/me", token);
            plan = Jsonl.GetString(me.RootElement, "user_level_name");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
        return FromApiPayload(usageDoc.RootElement, plan);
    }

    /// Kimi's /usages payload: snake_case windows with numeric or string
    /// ratios, and the booster balance in fixed point (1e6 per cent).
    internal static KimiUsageSnapshot FromApiPayload(JsonElement root, string? plan)
    {
        var usages = Jsonl.GetObject(root, "usages");
        long? booster = null;
        string? currency = null;
        if (Jsonl.GetObject(root, "boosterWallet") is { } wallet
            && Jsonl.GetObject(wallet, "balance") is { } balance
            && Jsonl.GetString(balance, "type") == "BOOSTER"
            && Number(balance, "amountLeft") is { } left)
        {
            booster = (long)Math.Round(left / 1e6);
            currency = Jsonl.GetObject(wallet, "monthlyUsed") is { } used ? Jsonl.GetString(used, "currency") : null;
        }
        return new KimiUsageSnapshot(
            Window(usages, "limit_5h", "used_ratio", "reset_time"),
            Window(usages, "limit_7d", "used_ratio", "reset_time"),
            Window(usages, "limit_month_total", "used_ratio", "reset_time"),
            Window(usages, "limit_month_code", "used_ratio", "reset_time"),
            plan,
            booster,
            currency);
    }

    // MARK: - Shared

    private static async Task<JsonDocument> Get(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        using var cancel = new CancellationTokenSource(Timeout);
        using var response = await Http.Client.SendAsync(request, cancel.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancel.Token);
    }

    private static KimiQuotaWindow? Window(JsonElement? usages, string key, string ratioKey, string resetKey)
    {
        if (usages is not { } parent || Jsonl.GetObject(parent, key) is not { } entry) return null;
        if (Number(entry, ratioKey) is not { } ratio) return null;
        DateTimeOffset? reset = Jsonl.GetString(entry, resetKey) is { } raw
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
        return new KimiQuotaWindow(Math.Clamp(ratio, 0, 1), reset);
    }

    /// Kimi sends numbers as numbers or numeric strings.
    private static double? Number(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }
}
