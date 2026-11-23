namespace AgentIsland.Usage;

/// Prefer the running app's quota and identity. Newer Windows desktop
/// versions keep their RPC private; agy's built-in /usage command can
/// report the signed-in CLI account's quota independently.
public static class AntigravityUsageFetcher
{
    public abstract record Outcome
    {
        /// Buckets plus identity. Email/tier ride along so the store never
        /// needs a second call.
        public sealed record Success(
            AntigravityQuotaSnapshot Snapshot, string? Email, bool FromCli = false) : Outcome;

        /// No running server or compatible CLI is available.
        public sealed record NotRunning : Outcome;

        public sealed record NotInstalled : Outcome;

        /// Skipped while offline or within wake grace so the CLI never falls
        /// back to an interactive browser sign-in. Keep cached snapshot as-is.
        public sealed record Deferred : Outcome;

        public sealed record Failed(string Message) : Outcome;
    }

    public static async Task<Outcome> Fetch()
    {
        if (!AntigravityCredentials.Detected) return new Outcome.NotInstalled();

        var port = await AntigravityLanguageServer.Discover().ConfigureAwait(false);
        if (port is not { } livePort) return AntigravityCliQuota.TryFetch() ?? new Outcome.NotRunning();

        var summary = await CallWithCsrfRetry(
            "RetrieveUserQuotaSummary", livePort, "{}").ConfigureAwait(false);
        if (summary is null)
        {
            AntigravityLanguageServer.ForgetCachedPort();
            return new Outcome.Failed("quota call failed");
        }
        if (summary.Status != 200)
        {
            AntigravityLanguageServer.ForgetCachedPort();
            if (summary.Status == 401 && AntigravityCliQuota.TryFetch() is { } cli) return cli;
            return new Outcome.Failed($"quota HTTP {summary.Status}");
        }
        if (AntigravityQuotaParser.ParseQuotaSummary(summary.Body) is not { } parsed)
        {
            return new Outcome.Failed("quota reply unreadable");
        }

        // Identity is garnish — a failed status call must not sink the
        // buckets that already arrived.
        AntigravityQuotaParser.UserProfile? profile = null;
        if (await CallWithCsrfRetry("GetUserStatus", livePort, "{}").ConfigureAwait(false)
            is { Status: 200 } status)
        {
            profile = AntigravityQuotaParser.ParseUserStatus(status.Body);
        }

        return new Outcome.Success(
            new AntigravityQuotaSnapshot(
                parsed.Buckets,
                profile?.TierId,
                profile?.TierLabel,
                parsed.Note),
            profile?.Email);
    }

    /// The CLI's server wants no CSRF token; the desktop IDE gates on one it
    /// passes on its own command line. The token is lifted only after a 401
    /// says the plain call was refused — a strictly additive retry on a path
    /// that already failed — and only from the process that owns the port:
    /// no other process's token is ever sent to it.
    internal static async Task<AntigravityLanguageServer.Reply?> CallWithCsrfRetry(
        string method, int port, string body)
    {
        var reply = await AntigravityLanguageServer
            .CallRpc(method, port, body, null, 6).ConfigureAwait(false);
        if (reply is not { Status: 401 }) return reply;
        if (AntigravityLanguageServer.ResolvePortOwner(port) is not { } pid
            || AntigravityLanguageServer.CsrfToken(pid) is not { Length: > 0 } token)
        {
            return reply;
        }
        return await AntigravityLanguageServer
            .CallRpc(method, port, body, token, 6).ConfigureAwait(false);
    }
}
