using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AgentIsland.Usage;

// A transport failure after a POST can hide a successful rotation. Remember the
// token's fingerprint (in memory only) so it is not replayed blindly. Replaying
// an unchanged file is safe in the end: if the server did rotate, the token on
// disk is dead and the replay only earns a clear 400; if it did not, retrying
// is what is wanted. So an ambiguous grant is held for a while, not forever, and
// retried a few times at growing intervals before giving up.
internal sealed class RefreshGrants(string provider)
{
    /// How long an ambiguous grant is held before the first, second and third retry.
    internal static readonly TimeSpan[] AmbiguousHolds = { TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60) };
    // A backstop only: callers settle on every path, and the wall clock keeps
    // running while the machine sleeps and a request's timeout does not.
    internal static readonly TimeSpan InFlightTtl = TimeSpan.FromMinutes(10);

    /// Test seam: the clock.
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    /// What Begin hands out. Settling or marking with a handle that is no longer
    /// the current one (the entry has since been begun again) does nothing.
    internal sealed record Grant(string Id, long Nonce)
    {
        public override string ToString() => "Grant";
    }

    private sealed record Held(bool Ambiguous, DateTimeOffset At, int Failures, long Nonce);

    private readonly object _gate = new();
    private readonly Dictionary<string, Held> _grants = new();
    private long _nonce;

    private static string Fingerprint(string grant)
    {
        var bytes = Encoding.UTF8.GetBytes(grant);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal Grant? Begin(string grant)
    {
        var id = Fingerprint(grant);
        lock (_gate)
        {
            var now = Clock();
            var failures = 0;
            if (_grants.TryGetValue(id, out var held))
            {
                failures = held.Failures;
                if (held.Ambiguous)
                {
                    // After the third retry it goes on at the longest interval, for as long as the token stays.
                    if (now - held.At < AmbiguousHolds[Math.Min(failures, AmbiguousHolds.Length) - 1]) return null;
                    CredentialRefreshLog.Note(provider, failures <= AmbiguousHolds.Length
                        ? $"ambiguous hold over, retry {failures} of {AmbiguousHolds.Length}"
                        : "ambiguous hold over, retrying hourly");
                }
                else
                {
                    if (now - held.At < InFlightTtl) return null;
                    CredentialRefreshLog.Note(provider, "in-flight grant expired, trying again");
                }
            }
            var nonce = ++_nonce;
            _grants[id] = new Held(false, now, failures, nonce);
            return new Grant(id, nonce);
        }
    }

    /// True once the three retries are used up; from then on it is tried again
    /// every hour, and this stays true until the token on disk changes or a try works.
    internal bool GaveUp(string grant)
    {
        var id = Fingerprint(grant);
        lock (_gate) return _grants.TryGetValue(id, out var held) && held.Ambiguous && held.Failures > AmbiguousHolds.Length;
    }

    internal void Settled(Grant grant)
    {
        lock (_gate)
        {
            if (_grants.TryGetValue(grant.Id, out var held) && held.Nonce == grant.Nonce) _grants.Remove(grant.Id);
        }
    }

    /// The POST may or may not have rotated the token. `reason` is a fixed
    /// word or phrase ("timeout", "HTTP 503"), never exception text.
    internal void Ambiguous(Grant grant, string reason)
    {
        int failures;
        lock (_gate)
        {
            if (!_grants.TryGetValue(grant.Id, out var held) || held.Nonce != grant.Nonce) return;
            failures = held.Failures + 1;
            _grants[grant.Id] = held with { Ambiguous = true, At = Clock(), Failures = failures };
        }
        CredentialRefreshLog.Note(provider, "ambiguous: " + reason);
        if (failures > AmbiguousHolds.Length) CredentialRefreshLog.Note(provider, "retries used up, trying hourly from now on");
    }

    internal void Clear()
    {
        lock (_gate) _grants.Clear();
    }

    /// A short, fixed description of a failed send, safe to log.
    internal static string ReasonFor(Exception error) => error switch
    {
        OperationCanceledException => "timeout",
        HttpRequestException => "connection lost",
        _ => "error",
    };

    internal static bool BeforeSend(Exception error) => error is HttpRequestException http
        && (http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError or HttpRequestError.ConnectionError
            || http.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.HostUnreachable });
}
