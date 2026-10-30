using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;

namespace AgentIsland.Bridge;

/// What the Swift side can ask Core. One JSON request in, one JSON reply out,
/// so the same calls work through a NativeAOT dylib (C exports) and through
/// a helper process (one request per line on stdin). Only public Core API is
/// used, and only AOT-safe JSON: Utf8JsonWriter out, JsonDocument in.
public static class BridgeApi
{
    public static string Handle(string requestJson)
    {
        try
        {
            using var request = JsonDocument.Parse(requestJson);
            var method = request.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
            return method switch
            {
                "version" => Version(),
                "configure" => Configure(request.RootElement),
                "scan" => Scan(request.RootElement),
                _ => Error($"unknown method '{method}'"),
            };
        }
        catch (Exception error)
        {
            return Error(error.GetType().Name + ": " + error.Message);
        }
    }

    private static string Version() => Write(json =>
    {
        json.WriteString("core", typeof(BridgeApi).Assembly.GetName().Version?.ToString() ?? "?");
        json.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        json.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        json.WriteString("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
    });

    /// Points Core at a world of its own: {"method":"configure","root":"…"}
    /// moves the home, the app's settings and caches and every agent's folder
    /// under <root>/home (IslandPaths.RootOverride). Benches and tests send it
    /// first so nothing reads or rewrites the real ones; an empty root goes
    /// back to the real folders.
    private static string Configure(JsonElement request)
    {
        var root = request.TryGetProperty("root", out var r) ? r.GetString() : null;
        IslandPaths.RootOverride = root;
        return Write(json => json.WriteBoolean("rooted", IslandPaths.RootOverride is not null));
    }

    /// A scan and a summary over one Kimi Code data folder: the cost reader's
    /// token events (with its parse cache) priced and summarised, and the
    /// session scanner's list. {"method":"scan","kimiHome":"…","lookbackDays":60}
    private static string Scan(JsonElement request)
    {
        if (request.TryGetProperty("kimiHome", out var home) && home.GetString() is { Length: > 0 } path)
        {
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", path);
        }
        var days = request.TryGetProperty("lookbackDays", out var d) && d.TryGetInt32(out var n) ? n : 60;
        var now = DateTimeOffset.UtcNow;
        var events = KimiLogReader.Scan(days);
        var summary = CostSummarizer.Summarize(events, now, TimeZoneInfo.Utc);
        var sessions = SessionScanner.ScanKimi(now, new Dictionary<string, DateTimeOffset>());
        return Write(json =>
        {
            json.WriteNumber("events", events.Count);
            json.WriteNumber("wireTokens", events.Sum(e => e.WireTokens));
            json.WriteNumber("dollars", Math.Round(events.Sum(e => e.Dollars), 4));
            json.WriteNumber("monthModels", summary.MonthModels.Count);
            json.WriteNumber("sessions", sessions.Count);
            json.WriteNumber("sessionsWorking", sessions.Count(s => s.Status == ActivityState.Working));
        });
    }

    private static string Error(string message) => Write(json => json.WriteString("error", message));

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
