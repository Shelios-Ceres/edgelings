using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Usage;

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
                "windows" => Windows(request.RootElement),
                "presence" => Presence(),
                "systemEvent" => SystemEvent(request.RootElement),
                "diagnose" => Diagnose(request.RootElement),
                "scan" => Scan(request.RootElement),
                "appStart" => AppModel.Start(),
                "frame" => AppModel.Frame(request.RootElement),
                "card" => AppModel.Card(request.RootElement),
                "looks" => AppModel.Looks(request.RootElement),
                "setDrawer" => AppModel.SetDrawer(request.RootElement),
                "tr" => SettingsApi.Tr(request.RootElement),
                "settings" => SettingsApi.Settings(request.RootElement),
                "set" => SettingsApi.Set(request.RootElement),
                "agent" => SettingsApi.Agent(request.RootElement),
                "usage" => SettingsApi.Usage(request.RootElement),
                "format" => SettingsApi.Format(request.RootElement),
                "rescan" => SettingsApi.Rescan(),
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
        // "native" is the folder that holds libe_sqlite3.dylib (the bundle's Frameworks).
        if (request.TryGetProperty("native", out var native) && native.GetString() is { Length: > 0 } nativeFolder) NativeResolver.Use(nativeFolder);
        if (request.TryGetProperty("root", out var r)) IslandPaths.RootOverride = r.GetString();
        // "cache" keeps the caches elsewhere while the home stays real (the diagnostic).
        if (request.TryGetProperty("cache", out var c)) IslandPaths.CacheDirOverride = c.GetString() is { Length: > 0 } cache ? cache : null;
        return Write(json => json.WriteBoolean("rooted", IslandPaths.RootOverride is not null));
    }

    /// What Swift saw of the user's windows, owner and state only, never a
    /// title: {"method":"windows","windows":[{"pid":123,"offscreen":false}]}.
    /// Presence uses the latest report for 20 seconds.
    private static string Windows(JsonElement request)
    {
        var windows = new List<AgentPresences.AppWindow>();
        if (request.TryGetProperty("windows", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var id))
                {
                    windows.Add(new AgentPresences.AppWindow(id, item.TryGetProperty("offscreen", out var off) && off.ValueKind == JsonValueKind.True));
                }
            }
        }
        MacWindowReport.Report(windows);
        return Write(json => json.WriteNumber("windows", windows.Count));
    }

    /// The Mac's sleep, wake and unlock notifications, forwarded by Swift:
    /// {"method":"systemEvent","kind":"wake"} or "unlock". Usage refreshes as on Windows.
    private static string SystemEvent(JsonElement request)
    {
        var kind = request.TryGetProperty("kind", out var k) ? k.GetString() : null;
        switch (kind)
        {
            case "wake": UsageStore.Shared.HandleWake(); break;
            case "unlock": UsageStore.Shared.HandleUnlock(); break;
            case "sleep": break;
            default: return Error($"unknown system event '{kind}'");
        }
        return Write(json => json.WriteString("handled", kind));
    }

    /// What Core sees on this machine, as ids, counts and states only: no
    /// titles, paths, e-mail addresses or tokens. Sessions and token totals
    /// come from the agents' local files; "fetch":true also asks Claude's and
    /// Codex's usage endpoints for their quota windows, which uses (and may
    /// rotate) the stored login the way the app does.
    private static string Diagnose(JsonElement request)
    {
        var fetch = request.TryGetProperty("fetch", out var f) && f.ValueKind == JsonValueKind.True;
        var now = DateTimeOffset.UtcNow;
        var presence = AgentPresences.Now();
        var sessions = Safe(() => SessionScanner.MonitoringScan(now, new Dictionary<string, DateTimeOffset>()), new List<ScannedSession>());
        var gone = AgentPresences.ProcessesGone();
        return Write(json =>
        {
            json.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            json.WriteString("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            json.WriteNumber("windowsReported", MacWindowReport.ReportedCount());
            json.WriteString("sqlite", SqliteProbe());
            json.WriteStartObject("agents");
            foreach (var tool in Enum.GetValues<TriggerTool>())
            {
                json.WriteStartObject(tool.ToString().ToLowerInvariant());
                json.WriteString("presence", (presence.TryGetValue(tool, out var p) ? p.ToString() : "unknown").ToLowerInvariant());
                json.WriteBoolean("processRunning", !gone.Contains(tool));
                var mine = sessions.Where(s => s.Tool == tool).ToList();
                json.WriteNumber("sessions", mine.Count);
                json.WriteStartObject("phases");
                foreach (var group in mine.GroupBy(s => s.Status).OrderBy(g => (int)g.Key))
                {
                    json.WriteNumber(group.Key.ToString().ToLowerInvariant(), group.Count());
                }
                json.WriteEndObject();
                json.WriteEndObject();
            }
            json.WriteEndObject();

            var login = ClaudeCredentials.Describe();
            json.WriteStartObject("credentials");
            json.WriteString("claudeLoginSource", login.Source);
            if (login.Plan is { } plan) json.WriteString("claudePlan", plan);
            if (login.MinutesLeft is { } left) json.WriteNumber("claudeAccessTokenMinutesLeft", left);
            json.WriteBoolean("codexAuthFile", File.Exists(IslandPaths.CodexAuthFile));
            json.WriteBoolean("grokAuthFile", GrokAuthFile.Exists());
            json.WriteBoolean("cursorDatabase", File.Exists(IslandPaths.CursorStateDb));
            json.WriteEndObject();

            json.WriteStartObject("tokens30d");
            foreach (var (name, scan) in Readers())
            {
                var events = Safe(() => scan(30), new List<TokenEvent>());
                json.WriteStartObject(name);
                json.WriteNumber("events", events.Count);
                json.WriteNumber("wireTokens", events.Sum(e => e.WireTokens));
                json.WriteNumber("models", events.Select(e => e.Model).Distinct(StringComparer.Ordinal).Count());
                json.WriteEndObject();
            }
            json.WriteEndObject();

            if (fetch)
            {
                json.WriteStartObject("quota");
                WriteQuota(json, "claude", () => UsageFetcher.FetchClaude());
                WriteQuota(json, "codex", () => UsageFetcher.FetchCodex());
                json.WriteEndObject();
            }
        });
    }

    /// Whether the bundled SQLite loads (Cursor's database needs it).
    private static string SqliteProbe()
    {
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "select sqlite_version()";
            return command.ExecuteScalar() is string version ? "ok " + version : "no answer";
        }
        catch (Exception error)
        {
            var inner = error.InnerException;
            return "failed: " + error.GetType().Name + (inner is null ? "" : " / " + inner.GetType().Name + ": " + inner.Message);
        }
    }

    private static IEnumerable<(string Name, Func<int, List<TokenEvent>> Scan)> Readers()
    {
        yield return ("claude", ClaudeLogReader.Scan);
        yield return ("codex", CodexLogReader.Scan);
        yield return ("antigravity", AntigravityLogReader.Scan);
        yield return ("grok", GrokLogReader.Scan);
        yield return ("cursor", CursorLogReader.Scan);
        yield return ("kimi", KimiLogReader.Scan);
        yield return ("deepseek", DeepSeekLogReader.Scan);
    }

    private static void WriteQuota(Utf8JsonWriter json, string name, Func<Task<AppUsage>> fetch)
    {
        json.WriteStartObject(name);
        try
        {
            var usage = fetch().GetAwaiter().GetResult();
            WriteWindow(json, "fiveHour", usage.FiveHour);
            WriteWindow(json, "weekly", usage.Weekly);
        }
        catch (Exception error)
        {
            json.WriteString("error", error.GetType().Name);
        }
        json.WriteEndObject();
    }

    private static void WriteWindow(Utf8JsonWriter json, string name, WindowUsage window)
    {
        json.WriteStartObject(name);
        json.WriteNumber("usedPercent", Math.Round(window.UsedPercent * 100, 1));
        if (window.ResetAt is { } reset) json.WriteNumber("resetsInMinutes", Math.Round((reset - DateTimeOffset.UtcNow).TotalMinutes));
        // Only the fixed captions the app itself shows; anything else (it could name a path) is not repeated.
        if (window.Error is { } error) json.WriteString("error", SafeCaption(error));
        json.WriteEndObject();
    }

    private static string SafeCaption(string error) =>
        error.Length <= 40 && error.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or ':') ? error : "(other)";

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    /// Every agent's presence as Core reads it now: open, minimized, hidden or closed.
    private static string Presence() => Write(json =>
    {
        foreach (var (tool, presence) in AgentPresences.Now().OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            json.WriteString(tool.ToString().ToLowerInvariant(), presence.ToString().ToLowerInvariant());
        }
    });

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
