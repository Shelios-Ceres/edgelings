using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// One selected login entry from the Grok CLI credential store.
public sealed record GrokAuthEntry(
    string Scope,
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? AuthMode,
    string? Email)
{
    public bool IsSuperGrok => string.Equals(AuthMode, "oidc", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"GrokAuthEntry {{ Scope = {Scope}, tokens and account redacted }}";
}

/// Read-only view of %USERPROFILE%\.grok\auth.json. Top-level keys are OIDC
/// scope URLs — "https://auth.x.ai::&lt;client-id&gt;" for SuperGrok logins.
/// `expires_at` is epoch milliseconds. Limen never refreshes this login and
/// never writes the file: the refresh token rotates on every exchange, so
/// only the grok CLI itself may use it. The refresh token is not even kept.
public static class GrokAuthFile
{
    private const string OidcScopeMarker = "auth.x.ai";

    /// GROK_HOME overrides the credential directory, matching the CLI.
    public static string DefaultPath => Path.Combine(IslandPaths.GrokHome, "auth.json");

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static GrokAuthEntry? LoadEntry(string? path = null) => Inspect(path ?? DefaultPath);

    internal static GrokAuthEntry? Inspect(string file)
    {
        try
        {
            if (ReadRoot(file) is not { } root) return null;
            return SelectEntry(root) is { } selected ? Entry(selected.Scope, selected.Raw) : null;
        }
        catch
        {
            return null;
        }
    }

    /// The file as it stands, or null when it is missing or cannot be read or
    /// parsed even after a few short waits: the grok CLI rewrites it in place,
    /// so it can be caught locked or half-written.
    private static JsonObject? ReadRoot(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = SharedFile.Open(path);
                if (JsonNode.Parse(stream) is JsonObject root) return root;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            if (attempt >= 4) return null;
            System.Threading.Thread.Sleep(25 * (attempt + 1));
        }
    }

    /// Whether the access token is gone or goes within the skew (a minute, so
    /// an in-flight request never races the clock). A login with no expiry
    /// is taken as live: the billing call will say otherwise.
    public static bool IsExpired(GrokAuthEntry entry, DateTimeOffset? now = null, TimeSpan? skew = null)
    {
        if (entry.ExpiresAt is not { } expiresAt) return false;
        return (now ?? DateTimeOffset.Now) + (skew ?? TimeSpan.FromSeconds(60)) >= expiresAt;
    }

    /// What the file looked like when it was read: modification time and size
    /// (of the target, when auth.json is a link), or null when it cannot be
    /// seen. Taken before the content is read, so a rewrite that lands in
    /// between shows as a change at the next look.
    internal static string? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.LinkTarget is not null) info = info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info;
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // MARK: - Entry selection

    /// Prefer "auth.x.ai" scopes (the SuperGrok OIDC login); among candidates
    /// prefer non-expired, then latest expiry, then stable scope order.
    /// Legacy session scopes are a last resort.
    private static (string Scope, JsonObject Raw)? SelectEntry(JsonObject root)
    {
        var oidc = new List<(string Scope, JsonObject Raw)>();
        var legacy = new List<(string Scope, JsonObject Raw)>();
        foreach (var (scope, value) in root)
        {
            if (value is not JsonObject raw || NonEmpty(raw["key"]) is null) continue;
            if (scope.Contains(OidcScopeMarker, StringComparison.Ordinal)) oidc.Add((scope, raw));
            else legacy.Add((scope, raw));
        }
        return Best(oidc) ?? Best(legacy);
    }

    private static (string Scope, JsonObject Raw)? Best(List<(string Scope, JsonObject Raw)> candidates)
    {
        if (candidates.Count == 0) return null;
        var now = DateTimeOffset.Now;
        return candidates
            // A missing expiry counts as "never expires" for the liveness
            // test but sorts last on recency — an undated entry should not
            // outrank a freshly minted one.
            .OrderBy(candidate => (ParseExpiry(candidate.Raw["expires_at"]) ?? DateTimeOffset.MaxValue) <= now)
            .ThenByDescending(candidate => ParseExpiry(candidate.Raw["expires_at"]) ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Scope, StringComparer.Ordinal)
            .First();
    }

    private static GrokAuthEntry? Entry(string scope, JsonObject raw)
    {
        if (NonEmpty(raw["key"]) is not { } accessToken) return null;
        return new GrokAuthEntry(
            scope,
            accessToken,
            ParseExpiry(raw["expires_at"]),
            NonEmpty(raw["auth_mode"]),
            NonEmpty(raw["email"]));
    }

    /// `expires_at` is epoch milliseconds on current CLI logins; tolerate
    /// epoch seconds and ISO strings from other writer versions.
    public static DateTimeOffset? ParseExpiry(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (Number(node) is { } raw) return FromEpochMilliseconds(raw > 1e11 ? raw : raw * 1000);
        return value.TryGetValue<string>(out var text) ? GrokTimestamp.Parse(text) : null;
    }

    // MARK: - Value coercion

    private static string? NonEmpty(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (!value.TryGetValue<string>(out var text)) return null;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// Numbers arrive either element-backed (parsed from the file) or as the
    /// long this app just assigned, so both backings are probed before the
    /// string fallback older writers used.
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static DateTimeOffset? FromEpochMilliseconds(double milliseconds)
    {
        if (double.IsNaN(milliseconds)
            || milliseconds < -62_135_596_800_000d
            || milliseconds > 253_402_300_799_999d)
        {
            return null;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }
}
