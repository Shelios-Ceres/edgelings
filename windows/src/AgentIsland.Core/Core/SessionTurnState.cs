using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentIsland.Core;

/// How a turn went wrong, as the agent wrote it down.
public enum TurnTrouble
{
    None,
    /// The service could not be reached: the connection dropped or timed out.
    Network,
    RateLimited,
    AuthRequired,
    /// Any other error.
    Failed,
}

/// Idle: the turn is over with nothing waiting on the user — it was
/// interrupted, or a local command answered itself. Trouble: the error a
/// finished turn ended on. RetryingUntil: the agent is retrying a lost
/// connection, as of its latest record.
public readonly record struct SessionTurnStatus(
    bool IsDone, string? Key, DateTimeOffset? ActivityDate, bool Idle = false,
    TurnTrouble Trouble = TurnTrouble.None, DateTimeOffset? RetryingUntil = null);

/// Classifies the tail of a transcript: is the latest turn finished (the user
/// is "up"), and which event identifies that turn. Direct port of the macOS
/// SessionTurnState — the turn key + activity date drive alarm dedup and the
/// bookkeeping-grace logic in SessionScanner.
public static class SessionTurnState
{
    /// Agents retry a failed request on their own, and single retries recover
    /// within seconds (every one in two months of real logs did), so only a
    /// third straight failure counts, for as long as the next try could take.
    private const int RetriesBeforeTrouble = 3;
    private static readonly TimeSpan RetryGrace = TimeSpan.FromSeconds(60);

    public static SessionTurnStatus Claude(IReadOnlyList<string> lines) =>
        ClaudeCore(lines, sidechainIsTheConversation: false);

    /// For agent transcripts, where every line is a sidechain by definition.
    public static SessionTurnStatus ClaudeAgent(IReadOnlyList<string> lines) =>
        ClaudeCore(lines, sidechainIsTheConversation: true);

    private static SessionTurnStatus ClaudeCore(IReadOnlyList<string> lines, bool sidechainIsTheConversation)
    {
        DateTimeOffset? retryingUntil = null;
        var retrySeen = false;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            // Older Claude Code interleaves subagent traffic into the main
            // transcript marked isSidechain — a subagent's end_turn there is
            // not the user's turn and must not classify the main session.
            if (!sidechainIsTheConversation && Jsonl.GetBool(root, "isSidechain") == true) continue;
            var type = Jsonl.GetString(root, "type");
            switch (type)
            {
                case "system":
                    // Claude Code records each retry of a failed request; only
                    // the newest, written after the conversation's latest
                    // line, speaks for the turn.
                    if (!retrySeen && Jsonl.GetString(root, "subtype") == "api_error")
                    {
                        retrySeen = true;
                        retryingUntil = ConnectionRetryUntil(root);
                    }
                    continue;
                case "assistant":
                {
                    var message = Jsonl.GetObject(root, "message");
                    var stop = message is { } m ? Jsonl.GetString(m, "stop_reason") : null;
                    // Claude Code writes rate-limit / API-error lines with the
                    // SAME envelope as a finished turn (type:assistant,
                    // stop_reason:"stop_sequence") but flags them
                    // isApiErrorMessage:true (e.g. "You've hit your session
                    // limit · resets 2:20am"). The turn is over, but on an
                    // error, so it must never read as plain "your turn".
                    if (Jsonl.GetBool(root, "isApiErrorMessage") == true)
                        return new SessionTurnStatus(true, Key(root, lines[i]), Date(root), Trouble: TroubleFrom(MessageText(message)));
                    var isDone = stop is "end_turn" or "stop_sequence" or "stop";
                    return new SessionTurnStatus(isDone, Key(root, lines[i]), Date(root), RetryingUntil: isDone ? null : retryingUntil);
                }
                case "user":
                    // Agent runs usually end on the final tool result (marked
                    // toolEndsTurn) rather than an assistant stop; without
                    // this a finished agent never reads as done.
                    if (sidechainIsTheConversation && Jsonl.GetBool(root, "toolEndsTurn") == true)
                        return new SessionTurnStatus(true, Key(root, lines[i]), Date(root));
                    // Image captions, command caveats and compaction summaries
                    // are not prompts.
                    if (Jsonl.GetBool(root, "isMeta") == true || Jsonl.GetBool(root, "isCompactSummary") == true) continue;
                    // An interrupt, or a local command answering itself
                    // (/compact, /model): over, and the model owes nothing.
                    if (AgentPhaseClassifier.IsInterrupt(root) || AgentPhaseClassifier.IsLocalCommandOutput(root))
                        return new SessionTurnStatus(false, Key(root, lines[i]), Date(root), Idle: true);
                    return new SessionTurnStatus(false, Key(root, lines[i]), Date(root), RetryingUntil: retryingUntil);
                default:
                    continue;
            }
        }
        return new SessionTurnStatus(false, null, null);
    }

    /// An open turn is dated by its newest line, not by its start, or every
    /// turn longer than the stall threshold would read as stalled. Codex
    /// stamps every line; the file's write time is no substitute, since Codex
    /// Desktop keeps the rollout open and Windows leaves that time at creation
    /// until the handle closes.
    public static SessionTurnStatus Codex(IReadOnlyList<string> lines)
    {
        DateTimeOffset? newest = null;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            newest ??= Date(root);
            var type = Jsonl.GetString(root, "type");
            var payload = Jsonl.GetObject(root, "payload");
            var payloadType = payload is { } p ? Jsonl.GetString(p, "type") : null;
            if (type == "event_msg")
            {
                if (IsCodexUserOrStart(payloadType))
                    return new SessionTurnStatus(false, Key(root, lines[i]), newest);
                if (payloadType is "task_complete" or "turn/completed")
                {
                    var trouble = ErrorText(payload) is { } error ? TroubleFrom(error) : TurnTrouble.None;
                    return new SessionTurnStatus(true, Key(root, lines[i]), Date(root), Trouble: trouble);
                }
                if (payloadType == "turn_aborted")
                    return new SessionTurnStatus(false, Key(root, lines[i]), Date(root), Idle: true);
            }
            if (type == "response_item"
                && payloadType == "message"
                && payload is { } pm
                && Jsonl.GetString(pm, "role") == "user")
            {
                return new SessionTurnStatus(false, Key(root, lines[i]), newest);
            }
        }
        return new SessionTurnStatus(false, null, newest);
    }

    /// Grok appends one JSON object per session event to `updates.jsonl` with
    /// an explicit `params.update.sessionUpdate` discriminator — and unlike
    /// Claude it names the turn boundary outright: `turn_completed`. Any part
    /// of the conversation written after it (tool calls, streaming chunks,
    /// retry_state) means the turn is open again; the bookkeeping Grok writes
    /// once a turn is over (memory dreams, background-task notices, rewind
    /// markers) does not. A run of retry_state updates at the end is Grok
    /// backing off a failing API. Timestamps here are unix SECONDS, not the
    /// milliseconds Claude Desktop uses.
    public static SessionTurnStatus Grok(IReadOnlyList<string> lines)
    {
        SessionTurnStatus? newest = null;
        var retries = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            if (Jsonl.GetObject(doc.RootElement, "params") is not { } parameters) continue;
            if (Jsonl.GetObject(parameters, "update") is not { } update) continue;
            var kind = Jsonl.GetString(update, "sessionUpdate");
            if (kind is not ("turn_completed" or "user_message_chunk" or "agent_thought_chunk"
                or "agent_message_chunk" or "tool_call" or "tool_call_update" or "retry_state"))
            {
                continue;
            }
            if (newest is null)
            {
                var stamp = UnixSeconds(Jsonl.GetDouble(doc.RootElement, "timestamp"));
                var seconds = stamp is { } s
                    ? s.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "";
                newest = new SessionTurnStatus(kind == "turn_completed", seconds + ":" + kind, stamp);
            }
            if (kind != "retry_state") break;
            retries++;
        }
        if (newest is not { } status) return new SessionTurnStatus(false, null, null);
        return retries >= RetriesBeforeTrouble && status.ActivityDate is { } at
            ? status with { RetryingUntil = at + RetryGrace }
            : status;
    }

    /// What an agent's error text says went wrong.
    internal static TurnTrouble TroubleFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TurnTrouble.Failed;
        var lower = text.ToLowerInvariant();
        if (Has(lower, "session limit", "usage limit", "usage_limit", "rate limit", "rate_limit", "quota")
            || Regex.IsMatch(lower, @"\b429\b"))
        {
            return TurnTrouble.RateLimited;
        }
        if (Has(lower, "not logged in", "/login", "authenticat", "unauthorized", "invalid api key")
            || Regex.IsMatch(lower, @"\b401\b"))
        {
            return TurnTrouble.AuthRequired;
        }
        if (Has(lower, "connection error", "connection dropped", "connection reset", "connection refused",
                "stream disconnected", "error sending request", "econnreset", "econnrefused", "etimedout",
                "enotfound", "eai_again", "socket hang up", "timed out", "timeout", "network", "dns",
                "apiconnectionerror"))
        {
            return TurnTrouble.Network;
        }
        return TurnTrouble.Failed;
    }

    private static bool Has(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    /// Until when a retry record keeps the session in network trouble; null
    /// for an early attempt, or an HTTP error rather than a lost connection.
    private static DateTimeOffset? ConnectionRetryUntil(JsonElement root)
    {
        if (Jsonl.GetDouble(root, "retryAttempt") is not { } attempt || attempt < RetriesBeforeTrouble) return null;
        if (Jsonl.GetObject(root, "error") is not { } error || TroubleFrom(error.GetRawText()) != TurnTrouble.Network) return null;
        if (Date(root) is not { } at) return null;
        var wait = Jsonl.GetDouble(root, "retryInMs") is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        return at + wait + RetryGrace;
    }

    private static string? MessageText(JsonElement? message)
    {
        if (message is not { } m || !m.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && Jsonl.GetString(block, "text") is { } part)
                text.Append(part).Append(' ');
        }
        return text.ToString();
    }

    /// A Codex completion's error, or null when the turn succeeded.
    private static string? ErrorText(JsonElement? payload)
    {
        if (payload is not { } p || !p.TryGetProperty("error", out var error)) return null;
        return error.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => error.GetString() is { Length: > 0 } text ? text : null,
            _ => error.GetRawText(),
        };
    }

    /// For sessions whose transcript has no verified turn boundary (Gemini's
    /// $set checkpoint stream): never claims "done", so the engine derives
    /// working/idle purely from file recency and can never raise a false
    /// "your turn" alarm on a format we have not verified. The restraint is
    /// deliberate — do not turn this into a heuristic without real samples.
    /// Cursor: one bubble per message, `type` 1 = user, 2 = assistant
    /// (verified against live conversation text, 2026-08-08). The assistant
    /// having spoken last is the same turn boundary Claude's stop_reason
    /// gives us; the caller adds the quiet gap that separates "still
    /// streaming" from "finished".
    public static SessionTurnStatus Cursor(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return new SessionTurnStatus(false, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(lines[^1]);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return new SessionTurnStatus(false, null, null);
            }
            var type = root.TryGetProperty("type", out var t) && t.TryGetInt32(out var parsed) ? parsed : 0;
            string? key = root.TryGetProperty("bubbleId", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String
                ? "cursor:" + b.GetString()
                : null;
            // Cursor writes bubble timestamps as an ISO-8601 string with
            // milliseconds ("2026-08-08T19:46:26.661Z") — verified against
            // the real store. Numbers are handled defensively.
            DateTimeOffset? stamp = null;
            if (root.TryGetProperty("createdAt", out var raw))
            {
                if (raw.ValueKind == System.Text.Json.JsonValueKind.String
                    && DateTimeOffset.TryParse(raw.GetString(), out var iso))
                {
                    stamp = iso;
                }
                else if (raw.ValueKind == System.Text.Json.JsonValueKind.Number && raw.TryGetInt64(out var epoch))
                {
                    stamp = epoch > 100_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                        : DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
            }
            // A tool bubble is the assistant's too, but its tool may still be
            // running (or waiting to be allowed to): that is not an answer.
            var toolRunning = root.TryGetProperty("toolFormerData", out var tool)
                && tool.ValueKind == System.Text.Json.JsonValueKind.Object
                && Jsonl.GetString(tool, "status") is { } status
                && status is not ("completed" or "cancelled" or "error");
            return new SessionTurnStatus(type == 2 && !toolRunning, key, stamp);
        }
        catch (Exception)
        {
            return new SessionTurnStatus(false, null, null);
        }
    }

    public static SessionTurnStatus MtimeOnly(IReadOnlyList<string> lines) =>
        new(false, null, null);

    /// Antigravity transcript records are `{step_index, source, type,
    /// status, created_at, content, tool_calls…}` where source is
    /// USER_EXPLICIT / SYSTEM / MODEL. Tool steps are ALSO source:MODEL
    /// (verified on real transcripts, 2026-08-08), so "MODEL last" alone
    /// false-fires mid-run — the agent has spoken only when the last MODEL
    /// step is a PLANNER_RESPONSE with real content and no tool_calls.
    /// `status` is a per-step marker appended on completion and carries no
    /// turn state; the caller's quiet gap still backstops a run that ends
    /// on a tool call.
    public static SessionTurnStatus Antigravity(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (Jsonl.GetString(root, "source") is not { } source) continue;

            DateTimeOffset? stamp = null;
            if (root.TryGetProperty("created_at", out var created))
            {
                if (created.ValueKind == System.Text.Json.JsonValueKind.String
                    && DateTimeOffset.TryParse(created.GetString(), out var iso))
                {
                    stamp = iso;
                }
                else if (created.ValueKind == System.Text.Json.JsonValueKind.Number
                    && created.TryGetDouble(out var seconds))
                {
                    stamp = seconds > 100_000_000_000d
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)seconds)
                        : DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                }
            }
            string? key = root.TryGetProperty("step_index", out var step)
                && step.TryGetInt32(out var index)
                ? "ag:" + index
                : null;

            switch (source)
            {
                case "MODEL":
                    var spoke = Jsonl.GetString(root, "type") == "PLANNER_RESPONSE"
                        && !AntigravityHasToolCalls(root)
                        && Jsonl.GetString(root, "content") is { Length: > 0 };
                    return new SessionTurnStatus(spoke, key, stamp);
                case "USER_EXPLICIT":
                    return new SessionTurnStatus(false, key, stamp);
                default:
                    continue;
            }
        }
        return new SessionTurnStatus(false, null, null);
    }

    private static bool AntigravityHasToolCalls(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("tool_calls", out var calls)) return false;
        return calls.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Null => false,
            System.Text.Json.JsonValueKind.Array => calls.GetArrayLength() > 0,
            _ => true,
        };
    }

    private static bool IsCodexUserOrStart(string? type)
    {
        if (type is null) return false;
        return type == "task_started"
            || type == "turn/started"
            || type == "user_message"
            || type.EndsWith("/task_started", StringComparison.Ordinal)
            || type.EndsWith("/user_message", StringComparison.Ordinal);
    }

    private static string Key(JsonElement root, string line)
    {
        if (Jsonl.GetString(root, "uuid") is { Length: > 0 } uuid) return uuid;
        if (Jsonl.GetString(root, "id") is { Length: > 0 } id) return id;
        if (Jsonl.GetObject(root, "payload") is { } payload)
        {
            foreach (var field in new[] { "turn_id", "id", "item_id", "call_id" })
            {
                if (Jsonl.GetString(payload, field) is { Length: > 0 } value) return value;
            }
        }
        var bytes = Encoding.UTF8.GetBytes(line);
        var encoded = Convert.ToBase64String(bytes);
        return encoded.Length <= 160 ? encoded : encoded[..160];
    }

    private static DateTimeOffset? Date(JsonElement root)
    {
        if (Jsonl.GetString(root, "timestamp") is { } timestamp
            && Jsonl.ParseIso8601(timestamp) is { } parsed)
        {
            return parsed;
        }
        if (Jsonl.GetObject(root, "payload") is { } payload)
        {
            foreach (var field in new[] { "completed_at", "started_at" })
            {
                if (UnixSeconds(Jsonl.GetDouble(payload, field)) is { } parsedSeconds)
                    return parsedSeconds;
            }
        }
        return null;
    }

    /// Kimi Code names its boundaries in wire.jsonl: agent.turn.ended,
    /// turn.ended and prompt.completed close a turn (reason completed or
    /// cancelled). Bookkeeping lands after the close — token counts, config
    /// and file-history checkpoints, the final messages being appended — so
    /// it is walked past; a user message or anything else reopens the turn.
    /// An open turn ending on turn.step.retrying (behind the request that
    /// retries it) is Kimi backing off a failing API. `time` is unix
    /// milliseconds.
    public static SessionTurnStatus Kimi(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (Jsonl.GetString(root, "type") is not { } type) continue;
            var stamp = UnixMilliseconds(Jsonl.GetDouble(root, "time"));
            var key = Stamp(stamp) + ":" + type;
            switch (type)
            {
                case "agent.turn.ended" or "turn.ended" or "prompt.completed" or "prompt.aborted":
                    return new SessionTurnStatus(true, key, stamp);
                case "agent.message.appended" when !IsKimiUserMessage(root):
                case "token_counting.turn_recorded" or "token_counting.measured" or "usage.record" or "config.update"
                    or "file_history.tracked" or "file_history.checkpoint" or "task.terminated" or "task.waitDelivered":
                    continue;
                default:
                    return new SessionTurnStatus(false, key, stamp, RetryingUntil: KimiRetryingUntil(lines, i));
            }
        }
        return new SessionTurnStatus(false, null, null);
    }

    /// The newest retry at the end of an open Kimi turn, past the requests
    /// that retry it: until when it keeps the session in network trouble,
    /// or null for an early attempt or an error that is not the connection.
    private static DateTimeOffset? KimiRetryingUntil(IReadOnlyList<string> lines, int from)
    {
        for (var i = from; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            switch (Jsonl.GetString(root, "type"))
            {
                case "llm.request" or "token_counting.measured" or "usage.record":
                    continue;
                case "turn.step.retrying":
                    if (Jsonl.GetDouble(root, "failedAttempt") is not { } failed || failed < RetriesBeforeTrouble) return null;
                    var error = $"{Jsonl.GetString(root, "errorName")} {Jsonl.GetString(root, "errorMessage")}";
                    if (TroubleFrom(error) != TurnTrouble.Network) return null;
                    if (UnixMilliseconds(Jsonl.GetDouble(root, "time")) is not { } at) return null;
                    var wait = Jsonl.GetDouble(root, "delayMs") is { } ms and > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
                    return at + wait + RetryGrace;
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool IsKimiUserMessage(System.Text.Json.JsonElement root) =>
        Jsonl.GetObject(root, "message") is { } envelope
        && Jsonl.GetObject(envelope, "message") is { } message
        && Jsonl.GetString(message, "role") == "user";

    /// DeepSeek Harness brackets every turn with turn/start … turn/end;
    /// after the end only session bookkeeping is written, and a spliced inbox
    /// message or turn/start opens the next one. `time` is unix milliseconds.
    public static SessionTurnStatus DeepSeek(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (Jsonl.GetString(root, "type") is not { } type) continue;
            var stamp = UnixMilliseconds(Jsonl.GetDouble(root, "time"));
            var key = Stamp(stamp) + ":" + type;
            switch (type)
            {
                case "turn/end":
                    return new SessionTurnStatus(true, key, stamp);
                case "session/end-seed" or "session/title" or "workspace/changes"
                    or "session-log-deepseek/delivery-accepted":
                    continue;
                default:
                    return new SessionTurnStatus(false, key, stamp);
            }
        }
        return new SessionTurnStatus(false, null, null);
    }

    private static string Stamp(DateTimeOffset? stamp) => stamp is { } s
        ? s.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "";

    private static DateTimeOffset? UnixMilliseconds(double? ms) =>
        ms is { } value and >= -62_135_596_800_000 and <= 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value)
            : null;

    /// FromUnixTimeMilliseconds throws on out-of-range input; a corrupt or
    /// foreign-unit timestamp must not fault the scan, so anything outside
    /// the representable range reads as "no date" instead.
    private static DateTimeOffset? UnixSeconds(double? seconds)
    {
        if (seconds is not { } value) return null;
        var ms = value * 1000;
        if (ms is >= -62_135_596_800_000 and <= 253_402_300_799_999)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        return null;
    }
}
