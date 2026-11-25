using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// One selected login entry from the Grok CLI credential store.
public sealed record GrokAuthEntry(
    string Scope,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? AuthMode,
    string? Email,
    string OidcIssuer,
    string? OidcClientId)
{
    public bool IsSuperGrok => string.Equals(AuthMode, "oidc", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"GrokAuthEntry {{ Scope = {Scope}, tokens and account redacted }}";
}

/// Reader/rotator for %USERPROFILE%\.grok\auth.json. Top-level keys are OIDC
/// scope URLs — "https://auth.x.ai::&lt;client-id&gt;" for SuperGrok logins, with
/// the client id riding after the "::". `expires_at` is epoch milliseconds.
///
/// The token endpoint rotates the refresh token on every exchange, so a
/// successful refresh MUST be written back — losing the new refresh token
/// strands the user's own `grok` CLI login. The writeback re-reads the file
/// first, so every field this app doesn't understand survives verbatim, and
/// lands atomically (sibling temp file + File.Replace, which keeps the ACL and
/// never recreates a file that was removed) so a crash mid-write can never
/// leave a half-written credential file behind.
public static class GrokAuthFile
{
    public const string DefaultIssuer = "https://auth.x.ai";
    private const string OidcScopeMarker = "auth.x.ai";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// GROK_HOME overrides the credential directory, matching the CLI.
    public static string DefaultPath => Path.Combine(IslandPaths.GrokHome, "auth.json");

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static GrokAuthEntry? LoadEntry(string? path = null) => Inspect(path ?? DefaultPath, out _);

    internal enum LoginState
    {
        Present,
        Missing,
        /// The file parses but holds no usable login: signed out.
        NoLogin,
        /// Locked, half-written or otherwise unreadable right now.
        Unreadable,
    }

    internal static GrokAuthEntry? Inspect(string file, out LoginState state)
    {
        try
        {
            if (!File.Exists(file))
            {
                state = LoginState.Missing;
                return null;
            }
            if (ReadRoot(file, out var missing) is not { } root)
            {
                state = missing ? LoginState.Missing : LoginState.Unreadable;
                return null;
            }
            if (SelectEntry(root) is not { } selected || Entry(selected.Scope, selected.Raw) is not { } entry)
            {
                state = LoginState.NoLogin;
                return null;
            }
            state = LoginState.Present;
            return entry;
        }
        catch
        {
            state = LoginState.Unreadable;
            return null;
        }
    }

    /// The file as it stands, or null when it is missing (`missing` is set:
    /// signed out) or cannot be read or parsed even after a few short waits:
    /// the grok CLI rewrites it in place, so it can be caught locked or
    /// half-written.
    private static JsonObject? ReadRoot(string path, out bool missing)
    {
        missing = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = SharedFile.Open(path);
                if (JsonNode.Parse(stream) is JsonObject root) return root;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                missing = true;
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            if (attempt >= 4) return null;
            System.Threading.Thread.Sleep(25 * (attempt + 1));
        }
    }

    /// Refresh a minute early so an in-flight request never races the clock.
    public static bool NeedsRefresh(GrokAuthEntry entry, DateTimeOffset? now = null, TimeSpan? skew = null)
    {
        if (entry.ExpiresAt is not { } expiresAt) return false;
        return (now ?? DateTimeOffset.Now) + (skew ?? TimeSpan.FromSeconds(60)) >= expiresAt;
    }

    /// Apply an OAuth token response (`access_token` / rotated
    /// `refresh_token` / `expires_in` seconds) to the entry at `scope` and
    /// persist it. `sentRefreshToken` is the one the request carried: the
    /// response belongs to that login, and is written only while the entry
    /// still holds it. If the user logged in again meanwhile (the scope is the
    /// same for any account), or the CLI refreshed first, the file is left
    /// untouched and the entry now in it is returned, so the poll goes on with
    /// the current login. Returns the updated entry, or null when nothing was
    /// written and there is no current entry to use (malformed response,
    /// missing file or scope, write failure) — the caller then keeps using its
    /// in-memory token.
    public static GrokAuthEntry? ApplyRefreshResponse(
        string responseJson, string path, string scope, string sentRefreshToken, DateTimeOffset? now = null)
    {
        var applied = ApplyRefresh(responseJson, path, scope, sentRefreshToken, now ?? DateTimeOffset.Now);
        return applied.Status is ApplyStatus.Written or ApplyStatus.Superseded ? applied.Entry : null;
    }

    internal enum ApplyStatus
    {
        Written,
        /// The file holds another login than the one that was refreshed; left alone.
        Superseded,
        /// The reply has no usable access token.
        BadResponse,
        /// The file or the login is gone; nothing was written and nothing is to be recreated.
        LoggedOut,
        /// The reply is good but the file could not be read or replaced.
        WriteFailed,
    }

    internal sealed record ApplyOutcome(ApplyStatus Status, GrokAuthEntry? Entry);

    /// What a token reply carries that matters here (not an id_token or anything
    /// else it may hold): enough to write the file or to use the pair from memory.
    internal sealed record TokenReply(string AccessToken, string? RefreshToken, double? ExpiresInSeconds)
    {
        public override string ToString() => "TokenReply { tokens redacted }";
    }

    internal static TokenReply? ParseReply(string responseJson)
    {
        try
        {
            if (JsonNode.Parse(responseJson) is not JsonObject response) return null;
            if (NonEmpty(response["access_token"]) is not { } accessToken) return null;
            return new TokenReply(accessToken, NonEmpty(response["refresh_token"]), Number(response["expires_in"]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ApplyOutcome ApplyRefresh(
        string responseJson, string path, string scope, string expectedOnDisk, DateTimeOffset now) =>
        ParseReply(responseJson) is { } reply
            ? ApplyRefresh(reply, path, scope, expectedOnDisk, now)
            : new(ApplyStatus.BadResponse, null);

    /// `expectedOnDisk` is the refresh token the file must still hold for the
    /// reply to be written: the one the request carried, or the one an earlier
    /// pending write-back is waiting to replace.
    internal static ApplyOutcome ApplyRefresh(
        TokenReply reply, string path, string scope, string expectedOnDisk, DateTimeOffset now)
    {
        try
        {
            var root = ReadRoot(path, out var missing);
            if (missing) return new(ApplyStatus.LoggedOut, null);
            if (root is null) return new(ApplyStatus.WriteFailed, null);
            if (root[scope] is not JsonObject raw)
            {
                return SelectEntry(root) is { } other
                    ? new(ApplyStatus.Superseded, Entry(other.Scope, other.Raw))
                    : new(ApplyStatus.LoggedOut, null);
            }
            if (!string.Equals(NonEmpty(raw["refresh_token"]), expectedOnDisk, StringComparison.Ordinal))
            {
                return new(ApplyStatus.Superseded, Entry(scope, raw));
            }

            // The write works on a fresh copy of the file, read again before each
            // try; `raw` above only served to see which login this is.
            var plan = new AtomicReplace.Plan(
                latest => latest[scope] is JsonObject now
                    && string.Equals(NonEmpty(now["refresh_token"]), expectedOnDisk, StringComparison.Ordinal),
                latest =>
                {
                    var entry = (JsonObject)latest[scope]!;
                    entry["key"] = reply.AccessToken;
                    if (reply.RefreshToken is { } rotated) entry["refresh_token"] = rotated;
                    if (reply.ExpiresInSeconds is { } expiresIn && expiresIn > 0)
                    {
                        entry["expires_at"] = now.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
                    }
                },
                WriteOptions);
            var (result, written) = AtomicReplace.Run(path, plan);
            return result switch
            {
                AtomicReplace.Result.Written => new(ApplyStatus.Written, Entry(scope, (JsonObject)written![scope]!)),
                AtomicReplace.Result.Gone => new(ApplyStatus.LoggedOut, null),
                AtomicReplace.Result.Superseded => new(ApplyStatus.Superseded, Inspect(path, out _)),
                _ => new(ApplyStatus.WriteFailed, null),
            };
        }
        catch
        {
            return new(ApplyStatus.WriteFailed, null);
        }
    }

    /// The entry as the reply leaves it, for use from memory while the file
    /// cannot be written.
    internal static GrokAuthEntry EntryFromReply(GrokAuthEntry current, TokenReply reply, DateTimeOffset now) =>
        current with
        {
            AccessToken = reply.AccessToken,
            RefreshToken = reply.RefreshToken ?? current.RefreshToken,
            ExpiresAt = reply.ExpiresInSeconds is { } expiresIn && expiresIn > 0 ? now.AddSeconds(expiresIn) : current.ExpiresAt,
        };

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
            NonEmpty(raw["refresh_token"]),
            ParseExpiry(raw["expires_at"]),
            NonEmpty(raw["auth_mode"]),
            NonEmpty(raw["email"]),
            NonEmpty(raw["oidc_issuer"]) ?? DefaultIssuer,
            NonEmpty(raw["oidc_client_id"]) ?? ClientIdFromScope(scope));
    }

    private static string? ClientIdFromScope(string scope)
    {
        var marker = scope.IndexOf("::", StringComparison.Ordinal);
        if (marker < 0) return null;
        var id = scope[(marker + 2)..];
        return id.Length == 0 ? null : id;
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
