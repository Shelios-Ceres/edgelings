using System.Text.Json;

namespace AgentIsland.Core;

/// 一份会话日志的“最近几次请求”。每家日志的写法不同，各自一个解析器，把行按先后喂进来：
/// 只认带用量的行和划定“这次请求什么时候发出”的几种行，其余行（对话内容、工具结果）在字节上就筛掉，
/// 不解码。读到的只有数字和时间。
internal abstract class MetricsParser
{
    /// 留最近这么多次请求，够缓存率（5 次）和速度（3 次够长的回复）挑。
    protected const int Keep = 12;

    protected readonly List<RequestSample> Samples = new();

    public DateTimeOffset? LastOutputAt { get; protected set; }

    /// 参与计算的请求，旧的在前。
    public virtual IReadOnlyList<RequestSample> Recent => Samples;

    public abstract void Offer(ReadOnlyMemory<byte> line);

    protected static JsonDocument? Parse(ReadOnlyMemory<byte> line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    protected static bool Has(ReadOnlyMemory<byte> line, ReadOnlySpan<byte> marker) => line.Span.IndexOf(marker) >= 0;

    protected static void Add(List<RequestSample> samples, RequestSample sample)
    {
        samples.Add(sample);
        if (samples.Count > Keep) samples.RemoveRange(0, samples.Count - Keep);
    }

    protected void Output(DateTimeOffset at)
    {
        if (LastOutputAt is not { } last || at > last) LastOutputAt = at;
    }
}

/// Claude Code：一个请求写成好几行 assistant（每个内容块一行，共用 message.id 和 requestId，
/// 用量和 stop_reason 在各行上相同）。缓存率按请求去重，同一请求留下写完的那一行。
/// 请求是什么时候发出的：优先取它前面最近一条 api-request 的时间（重试的取最后一次），
/// 没有就取前面最近一条 user（工具结果）。不取“上一条记录”：那常常是写在请求之后的 attachment，
/// 耗时会算成 0。subagent 的行（isSidechain）不是当前对话，跳过。
internal sealed class ClaudeMetricsParser : MetricsParser
{
    private DateTimeOffset? _apiAt;
    private DateTimeOffset? _userAt;

    public override void Offer(ReadOnlyMemory<byte> line)
    {
        if (!Has(line, "\"type\":\"assistant\""u8) && !Has(line, "\"type\":\"api-request\""u8) && !Has(line, "\"type\":\"user\""u8)) return;
        using var doc = Parse(line);
        if (doc is null) return;
        var root = doc.RootElement;
        if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } at) return;
        switch (Jsonl.GetString(root, "type"))
        {
            case "api-request":
                _apiAt = at;
                break;
            case "user":
                _userAt = at;
                break;
            case "assistant":
                Assistant(root, at);
                break;
        }
    }

    private void Assistant(JsonElement root, DateTimeOffset at)
    {
        if (Jsonl.GetBool(root, "isSidechain") == true) return;
        if (Jsonl.GetObject(root, "message") is not { } message || Jsonl.GetObject(message, "usage") is not { } usage) return;
        var model = Jsonl.GetString(message, "model") ?? "";
        if (model.Length == 0 || model.StartsWith("<synthetic>", StringComparison.Ordinal)
            || model.StartsWith("synthetic", StringComparison.OrdinalIgnoreCase)) return;
        var input = Jsonl.GetLong(usage, "input_tokens") ?? 0;
        var output = Jsonl.GetLong(usage, "output_tokens") ?? 0;
        var read = Jsonl.GetLong(usage, "cache_read_input_tokens");
        var creation = Jsonl.GetLong(usage, "cache_creation_input_tokens");
        if (input == 0 && output == 0 && (read ?? 0) == 0 && (creation ?? 0) == 0) return;
        var complete = Jsonl.GetString(message, "stop_reason") is { Length: > 0 };
        var messageId = Jsonl.GetString(message, "id");
        var requestId = Jsonl.GetString(root, "requestId");
        // 没有 requestId 的老日志：message id 本身就是同一个响应的所有行共用的，用它去重；两个都没有才按形状。
        var key = messageId is { Length: > 0 }
            ? requestId is { Length: > 0 } ? $"{messageId}:{requestId}" : $"{messageId}:"
            : $"shape:{at.ToUnixTimeMilliseconds()}|{input}|{output}|{read}|{creation}";
        var hit = read ?? 0;
        var total = input + hit + (creation ?? 0);
        Output(at);

        for (var i = Samples.Count - 1; i >= 0; i--)
        {
            if (Samples[i].Key != key) continue;
            // 同一个请求的后一行：时间往后推；用量取写完的那一行（都写完了取后一行，都没写完取输出多的）。
            var old = Samples[i];
            var merged = old with { End = at > old.End ? at : old.End };
            if (complete != old.Complete ? complete : output >= old.Output)
            {
                merged = merged with { CacheHit = hit, InputTotal = total, Output = output, HasCache = read is not null || creation is not null, Complete = complete };
            }
            Samples[i] = merged;
            return;
        }
        var start = _apiAt ?? _userAt;
        _apiAt = _userAt = null;
        Add(Samples, new RequestSample(key, start is { } s && s <= at ? s : null, at, hit, total, output,
            read is not null || creation is not null, complete));
    }
}

/// Codex：每个 API 调用写一条 token_usage_record（带 response_id，按它去重），input 已含缓存。
/// 请求是什么时候发出的：前面最近一次工具输出、用户消息、回合开始或上一条记录的时间。
/// 老版本只有 token_count（那时它写在工具输出之后，量不出耗时），没有 token_usage_record 时才用它，
/// 只算缓存率，不算速度；两种都有时只用 token_usage_record，不叠加。
internal sealed class CodexMetricsParser : MetricsParser
{
    private readonly List<RequestSample> _legacy = new();
    private DateTimeOffset? _boundary;
    private bool _sawRecord;
    private long _legacyTotal = -1;

    public override IReadOnlyList<RequestSample> Recent => _sawRecord ? Samples : _legacy;

    public override void Offer(ReadOnlyMemory<byte> line)
    {
        if (!Has(line, "\"token_usage_record\""u8) && !Has(line, "\"token_count\""u8) && !Has(line, "\"response_item\""u8)
            && !Has(line, "\"task_started\""u8)) return;
        using var doc = Parse(line);
        if (doc is null) return;
        var root = doc.RootElement;
        if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } at) return;
        if (Jsonl.GetObject(root, "payload") is not { } payload) return;
        var payloadType = Jsonl.GetString(payload, "type") ?? "";
        switch (Jsonl.GetString(root, "type"))
        {
            case "token_usage_record":
                Record(payload, at);
                break;
            case "event_msg" when payloadType == "token_count":
                Legacy(payload, at);
                break;
            case "event_msg" when payloadType == "task_started":
                _boundary = at;
                break;
            case "response_item":
                Item(payload, payloadType, at);
                break;
        }
    }

    private void Item(JsonElement payload, string type, DateTimeOffset at)
    {
        var role = Jsonl.GetString(payload, "role");
        if (type.EndsWith("_output", StringComparison.Ordinal) || type == "message" && role is "user" or "developer")
        {
            _boundary = at;
            return;
        }
        if (type is "reasoning" or "function_call" or "custom_tool_call" or "local_shell_call" or "web_search_call" or "tool_search_call"
            || type == "message" && role == "assistant")
        {
            Output(at);
        }
    }

    private void Record(JsonElement payload, DateTimeOffset at)
    {
        if (Jsonl.GetObject(payload, "usage") is not { } usage) return;
        _sawRecord = true;
        var key = "resp:" + (Jsonl.GetString(payload, "response_id") ?? at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Samples.Any(sample => sample.Key == key)) return;
        var sample = Usage(key, usage, at, _boundary is { } start && start < at ? start : null);
        _boundary = at;
        Output(at);
        if (sample is not null) Add(Samples, sample);
    }

    private void Legacy(JsonElement payload, DateTimeOffset at)
    {
        if (Jsonl.GetObject(payload, "info") is not { } info || Jsonl.GetObject(info, "last_token_usage") is not { } usage) return;
        // 同一个累计值再写一遍（重试、取消后的重发）不是新请求。
        if (Jsonl.GetObject(info, "total_token_usage") is { } totals && Jsonl.GetLong(totals, "total_tokens") is { } running)
        {
            if (running == _legacyTotal) return;
            _legacyTotal = running;
        }
        Output(at);
        if (Usage($"count:{at.ToUnixTimeMilliseconds()}", usage, at, null) is { } sample) Add(_legacy, sample);
    }

    private static RequestSample? Usage(string key, JsonElement usage, DateTimeOffset at, DateTimeOffset? start)
    {
        var input = Math.Max(0, Jsonl.GetLong(usage, "input_tokens") ?? 0);
        var cached = Jsonl.GetLong(usage, "cached_input_tokens");
        var output = Math.Max(0, Jsonl.GetLong(usage, "output_tokens") ?? 0);
        if (input == 0 && output == 0) return null;
        return new RequestSample(key, start, at, Math.Clamp(cached ?? 0, 0, input), input, output, cached is not null, Complete: true);
    }
}

/// Kimi Code：每个模型请求先写一条 llm.request，写完回复再写 usage.record（毫秒时间戳，
/// 用量按 inputOther / inputCacheRead / inputCacheCreation 拆开）。耗时就是这两条的时间差，按 agentId 配对。
internal sealed class KimiMetricsParser : MetricsParser
{
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);

    public override void Offer(ReadOnlyMemory<byte> line)
    {
        if (!Has(line, "\"usage.record\""u8) && !Has(line, "\"llm.request\""u8)) return;
        using var doc = Parse(line);
        if (doc is null) return;
        var root = doc.RootElement;
        if (Jsonl.GetDouble(root, "time") is not { } ms || ms <= 0 || ms > 253_402_300_799_999) return;
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        var agent = Jsonl.GetString(root, "agentId") ?? "";
        switch (Jsonl.GetString(root, "type"))
        {
            case "llm.request":
                _pending[agent] = at;
                break;
            case "usage.record" when Jsonl.GetObject(root, "usage") is { } usage:
                Record(agent, usage, at);
                break;
        }
    }

    private void Record(string agent, JsonElement usage, DateTimeOffset at)
    {
        var other = Jsonl.GetLong(usage, "inputOther") ?? 0;
        var output = Jsonl.GetLong(usage, "output") ?? 0;
        var read = Jsonl.GetLong(usage, "inputCacheRead");
        var creation = Jsonl.GetLong(usage, "inputCacheCreation");
        var hadStart = _pending.Remove(agent, out var start);
        if (other == 0 && output == 0 && (read ?? 0) == 0 && (creation ?? 0) == 0) return;
        Output(at);
        Add(Samples, new RequestSample($"{agent}|{at.ToUnixTimeMilliseconds()}", hadStart && start < at ? start : null, at,
            read ?? 0, other + (read ?? 0) + (creation ?? 0), output, read is not null || creation is not null, Complete: true));
    }
}

/// 一份会话日志的增量读取：第一次只读末尾一段（请求不够就放宽到最多 4 MB），之后只读新追加的字节，
/// 半行留到下次。文件变短（被重写）或一下子长了很多（会话闲置后又活了）就重新从末尾读。
/// 比 maxLine 更长的行直接跳过：它不可能是用量记录，不为它占内存。
internal sealed class SessionMetricsFile
{
    private static readonly long[] Seeds = { 256 << 10, 1 << 20, 4 << 20 };
    private const int MaxLine = 1 << 20;
    private const long MaxCatchUp = 4 << 20;

    /// 读到这么多次请求就够了，不再放宽。
    private const int Enough = 6;

    private readonly string _path;
    private readonly Func<MetricsParser> _make;
    private MetricsParser? _parser;
    private long _offset;

    public SessionMetricsFile(string path, Func<MetricsParser> make)
    {
        _path = path;
        _make = make;
    }

    /// 读到新内容后的读数；文件读不了返回 null。
    public SessionMetrics? Update()
    {
        try
        {
            using var stream = SharedFile.Open(_path);
            var length = stream.Length;
            if (_parser is null || length < _offset || length - _offset > MaxCatchUp) Seed(stream, length);
            else Consume(stream, _offset, length, skipFirst: false);
            return SessionMetrics.Compute(_parser!.Recent, _parser.LastOutputAt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Seed(FileStream stream, long length)
    {
        foreach (var seed in Seeds)
        {
            _parser = _make();
            var start = Math.Max(0, length - seed);
            Consume(stream, start, length, skipFirst: start > 0);
            if (start == 0 || _parser.Recent.Count >= Enough) break;
        }
    }

    private void Consume(FileStream stream, long start, long end, bool skipFirst)
    {
        var data = new byte[end - start];
        stream.Seek(start, SeekOrigin.Begin);
        var got = 0;
        while (got < data.Length)
        {
            var read = stream.Read(data, got, data.Length - got);
            if (read <= 0) break;
            got += read;
        }
        var from = 0;
        if (skipFirst)
        {
            var newline = data.AsSpan(0, got).IndexOf((byte)'\n');
            if (newline < 0) { _offset = start; return; }
            from = newline + 1;
        }
        var last = data.AsSpan(0, got).LastIndexOf((byte)'\n');
        if (last < from) { _offset = start + from; return; }
        while (from <= last)
        {
            var newline = data.AsSpan(from, last + 1 - from).IndexOf((byte)'\n');
            var lineEnd = from + newline;
            var length = lineEnd - from;
            if (length > 0 && data[lineEnd - 1] == (byte)'\r') length--;
            if (length > 0 && length <= MaxLine) _parser!.Offer(new ReadOnlyMemory<byte>(data, from, length));
            from = lineEnd + 1;
        }
        _offset = start + last + 1;
    }
}
