using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// Deep module owning Claude OAuth credential acquisition: the
/// env -&gt; credentials-file -&gt; refresh -&gt; rotation-writeback flow. On
/// macOS the store is the login Keychain; on Windows, Claude Code persists
/// the same claudeAiOauth JSON at %USERPROFILE%\.claude\.credentials.json.
///
/// The asymmetry between token sources is load-bearing:
///   - An env-token scope-insufficient (403) does NOT short-circuit; we
///     still try the stored token.
///   - A stored-token (or refreshed-token) scope-insufficient short-circuits
///     to re-auth, because refresh re-issues the same scope set and cannot
///     recover a missing `user:profile`.
public static class ClaudeCredentials
{
    /// The UI layer matches on this exact string to swap the error caption
    /// for an in-app re-auth button.
    public const string ReauthRequiredMessage = "re-login: claude /login";

    public const string AuthRequiredMessage = "auth required — run claude";

    /// The stored token 401'd AND the refresh grant failed. Distinct from the
    /// cold-start message on purpose: without it the caption stayed on
    /// "auth required — run claude" forever, so a revoked refresh token was
    /// indistinguishable from never having signed in (macOS #22).
    public const string TokenRefreshFailedMessage = "token refresh failed — sign in again";

    public static bool IsAuthRecoverableError(string? message) =>
        message is ReauthRequiredMessage or AuthRequiredMessage or TokenRefreshFailedMessage;

    // OAuth constants — captured verbatim from what the official `claude`
    // CLI 2.1.153 opens, and shared with the macOS ClaudeCredentials.
    public const string OauthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// Token endpoint for the refresh grant.
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";

    public abstract record ProbeOutcome
    {
        public sealed record Success(AppUsage Usage) : ProbeOutcome;
        public sealed record RateLimited : ProbeOutcome;
        public sealed record Unauthorized : ProbeOutcome;
        /// Token is structurally valid but missing a scope the server now
        /// requires (`user:profile`, added mid-2026). Refresh won't help.
        public sealed record ScopeInsufficient : ProbeOutcome;
        public sealed record OtherError(string Message) : ProbeOutcome;
    }

    public abstract record Resolution
    {
        public sealed record Usage(AppUsage Value) : Resolution;
        public sealed record ReauthRequired(string Message) : Resolution;
        public sealed record Failed(string Message) : Resolution;
    }

    // MARK: - Resolution

    /// Token sources, in order of freshness:
    ///   1. CLAUDE_CODE_OAUTH_TOKEN — set by Claude Desktop for child
    ///      processes; always fresh while Desktop is running.
    ///   2. ~/.claude/.credentials.json — written by `claude /login`; the
    ///      access token expires after ~8h, after which we refresh.
    ///   3. platform.claude.com/v1/oauth/token refresh — Anthropic rotates
    ///      the refresh_token on every call; the new pair MUST be persisted
    ///      back or Claude Code itself 401s on its next refresh.
    public static async Task<Resolution> ResolveUsage(Func<string, string?, Task<ProbeOutcome>> probe)
    {
        var lastError = AuthRequiredMessage;
        var cachedCreds = ReadClaudeCreds();
        var plan = cachedCreds?.SubscriptionType;

        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
        {
            switch (await probe(envToken, plan))
            {
                case ProbeOutcome.Success success: return new Resolution.Usage(success.Usage);
                case ProbeOutcome.RateLimited: lastError = "rate limited"; break;
                case ProbeOutcome.Unauthorized: break;
                case ProbeOutcome.ScopeInsufficient: lastError = ReauthRequiredMessage; break;
                case ProbeOutcome.OtherError error: lastError = error.Message; break;
            }
        }

        if (cachedCreds is { } creds)
        {
            switch (await probe(creds.AccessToken, plan))
            {
                case ProbeOutcome.Success success: return new Resolution.Usage(success.Usage);
                // A 429 is rate limiting, not an expired token — refreshing
                // wouldn't help and only doubles load on an already-limited
                // endpoint (and needlessly rotates the refresh token).
                case ProbeOutcome.RateLimited: return new Resolution.Failed("rate limited");
                // Refresh hands back tokens with the same scope set, so it
                // cannot recover from a missing-scope 403.
                case ProbeOutcome.ScopeInsufficient: return new Resolution.ReauthRequired(ReauthRequiredMessage);
                case ProbeOutcome.OtherError error: lastError = error.Message; break;
                case ProbeOutcome.Unauthorized:
                    // Only a 401 means the access token expired; refresh it.
                    var (recovered, recoveryError) = await RecoverFromUnauthorized(creds, plan, probe);
                    if (recovered is not null) return recovered;
                    if (recoveryError is not null) lastError = recoveryError;
                    break;
            }
        }

        return new Resolution.Failed(lastError);
    }

    /// The stored access token 401'd. Claude Code refreshes at about the same
    /// moment this does, and Anthropic rotates the refresh token on every
    /// call, so the file may already hold a newer pair than the copy read at
    /// the start: that pair is tried first, and refreshing the old one would
    /// only fail and read as a lost sign-in. A refresh that fails is followed
    /// by one more look at the file, for the same reason. Returns the final
    /// resolution, or null with the reason to keep when nothing here settled it.
    private static async Task<(Resolution? Final, string? Error)> RecoverFromUnauthorized(
        ClaudeCreds creds, string? plan, Func<string, string?, Task<ProbeOutcome>> probe)
    {
        var current = creds;
        if (ReadClaudeCreds() is { } latest && latest != creds)
        {
            current = latest;
            switch (await probe(latest.AccessToken, plan))
            {
                case ProbeOutcome.Success success: return (new Resolution.Usage(success.Usage), null);
                case ProbeOutcome.RateLimited: return (new Resolution.Failed("rate limited"), null);
                case ProbeOutcome.ScopeInsufficient: return (new Resolution.ReauthRequired(ReauthRequiredMessage), null);
                case ProbeOutcome.OtherError error: return (null, error.Message);
            }
        }
        var refresh = await RefreshToken(current.RefreshToken);
        if (refresh is not RefreshResult.Refreshed { Tokens: var refreshed })
        {
            // Claude Code may have refreshed between the read above and this
            // call, which spends our copy: one more look at the file, one probe.
            if (ReadClaudeCreds() is { } rotated && rotated != current)
            {
                switch (await probe(rotated.AccessToken, plan))
                {
                    case ProbeOutcome.Success success: return (new Resolution.Usage(success.Usage), null);
                    case ProbeOutcome.RateLimited: return (new Resolution.Failed("rate limited"), null);
                    case ProbeOutcome.ScopeInsufficient: return (new Resolution.ReauthRequired(ReauthRequiredMessage), null);
                    case ProbeOutcome.OtherError error: return (null, error.Message);
                }
            }
            // Only a refused grant means the sign-in is gone. A timeout, a
            // rate limit or a server error says nothing about it, and the
            // next poll asks again.
            return (null, refresh is RefreshResult.Unavailable unavailable ? unavailable.Message : TokenRefreshFailedMessage);
        }
        switch (WriteClaudeCreds(refreshed, current.RefreshToken))
        {
            // Anthropic rotates the refresh token, invalidating the pair we
            // just used. If we can't persist the rotated tokens, the on-disk
            // creds are now dead — force a re-login instead of 401-ing forever.
            case WriteBack.Failed:
                return (new Resolution.ReauthRequired(ReauthRequiredMessage), null);
            // Claude Code refreshed first, or the user signed in again (maybe
            // as someone else): the login in the file is the one to show.
            case WriteBack.Superseded:
                if (ReadClaudeCreds() is { } signedIn && signedIn != current)
                {
                    switch (await probe(signedIn.AccessToken, signedIn.SubscriptionType))
                    {
                        case ProbeOutcome.Success success: return (new Resolution.Usage(success.Usage), null);
                        case ProbeOutcome.RateLimited: return (new Resolution.Failed("rate limited"), null);
                        case ProbeOutcome.ScopeInsufficient: return (new Resolution.ReauthRequired(ReauthRequiredMessage), null);
                        case ProbeOutcome.OtherError error: return (null, error.Message);
                    }
                }
                return (null, null);
        }
        switch (await probe(refreshed.AccessToken, plan))
        {
            case ProbeOutcome.Success success: return (new Resolution.Usage(success.Usage), null);
            case ProbeOutcome.RateLimited: return (new Resolution.Failed("rate limited"), null);
            case ProbeOutcome.ScopeInsufficient: return (new Resolution.ReauthRequired(ReauthRequiredMessage), null);
            case ProbeOutcome.OtherError error: return (null, error.Message);
            default: return (null, null);
        }
    }

    // MARK: - Credentials file

    /// Test seams: another credentials file, and the refresh call.
    internal static string? CredentialsFileOverride;
    internal static Func<string, Task<RefreshResult>> RefreshToken = RefreshClaudeToken;

    private static string CredentialsFile => CredentialsFileOverride ?? IslandPaths.ClaudeCredentialsFile;

    private sealed record ClaudeCreds(
        string AccessToken,
        string RefreshToken,
        string? SubscriptionType);

    private static ClaudeCreds? ReadClaudeCreds()
    {
        try
        {
            var path = CredentialsFile;
            if (!File.Exists(path)) return null;
            using var stream = SharedFile.Open(path);
            using var doc = JsonDocument.Parse(stream);
            if (Jsonl.GetObject(doc.RootElement, "claudeAiOauth") is not { } oauth) return null;
            var access = Jsonl.GetString(oauth, "accessToken");
            var refresh = Jsonl.GetString(oauth, "refreshToken");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return null;
            return new ClaudeCreds(access!, refresh!, Jsonl.GetString(oauth, "subscriptionType"));
        }
        catch
        {
            return null;
        }
    }

    private enum WriteBack
    {
        Written,
        /// The file no longer holds the refresh token that was sent; it was
        /// left alone.
        Superseded,
        Failed,
    }

    /// Updates the claudeAiOauth block in place, preserving unrelated fields
    /// (scopes, subscriptionType, rateLimitTier) and any other top-level
    /// keys in the file; seeds a minimal structure when the file doesn't
    /// exist yet (the CLI deleted it between the read and the write).
    /// Failed if the write ultimately fails — Anthropic has already
    /// invalidated the OLD refresh token server-side, so a lost write means
    /// the on-disk credentials are dead and the caller must force a re-login
    /// rather than 401 forever.
    ///
    /// The tokens belong to the login whose refresh token was sent, so they
    /// are written only while the file still holds it (the Grok rule of
    /// c9c591e): if Claude Code refreshed first, or the user signed in again
    /// as another account, the file is left alone. A file that exists but
    /// cannot be read or parsed is never replaced either — a bare skeleton
    /// would wipe scopes, subscriptionType and the MCP logins beside the pair.
    ///
    /// There is no chmod 0600 to mirror here: the file lives inside
    /// %USERPROFILE%, and both the temp file and the destination inherit that
    /// tree's per-user DACL — the same protection Claude Code's own writer
    /// gets on Windows.
    private static WriteBack WriteClaudeCreds(RefreshedTokens refreshed, string sentRefreshToken)
    {
        var tmp = string.Empty;
        try
        {
            var path = CredentialsFile;
            if (ReadForWriteBack(path, out var missing) is not { } root) return WriteBack.Failed;
            if (root["claudeAiOauth"] is JsonObject oauth)
            {
                var held = oauth["refreshToken"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                if (!string.Equals(held, sentRefreshToken, StringComparison.Ordinal)) return WriteBack.Superseded;
            }
            else if (missing)
            {
                oauth = new JsonObject();
                root["claudeAiOauth"] = oauth;
            }
            else
            {
                return WriteBack.Superseded;
            }
            oauth["accessToken"] = refreshed.AccessToken;
            oauth["refreshToken"] = refreshed.RefreshToken;
            oauth["expiresAt"] = refreshed.ExpiresAtMs;

            // The CLI may have removed the folder meanwhile,
            // so make sure it exists.
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            // Per-write temp name, not a fixed ".tmp": Windows denies a second
            // opener while the first holds the handle, so two Agent Island
            // instances refreshing at once would fail each other's write —
            // and a failed write here forces the user through a re-login.
            tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            // Retry the rename: Claude Code / Claude Desktop may hold a brief
            // read lock on the creds file, and dropping the rotated token
            // bricks auth for every consumer. A handle held without
            // share-delete makes the replace throw UnauthorizedAccessException.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return WriteBack.Written;
                }
                catch (Exception moveError) when (attempt < 4 && moveError is IOException or UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"AgentIsland: failed to write rotated Claude tokens: {error.Message}");
            // Never strand the temp copy: it holds a live token pair, and
            // nothing else ever cleans up ~\.claude.
            try { if (tmp.Length > 0) File.Delete(tmp); } catch { }
            return WriteBack.Failed;
        }
    }

    /// The file as it stands, or null when it exists but cannot be read or
    /// parsed even after a few short waits (Claude Code rewrites it in place,
    /// so it can be caught empty or half-written). Shared, because Claude Code
    /// and Claude Desktop keep it open. Only a missing file reads as a fresh
    /// object.
    private static JsonObject? ReadForWriteBack(string path, out bool missing)
    {
        missing = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var existing = SharedFile.Open(path);
                if (JsonNode.Parse(existing) is JsonObject root) return root;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                missing = true;
                return new JsonObject();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            if (attempt >= 4) return null;
            System.Threading.Thread.Sleep(25 * (attempt + 1));
        }
    }

    /// Cheap local change signal used by the re-auth poll — file mtime here,
    /// keychain `mdat` on macOS.
    public static string? CredentialsModificationStamp()
    {
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

    // MARK: - Refresh

    internal sealed record RefreshedTokens(string AccessToken, string RefreshToken, long ExpiresAtMs);

    internal abstract record RefreshResult
    {
        internal sealed record Refreshed(RefreshedTokens Tokens) : RefreshResult;
        /// The token endpoint refused the grant: the refresh token is spent
        /// or revoked, and only a new sign-in helps.
        internal sealed record Rejected : RefreshResult;
        /// The network, a rate limit or a server error stood in the way.
        internal sealed record Unavailable(string Message) : RefreshResult;
    }

    private static async Task<RefreshResult> RefreshClaudeToken(string refreshToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = OauthClientId,
            });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.Client.SendAsync(request);
            return RefreshResultFor((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(), DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return new RefreshResult.Unavailable(L10n.Tr("network drop"));
        }
        catch (Exception error)
        {
            return new RefreshResult.Unavailable(error.Message);
        }
    }

    /// 400 is invalid_grant, 401 and 403 a refused client or account: those
    /// are the sign-in. Anything else — 429, a 5xx, a body that is not the
    /// token reply (a captive portal) — is not, so it never asks the user to
    /// sign in again.
    internal static RefreshResult RefreshResultFor(int status, byte[] body, DateTimeOffset now)
    {
        if (status is 400 or 401 or 403) return new RefreshResult.Rejected();
        if (status != 200) return new RefreshResult.Unavailable($"HTTP {status}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var access = Jsonl.GetString(doc.RootElement, "access_token");
            var refresh = Jsonl.GetString(doc.RootElement, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return new RefreshResult.Unavailable("bad response");
            var expiresIn = Jsonl.GetDouble(doc.RootElement, "expires_in") ?? 28_800;
            var expiresAt = now.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
            return new RefreshResult.Refreshed(new RefreshedTokens(access!, refresh!, expiresAt));
        }
        catch (JsonException)
        {
            return new RefreshResult.Unavailable("bad response");
        }
    }
}
