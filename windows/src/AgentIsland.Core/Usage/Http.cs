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
