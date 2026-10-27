using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentIsland.Core;

/// Claude Code writes nothing to its transcript while a permission prompt is
/// open, so from the transcript alone a session waiting for approval looks
/// like a long-running tool. An opt-in Notification hook in Claude Code's
/// settings.json fills that gap: on a permission prompt or an MCP
/// input dialog it records the session id, transcript path, prompt type and
/// time — never the prompt text — in AppSupportDir/claude-hooks. A
/// Notification hook has no say over permissions; it only observes.
public static class ClaudeHooks
{
    public const string PermissionPrompt = "permission_prompt";
    public const string ElicitationDialog = "elicitation_dialog";

    /// The marker that identifies our matcher group in settings.json.
    private const string Marker = "AgentIsland\\claude-hooks";
    private const string Matcher = PermissionPrompt + "|" + ElicitationDialog;
    private static readonly TimeSpan PromptLifetime = TimeSpan.FromHours(2);

    /// Async, so Claude Code never waits on it; PowerShell, so it runs on
    /// machines without Git Bash.
    internal const string Command =
        "$e = [Console]::In.ReadToEnd() | ConvertFrom-Json; " +
        "$d = Join-Path $env:APPDATA '" + Marker + "'; " +
        "New-Item -ItemType Directory -Force -Path $d | Out-Null; " +
        "$id = ($e.session_id -replace '[^A-Za-z0-9-]', ''); " +
        "if ($id) { [ordered]@{ session_id = $id; transcript_path = $e.transcript_path; type = $e.notification_type; " +
        "at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() } | ConvertTo-Json -Compress | " +
        "Set-Content -Encoding UTF8 -Path (Join-Path $d ($id + '.json')) }";

    /// Claude Code's user settings, in the config folder it reads
    /// (CLAUDE_CONFIG_DIR, else ~/.claude); tests point it elsewhere.
    public static string SettingsPath => SettingsFileOverride ?? Path.Combine(IslandPaths.ClaudeConfigRoots[0], "settings.json");

    internal static string? SettingsFileOverride { get; set; }

    /// Where the hook drops its markers; tests point it elsewhere.
    internal static string SignalDirectory { get; set; } = Path.Combine(IslandPaths.AppSupportDir, "claude-hooks");

    // A switch flipped on and straight back off must not read the file
    // before the first change has landed.
    private static readonly object InstallGate = new();

    // MARK: - Install

    public static bool IsInstalled()
    {
        try
        {
            return File.Exists(SettingsPath) && Contains(JsonNode.Parse(File.ReadAllText(SettingsPath)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// Adds or removes the hook, keeping everything else in the file. The
    /// first change leaves a backup beside it. Returns an error message, or
    /// null on success. A file that is not plain JSON is left alone. A
    /// settings.json that links elsewhere (a dotfiles folder) is rewritten
    /// where it points, so the link stays.
    public static string? SetInstalled(bool installed)
    {
        lock (InstallGate)
        {
            var temp = "";
            try
            {
                var path = SettingsPath;
                var file = new FileInfo(path);
                var target = file.LinkTarget is null ? path : file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
                if (target != path && !File.Exists(target)) return $"settings.json links to {target}, which doesn't exist, so it was left alone.";
                JsonNode? root = File.Exists(target) ? JsonNode.Parse(File.ReadAllText(target)) : new JsonObject();
                if (root is not JsonObject settings) return "settings.json is not a JSON object";
                var changed = installed ? Install(settings) : Uninstall(settings);
                if (!changed) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var backup = path + ".agentisland.bak";
                if (File.Exists(target) && !File.Exists(backup)) File.Copy(target, backup);
                // Relaxed escaping keeps quotes and non-ASCII text in the user's
                // file readable instead of turning them into escape sequences.
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                temp = target + ".agentisland-" + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Encoding.UTF8.GetBytes(settings.ToJsonString(options) + Environment.NewLine));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, target, overwrite: true);
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                try { if (temp.Length > 0) File.Delete(temp); } catch { }
                return error.Message;
            }
        }
    }

    internal static bool Contains(JsonNode? root) =>
        root?["hooks"]?["Notification"] is JsonArray groups && groups.Any(IsOurs);

    internal static bool Install(JsonObject settings)
    {
        if (Contains(settings)) return false;
        if (settings["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            settings["hooks"] = hooks;
        }
        if (hooks["Notification"] is not JsonArray groups)
        {
            groups = new JsonArray();
            hooks["Notification"] = groups;
        }
        groups.Add(new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["shell"] = "powershell",
                    ["command"] = Command,
                    ["async"] = true,
                    ["timeout"] = 30,
                },
            },
        });
        return true;
    }

    internal static bool Uninstall(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks["Notification"] is not JsonArray groups) return false;
        var ours = groups.Where(IsOurs).ToList();
        if (ours.Count == 0) return false;
        foreach (var group in ours) groups.Remove(group);
        if (groups.Count == 0) hooks.Remove("Notification");
        if (hooks.Count == 0) settings.Remove("hooks");
        return true;
    }

    private static bool IsOurs(JsonNode? group) =>
        group?["hooks"] is JsonArray handlers
        && handlers.Any(h => h?["command"]?.GetValueKind() == JsonValueKind.String
            && h["command"]!.GetValue<string>().Contains(Marker, StringComparison.Ordinal));

    // MARK: - Signals

    /// The open prompt for a transcript: the latest marker for it that is
    /// newer than the transcript's last write. Answering the prompt writes
    /// the tool's result to the transcript, which retires the marker.
    public static (string Type, DateTimeOffset At)? Pending(string transcriptPath, DateTimeOffset now) =>
        PendingFor(new[] { transcriptPath }, now).TryGetValue(transcriptPath, out var prompt) ? prompt : null;

    internal static Dictionary<string, (string Type, DateTimeOffset At)> PendingFor(
        IEnumerable<string> transcriptPaths, DateTimeOffset now)
    {
        var pending = new Dictionary<string, (string Type, DateTimeOffset At)>(StringComparer.OrdinalIgnoreCase);
        var targets = transcriptPaths.Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(Normalize).ToDictionary(group => group.Key, group => group.First());
        if (targets.Count == 0) return pending;
        try
        {
            if (!Directory.Exists(SignalDirectory)) return pending;
            foreach (var file in Directory.EnumerateFiles(SignalDirectory, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (Jsonl.GetString(root, "transcript_path") is not { } path
                        || !targets.TryGetValue(Normalize(path), out var target) || !File.Exists(target)) continue;
                    var type = Jsonl.GetString(root, "type");
                    if (type is not (PermissionPrompt or ElicitationDialog)) continue;
                    var ms = Jsonl.GetDouble(root, "at") ?? 0;
                    if (ms is <= 0 or > 253_402_300_799_999) continue;
                    var at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
                    var written = new DateTimeOffset(File.GetLastWriteTimeUtc(target), TimeSpan.Zero);
                    if (at > now || now - at >= PromptLifetime || at < written) continue;
                    if (!pending.TryGetValue(target, out var latest) || at > latest.At) pending[target] = (type, at);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A concurrent hook write must not hide another session's prompt.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return pending;
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
