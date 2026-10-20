using System.Text.Json;
using AgentIsland.Usage;

namespace AgentIsland.Core;

/// What Kimi Code's desktop app says about the sessions it runs. Its server
/// reports each one mid-turn, and waiting on an approval or a question —
/// which wire.jsonl never records. Asked over loopback with the server's own
/// token, only while the desktop app is open, at most every few seconds. The
/// list also holds sessions the CLI runs, which the desktop app knows only
/// as idle, so a report can say a session is working or waiting, never that
/// it stopped.
internal static class KimiLiveSessions
{
    internal readonly record struct Report(bool TurnActive, string Pending);

    // Declared before the cache that starts out holding it.
    private static readonly IReadOnlyDictionary<string, Report> Empty = new Dictionary<string, Report>();
    private static readonly object Gate = new();
    private static (DateTimeOffset At, IReadOnlyDictionary<string, Report> Sessions) _last = (DateTimeOffset.MinValue, Empty);

    private static TimeSpan Fresh => TimeSpan.FromSeconds(3);
    private static TimeSpan Timeout => TimeSpan.FromSeconds(2);

    /// The recent sessions' reports by session id ("session_…"), empty while
    /// the desktop app is closed.
    public static IReadOnlyDictionary<string, Report> Read(DateTimeOffset now)
    {
        lock (Gate)
        {
            if (now - _last.At < Fresh) return _last.Sessions;
        }
        var sessions = Fetch();
        lock (Gate) _last = (now, sessions);
        return sessions;
    }

    private static IReadOnlyDictionary<string, Report> Fetch()
    {
        if (KimiUsageFetcher.LocalServer() is not { } server) return Empty;
        try
        {
            // The scan runs on a worker; the server answers from memory.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}/sessions?page_size=30");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
            using var cancel = new CancellationTokenSource(Timeout);
            using var response = Http.Client.Send(request, cancel.Token);
            if (!response.IsSuccessStatusCode) return Empty;
            using var stream = response.Content.ReadAsStream(cancel.Token);
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc.RootElement);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return Empty;
        }
    }

    /// The server's page: { code: 0, data: { items: [session…], has_more } },
    /// each session carrying main_turn_active and pending_interaction (none,
    /// approval or question).
    internal static Dictionary<string, Report> Parse(JsonElement root)
    {
        var output = new Dictionary<string, Report>(StringComparer.Ordinal);
        if (Jsonl.GetLong(root, "code") is not 0 || Jsonl.GetObject(root, "data") is not { } data) return output;
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return output;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Jsonl.GetString(item, "id") is not { Length: > 0 } id) continue;
            var active = Jsonl.GetBool(item, "main_turn_active") ?? Jsonl.GetBool(item, "busy") ?? false;
            output[id] = new Report(active, Jsonl.GetString(item, "pending_interaction") ?? "none");
        }
        return output;
    }

    /// A session as the desktop app reports it: waiting on the user's
    /// approval or answer, or mid-turn while its log is quiet. Anything else
    /// leaves the log's own reading.
    internal static ScannedSession Apply(ScannedSession session, Report report)
    {
        var waiting = report.Pending switch
        {
            "approval" => AgentPhase.Approving,
            "question" => AgentPhase.Asking,
            _ => (AgentPhase?)null,
        };
        if (waiting is { } phase)
        {
            return session with { Status = ActivityState.Working, PendingPrompt = new PhaseReading(phase, null, session.Modified) };
        }
        return report.TurnActive && session.Status is ActivityState.Idle or ActivityState.NeedsYou
            ? session with { Status = ActivityState.Working }
            : session;
    }
}
