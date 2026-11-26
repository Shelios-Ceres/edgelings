using System.Net;
using System.Net.Sockets;

namespace AgentIsland.Usage;

/// One shared HttpClient for provider calls; per-request headers are set on
/// the request messages, never on defaults, so the two providers can't
/// contaminate each other.
public static class Http
{
    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    // A grant is single use. Even a 307/308 must not replay its body.
    internal static HttpClient OAuthClient { get; } = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectCallback = (context, ct) => ConnectWithTimeout(
            context.DnsEndPoint, PerAddressTimeout, ConnectTimeout, Dns.GetHostAddressesAsync, ConnectSocket, ct),
    }) { Timeout = TimeSpan.FromSeconds(60) };

    /// Per address, and for the whole connect: a black-holed IPv6 address is given
    /// up on after five seconds, so the IPv4 one after it still gets its turn.
    internal static TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(5);
    internal static TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// A request that has to open a connection first can take a whole connect budget
    /// before it is even sent; its own deadline must outlast that, or the connect
    /// still running when the deadline passes is left for the next request to wait on.
    internal static TimeSpan DeadlineAfterConnect(TimeSpan afterConnect) => DeadlineRule(afterConnect);

    /// How long a request may take once connected: a refresh grant.
    internal static TimeSpan GrantMargin = TimeSpan.FromSeconds(20);

    /// Test seam: the rule that turns a margin into a deadline.
    internal static Func<TimeSpan, TimeSpan> DeadlineRule = afterConnect => ConnectTimeout + afterConnect;

    private static ValueTask ConnectSocket(Socket socket, IPEndPoint endpoint, CancellationToken ct) => socket.ConnectAsync(endpoint, ct);

    /// A refresh grant is single use, so it matters whether a failure came
    /// before or after the request left. SocketsHttpHandler's own ConnectTimeout
    /// surfaces as a TaskCanceledException, which looks exactly like the 30 s
    /// request timeout; here a connect that times out is an HttpRequestException
    /// (ConnectionError) around a TimeoutException, which RefreshGrants.BeforeSend
    /// reads as "never sent". The name is resolved here and the addresses tried in
    /// the order given, so the budget is spent address by address. `resolve` and
    /// `connect` are seams. With a proxy the endpoint handed in is the proxy's.
    internal static async ValueTask<Stream> ConnectWithTimeout(
        DnsEndPoint endpoint, TimeSpan perAddress, TimeSpan total,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<Socket, IPEndPoint, CancellationToken, ValueTask> connect, CancellationToken ct)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(total);
        Exception? last = null;
        try
        {
            var addresses = await resolve(endpoint.Host, overall.Token);
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    attempt.CancelAfter(perAddress);
                    await connect(socket, new IPEndPoint(address, endpoint.Port), attempt.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    socket.Dispose();
                    last = new TimeoutException();
                    if (overall.IsCancellationRequested) break;
                }
                catch (Exception error) when (error is SocketException)
                {
                    socket.Dispose();
                    last = error;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            last = new TimeoutException();
        }
        if (last is SocketException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(last);
        throw new HttpRequestException(HttpRequestError.ConnectionError, "connect timed out", last ?? new TimeoutException());
    }

    // For a local server's own token: never through the system proxy, and a
    // redirect must not carry the token anywhere else.
    internal static HttpClient LoopbackClient { get; } = new(LoopbackHandler()) { Timeout = TimeSpan.FromSeconds(30) };

    internal static SocketsHttpHandler LoopbackHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    /// Test seam: the waits between tries.
    internal static TimeSpan[] TransientBackoff = { TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(2.4) };

    /// An SSL handshake through a flaky proxy or VPN, a dropped connection or
    /// a timeout clears within seconds, so the request gets two more tries
    /// (macOS transientRetryData). A reply never does, whatever its status:
    /// asking again after a 429 or a 403 only keeps the endpoint limited.
    /// The request is built afresh for each try; a message can't be resent.
    public static Task<HttpResponseMessage> SendRetryingTransient(Func<HttpRequestMessage> request, CancellationToken ct) =>
        SendRetryingTransient(request, Client.SendAsync, ct);

    internal static async Task<HttpResponseMessage> SendRetryingTransient(
        Func<HttpRequestMessage> request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await send(request(), ct);
            }
            catch (Exception error) when (attempt < TransientBackoff.Length && IsTransient(error, ct))
            {
            }
            await Task.Delay(TransientBackoff[attempt], ct);
        }
    }

    /// A transport failure that a moment's wait can fix. A cancellation the
    /// caller asked for is not one: HttpClient's own timeout is.
    internal static bool IsTransient(Exception error, CancellationToken ct) => error switch
    {
        TaskCanceledException => !ct.IsCancellationRequested,
        HttpRequestException http => http.HttpRequestError
            is HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError
            or HttpRequestError.ResponseEnded
            || (http.HttpRequestError == HttpRequestError.Unknown && http.InnerException is IOException),
        _ => false,
    };
}
