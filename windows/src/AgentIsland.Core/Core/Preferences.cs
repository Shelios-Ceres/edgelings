using System.Text.Json;

namespace AgentIsland.Core;

/// UserDefaults equivalent: one JSON file of keyed values under
/// %APPDATA%\AgentIsland. Writes are atomic (temp + rename) so a crash
/// mid-save never truncates every preference at once.
///
/// Every write re-reads the file and layers only the caller's key on top.
/// The old write-my-whole-snapshot design silently destroyed data whenever
/// two copies of the app ran at once (portable exe + a second unzip, or the
/// updater's relaunch overlap): each instance's periodic saves kept
/// resurrecting its stale launch snapshot — which is how a freshly created
/// auto-resume rule could vanish minutes later.
public static class Preferences
{
    private static readonly object Gate = new();
    private static Dictionary<string, JsonElement>? _values;

    /// Test seam: another settings file, and a way to forget what was read.
    internal static string? FileOverride;
    internal static void Forget()
    {
        lock (Gate) _values = null;
    }

    private static string SettingsPath => FileOverride ?? IslandPaths.SettingsFile;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static T? Get<T>(string key)
    {
        lock (Gate)
        {
            if (Values().TryGetValue(key, out var element))
            {
                try { return element.Deserialize<T>(); }
                catch { return default; }
            }
            return default;
        }
    }

    public static bool Has(string key)
    {
        lock (Gate)
        {
            return Values().ContainsKey(key);
        }
    }

    public static void Set<T>(string key, T value)
    {
        lock (Gate)
        {
            _values = Reload();
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            _values = Reload();
            if (_values.Remove(key)) Save();
        }
    }

    /// One-time sweep of the MacIsland.* era: settings written before 1.7
    /// carried the prefix inherited from the macOS source. Copy-if-absent
    /// then delete — an AgentIsland.* value that already exists wins, so
    /// running an old build after the sweep can't resurrect stale state
    /// over a newer choice. Runs before any store singleton reads a key.
    public static void MigrateLegacyPrefix()
    {
        lock (Gate)
        {
            // A file that cannot be read right now has nothing to sweep.
            if (LoadFromDisk() is not { } values) return;
            _values = values;
            var legacy = _values.Keys
                .Where(k => k.StartsWith("MacIsland.", StringComparison.Ordinal))
                .ToList();
            if (legacy.Count == 0) return;
            foreach (var oldKey in legacy)
            {
                var newKey = "AgentIsland." + oldKey["MacIsland.".Length..];
                if (!_values.ContainsKey(newKey)) _values[newKey] = _values[oldKey];
                _values.Remove(oldKey);
            }
            Save();
        }
    }

    /// The values in memory, read on first use. A file that cannot be read
    /// right now is read again next time rather than taken as empty.
    private static Dictionary<string, JsonElement> Values()
    {
        if (_values is null && LoadFromDisk() is { } loaded) _values = loaded;
        return _values ?? new Dictionary<string, JsonElement>();
    }

    /// What a write layers its key onto. A file that cannot be read right
    /// now falls back to the values in memory: taking it as empty made that
    /// write save one key over every other preference.
    private static Dictionary<string, JsonElement> Reload() =>
        LoadFromDisk() ?? _values ?? new Dictionary<string, JsonElement>();

    /// The file's values: empty when there is no file yet or it is not
    /// valid JSON, null when it cannot be read right now. A file that is not
    /// valid JSON (a crash mid-save can leave it empty or zeroed) is moved
    /// aside as settings.json.corrupt-<time> and logged, so the next write
    /// can't destroy what may still be recovered from it.
    private static Dictionary<string, JsonElement>? LoadFromDisk()
    {
        var path = SettingsPath;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return new Dictionary<string, JsonElement>();
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
                    ?? new Dictionary<string, JsonElement>();
            }
            catch (JsonException error)
            {
                var aside = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    File.Move(path, aside);
                    CoreLog.Error(error, $"Settings unreadable, moved aside as {Path.GetFileName(aside)}");
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    // Another copy of the app moved it first, or it is held:
                    // either way the values read as defaults this time.
                }
                return new Dictionary<string, JsonElement>();
            }
            catch (IOException) when (attempt < 2)
            {
                // An antivirus scan or another reader can hold it for a moment.
                Thread.Sleep(20 * (attempt + 1));
            }
            catch
            {
                return null;
            }
        }
    }

    private static void Save()
    {
        // Per-write temp name, not a fixed ".tmp". Windows denies a second
        // opener while the first holds the handle, so with two copies of the
        // app running — the very case the re-read-and-layer design above
        // exists for — one instance's write would fail and be swallowed here,
        // silently losing a preference change.
        var tmp = string.Empty;
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            // On disk before the rename: a power cut after it must not leave
            // settings.json renamed onto bytes that were never written.
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _values, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception error)
        {
            // A failed save costs one preference write, not the app — but the
            // temp copy must not be left behind, since nothing else sweeps it.
            // The write is not retried; the value stays in memory for this run.
            CoreLog.Error(error, "Settings save");
            try { if (tmp.Length > 0) File.Delete(tmp); } catch { }
        }
    }
}
