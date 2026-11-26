using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// Fetches the Grok weekly credit pool + monthly budget from the CLI's own
/// proxy (`cli-chat-proxy.grok.com/v1/billing`), authenticating with the
/// %USERPROFILE%\.grok\auth.json access token. Read-only: it never refreshes
/// the login or writes the file, because the refresh token rotates on every
/// exchange and belongs to the grok CLI. Display-only — no transcript
/// monitoring, no alarms live here.
public static class GrokUsageFetcher
{
    public abstract record Outcome
    {
        public sealed record Success(GrokBillingSnapshot Snapshot) : Outcome;
        /// The access token is expired or was refused: nothing is sent until the
        /// grok CLI has written auth.json again. `Stamp` is how the file looked
        /// when it was read, for whoever waits for it to change.
        public sealed record SyncRequired(string? Stamp) : Outcome;
        public sealed record Failed(string Message) : Outcome;
        /// No auth.json — the Grok CLI never logged in on this machine.
        public sealed record NotInstalled : Outcome;
        /// auth.json is there but holds no login that can be used (signed out, empty,
        /// half written). `Stamp` is how the file looked, so a change is waited for.
        public sealed record NoLogin(string Stamp) : Outcome;
    }

    private const string BillingBase = "https://cli-chat-proxy.grok.com/v1/billing";

    /// Where a refused token was seen: the file's stamp then. While auth.json
    /// still looks the same, the same token is not sent again, by a poll or by
    /// a manual refresh alike. Kept in Preferences (path and stamp, never a
    /// token) so that a restart does not send the refused token once more.
    private const string RefusedKey = "GrokUsageFetcher.refused.v1";
    private static readonly object RefusedGate = new();
    private static Dictionary<string, string>? _refused;

    public static Task<Outcome> Fetch(CancellationToken ct = default) =>
        Fetch(GrokAuthFile.DefaultPath, Http.Client, ct);

    internal static async Task<Outcome> Fetch(string authPath, HttpClient billing, CancellationToken ct)
    {
        var stamp = GrokAuthFile.Stamp(authPath);
        if (stamp is null)
        {
            Forget(authPath);
            return new Outcome.NotInstalled();
        }
        if (IsRefused(authPath, stamp)) return new Outcome.SyncRequired(stamp);

        var entry = GrokAuthFile.Inspect(authPath);
        if (entry is null) return new Outcome.NoLogin(stamp);
        if (GrokAuthFile.IsExpired(entry)) return new Outcome.SyncRequired(stamp);

        var identity = GrokClientIdentity.Current(Path.GetDirectoryName(authPath) ?? "");
        switch (await FetchBilling(entry.AccessToken, identity, billing, ct))
        {
            case BillingResult.Success success:
                return new Outcome.Success(success.Snapshot);
            case BillingResult.Failed failed:
                return new Outcome.Failed(failed.Message);
            case BillingResult.Unauthorized:
                Refuse(authPath, stamp);
                return new Outcome.SyncRequired(stamp);
            default:
                return new Outcome.Failed("parse error");
        }
    }

    /// Under RefusedGate. Loaded on first use, so a restart finds what the last run left.
    private static Dictionary<string, string> Loaded() => _refused ??=
        new Dictionary<string, string>(Preferences.Get<Dictionary<string, string>>(RefusedKey) ?? new(), StringComparer.OrdinalIgnoreCase);

    /// A file that has moved on from the refused stamp lifts the hold for good.
    private static bool IsRefused(string authPath, string stamp)
    {
        lock (RefusedGate)
        {
            var refused = Loaded();
            if (!refused.TryGetValue(authPath, out var seen)) return false;
            if (seen == stamp) return true;
            refused.Remove(authPath);
            Save(refused);
            return false;
        }
    }

    private static void Refuse(string authPath, string stamp)
    {
        lock (RefusedGate)
        {
            var refused = Loaded();
            refused[authPath] = stamp;
            Save(refused);
        }
    }

    private static void Forget(string authPath)
    {
        lock (RefusedGate)
        {
            var refused = Loaded();
            if (refused.Remove(authPath)) Save(refused);
        }
    }

    private static void Save(Dictionary<string, string> refused)
    {
        if (refused.Count == 0) Preferences.Remove(RefusedKey);
        else Preferences.Set(RefusedKey, new Dictionary<string, string>(refused));
    }

    internal static void ResetStateForTests()
    {
        lock (RefusedGate)
        {
            _refused = null;
            Preferences.Remove(RefusedKey);
        }
    }

    /// Test seam: what a restart does, the memory gone and Preferences kept.
    internal static void ForgetMemoryForTests()
    {
        lock (RefusedGate) _refused = null;
    }

    // MARK: - Billing

    private abstract record BillingResult
    {
        public sealed record Success(GrokBillingSnapshot Snapshot) : BillingResult;
        public sealed record Unauthorized : BillingResult;
        public sealed record Failed(string Message) : BillingResult;
    }

    private static async Task<BillingResult> FetchBilling(string token, GrokClientIdentity identity, HttpClient billing, CancellationToken ct)
    {
        // Both calls go out together; neither Get throws, so awaiting them in
        // sequence can't strand the other request.
        var weeklyCall = Get(BillingBase + "?format=credits", token, identity, billing, ct);
        var monthlyCall = Get(BillingBase, token, identity, billing, ct);
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

    private static async Task<HttpOutcome> Get(string url, string token, GrokClientIdentity identity, HttpClient billing, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
            if (identity.Version is { } version) request.Headers.TryAddWithoutValidation("x-grok-client-version", version);
            request.Headers.TryAddWithoutValidation("User-Agent", identity.UserAgent);
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
}
