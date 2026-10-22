using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentIsland.Core;

/// Grok's updates.jsonl records no approval while a permission prompt is
/// open, so a session waiting for one looks like a long-running tool. An
/// opt-in Notification hook fills the gap, as ClaudeHooks does for Claude
/// Code: on a permission prompt it records the session id, the prompt type
/// and the time (never the prompt's text) in AppSupportDir/grok-hooks.
///
/// The hook is one file of ours, ~/.grok/hooks/agentisland-notify.json
/// (Grok's global hooks are always trusted, and nothing else in ~/.grok is
/// touched), which runs a small PowerShell script of ours kept in
/// AppSupportDir. A file by that name that isn't ours is left alone.
/// A Notification hook has no say over permissions; it only observes.
public static class GrokHooks
{
    public const string PermissionPrompt = "permission_prompt";
    public const string HookFileName = "agentisland-notify.json";
    private const string ScriptFileName = "grok-hook.ps1";
    private static readonly TimeSpan PromptLifetime = TimeSpan.FromHours(2);

    /// What the hook's command holds, which tells our file from another's.
    private const string Marker = ScriptFileName;

    /// Grok's home: GROK_HOME, else ~/.grok.
    public static string GrokHome =>
        Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } home ? home : Path.Combine(IslandPaths.Home, ".grok");

    /// Where the hook file goes; tests point it elsewhere.
    internal static string HooksDirectory { get; set; } = "";

    /// Where the script and its markers live; tests point them elsewhere.
    internal static string ScriptDirectory { get; set; } = "";
    internal static string SignalDirectory { get; set; } = "";

    private static string Hooks => HooksDirectory.Length > 0 ? HooksDirectory : Path.Combine(GrokHome, "hooks");
    private static string Scripts => ScriptDirectory.Length > 0 ? ScriptDirectory : IslandPaths.AppSupportDir;
    internal static string Signals => SignalDirectory.Length > 0 ? SignalDirectory : Path.Combine(IslandPaths.AppSupportDir, "grok-hooks");

    public static string HookPath => Path.Combine(Hooks, HookFileName);
    private static string ScriptPath => Path.Combine(Scripts, ScriptFileName);

    /// The script. It reads Grok's event from stdin (camelCase keys, with
    /// snake_case aliases) and writes a marker named for the session; the
    /// keys it reads carry ids and a type, never text.
    internal const string Script =
        "$e = [Console]::In.ReadToEnd() | ConvertFrom-Json\r\n" +
        "$d = if ($env:AGENTISLAND_GROK_SIGNALS) { $env:AGENTISLAND_GROK_SIGNALS } else { Join-Path $env:APPDATA 'AgentIsland\\grok-hooks' }\r\n" +
        "New-Item -ItemType Directory -Force -Path $d | Out-Null\r\n" +
        "$sid = if ($e.sessionId) { $e.sessionId } else { $e.session_id }\r\n" +
        "$id = ($sid -replace '[^A-Za-z0-9-]', '')\r\n" +
        "$type = if ($e.notificationType) { $e.notificationType } else { $e.notification_type }\r\n" +
        "if ($id) { [ordered]@{ session_id = $id; type = $type; at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() } | ConvertTo-Json -Compress | Set-Content -Encoding UTF8 -Path (Join-Path $d ($id + '.json')) }\r\n";

    /// The hook file's JSON: Grok reads only type, command and timeout from
    /// a handler, and PowerShell is called by name so the command means the
    /// same under pwsh, Windows PowerShell and Git Bash.
    internal static string HookJson()
    {
        var command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + ScriptPath.Replace('\\', '/') + "\"";
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Notification"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = PermissionPrompt,
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 10 },
                        },
                    },
                },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine;
    }

    // MARK: - Install

    /// Our hook file is in place.
    public static bool IsInstalled() => File.Exists(HookPath) && IsOurs(HookPath);

    /// A file by our name is there, and it isn't ours: it is left alone.
    public static bool Conflicts() => File.Exists(HookPath) && !IsOurs(HookPath);

    private static bool IsOurs(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// Adds or removes our hook file and script, nothing else. Returns an
    /// error message, or null on success. Sessions started afterwards pick
    /// the change up.
    public static string? SetInstalled(bool installed)
    {
        try
        {
            if (Conflicts()) return $"{HookFileName} in {Hooks} is not Agent Island's, so it was left alone.";
            if (!installed)
            {
                if (File.Exists(HookPath)) File.Delete(HookPath);
                if (File.Exists(ScriptPath)) File.Delete(ScriptPath);
                if (Directory.Exists(Hooks) && !Directory.EnumerateFileSystemEntries(Hooks).Any()) Directory.Delete(Hooks);
                return null;
            }
            Directory.CreateDirectory(Scripts);
            Directory.CreateDirectory(Hooks);
            Write(ScriptPath, Script);
            Write(HookPath, HookJson());
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }

    private static void Write(string path, string text)
    {
        var temp = path + ".agentisland.tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // MARK: - Signals

    /// The open permission prompt of each session: the latest marker for it
    /// that is newer than its transcript's last write (answering the prompt
    /// writes the tool's result, which retires the marker) and not older than
    /// PromptLifetime. Keyed by the session id, the name of Grok's session
    /// folder. A marker of another type, from the future or unreadable is
    /// ignored, and never hides another session's prompt.
    internal static Dictionary<string, (string Type, DateTimeOffset At)> PendingFor(
        IEnumerable<(string SessionId, string TranscriptPath)> sessions, DateTimeOffset now)
    {
        var pending = new Dictionary<string, (string Type, DateTimeOffset At)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(Signals)) return pending;
            foreach (var (sessionId, transcript) in sessions)
            {
                var file = Path.Combine(Signals, sessionId + ".json");
                if (!File.Exists(file) || !File.Exists(transcript)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (Jsonl.GetString(root, "type") != PermissionPrompt) continue;
                    var ms = Jsonl.GetDouble(root, "at") ?? 0;
                    if (ms is <= 0 or > 253_402_300_799_999) continue;
                    var at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
                    var written = new DateTimeOffset(File.GetLastWriteTimeUtc(transcript), TimeSpan.Zero);
                    if (at > now || now - at >= PromptLifetime || at < written) continue;
                    pending[sessionId] = (PermissionPrompt, at);
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
}
