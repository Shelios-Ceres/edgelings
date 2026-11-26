using System.Globalization;

namespace AgentIsland.Usage;

/// Lines for the diagnostic report on why Antigravity's quota does or does not
/// arrive: counts, booleans, statuses and process names only. Never a token,
/// an e-mail address, a path or any file content.
public static class AntigravityDiagnostics
{
    /// Test seam: a process's name by id.
    internal static Func<int, string?> ProcessName = DefaultProcessName;

    private static string? DefaultProcessName(int pid)
    {
        var found = AntigravityLanguageServer.Processes().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefault();
        return found;
    }

    /// Asks the local language server once, the way the app's own refresh does.
    public static List<string> Describe()
    {
        var lines = new List<string>();
        var number = CultureInfo.InvariantCulture;
        try
        {
            var (total, matching, withToken) = AntigravityLanguageServer.HubCensus();
            lines.Add(string.Create(number,
                $"quota hub files: {total} found, {matching} match a live process (pid alive, written after it started, it listens on a port named in the file), {withToken} of those carry a token"));
        }
        catch (Exception error)
        {
            lines.Add("quota hub files: failed (" + error.GetType().Name + ")");
        }

        int? port;
        int pid;
        AntigravityLanguageServer.ProbeResult state;
        try
        {
            var processes = AntigravityLanguageServer.ListAntigravityProcessIds();
            var listening = processes.Sum(id => AntigravityLanguageServer.ListListeningPorts(id).Count);
            lines.Add(string.Create(number, $"quota processes: {processes.Count} Antigravity process(es), {listening} listening port(s) on 127.0.0.1"));
            (port, pid, state) = AntigravityLanguageServer.DiscoverWithState().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            lines.Add("quota discovery: failed (" + error.GetType().Name + ")");
            return lines;
        }
        if (port is not { } livePort)
        {
            lines.Add($"quota discovery: no port found, probe {state}");
            return lines;
        }
        var kind = Kind(ProcessName(pid));
        var hasToken = AntigravityLanguageServer.StartTime(pid) is { } started
            && AntigravityLanguageServer.HubToken(pid, started) is not null;
        lines.Add($"quota discovery: a port owned by {kind}, probe {state}, hub token for that process {(hasToken ? "yes" : "no")}");

        try
        {
            var reply = AntigravityUsageFetcher.CallWithCsrfRetry("RetrieveUserQuotaSummary", livePort, "{}")
                .GetAwaiter().GetResult();
            if (reply is null)
            {
                lines.Add("quota call: RetrieveUserQuotaSummary got no reply");
            }
            else
            {
                var buckets = reply.Status == 200 ? AntigravityQuotaParser.ParseQuotaSummary(reply.Body)?.Buckets.Count : null;
                lines.Add(string.Create(number,
                    $"quota call: RetrieveUserQuotaSummary HTTP {reply.Status}, {(buckets is { } n ? n + " bucket(s)" : "no buckets")}"));
            }
        }
        catch (Exception error)
        {
            lines.Add("quota call: failed (" + error.GetType().Name + ")");
        }
        return lines;
    }

    /// Only the process's name, grouped the way the discovery rules treat it.
    private static string Kind(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "an unnamed process";
        if (name.Equals("agy", StringComparison.OrdinalIgnoreCase)) return "agy";
        if (name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase)) return "language_server";
        return AntigravityLanguageServer.IsAntigravityName(name) ? name.ToLowerInvariant() : "another process (" + name + ")";
    }
}
