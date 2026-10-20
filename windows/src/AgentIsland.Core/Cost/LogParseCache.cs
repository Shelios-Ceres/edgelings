using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Per-file parse memoization keyed by (mtime, size). Steady-state polls,
/// where almost no file changed, skip file I/O entirely; disappeared or
/// out-of-window files are pruned. Cache JSON lives under
/// %LOCALAPPDATA%\AgentIsland\cache.
public sealed class LogParseCache
{
    private sealed record EventDto(
        long Ts, string Model, long In, long Out, long Cc, long Cr,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResponseKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsFinalUsage = false);

    private sealed record Entry(long MtimeTicks, long Size, List<EventDto> Events);

    private readonly string _cachePath;
    private readonly TriggerTool _provider;
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private bool _loaded;

    public LogParseCache(TriggerTool provider, string cacheFileName)
        : this(provider, cacheFileName, IslandPaths.CacheDir)
    {
    }

    internal LogParseCache(TriggerTool provider, string cacheFileName, string cacheDirectory)
    {
        _provider = provider;
        _cachePath = Path.Combine(cacheDirectory, cacheFileName);
    }

    public List<TokenEvent> Walk(
        IEnumerable<string> files,
        DateTimeOffset cutoff,
        Func<string, List<TokenEvent>> parseFile)
    {
        Load();
        var output = new List<TokenEvent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            _entries.TryGetValue(path, out var entry);
            DateTimeOffset mtime;
            long size;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                mtime = new DateTimeOffset(info.LastWriteTimeUtc);
                size = info.Length;
            }
            catch
            {
                seen.Add(path);
                if (entry is not null) output.AddRange(entry.Events.Select(FromDto));
                continue;
            }
            if (mtime < cutoff) continue;
            seen.Add(path);

            if (entry is not null && entry.MtimeTicks == mtime.UtcTicks
                && entry.Size == size)
            {
                output.AddRange(entry.Events.Select(FromDto));
                continue;
            }

            try
            {
                var events = parseFile(path);
                var after = new FileInfo(path);
                if (after.Exists && after.LastWriteTimeUtc.Ticks == mtime.UtcTicks && after.Length == size)
                {
                    _entries[path] = new Entry(mtime.UtcTicks, size, events.Select(ToDto).ToList());
                    _dirty = true;
                }
                output.AddRange(events);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A failed read must neither erase the last success nor be
                // remembered as an empty log. Its old fingerprint forces retry.
                if (entry is not null) output.AddRange(entry.Events.Select(FromDto));
            }
        }

        // Prune entries for files gone or aged out of the window.
        var stale = _entries.Keys.Where(key => !seen.Contains(key)).ToList();
        if (stale.Count > 0)
        {
            foreach (var key in stale) _entries.Remove(key);
            _dirty = true;
        }
        SaveIfDirty();
        return output;
    }

    private TokenEvent FromDto(EventDto dto) => new(
        _provider,
        // Clamp: a corrupt cache file with an out-of-range Ts would otherwise
        // throw out of FromUnixTimeMilliseconds and take the whole cost scan
        // down on a cache hit.
        DateTimeOffset.FromUnixTimeMilliseconds(
            Math.Clamp(dto.Ts, -62_135_596_800_000, 253_402_300_799_999)),
        dto.Model,
        dto.In,
        dto.Out,
        dto.Cc,
        dto.Cr)
    {
        ResponseKey = dto.ResponseKey,
        IsFinalUsage = dto.IsFinalUsage,
    };

    private static EventDto ToDto(TokenEvent tokenEvent) => new(
        tokenEvent.Timestamp.ToUnixTimeMilliseconds(),
        tokenEvent.Model,
        tokenEvent.InputTokens,
        tokenEvent.OutputTokens,
        tokenEvent.CacheCreationTokens,
        tokenEvent.CacheReadTokens,
        tokenEvent.ResponseKey,
        tokenEvent.IsFinalUsage);

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(_cachePath)
                && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_cachePath)) is { } saved)
            {
                // The deserializer's dictionary compares case-sensitively; a
                // path in other casing would miss its entry and add a twin.
                foreach (var (path, entry) in saved) _entries[path] = entry;
            }
        }
        catch
        {
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch
        {
            _dirty = true;
        }
    }
}
