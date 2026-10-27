using System.Net;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Only a transient transport failure is asked again. A reply — 429, 403, a
/// lost sign-in — never is: Anthropic rate-limits the usage endpoint hard,
/// and asking again within seconds keeps it limited. No network: the sends
/// are scripted.
public static class UsageRetryTests
{
    public static void RunAll()
    {
        var backoff = Http.TransientBackoff;
        try
        {
            Http.TransientBackoff = new[] { TimeSpan.Zero, TimeSpan.Zero };
            var tests = new (string Name, Action Test)[]
            {
                ("a dropped connection, a failed handshake or a timeout is tried twice more", TransientIsRetried),
                ("a reply is never asked again, whatever its status", RepliesAreFinal),
                ("other failures and the caller's own cancel are not retried", OthersAreFinal),
                ("a refresh asks each provider once, even when the answer is an error", StoreAsksOnce),
            };
            foreach (var (name, test) in tests)
            {
                test();
                Console.WriteLine($"PASS usage retry: {name}");
            }
            Console.WriteLine("UsageRetryTests GREEN");
        }
        finally
        {
            Http.TransientBackoff = backoff;
        }
    }

    private static (int Sends, HttpResponseMessage? Response, Exception? Error) Send(
        CancellationToken ct, params Func<HttpResponseMessage>[] answers)
    {
        var sends = 0;
        try
        {
            var response = Http.SendRetryingTransient(
                () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/usage"),
                (_, _) =>
                {
                    var answer = answers[Math.Min(sends, answers.Length - 1)];
                    sends++;
                    return Task.FromResult(answer());
                },
                ct).GetAwaiter().GetResult();
            return (sends, response, null);
        }
        catch (Exception error)
        {
            return (sends, null, error);
        }
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode status) => () => new HttpResponseMessage(status);

    private static Func<HttpResponseMessage> Throws(Exception error) => () => throw error;

    private static void TransientIsRetried()
    {
        foreach (var kind in new[] { HttpRequestError.ConnectionError, HttpRequestError.SecureConnectionError, HttpRequestError.NameResolutionError })
        {
            var (sends, response, _) = Send(CancellationToken.None,
                Throws(new HttpRequestException(kind, "blip")), Throws(new HttpRequestException(kind, "blip")), Status(HttpStatusCode.OK));
            Expect(sends == 3 && response?.StatusCode == HttpStatusCode.OK, $"{kind}: retried until the reply, got {sends} sends");
        }
        var timeout = Send(CancellationToken.None, Throws(new TaskCanceledException("timeout")), Status(HttpStatusCode.OK));
        Expect(timeout.Sends == 2 && timeout.Response?.StatusCode == HttpStatusCode.OK, "HttpClient's own timeout is retried");
        var outage = Send(CancellationToken.None, Throws(new HttpRequestException(HttpRequestError.ConnectionError, "down")));
        Expect(outage.Sends == 3 && outage.Error is HttpRequestException, "a real outage still ends after three tries");
    }

    private static void RepliesAreFinal()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.ServiceUnavailable })
        {
            var (sends, response, _) = Send(CancellationToken.None, Status(status), Status(HttpStatusCode.OK));
            Expect(sends == 1 && response?.StatusCode == status, $"{(int)status} is answered once, got {sends} sends");
        }
    }

    private static void OthersAreFinal()
    {
        var refused = Send(CancellationToken.None, Throws(new HttpRequestException(HttpRequestError.UserAuthenticationError, "proxy auth")));
        Expect(refused.Sends == 1 && refused.Error is HttpRequestException, "a non-transient transport failure is not retried");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var superseded = Send(cancelled.Token, Throws(new TaskCanceledException("superseded")));
        Expect(superseded.Sends == 1, "a refresh that was superseded stops at once");
    }

    private static void StoreAsksOnce()
    {
        var claude = 0;
        var codex = 0;
        var store = new UsageStore(restore: false)
        {
            ClaudeFetcher = _ =>
            {
                Interlocked.Increment(ref claude);
                return Task.FromResult(AppUsage.ErrorPair("rate limited"));
            },
            CodexFetcher = _ =>
            {
                Interlocked.Increment(ref codex);
                return Task.FromResult(AppUsage.ErrorPair("http 429"));
            },
            CodexLogin = () => "account:a",
        };
        store.Refresh();
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(10));
        Expect(claude == 1, $"a rate-limited Claude was asked once, got {claude}");
        Expect(codex == 1, $"a rate-limited Codex was asked once, got {codex}");
        Expect(store.Claude.FiveHour.Error == "rate limited", "the reply is shown as it came");
    }
}
