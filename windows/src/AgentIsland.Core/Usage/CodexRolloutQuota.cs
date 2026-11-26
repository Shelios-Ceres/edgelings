using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Codex 的额度，读 Codex 自己写进 rollout 的 token_count 事件里的
/// payload.rate_limits。不联网、不起进程、不读任何 token：官方程序落了什么，
/// 这里就显示什么。代价是不用 Codex 时额度不会更新，卡上会照常标出读数的时间。
///
/// 多账号：每个 rollout 的 session_meta 带 creator_account_id 和
/// creator_user_id（同一个 Team workspace 的成员 account 相同、user 不同），
/// 只采用和当前登录一致的 rollout。没有 creator_account_id 的旧 rollout 视为匹配。
internal static class CodexRolloutQuota
{
    /// 当前登录：账号，以及登录串里有 user 段时的用户。
    internal readonly record struct Identity(string Account, string? User);

    /// 最多读几个“账号匹配”的 rollout 的尾部（按修改时间从新到旧）。
    /// 账号对不上的文件只读第一行，不占名额。
    internal const int MaxFiles = 8;

    private const int ChunkBytes = 64 * 1024;
    /// 单行上限：更长的行不可能是 token_count，整行跳过。
    private const int MaxLineBytes = 512 * 1024;
    /// 一个文件从末尾往回最多读这么多。
    private const int MaxTailBytes = 4 * 1024 * 1024;

    private static readonly byte[] TokenCountMark = "\"token_count\""u8.ToArray();
    private static readonly byte[] RateLimitsMark = "\"rate_limits\":{"u8.ToArray();

    // 第一行（session_meta）写下就不变：按路径记住，不再重读。
    private static readonly ConcurrentDictionary<string, (string? Account, string? User)> Creators = new(StringComparer.Ordinal);
    // “这个文件里没有读数”：文件没变（长度和修改时间都一样）就不再扫。
    private static readonly ConcurrentDictionary<string, (long Length, long Ticks)> NoReading = new(StringComparer.Ordinal);

    /// 一次取数：身份已知按身份对应；auth.json 在但读不出账号（API key、读不到）
    /// 时不猜是谁的，显示没有数据；完全没有 auth.json 才退回最新的任意账号，
    /// 那时仍没有读数就沿用“未找到 Codex 登录”，登录入口照旧。
    internal static AppUsage Fetch(IReadOnlyList<string> roots, string? login, bool authFileExists)
    {
        var identity = CodexCredentials.IdentityOf(login);
        if (identity is not null) return Read(roots, identity) ?? AppUsage.NoData;
        if (authFileExists) return AppUsage.NoData;
        return Read(roots, null) ?? AppUsage.ErrorPair("no codex auth");
    }

    /// 取最新的一条带额度的读数；没有任何可用读数时返回 null。
    /// who 为 null 表示不筛账号。
    internal static AppUsage? Read(IReadOnlyList<string> roots, Identity? who)
    {
        var stamped = new List<(string Path, DateTime Modified, long Length)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            List<string> paths;
            try { paths = SessionScanner.SafeEnumerateFiles(root, "*.jsonl"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in paths)
            {
                try
                {
                    var info = new FileInfo(path);
                    stamped.Add((path, info.LastWriteTimeUtc, info.Length));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        stamped.Sort((a, b) => b.Modified.CompareTo(a.Modified));

        AppUsage? best = null;
        DateTimeOffset bestAt = default;
        var read = 0;
        foreach (var (path, modified, length) in stamped)
        {
            // 文件的修改时间不早于它最后一个事件：比已有读数还旧的文件不会有更新的事件。
            if (best is not null && new DateTimeOffset(modified, TimeSpan.Zero) < bestAt) break;
            if (read >= MaxFiles) break;
            try
            {
                if (who is { } want && !Matches(path, want)) continue;
                read++;
                var stamp = (length, modified.Ticks);
                if (NoReading.TryGetValue(path, out var seen) && seen == stamp) continue;
                if (LastReading(path) is not { } reading)
                {
                    NoReading[path] = stamp;
                    continue;
                }
                NoReading.TryRemove(path, out _);
                if (best is null || reading.At > bestAt)
                {
                    best = reading.Usage;
                    bestAt = reading.At;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return best;
    }

    /// 这个 rollout 属于当前登录吗。没有 creator_account_id 的旧文件视为匹配；
    /// 登录没有 user 段、或文件没写 user 时，只按 account。
    private static bool Matches(string path, Identity who)
    {
        var (account, user) = Creator(path);
        if (account is null) return true;
        if (account != who.Account) return false;
        return who.User is null || user is null || user == who.User;
    }

    private static (string? Account, string? User) Creator(string path)
    {
        if (Creators.TryGetValue(path, out var known)) return known;
        using var stream = SharedFile.Open(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024);
        if (reader.ReadLine() is not { Length: > 0 } first) return (null, null);
        using var doc = Jsonl.TryParseLine(first);
        if (doc is null) return (null, null);
        (string?, string?) found = (null, null);
        if (Jsonl.GetString(doc.RootElement, "type") == "session_meta"
            && Jsonl.GetObject(doc.RootElement, "payload") is { } meta)
        {
            found = (Jsonl.GetString(meta, "creator_account_id"), Jsonl.GetString(meta, "creator_user_id"));
        }
        Creators[path] = found;
        return found;
    }

    /// 文件里最后一条带额度的 token_count：从末尾往前分块读，一行一行在字节上筛，
    /// 只有可能是读数的行才转成字符串。缓冲区来自池子，读一个文件几乎不分配。
    private static (AppUsage Usage, DateTimeOffset At)? LastReading(string path)
    {
        using var stream = SharedFile.Open(path);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes + MaxLineBytes);
        try
        {
            var pos = stream.Length;
            long scanned = 0;
            var carry = 0;           // buffer[0..carry) 是上一轮留下的、还没见到行首的半行
            var skipping = false;    // 正在丢弃一个超长行的剩余部分
            while (pos > 0 && scanned < MaxTailBytes)
            {
                var size = (int)Math.Min(ChunkBytes, pos);
                pos -= size;
                scanned += size;
                if (carry > 0) buffer.AsSpan(0, carry).CopyTo(buffer.AsSpan(size));
                stream.Seek(pos, SeekOrigin.Begin);
                var got = 0;
                while (got < size)
                {
                    var n = stream.Read(buffer, got, size - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < size) return null; // 文件在读的时候缩短了
                var total = size + carry;

                var end = total;
                for (var i = total - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    if (skipping) skipping = false;
                    else if (Candidate(buffer.AsSpan(i + 1, end - i - 1)) is { } found) return found;
                    end = i;
                }
                if (pos == 0)
                {
                    // 文件开头那一行。
                    if (!skipping && Candidate(buffer.AsSpan(0, end)) is { } first) return first;
                    return null;
                }
                carry = end;
                if (carry > MaxLineBytes)
                {
                    // 超长行不可能是读数：丢掉，并跳过它在更早块里的剩余部分。
                    carry = 0;
                    skipping = true;
                }
                else if (skipping && end == total)
                {
                    carry = 0; // 整块都在超长行里
                }
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (AppUsage Usage, DateTimeOffset At)? Candidate(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf(RateLimitsMark) < 0 || line.IndexOf(TokenCountMark) < 0) return null;
        return Parse(Encoding.UTF8.GetString(line));
    }

    /// 一行事件 → 读数；不是带额度的 token_count、或写了一半的行、或时间戳超出范围，返回 null。
    internal static (AppUsage Usage, DateTimeOffset At)? Parse(string line)
    {
        using var doc = Jsonl.TryParseLine(line);
        if (doc is null) return null;
        var root = doc.RootElement;
        if (Jsonl.GetString(root, "type") != "event_msg"
            || Jsonl.GetObject(root, "payload") is not { } payload
            || Jsonl.GetString(payload, "type") != "token_count"
            || Jsonl.GetObject(payload, "rate_limits") is not { } limits) return null;
        // 其他 limit_id（按模型的附加额度）不是主额度。
        if (Jsonl.GetString(limits, "limit_id") is { Length: > 0 } id && id != "codex") return null;
        if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } at) return null;
        try
        {
            var primary = Window(Jsonl.GetObject(limits, "primary"));
            var secondary = Window(Jsonl.GetObject(limits, "secondary"));
            if (primary.HasError && secondary.HasError) return null;
            return (new AppUsage(primary, secondary, Jsonl.GetString(limits, "plan_type")) { ObservedAt = at }, at);
        }
        catch (ArgumentOutOfRangeException)
        {
            // resets_at 超出可表示的时间：整行当坏行。
            return null;
        }
    }

    internal static WindowUsage Window(JsonElement? obj)
    {
        if (obj is not { } window) return WindowUsage.Unknown;
        if (Jsonl.GetDouble(window, "used_percent") is not { } used) return WindowUsage.Unknown;
        DateTimeOffset? resetAt = Jsonl.GetDouble(window, "resets_at") is { } epoch and > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)epoch)
            : null;
        double? period = Jsonl.GetDouble(window, "window_minutes") is { } minutes and > 0 ? minutes * 60 : null;
        return new WindowUsage(Math.Clamp(used / 100, 0, 1), resetAt, null, period) { Observed = true };
    }
}
