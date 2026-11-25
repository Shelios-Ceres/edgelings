using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// Fetches the Grok weekly credit pool + monthly budget from the CLI's own
/// proxy (`cli-chat-proxy.grok.com/v1/billing`), authenticating with the
/// %USERPROFILE%\.grok\auth.json access token and refreshing it through
/// auth.x.ai when it is expired or rejected. Display-only — no transcript
/// monitoring, no alarms live here.
public static class GrokUsageFetcher
{
    public abstract record Outcome
    {
        public sealed record Success(GrokBillingSnapshot Snapshot) : Outcome;
        /// Refresh path exhausted — the user has to run `grok login`.
        public sealed record ReauthRequired : Outcome;
        public sealed record Failed(string Message) : Outcome;
        /// No auth.json — the Grok CLI never logged in on this machine.
        public sealed record NotInstalled : Outcome;
    }

    private const string BillingBase = "https://cli-chat-proxy.grok.com/v1/billing";
    private const string ClientVersion = "0.2.114";

    /// The CLI stamps its own platform into the agent string; a Windows build
    /// claiming macOS would be the odd request in xAI's logs, not the safe one.
    private const string UserAgent = "grok-pager/0.2.114 grok-shell/0.2.114 (windows; x86_64)";

    private static readonly RefreshGrants Grants = new("grok");
    private static readonly SemaphoreSlim FetchGate = new(1);

    public static Task<Outcome> Fetch(CancellationToken ct = default) =>
        Fetch(GrokAuthFile.DefaultPath, Http.Client, Http.OAuthClient, ct);

    private const string SaveFailedMessage = "Couldn't save the refreshed sign-in; Grok may need to sign in again";
    private const string GaveUpMessage = ClaudeCredentials.GaveUpMessage;

    internal static async Task<Outcome> Fetch(string authPath, HttpClient billing, HttpClient oauth, CancellationToken ct)
    {
        // A new show may follow a hide while an earlier grant is still being persisted.
        // Read the login only after its predecessor has completed.
        await FetchGate.WaitAsync(ct);
        try
        {
            var outcome = await FetchCore(authPath, billing, oauth, ct);
            // The quota came from a pair that could not be saved for good: say so,
            // while the grok CLI may be about to refresh with the token the file holds.
            return outcome is Outcome.Success && IsExhausted(authPath) ? new Outcome.Failed(L10n.Tr(SaveFailedMessage)) : outcome;
        }
        finally { FetchGate.Release(); }
    }

    private static async Task<Outcome> FetchCore(string authPath, HttpClient billing, HttpClient oauth, CancellationToken ct)
    {
        var onFile = GrokAuthFile.Inspect(authPath, out var fileState);
        if (ApplyPending(authPath, onFile, fileState, tryWrite: true) is not { } entry) return new Outcome.NotInstalled();

        // Proactive refresh near expiry (the access token lives ~6h). A failed
        // refresh still tries the billing call with the old token — clock skew
        // can make a perfectly good token look expired.
        var attempted = GrokAuthFile.NeedsRefresh(entry) && entry.RefreshToken is not null;
        var failure = RefreshState.Rejected;
        if (attempted)
        {
            var proactive = await Refresh(entry, authPath, oauth, ct);
            if (proactive.Entry is { } renewed) entry = renewed;
            failure = proactive.State;
        }

        switch (await FetchBilling(entry.AccessToken, billing, ct))
        {
            case BillingResult.Success success:
                return new Outcome.Success(success.Snapshot);
            case BillingResult.Failed failed:
                return new Outcome.Failed(failed.Message);
            case BillingResult.Unauthorized:
            {
                if (!attempted && entry.RefreshToken is not null)
                {
                    var reactive = await Refresh(entry, authPath, oauth, ct);
                    if (reactive.Entry is { } refreshed)
                    {
                        return await FetchBilling(refreshed.AccessToken, billing, ct) switch
                        {
                            BillingResult.Success retried => new Outcome.Success(retried.Snapshot),
                            BillingResult.Failed retryFailed => new Outcome.Failed(retryFailed.Message),
                            _ => new Outcome.ReauthRequired(),
                        };
                    }
                    failure = reactive.State;
                }
                // Only a refused grant (or none to send) says the sign-in is gone:
                // a held-back refresh, a timeout or a rate limit does not.
                return failure switch
                {
                    RefreshState.Waiting => new Outcome.Failed(L10n.Tr("Waiting for updated sign-in")),
                    RefreshState.GaveUp => new Outcome.Failed(L10n.Tr(GaveUpMessage)),
                    RefreshState.Transient => new Outcome.Failed(L10n.Tr("network drop")),
                    _ => new Outcome.ReauthRequired(),
                };
            }
            default:
                return new Outcome.Failed("parse error");
        }
    }

    // MARK: - Pending write-back

    /// A rotation the server accepted but auth.json would not take. The old
    /// refresh token is spent, so the new pair is used from memory and written
    /// again while the file still holds `Replaced`. Lost when the process exits,
    /// unless FlushPendingWriteBacks gets one more try.
    private sealed record PendingWrite(GrokAuthFile.TokenReply Reply, DateTimeOffset ReceivedAt, string Scope, string Replaced, GrokAuthEntry Entry)
    {
        public override string ToString() => "PendingWrite { tokens redacted }";
    }

    private static readonly object PendingGate = new();
    // Held across "read what the file holds, write, record the result" so the
    // quota poll and the retry loop cannot interleave their write-backs.
    private static readonly object WriteGate = new();
    private static readonly Dictionary<string, PendingWrite> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Exhausted = new(StringComparer.OrdinalIgnoreCase);

    private static PendingWrite? PendingFor(string authPath)
    {
        lock (PendingGate) return Pending.GetValueOrDefault(authPath);
    }

    private static bool IsExhausted(string authPath)
    {
        lock (PendingGate) return Pending.ContainsKey(authPath) && Exhausted.Contains(authPath);
    }

    private static void DropPending(string authPath, string reason)
    {
        bool had;
        lock (PendingGate)
        {
            had = Pending.Remove(authPath);
            Exhausted.Remove(authPath);
        }
        if (had) CredentialRefreshLog.Note("grok", reason);
    }

    private static void HoldPending(string authPath, PendingWrite pending)
    {
        lock (PendingGate)
        {
            Pending[authPath] = pending;
            Exhausted.Remove(authPath);
        }
        CredentialRefreshLog.Note("grok", "write-back failed, holding rotated tokens in memory");
        _ = CredentialWriteBacks.Retry("grok", "grok:" + authPath, () => RetryPath(authPath), () =>
        {
            lock (PendingGate)
            {
                if (Pending.TryGetValue(authPath, out var now) && ReferenceEquals(now, pending)) Exhausted.Add(authPath);
            }
        });
    }

    private static bool RetryPath(string path)
    {
        lock (WriteGate)
        {
            var onFile = GrokAuthFile.Inspect(path, out var state);
            ApplyPending(path, onFile, state, tryWrite: true);
        }
        return !HasPendingWriteBack(path);
    }

    /// One last try for pairs still held, for a normal exit.
    internal static void FlushPendingWriteBacks()
    {
        string[] paths;
        lock (PendingGate) paths = Pending.Keys.ToArray();
        foreach (var path in paths) RetryPath(path);
    }

    /// What the file says, adjusted for a pending write-back: the file wins
    /// when it has moved on or holds no login (signed out: never recreated);
    /// while it still holds the token that was replaced, the entry in memory
    /// stands in for it. A file that cannot be read just now changes nothing.
    private static GrokAuthEntry? ApplyPending(string authPath, GrokAuthEntry? file, GrokAuthFile.LoginState state, bool tryWrite)
    {
        // Put-backs and writes alike go through the gate, so a read that finds the
        // file moved aside cannot interleave with a write in progress.
        lock (WriteGate) return ApplyPendingCore(authPath, file, state, tryWrite);
    }

    private static GrokAuthEntry? ApplyPendingCore(string authPath, GrokAuthEntry? file, GrokAuthFile.LoginState state, bool tryWrite)
    {
        if (PendingFor(authPath) is not { } held) return file;
        // A swap that failed half way leaves the file moved aside, not signed out.
        if (file is null && state == GrokAuthFile.LoginState.Missing)
        {
            switch (AtomicReplace.RestoreInterrupted(authPath,
                root => root[held.Scope] is System.Text.Json.Nodes.JsonObject now && (held.Reply.RefreshToken is { } rotated
                    ? (string?)now["refresh_token"] == rotated : (string?)now["key"] == held.Reply.AccessToken),
                root => root[held.Scope] is System.Text.Json.Nodes.JsonObject then && (string?)then["refresh_token"] == held.Replaced))
            {
                case AtomicReplace.Restoration.Restored:
                    file = GrokAuthFile.Inspect(authPath, out state);
                    break;
                case AtomicReplace.Restoration.Unsure:
                    return held.Entry;
            }
        }
        // The file is there: what an earlier cut-short swap left is of no use any more.
        if (file is not null) AtomicReplace.DiscardIfPresent(authPath);
        if (file is null)
        {
            if (state == GrokAuthFile.LoginState.Unreadable) return held.Entry;
            DropPending(authPath, "pending write-back dropped, file removed");
            return null;
        }
        if (file.Scope != held.Scope || !string.Equals(file.RefreshToken, held.Replaced, StringComparison.Ordinal))
        {
            DropPending(authPath, "pending write-back dropped, file changed");
            return file;
        }
        if (!tryWrite) return held.Entry;
        var applied = GrokAuthFile.ApplyRefresh(held.Reply, authPath, held.Scope, held.Replaced, held.ReceivedAt);
        switch (applied.Status)
        {
            case GrokAuthFile.ApplyStatus.Written:
                DropPending(authPath, "pending write-back saved");
                return applied.Entry ?? held.Entry;
            case GrokAuthFile.ApplyStatus.Superseded:
                DropPending(authPath, "pending write-back dropped, file changed");
                return applied.Entry;
            case GrokAuthFile.ApplyStatus.LoggedOut:
                DropPending(authPath, "pending write-back dropped, file removed");
                return null;
            default:
                return held.Entry;
        }
    }

    internal static bool HasPendingWriteBack(string authPath) => PendingFor(authPath) is not null;

    internal static void ResetStateForTests()
    {
        CredentialWriteBacks.ResetForTests();
        AtomicReplace.ResetForTests();
        lock (PendingGate)
        {
            Pending.Clear();
            Exhausted.Clear();
        }
        Grants.Clear();
    }

    // MARK: - Billing

    private abstract record BillingResult
    {
        public sealed record Success(GrokBillingSnapshot Snapshot) : BillingResult;
        public sealed record Unauthorized : BillingResult;
        public sealed record Failed(string Message) : BillingResult;
    }

    private static async Task<BillingResult> FetchBilling(string token, HttpClient billing, CancellationToken ct)
    {
        // Both calls go out together; neither Get throws, so awaiting them in
        // sequence can't strand the other request.
        var weeklyCall = Get(BillingBase + "?format=credits", token, billing, ct);
        var monthlyCall = Get(BillingBase, token, billing, ct);
        var weekly = await weeklyCall;
        var monthly = await monthlyCall;

        if (weekly is HttpOutcome.Transport transport) return new BillingResult.Failed(transport.Message);
        if (weekly is not HttpOutcome.Response response) return new BillingResult.Failed("parse error");
        if (response.Status is 401 or 403) return new BillingResult.Unauthorized();
        if (response.Status != 200) return new BillingResult.Failed($"http {response.Status}");
        if (GrokBillingParser.ParseWeekly(response.Body) is not { } pool)
        {
            return new BillingResult.Failed("parse error");
        }

        // Monthly is best-effort garnish — a failure only costs the dollar
        // caption, never the weekly bar.
        var budget = monthly is HttpOutcome.Response { Status: 200 } monthlyResponse
            ? GrokBillingParser.ParseMonthly(monthlyResponse.Body)
            : null;

        return new BillingResult.Success(new GrokBillingSnapshot(
            pool.UsedPercent,
            pool.PeriodEnd,
            budget?.UsedCents,
            budget?.LimitCents,
            budget?.PeriodEnd,
            pool.Products.Count == 0 ? null : pool.Products));
    }

    private abstract record HttpOutcome
    {
        public sealed record Response(int Status, byte[] Body) : HttpOutcome;
        public sealed record Transport(string Message) : HttpOutcome;
    }

    private static async Task<HttpOutcome> Get(string url, string token, HttpClient billing, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
            request.Headers.TryAddWithoutValidation("x-grok-client-version", ClientVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            // Never let a wedged tunnel hang the poll loop.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            using var response = await billing.SendAsync(request, deadline.Token);
            return new HttpOutcome.Response(
                (int)response.StatusCode,
                await response.Content.ReadAsByteArrayAsync(deadline.Token));
        }
        catch (Exception error)
        {
            return new HttpOutcome.Transport(
                error is HttpRequestException or OperationCanceledException
                    ? L10n.Tr("network drop")
                    : error.Message);
        }
    }

    // MARK: - Token refresh

    private enum RefreshState
    {
        Refreshed,
        /// No grant to send, or the endpoint refused it: the sign-in is gone.
        Rejected,
        /// A timeout, a lost reply, a rate limit or a server error.
        Transient,
        /// Held back because an earlier attempt on the same token is unresolved.
        Waiting,
        /// The earlier attempts' retries are used up.
        GaveUp,
    }

    private readonly record struct RefreshOutcome(RefreshState State, GrokAuthEntry? Entry = null);

    /// Exchange the refresh token at the issuer's token endpoint. The response
    /// rotates the refresh token, so it is written back to auth.json, or the
    /// user's own `grok` CLI login would be stranded the next time it refreshes.
    /// If the write fails the rotated pair is kept in memory and written again
    /// (see ApplyPending). If the login in the file has changed since the
    /// request left, nothing is written and the current entry comes back instead.
    private static async Task<RefreshOutcome> Refresh(
        GrokAuthEntry entry, string authPath, HttpClient oauth, CancellationToken ct)
    {
        if (entry.RefreshToken is not { } refreshToken) return new(RefreshState.Rejected);
        if (entry.OidcClientId is not { } clientId) return new(RefreshState.Rejected);
        RefreshGrants.Grant? grant = null;
        try
        {
            var endpoint = await TokenEndpoint(entry.OidcIssuer, oauth, ct);
            if (ct.IsCancellationRequested) return new(RefreshState.Transient);
            if (endpoint is null) return new(RefreshState.Rejected);

            // The discovery request above can take a while. If the file has
            // moved on since it was read (the grok CLI refreshed, or the user
            // signed in again), the token in hand is stale: use the file's login.
            var expectedOnDisk = PendingFor(authPath)?.Replaced ?? refreshToken;
            var latest = GrokAuthFile.Inspect(authPath, out var latestState);
            if (latest is null && latestState != GrokAuthFile.LoginState.Unreadable) return new(RefreshState.Rejected);
            if (latest is not null && (latest.Scope != entry.Scope
                || !string.Equals(latest.RefreshToken, expectedOnDisk, StringComparison.Ordinal)))
            {
                return new(RefreshState.Refreshed, latest);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Content = new StringContent(
                FormEncoded(
                    ("grant_type", "refresh_token"),
                    ("refresh_token", refreshToken),
                    ("client_id", clientId)),
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            // Once sent, finish persisting a successful rotation even if the card is hidden.
            using var deadline = new CancellationTokenSource(Http.DeadlineAfterConnect(Http.GrantMargin));
            grant = Grants.Begin(refreshToken);
            if (grant is null) return new(Grants.GaveUp(refreshToken) ? RefreshState.GaveUp : RefreshState.Waiting);
            using var response = await oauth.SendAsync(request, deadline.Token);
            var status = (int)response.StatusCode;
            if (status != 200)
            {
                if (status < 500) Grants.Settled(grant);
                else Grants.Ambiguous(grant, $"HTTP {status}");
                return new(status is 400 or 401 or 403 ? RefreshState.Rejected : RefreshState.Transient);
            }
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            var now = DateTimeOffset.Now;
            if (GrokAuthFile.ParseReply(body) is not { } reply)
            {
                Grants.Ambiguous(grant, "bad response");
                return new(RefreshState.Transient);
            }
            CredentialRefreshLog.Recovered("grok");
            lock (WriteGate)
            {
                // While a write-back is pending the file holds an older token
                // than the one just sent.
                var held = PendingFor(authPath);
                var applied = GrokAuthFile.ApplyRefresh(reply, authPath, entry.Scope, held?.Replaced ?? refreshToken, now);
                Grants.Settled(grant);
                switch (applied.Status)
                {
                    case GrokAuthFile.ApplyStatus.WriteFailed:
                        var kept = GrokAuthFile.EntryFromReply(entry, reply, now);
                        HoldPending(authPath, new PendingWrite(reply, now, entry.Scope, held?.Replaced ?? refreshToken, kept));
                        return new(RefreshState.Refreshed, kept);
                    case GrokAuthFile.ApplyStatus.Written:
                        DropPending(authPath, "pending write-back saved");
                        return new(RefreshState.Refreshed, applied.Entry);
                    case GrokAuthFile.ApplyStatus.Superseded:
                        DropPending(authPath, "pending write-back dropped, file changed");
                        return applied.Entry is null ? new(RefreshState.Rejected) : new(RefreshState.Refreshed, applied.Entry);
                    default:
                        DropPending(authPath, "pending write-back dropped, file removed");
                        return new(RefreshState.Rejected);
                }
            }
        }
        catch (Exception error)
        {
            if (grant is not null)
            {
                if (RefreshGrants.BeforeSend(error)) Grants.Settled(grant);
                else Grants.Ambiguous(grant, RefreshGrants.ReasonFor(error));
            }
            return new(RefreshState.Transient);
        }
    }

    /// OIDC discovery for the token endpoint, with the conventional
    /// `/oauth2/token` path as the offline fallback.
    private static async Task<Uri?> TokenEndpoint(string issuer, HttpClient oauth, CancellationToken ct)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var origin) || !TrustedAuthUri(origin)
            || origin.Query.Length != 0 || origin.Fragment.Length != 0) return null;
        var prefix = origin.AbsoluteUri.TrimEnd('/') + "/";
        var fallback = new Uri(prefix + "oauth2/token");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Http.DeadlineAfterConnect(Http.DiscoveryMargin));
            using var response = await oauth.GetAsync(prefix + ".well-known/openid-configuration", deadline.Token);
            if ((int)response.StatusCode != 200) return fallback;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            if (Jsonl.GetString(doc.RootElement, "issuer") is { } advertised
                && !string.Equals(advertised.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.Ordinal)) return null;
            var endpoint = Jsonl.GetString(doc.RootElement, "token_endpoint");
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && TrustedAuthUri(uri)
                && uri.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) ? uri : null;
        }
        catch (HttpRequestException) { return fallback; }
        catch (OperationCanceledException) { return ct.IsCancellationRequested ? null : fallback; }
    }

    private static bool TrustedAuthUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0 && uri.IdnHost.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase);

    /// Uri.EscapeDataString leaves exactly the RFC 3986 unreserved set alone,
    /// which is the encoding the CLI's own form encoder produces.
    private static string FormEncoded(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
}
