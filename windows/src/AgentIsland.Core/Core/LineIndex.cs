using System.Buffers;

namespace AgentIsland.Core;

/// 从上次读到的位置接着读一个只追加的日志，只把含有关键字的行交给调用方。
/// 用来在不重读整份文件的前提下，找出散落在几百 MB 记录里的少数几种事件
/// （Workflow 的启动记录、Kimi 的 subagent / task 事件）：关键字先在字节上匹配，
/// 没命中的行连解码都不做。文件变短（被重写）就从头再来。
///
/// 锁分两层：索引表的锁只在查表、登记和淘汰时短暂持有；读文件在这个文件自己的锁里做，
/// 所以一个慢文件不会挡住别的文件。同一个文件同时被问到时只读一遍。
/// 缓冲区从 ArrayPool 借，不每次新分配。表满时淘汰最久没用的，不清空整张表。
internal sealed class LineIndex<TState> where TState : class
{
    private const int Chunk = 1 << 20;
    /// 比这更长的一行（嵌了图片之类的）直接放弃，不为它占内存。
    private const int LongestLine = 64 << 20;
    private const int Bound = 400;

    private sealed class Entry
    {
        public long Offset;
        public TState? State;
        public long LastUsed;
    }

    private readonly byte[][] _needles;
    private readonly Func<TState> _make;
    private readonly Action<TState, string> _add;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _tick;

    public LineIndex(string[] needles, Func<TState> make, Action<TState, string> add)
    {
        _needles = needles.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();
        _make = make;
        _add = add;
    }

    public int Count
    {
        get
        {
            lock (_entries) return _entries.Count;
        }
    }

    public TResult Read<TResult>(string path, Func<TState, TResult> project, TResult unreadable)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(path, out entry!))
            {
                if (_entries.Count >= Bound)
                {
                    var oldest = _entries.MinBy(pair => pair.Value.LastUsed).Key;
                    _entries.Remove(oldest);
                }
                _entries[path] = entry = new Entry();
            }
            entry.LastUsed = ++_tick;
        }
        // 读文件在这个文件自己的锁里；被淘汰的条目仍可被手里拿着它的线程用完。
        lock (entry)
        {
            try
            {
                using var stream = SharedFile.Open(path);
                if (entry.State is null || stream.Length < entry.Offset)
                {
                    entry.State = _make();
                    entry.Offset = 0;
                }
                entry.Offset += Consume(stream, entry.Offset, entry.State);
                return project(entry.State);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return unreadable;
            }
        }
    }

    /// 读完整的行，返回消耗的字节数（半行留到下次）。
    private long Consume(FileStream stream, long offset, TState state)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var data = ArrayPool<byte>.Shared.Rent(Chunk * 2);
        try
        {
            var length = 0;
            long consumed = 0;
            while (true)
            {
                if (data.Length - length < Chunk)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(data.Length * 2, LongestLine + Chunk));
                    Buffer.BlockCopy(data, 0, bigger, 0, length);
                    ArrayPool<byte>.Shared.Return(data);
                    data = bigger;
                }
                var read = stream.Read(data, length, Chunk);
                if (read <= 0) break;
                var scanFrom = length;
                length += read;
                var start = 0;
                for (var i = scanFrom; i < length; i++)
                {
                    if (data[i] != (byte)'\n') continue;
                    Offer(data.AsSpan(start, i - start), state);
                    start = i + 1;
                }
                consumed += start;
                if (start > 0)
                {
                    Buffer.BlockCopy(data, start, data, 0, length - start);
                    length -= start;
                }
                // 一行长到装不下：跳过它，下一行重新对齐。
                if (length >= LongestLine)
                {
                    consumed += length;
                    length = 0;
                }
            }
            return consumed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private void Offer(ReadOnlySpan<byte> line, TState state)
    {
        foreach (var needle in _needles)
        {
            if (line.IndexOf(needle) < 0) continue;
            _add(state, System.Text.Encoding.UTF8.GetString(line).TrimEnd('\r'));
            return;
        }
    }

    public void Clear()
    {
        lock (_entries) _entries.Clear();
    }
}
