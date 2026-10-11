using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentIsland.Core;

/// Classifies the tail of a Claude Code or Codex transcript into an
/// AgentPhase. Reads only event types, block types, stop reasons, tool names,
/// call ids and timestamps; message content is never inspected or kept,
/// except the fixed "[Request interrupted" marker Claude Code writes on Esc.
///
/// Claude Code writes an assistant message once it completes, one content
/// block per line, so generation itself is invisible: a trailing tool result
/// or prompt means the model is working on its next step. Tools run in
/// parallel, so "pending" means a call whose id has no result yet — a fast
/// call's result can land while a slow sibling is still running.
public static class AgentPhaseClassifier
{
    private const int TailBytes = 64 * 1024;
    private static readonly TimeSpan FinishedFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ToolFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ThinkingFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SpawnShownFor = TimeSpan.FromSeconds(3);

    /// Live phase of one transcript, or null for providers whose transcripts
    /// carry no tool-level events (or an unreadable file).
    public static PhaseReading? Classify(TriggerTool tool, string transcriptPath, DateTimeOffset now)
    {
        if (tool is not (TriggerTool.Claude or TriggerTool.Codex)) return null;
        if (!transcriptPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var lines = ReadTail(transcriptPath);
            return tool == TriggerTool.Claude ? Claude(lines, now) : Codex(lines, now);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PhaseReading Claude(IReadOnlyList<string> lines, DateTimeOffset now)
    {
        var results = new HashSet<string>();
        DateTimeOffset? resultAt = null;
        var inCalls = false; // walked back into the latest step's tool calls
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            // Subagent traffic interleaved into the main transcript is not
            // what the main session is doing.
            if (Jsonl.GetBool(root, "isSidechain") == true) continue;
            var at = Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp"));
            var stepDone = resultAt is not null || inCalls;
            switch (Jsonl.GetString(root, "type"))
            {
                case "assistant":
                {
                    var message = Jsonl.GetObject(root, "message");
                    var block = message is { } m ? LastBlock(m) : null;
                    var blockType = block is { } b ? Jsonl.GetString(b, "type") : null;
                    if (blockType == "tool_use")
                    {
                        var id = Jsonl.GetString(block!.Value, "id");
                        if (id is null ? resultAt is null : !results.Contains(id))
                        {
                            return Aged(Tool(Jsonl.GetString(block.Value, "name"), at, now), now);
                        }
                        inCalls = true;
                        continue;
                    }
                    // Text/thinking before the step's calls: every call has
                    // returned, so the model is working on the results.
                    if (stepDone) return Aged(new(AgentPhase.Thinking, null, resultAt ?? at), now);
                    if (Jsonl.GetBool(root, "isApiErrorMessage") == true) return Aged(new(AgentPhase.Failed, "API", at), now);
                    var stop = message is { } mm ? Jsonl.GetString(mm, "stop_reason") : null;
                    if (stop is "end_turn" or "stop_sequence" or "stop") return Aged(new(AgentPhase.Finished, null, at), now);
                    return Aged(new(blockType == "thinking" ? AgentPhase.Thinking : AgentPhase.Streaming, null, at), now);
                }
                case "user":
                    if (IsInterrupt(root)) return new(AgentPhase.Idle, null, at);
                    var answered = ToolResultIds(root);
                    if (answered is null || inCalls) return Aged(new(AgentPhase.Thinking, null, resultAt ?? at), now);
                    results.UnionWith(answered);
                    resultAt ??= at;
                    continue;
                case "system":
                    if (stepDone) continue;
                    switch (Jsonl.GetString(root, "subtype"))
                    {
                        case "compact_boundary": return Aged(new(AgentPhase.Compacting, null, at), now);
                        case "api_error": return Aged(new(AgentPhase.Failed, "API", at), now);
                    }
                    continue;
                default:
                    continue; // attachments, titles, snapshots, queue and hook bookkeeping
            }
        }
        return resultAt is not null ? Aged(new(AgentPhase.Thinking, null, resultAt), now) : new(AgentPhase.Idle, null, null);
    }

    /// Same parallel-call rule as Claude, keyed by call_id.
    public static PhaseReading Codex(IReadOnlyList<string> lines, DateTimeOffset now)
    {
        var outputs = new HashSet<string>();
        DateTimeOffset? outputAt = null;
        var inCalls = false;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            var at = Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp"));
            var payload = Jsonl.GetObject(root, "payload");
            var kind = payload is { } p ? Jsonl.GetString(p, "type") : null;
            var stepDone = outputAt is not null || inCalls;
            switch (Jsonl.GetString(root, "type"))
            {
                case "event_msg":
                    if (stepDone) continue;
                    switch (kind)
                    {
                        case "task_started": return Aged(new(AgentPhase.Thinking, null, at), now);
                        case "task_complete": return Aged(new(AgentPhase.Finished, null, at), now);
                        case "turn_aborted": return new(AgentPhase.Idle, null, at);
                    }
                    continue; // token counts, item completions, settings
                case "response_item":
                    switch (kind)
                    {
                        case "function_call_output":
                        case "custom_tool_call_output":
                            if (inCalls) return Aged(new(AgentPhase.Thinking, null, outputAt ?? at), now);
                            if (Jsonl.GetString(payload!.Value, "call_id") is { } done) outputs.Add(done);
                            outputAt ??= at;
                            continue;
                        case "function_call":
                        case "custom_tool_call":
                        case "local_shell_call":
                        case "web_search_call":
                            var callId = Jsonl.GetString(payload!.Value, "call_id");
                            if (callId is null ? outputAt is null : !outputs.Contains(callId))
                            {
                                return Aged(Tool(Jsonl.GetString(payload.Value, "name") ?? kind, at, now), now);
                            }
                            inCalls = true;
                            continue;
                        case "reasoning":
                            return Aged(new(AgentPhase.Thinking, null, outputAt ?? at), now);
                        case "agent_message":
                            return Aged(new(stepDone ? AgentPhase.Thinking : AgentPhase.Streaming, null, outputAt ?? at), now);
                        case "message":
                            switch (Jsonl.GetString(payload!.Value, "role"))
                            {
                                case "assistant":
                                    return Aged(new(stepDone ? AgentPhase.Thinking : AgentPhase.Streaming, null, outputAt ?? at), now);
                                case "user":
                                    return Aged(new(AgentPhase.Thinking, null, outputAt ?? at), now);
                            }
                            continue; // developer/system instructions
                    }
                    continue;
                case "compacted":
                    if (stepDone) continue;
                    return Aged(new(AgentPhase.Compacting, null, at), now);
                default:
                    continue; // session_meta, turn_context, world_state, usage records
            }
        }
        return outputAt is not null ? Aged(new(AgentPhase.Thinking, null, outputAt), now) : new(AgentPhase.Idle, null, null);
    }

    /// A pending tool call by name, shared by both transcript formats. A
    /// subagent call reads as spawning briefly, then as waiting on it.
    public static PhaseReading Tool(string? name, DateTimeOffset? at, DateTimeOffset now)
    {
        var phase = ToolPhase(name ?? "");
        if (phase == AgentPhase.Spawning && at is { } since && now - since > SpawnShownFor)
        {
            phase = AgentPhase.WaitingOnAgents;
        }
        return new(phase, ShortName(name), at);
    }

    public static AgentPhase ToolPhase(string name)
    {
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return name.Contains("Browser", StringComparison.OrdinalIgnoreCase)
                || name.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                || name.Contains("computer", StringComparison.OrdinalIgnoreCase)
                || name.Contains("kimi-cu", StringComparison.OrdinalIgnoreCase)
                ? AgentPhase.Controlling
                : AgentPhase.Working;
        }
        return name switch
        {
            "Read" or "NotebookRead" or "read_file" or "read_item" or "list_items" or "view_image" => AgentPhase.Reading,
            "Grep" or "Glob" or "LS" or "ToolSearch" or "search_contents" or "grep" or "list_dir" => AgentPhase.Searching,
            "WebSearch" or "WebFetch" or "web_search" or "web_search_call" or "fetch" => AgentPhase.WebSearching,
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "apply_patch" or "write_file" or "append_to_file"
                or "edit_file" => AgentPhase.Writing,
            "Bash" or "PowerShell" or "BashOutput" or "KillShell" or "Monitor" or "exec" or "shell" or "exec_command"
                or "local_shell_call" or "js" => AgentPhase.Running,
            "Task" or "Agent" or "Workflow" or "spawn_agent" or "followup_task" => AgentPhase.Spawning,
            "wait_agent" or "wait" or "list_agents" or "TaskOutput" => AgentPhase.WaitingOnAgents,
            "SendMessage" or "send_message" => AgentPhase.Sending,
            "AskUserQuestion" or "ExitPlanMode" or "request_user_input" or "request_user_input_async" => AgentPhase.Asking,
            _ => AgentPhase.Working,
        };
    }

    /// An old "finished" is idle again, and an open phase whose transcript
    /// went quiet for too long is stale.
    private static PhaseReading Aged(PhaseReading reading, DateTimeOffset now)
    {
        if (reading.At is not { } at) return reading;
        var limit = reading.Phase switch
        {
            AgentPhase.Finished => FinishedFor,
            AgentPhase.Failed => FailedFor,
            AgentPhase.Thinking or AgentPhase.Streaming or AgentPhase.Compacting => ThinkingFor,
            _ => ToolFor,
        };
        return now - at > limit ? new(AgentPhase.Idle, null, at) : reading;
    }

    private static string? ShortName(string? name)
    {
        if (name is null) return null;
        var parts = name.Split("__");
        return parts.Length >= 3 ? parts[^1] : name;
    }

    private static bool IsInterrupt(JsonElement root)
    {
        if (Jsonl.GetObject(root, "message") is not { } message
            || !message.TryGetProperty("content", out var content))
        {
            return false;
        }
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString()!.StartsWith("[Request interrupted", StringComparison.Ordinal);
        if (content.ValueKind != JsonValueKind.Array) return false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && Jsonl.GetString(block, "type") == "text"
                && Jsonl.GetString(block, "text") is { } text
                && text.StartsWith("[Request interrupted", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// tool_use ids a user line answers, or null when it is a prompt.
    private static List<string>? ToolResultIds(JsonElement root)
    {
        if (Jsonl.GetObject(root, "message") is not { } message
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        List<string>? ids = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || Jsonl.GetString(block, "type") != "tool_result") continue;
            ids ??= new List<string>();
            if (Jsonl.GetString(block, "tool_use_id") is { } id) ids.Add(id);
        }
        return ids;
    }

    private static JsonElement? LastBlock(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        JsonElement? last = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object) last = block;
        }
        return last;
    }

    /// Complete lines from the end of a file its writer keeps appending to.
    /// A tail starting mid-file begins with a partial line, and the writer
    /// may be mid-line at the end; JSON parsing skips both.
    private static IReadOnlyList<string> ReadTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n');
        return start > 0 ? lines[1..] : lines;
    }
}
