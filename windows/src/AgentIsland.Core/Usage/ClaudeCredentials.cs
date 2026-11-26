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

    /// Shown when the retries for a refresh whose outcome was unknown are used up:
    /// only a new sign-in is left to try.
    public const string GaveUpMessage = "Couldn't renew the sign-in; check the network or sign in again";

    public static bool IsAuthRecoverableError(string? message) =>
        message is ReauthRequiredMessage or AuthRequiredMessage or TokenRefreshFailedMessage or GaveUpMessage
        || (message is not null && message == L10n.Tr(GaveUpMessage));

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
        var resolution = await ResolveUsageCore(probe);
        // The quota came from a pair that could not be saved for good: say so,
        // while Claude Code may be about to refresh with the token the file holds.
        if (resolution is Resolution.Usage && PendingExhausted && HasPendingWriteBack)
        {
            return new Resolution.Failed(L10n.Tr(SaveFailedMessage));
        }
        return resolution;
    }

    private const string SaveFailedMessage = "Couldn't save the refreshed sign-in; Claude Code may need to sign in again";

    private static async Task<Resolution> ResolveUsageCore(Func<string, string?, Task<ProbeOutcome>> probe)
    {
        var lastError = AuthRequiredMessage;
        var cachedCreds = ApplyPending(ReadClaudeCreds(), tryWrite: true);
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

        if (cachedCreds is { } held && IsDeadSignIn(held))
        {
            // The refresh token in the store was refused and the store has not
            // changed since: asking again would only replay a dead token.
            return new Resolution.Failed(TokenRefreshFailedMessage);
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
        if (ApplyPending(ReadClaudeCreds(), tryWrite: false) is not { } latest) return (null, AuthRequiredMessage);
        if (latest != creds)
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
        var grant = Grants.Begin(current.RefreshToken);
        if (grant is null) return (null, L10n.Tr(Grants.GaveUp(current.RefreshToken) ? GaveUpMessage : "Waiting for updated sign-in"));
        var refresh = await RefreshToken(current.RefreshToken);
        if (refresh is not RefreshResult.Refreshed { Tokens: var refreshed })
        {
            if (refresh is RefreshResult.Unavailable { Uncertain: true } uncertain) Grants.Ambiguous(grant, uncertain.Reason);
            else Grants.Settled(grant);
            if (refresh is RefreshResult.Rejected) MarkDead(current);
            // Claude Code may have refreshed between the read above and this
            // call, which spends our copy: one more look at the file, one probe.
            if (ApplyPending(ReadClaudeCreds(), tryWrite: false) is { } rotated && rotated != current)
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
            // rate limit or a server error does not prove sign-out.
            return (null, refresh is RefreshResult.Unavailable unavailable ? unavailable.Message : TokenRefreshFailedMessage);
        }
        CredentialRefreshLog.Recovered("claude");
        var write = PersistRotation(refreshed, current, grant);
        switch (write)
        {
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

    // MARK: - Dead sign-in

    /// A refresh the server refused (400, 401, 403) means the refresh token is
    /// spent for good. It is remembered by a hash, never the value, and not
    /// sent again, nor is its access token, until the store holds a different
    /// refresh token: a new sign-in. Only the refresh token counts. The
    /// credentials file also keeps Claude Code's own things (MCP logins), and
    /// the Keychain item's date moves with them, so a changed stamp proves
    /// nothing. When the store cannot be read there is nothing to send, so
    /// the hold stays until it can be, however the stamp flickers meanwhile.
    private sealed record DeadSignIn(string Fingerprint);

    private static readonly object DeadGate = new();
    private static DeadSignIn? Dead;

    private static string Fingerprint(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void MarkDead(ClaudeCreds creds)
    {
        lock (DeadGate) Dead = new DeadSignIn(Fingerprint(creds.RefreshToken));
        CredentialRefreshLog.Note("claude", "refresh refused, waiting for a new sign-in");
    }

    private static bool IsDeadSignIn(ClaudeCreds creds)
    {
        DeadSignIn? dead;
        lock (DeadGate) dead = Dead;
        if (dead is null) return false;
        if (dead.Fingerprint == Fingerprint(creds.RefreshToken)) return true;
        lock (DeadGate) if (ReferenceEquals(Dead, dead)) Dead = null;
        CredentialRefreshLog.Note("claude", "new sign-in, trying again");
        return false;
    }

    // MARK: - Credentials file

    /// Test seams: another credentials file, and the refresh call.
    internal static string? CredentialsFileOverride;
    internal static Func<string, Task<RefreshResult>> RefreshToken = RefreshClaudeToken;

    private static string CredentialsFile => CredentialsFileOverride ?? IslandPaths.ClaudeCredentialsFile;

    /// KeychainService names the Keychain item the pair was read from (macOS);
    /// null means the credentials file.
    private sealed record ClaudeCreds(
        string AccessToken,
        string RefreshToken,
        string? SubscriptionType,
        string? KeychainService = null)
    {
        public override string ToString() => "ClaudeCreds { tokens redacted }";
    }

    /// Test seam: the keychain to read, or null for the real one on a Mac.
    /// The real login keychain is used only on macOS, with no file override
    /// and no root override: a test or a bench never reaches it.
    internal static ClaudeKeychain? KeychainOverride;

    private static ClaudeKeychain? Keychain =>
        KeychainOverride ?? (OperatingSystem.IsMacOS() && CredentialsFileOverride is null && IslandPaths.RootOverride is null ? ClaudeKeychain.Default : null);

    /// The Keychain first on a Mac, where Claude Code keeps the login; the
    /// credentials file when it holds none (Claude Code run with another config).
    private static ClaudeCreds? ReadClaudeCreds() => ReadClaudeCreds(out _);

    /// `state` says why nothing came back: the store holds no login (signed
    /// out), or it could not be read just now. On a Mac the Keychain's answer
    /// counts only when `security` really said there was no item.
    private static ClaudeCreds? ReadClaudeCreds(out LoginState state)
    {
        var keychainFailed = false;
        if (Keychain is { } keychain)
        {
            if (keychain.Read() is { } item)
            {
                state = LoginState.Present;
                return new ClaudeCreds(item.AccessToken!, item.RefreshToken!, item.SubscriptionType, item.Service);
            }
            keychainFailed = keychain.LastReadFailed;
        }
        var creds = ReadClaudeCredsFile(out state);
        if (creds is null && keychainFailed) state = LoginState.Unreadable;
        return creds;
    }

    private enum LoginState
    {
        Present,
        Missing,
        /// The file parses but holds no Claude login: signed out.
        NoLogin,
        /// Locked, half-written or otherwise unreadable right now.
        Unreadable,
    }

    private static ClaudeCreds? ReadClaudeCredsFile() => ReadClaudeCredsFile(out _);

    private static ClaudeCreds? ReadClaudeCredsFile(out LoginState state)
    {
        try
        {
            var path = CredentialsFile;
            if (!File.Exists(path))
            {
                state = LoginState.Missing;
                return null;
            }
            using var stream = SharedFile.Open(path);
            using var doc = JsonDocument.Parse(stream);
            if (Jsonl.GetObject(doc.RootElement, "claudeAiOauth") is not { } oauth)
            {
                state = LoginState.NoLogin;
                return null;
            }
            var access = Jsonl.GetString(oauth, "accessToken");
            var refresh = Jsonl.GetString(oauth, "refreshToken");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            {
                state = LoginState.NoLogin;
                return null;
            }
            state = LoginState.Present;
            return new ClaudeCreds(access!, refresh!, Jsonl.GetString(oauth, "subscriptionType"));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            state = LoginState.Missing;
            return null;
        }
        catch
        {
            state = LoginState.Unreadable;
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

    /// A rotation the server accepted but the store would not take. The old
    /// token is spent, so the new pair is the only live one: it is used from
    /// memory and written again while the store still holds `Replaced`. Lost
    /// when the process exits, unless FlushPendingWriteBack gets one more try.
    private sealed record PendingWriteBack(RefreshedTokens Tokens, string Replaced, ClaudeCreds Creds)
    {
        public override string ToString() => "PendingWriteBack { tokens redacted }";
    }

    private static readonly object PendingGate = new();
    // Held across "read what the file holds, write, record the result", so two
    // refreshes (a stuck poll's successor, an account switch, a re-auth) cannot
    // interleave their write-backs.
    private static readonly object WriteGate = new();
    private static PendingWriteBack? Pending;
    private static bool PendingExhausted;

    private static void HoldPending(PendingWriteBack pending)
    {
        lock (PendingGate)
        {
            Pending = pending;
            PendingExhausted = false;
        }
        CredentialRefreshLog.Note("claude", "write-back failed, holding rotated tokens in memory");
        _ = CredentialWriteBacks.Retry("claude", "claude", RetryPending, () =>
        {
            lock (PendingGate) PendingExhausted = ReferenceEquals(Pending, pending);
        });
    }

    private static bool RetryPending()
    {
        lock (WriteGate) ApplyPending(ReadClaudeCreds(), tryWrite: true);
        return !HasPendingWriteBack;
    }

    /// One last try for a pair still held, for a normal exit.
    internal static void FlushPendingWriteBack()
    {
        if (HasPendingWriteBack) RetryPending();
    }

    private static string? PendingReplaced()
    {
        lock (PendingGate) return Pending?.Replaced;
    }

    private static void DropPending(string reason)
    {
        bool had;
        lock (PendingGate)
        {
            had = Pending is not null;
            Pending = null;
            PendingExhausted = false;
        }
        if (had) CredentialRefreshLog.Note("claude", reason);
    }

    /// The token that was sent is spent either way. While a write-back is
    /// pending the file still holds an older token than the one just sent, so
    /// that is the one it must still hold.
    private static WriteBack PersistRotation(RefreshedTokens refreshed, ClaudeCreds current, RefreshGrants.Grant grant)
    {
        lock (WriteGate)
        {
            var onDisk = PendingReplaced() ?? current.RefreshToken;
            var write = WriteClaudeCreds(refreshed, current, onDisk);
            Grants.Settled(grant);
            switch (write)
            {
                // Anthropic rotates the refresh token, invalidating the pair we
                // just used. The rotated pair is all that is left of this login,
                // so it is kept in memory and written again.
                case WriteBack.Failed:
                    HoldPending(new PendingWriteBack(refreshed, onDisk,
                        new ClaudeCreds(refreshed.AccessToken, refreshed.RefreshToken, current.SubscriptionType, current.KeychainService)));
                    break;
                case WriteBack.Written:
                    DropPending("pending write-back saved");
                    break;
                case WriteBack.Superseded:
                    DropPending(Keychain is null && !File.Exists(CredentialsFile)
                        ? "pending write-back dropped, file removed" : "pending write-back dropped, file changed");
                    break;
            }
            return write;
        }
    }

    /// What the store says, adjusted for a pending write-back. The file wins
    /// when it has moved on (Claude Code rotated, or the user signed in again)
    /// or holds no login (signed out: never recreated); while it still holds
    /// the token that was replaced, the pair in memory stands in for it.
    /// `disk` is null both for a signed-out store and for one that could not
    /// be read just now; only the first drops the pending pair.
    private static ClaudeCreds? ApplyPending(ClaudeCreds? disk, bool tryWrite)
    {
        // Put-backs and writes alike go through the gate, so a read that finds the
        // file moved aside cannot interleave with a write in progress.
        lock (WriteGate) return ApplyPendingCore(disk, tryWrite);
    }

    private static ClaudeCreds? ApplyPendingCore(ClaudeCreds? disk, bool tryWrite)
    {
        PendingWriteBack? held;
        lock (PendingGate) held = Pending;
        if (held is null) return disk;
        if (disk is null)
        {
            // Nothing came back: look once more before deciding what that meant.
            // A store that cannot be read right now is a wait, not a sign-out.
            BeforePendingReread?.Invoke();
            disk = ReadClaudeCreds(out var state);
            // A swap that failed half way leaves the file moved aside, not signed out.
            if (disk is null && state == LoginState.Missing)
            {
                switch (AtomicReplace.RestoreInterrupted(CredentialsFile,
                    root => TokenOf(root) == held.Tokens.RefreshToken, root => TokenOf(root) == held.Replaced))
                {
                    case AtomicReplace.Restoration.Restored:
                        disk = ReadClaudeCreds(out state);
                        break;
                    case AtomicReplace.Restoration.Unsure:
                        return held.Creds;
                }
            }
            if (disk is null)
            {
                if (state == LoginState.Unreadable) return held.Creds;
                DropPending("pending write-back dropped, file removed");
                return null;
            }
        }
        // The file is there: what an earlier cut-short swap left is of no use any more.
        AtomicReplace.DiscardIfPresent(CredentialsFile);
        if (!string.Equals(disk.RefreshToken, held.Replaced, StringComparison.Ordinal))
        {
            DropPending("pending write-back dropped, file changed");
            return disk;
        }
        if (!tryWrite) return held.Creds;
        switch (WriteClaudeCreds(held.Tokens, disk, held.Replaced))
        {
            case WriteBack.Written:
                DropPending("pending write-back saved");
                return ReadClaudeCreds() ?? held.Creds;
            case WriteBack.Superseded:
                DropPending("pending write-back dropped, file changed");
                return ReadClaudeCreds();
            default:
                return held.Creds;
        }
    }

    private static string? TokenOf(JsonObject root) =>
        root["claudeAiOauth"] is JsonObject oauth && oauth["refreshToken"] is JsonValue value && value.TryGetValue<string>(out var token) ? token : null;

    /// Test seam: runs just before a store that read as empty is read again.
    internal static Action? BeforePendingReread;

    internal static bool HasPendingWriteBack
    {
        get { lock (PendingGate) return Pending is not null; }
    }

    internal static void ResetStateForTests()
    {
        CredentialWriteBacks.ResetForTests();
        AtomicReplace.ResetForTests();
        lock (PendingGate)
        {
            Pending = null;
            PendingExhausted = false;
        }
        lock (DeadGate) Dead = null;
        Grants.Clear();
    }

    /// Updates the claudeAiOauth block in place, preserving unrelated fields
    /// (scopes, subscriptionType, rateLimitTier) and any other top-level
    /// keys in the file. A missing login supersedes the refresh.
    /// `expectedOnDisk` is the refresh token the store must still hold for the
    /// write to land: the one just sent, unless an earlier write-back is
    /// pending and the store holds the token that one replaced.
    private static WriteBack WriteClaudeCreds(RefreshedTokens refreshed, ClaudeCreds from, string expectedOnDisk)
    {
        if (from.KeychainService is { } service && Keychain is { } keychain)
        {
            return keychain.WriteBack(service, refreshed.AccessToken, refreshed.RefreshToken, refreshed.ExpiresAtMs, expectedOnDisk) switch
            {
                ClaudeKeychain.WriteResult.Written => WriteBack.Written,
                ClaudeKeychain.WriteResult.Superseded or ClaudeKeychain.WriteResult.Gone => WriteBack.Superseded,
                _ => WriteBack.Failed,
            };
        }
        var sentRefreshToken = expectedOnDisk;
        try
        {
            var path = CredentialsFile;
            // Per-write temp name, not a fixed ".tmp": Windows denies a second
            // opener while the first holds the handle, so two Agent Island
            // instances refreshing at once would fail each other's write.
            // The replace is retried (Claude Code / Claude Desktop may hold a
            // brief lock on the file, and dropping the rotated token would sign
            // the user out); the file is read again before each try, so a login
            // made during the wait is never overwritten.
            var plan = new AtomicReplace.Plan(
                root => root["claudeAiOauth"] is JsonObject oauth && oauth["refreshToken"] is JsonValue value
                    && value.TryGetValue<string>(out var held) && string.Equals(held, sentRefreshToken, StringComparison.Ordinal),
                root =>
                {
                    var oauth = (JsonObject)root["claudeAiOauth"]!;
                    oauth["accessToken"] = refreshed.AccessToken;
                    oauth["refreshToken"] = refreshed.RefreshToken;
                    oauth["expiresAt"] = refreshed.ExpiresAtMs;
                },
                new JsonSerializerOptions { WriteIndented = false });
            return AtomicReplace.Run(path, plan).Result switch
            {
                AtomicReplace.Result.Written => WriteBack.Written,
                AtomicReplace.Result.Failed => WriteBack.Failed,
                _ => WriteBack.Superseded,
            };
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"AgentIsland: failed to write rotated Claude tokens: {error.GetType().Name}");
            return WriteBack.Failed;
        }
    }

    /// Where Claude's login is kept and when its access token runs out: a
    /// description for a diagnostic, never a token. Source is "keychain",
    /// "file" or "none".
    public sealed record LoginStatus(string Source, string? Plan, double? MinutesLeft);

    public static LoginStatus Describe()
    {
        double? MinutesLeft(double expiresAtMs) =>
            expiresAtMs > 0 ? Math.Round((DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMs) - DateTimeOffset.UtcNow).TotalMinutes) : null;
        if (Keychain is { } keychain && keychain.Read() is { } item) return new LoginStatus("keychain", item.SubscriptionType, MinutesLeft(item.ExpiresAt));
        try
        {
            if (!File.Exists(CredentialsFile)) return new LoginStatus("none", null, null);
            using var stream = SharedFile.Open(CredentialsFile);
            using var doc = JsonDocument.Parse(stream);
            if (Jsonl.GetObject(doc.RootElement, "claudeAiOauth") is not { } oauth) return new LoginStatus("none", null, null);
            return new LoginStatus("file", Jsonl.GetString(oauth, "subscriptionType"), MinutesLeft(Jsonl.GetDouble(oauth, "expiresAt") ?? 0));
        }
        catch
        {
            return new LoginStatus("none", null, null);
        }
    }

    /// Cheap local change signal used by the re-auth poll — file mtime here,
    /// keychain `mdat` on macOS.
    public static string? CredentialsModificationStamp()
    {
        if (Keychain is { } keychain && keychain.ModificationStamp() is { } stamp) return stamp;
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

    internal sealed record RefreshedTokens(string AccessToken, string RefreshToken, long ExpiresAtMs)
    {
        public override string ToString() => "RefreshedTokens { tokens redacted }";
    }

    private static readonly RefreshGrants Grants = new("claude");

    internal abstract record RefreshResult
    {
        internal sealed record Refreshed(RefreshedTokens Tokens) : RefreshResult;
        /// The token endpoint refused the grant: the refresh token is spent
        /// or revoked, and only a new sign-in helps.
        internal sealed record Rejected : RefreshResult;
        /// The network, a rate limit or a server error stood in the way.
        /// `Reason` is a fixed phrase for the diagnostic log, never exception text.
        internal sealed record Unavailable(string Message, bool Uncertain = false, string Reason = "error") : RefreshResult;
    }

    /// Test seam: the client a refresh grant goes through.
    internal static HttpClient OAuth = Http.OAuthClient;

    internal static async Task<RefreshResult> RefreshClaudeToken(string refreshToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = OauthClientId,
            }, CoreJson.Default.StringMap);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            // The client's own timeout is only a backstop for a request that has to connect first.
            using var deadline = new CancellationTokenSource(Http.DeadlineAfterConnect(Http.GrantMargin));
            using var response = await OAuth.SendAsync(request, deadline.Token);
            return RefreshResultFor((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(deadline.Token), DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return new RefreshResult.Unavailable(L10n.Tr("network drop"), !RefreshGrants.BeforeSend(error), RefreshGrants.ReasonFor(error));
        }
        catch (Exception error)
        {
            return new RefreshResult.Unavailable(L10n.Tr("Couldn't refresh the sign-in"), Uncertain: true, RefreshGrants.ReasonFor(error));
        }
    }

    /// 400 is invalid_grant, 401 and 403 a refused client or account: those
    /// are the sign-in. Anything else — 429, a 5xx, a body that is not the
    /// token reply (a captive portal) — is not, so it never asks the user to
    /// sign in again.
    internal static RefreshResult RefreshResultFor(int status, byte[] body, DateTimeOffset now)
    {
        if (status is 400 or 401 or 403) return new RefreshResult.Rejected();
        if (status != 200) return new RefreshResult.Unavailable($"HTTP {status}", Uncertain: status >= 500, Reason: $"HTTP {status}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var access = Jsonl.GetString(doc.RootElement, "access_token");
            var refresh = Jsonl.GetString(doc.RootElement, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return new RefreshResult.Unavailable("bad response", Uncertain: true, Reason: "bad response");
            var expiresIn = Jsonl.GetDouble(doc.RootElement, "expires_in") ?? 28_800;
            var expiresAt = now.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
            return new RefreshResult.Refreshed(new RefreshedTokens(access!, refresh!, expiresAt));
        }
        catch (JsonException)
        {
            return new RefreshResult.Unavailable("bad response", Uncertain: true, Reason: "bad response");
        }
    }
}
