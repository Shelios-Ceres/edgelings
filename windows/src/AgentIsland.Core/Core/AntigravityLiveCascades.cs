using System.Text.Json;
using AgentIsland.Usage;

namespace AgentIsland.Core;

/// What Antigravity's own language servers say about its agent runs. The
/// transcript is written a step at a time, after each step, so a long
/// command or a slow model call is only silence there; the server holds each
/// cascade (conversation) as running, busy or idle. Every server is asked —
/// the desktop app's and the CLI's — over loopback, at most every few
/// seconds, and only while Antigravity runs.
internal static class AntigravityLiveCascades
{
    /// A cascade's run status ("CASCADE_RUN_STATUS_RUNNING", …) and why its
    /// last run stopped ("EXECUTOR_TERMINATION_REASON_NO_TOOL_CALL", …).
    internal readonly record struct Run(string Status, string? Stopped);

    // Declared before the caches that start out holding it.
    private static readonly IReadOnlyDictionary<string, Run> Empty = new Dictionary<string, Run>();
    private static readonly object Gate = new();
    private static (DateTimeOffset At, IReadOnlyDictionary<string, Run> Runs) _last = (DateTimeOffset.MinValue, Empty);
    private static (DateTimeOffset At, List<(int Port, string? Token)> Servers) _servers = (DateTimeOffset.MinValue, new());
    // Reading a token starts PowerShell: once per process.
    private static readonly Dictionary<int, string?> TokenByPid = new();

    private static TimeSpan Fresh => TimeSpan.FromSeconds(3);
    // Looked for again this often while none answers (Antigravity closed),
    // and now and then while some do, for one started since.
    private static TimeSpan LookAgainWhileNone => TimeSpan.FromSeconds(30);
    private static TimeSpan LookAgainAnyway => TimeSpan.FromMinutes(5);

    /// Each conversation the servers hold, by its id — the conversation's
    /// folder name under brain\. Empty while no server answers.
    public static IReadOnlyDictionary<string, Run> Read(DateTimeOffset now)
    {
        lock (Gate)
        {
            if (now - _last.At < Fresh) return _last.Runs;
        }
        var runs = Fetch(now);
        lock (Gate) _last = (now, runs);
        return runs;
    }

    /// The scan runs on a worker thread, so the server calls are waited on.
    private static IReadOnlyDictionary<string, Run> Fetch(DateTimeOffset now)
    {
        (DateTimeOffset At, List<(int Port, string? Token)> Servers) known;
        lock (Gate) known = _servers;
        var servers = known.Servers;
        if (now - known.At > (servers.Count == 0 ? LookAgainWhileNone : LookAgainAnyway))
        {
            servers = Discover();
            lock (Gate) _servers = (now, servers);
        }
        if (servers.Count == 0) return Empty;
        var runs = new Dictionary<string, Run>(StringComparer.Ordinal);
        foreach (var (port, token) in servers)
        {
            var reply = AntigravityLanguageServer.Call("GetAllCascadeTrajectories", port, "{}", token, timeoutSeconds: 2)
                .GetAwaiter().GetResult();
            if (reply is not { Status: 200 })
            {
                // Gone or restarted on another port: look again next time.
                lock (Gate) _servers = (DateTimeOffset.MinValue, new());
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(reply.Body);
                foreach (var (id, run) in Parse(doc.RootElement)) runs[id] = run;
            }
            catch (JsonException)
            {
            }
        }
        return runs;
    }

    /// Every Antigravity process's RPC port. The CLI's server wants no token;
    /// the desktop app's takes the one on its command line, read once.
    private static List<(int Port, string? Token)> Discover()
    {
        var servers = new List<(int, string?)>();
        var pids = AntigravityLanguageServer.AntigravityProcessIds();
        lock (Gate)
        {
            foreach (var gone in TokenByPid.Keys.Except(pids).ToList()) TokenByPid.Remove(gone);
        }
        foreach (var pid in pids)
        {
            string? token;
            bool tokenRead;
            lock (Gate) tokenRead = TokenByPid.TryGetValue(pid, out token);
            foreach (var port in AntigravityLanguageServer.ListeningPorts(pid))
            {
                var reply = AntigravityLanguageServer.Call("GetAllCascadeTrajectories", port, "{}", token, timeoutSeconds: 2)
                    .GetAwaiter().GetResult();
                if (reply is { Status: 401 } && !tokenRead)
                {
                    tokenRead = true;
                    token = AntigravityLanguageServer.CsrfToken(pid);
                    lock (Gate) TokenByPid[pid] = token;
                    if (token is not null)
                    {
                        reply = AntigravityLanguageServer.Call("GetAllCascadeTrajectories", port, "{}", token, timeoutSeconds: 2)
                            .GetAwaiter().GetResult();
                    }
                }
                if (reply is not { Status: 200 }) continue;
                servers.Add((port, token));
                break;
            }
        }
        return servers;
    }

    /// GetAllCascadeTrajectories: { trajectorySummaries: { id: { status,
    /// latestExecutorTerminationReason, … } } }, holding the conversations
    /// opened since the server started (verified 2026-10-16).
    internal static IEnumerable<(string Id, Run Run)> Parse(JsonElement root)
    {
        if (Jsonl.GetObject(root, "trajectorySummaries") is not { } summaries) yield break;
        foreach (var entry in summaries.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Object && Jsonl.GetString(entry.Value, "status") is { Length: > 0 } status)
            {
                yield return (entry.Name, new Run(status, Jsonl.GetString(entry.Value, "latestExecutorTerminationReason")));
            }
        }
    }

    /// A conversation as its server reports it: a run in progress is work
    /// whatever the transcript's silence; one the user cancelled is over
    /// with nothing owed; one stopped any other way while the transcript's
    /// turn is still open stopped for the user (an approval, an error, a
    /// limit). Anything else leaves the transcript's own reading.
    internal static ActivityState Apply(ActivityState fromTranscript, Run run) => run.Status switch
    {
        "CASCADE_RUN_STATUS_RUNNING" or "CASCADE_RUN_STATUS_BUSY" or "CASCADE_RUN_STATUS_CANCELING" => ActivityState.Working,
        "CASCADE_RUN_STATUS_IDLE" when run.Stopped == "EXECUTOR_TERMINATION_REASON_USER_CANCELED" => ActivityState.Idle,
        "CASCADE_RUN_STATUS_IDLE" when fromTranscript == ActivityState.Working => ActivityState.NeedsYou,
        _ => fromTranscript,
    };
}
