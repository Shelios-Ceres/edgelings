using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AgentIsland.Usage;

// A transport failure after a POST can hide a successful rotation. Keep only its
// fingerprint for the process lifetime; a new login/official rotation proceeds normally.
internal sealed class RefreshGrants
{
    private readonly ConcurrentDictionary<string, byte> _pending = new();

    internal string? Begin(string grant)
    {
        var bytes = Encoding.UTF8.GetBytes(grant);
        try
        {
            var id = Convert.ToHexString(SHA256.HashData(bytes));
            return _pending.TryAdd(id, 0) ? id : null;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void Settled(string id) => _pending.TryRemove(id, out _);

    internal static bool BeforeSend(Exception error) => error is HttpRequestException http
        && (http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError
            || http.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.HostUnreachable });
}
