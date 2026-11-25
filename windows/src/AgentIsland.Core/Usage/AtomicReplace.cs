using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Puts a rotated token pair into a credential JSON file without ever
/// overwriting a login that someone else wrote in the meantime. Before each try
/// the file is read again: if it no longer holds the login that was refreshed it
/// is left alone; if it still does but its bytes have changed (another program
/// saved an MCP login, say), the new file is rebuilt from the latest content so
/// that nothing else in it is lost. After the swap the displaced copy is read
/// back, and if it turns out to hold another login, that copy is restored.
internal static class AtomicReplace
{
    internal enum Result
    {
        Written,
        /// The file is gone: a sign-out. Nothing is created.
        Gone,
        /// The file holds another login now; it was left alone (or put back).
        Superseded,
        Failed,
    }

    /// What to do to the parsed file: whether it still holds the login being
    /// refreshed, and how to put the new tokens in.
    internal sealed record Plan(Func<JsonObject, bool> Matches, Action<JsonObject> Apply, JsonSerializerOptions Options);

    /// Test seam: File.Replace.
    internal static Action<string, string, string> ReplaceFile = (source, target, backup) => File.Replace(source, target, backup);

    private enum Read
    {
        Ok,
        Missing,
        Unreadable,
    }

    internal static (Result Result, JsonObject? Written) Run(string path, Plan plan)
    {
        // File.Replace can fail half way (ERROR_UNABLE_TO_MOVE_REPLACEMENT): the
        // old file already moved to the backup name, the new one still at its
        // temp name. Keeping a backup next to it makes that recoverable instead
        // of looking like a sign-out.
        DiscardIfPresent(path);
        var backup = path + ".bak-" + Guid.NewGuid().ToString("N");
        string? tmp = null;
        string? tmpBase = null;
        JsonObject? applied = null;
        var keepTmp = false;
        var keepBackup = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var state = Load(path, out var root, out var bytes);
                if (state == Read.Missing) return (Result.Gone, null);
                if (state == Read.Unreadable)
                {
                    if (attempt >= 4) return (Result.Failed, null);
                    System.Threading.Thread.Sleep(40 * (attempt + 1));
                    continue;
                }
                if (!plan.Matches(root!)) return (Result.Superseded, null);

                var hash = Convert.ToHexString(SHA256.HashData(bytes!));
                if (tmp is null || hash != tmpBase)
                {
                    if (tmp is not null) TryDelete(tmp);
                    plan.Apply(root!);
                    tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    CredentialFiles.WriteNew(tmp, root!.ToJsonString(plan.Options));
                    tmpBase = hash;
                    applied = root;
                }

                try
                {
                    ReplaceFile(tmp, path, backup);
                }
                catch (Exception) when (!File.Exists(path) && File.Exists(backup))
                {
                    if (!Recover(tmp, path, backup))
                    {
                        // Whatever is left is all there is: neither copy is touched again.
                        keepTmp = true;
                        keepBackup = true;
                        Touch(tmp);
                        Touch(backup);
                        lock (InterruptedGate) Interrupted[path] = (tmp, backup);
                        return (Result.Failed, null);
                    }
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException && !File.Exists(path))
                {
                    return (Result.Gone, null);
                }
                catch (Exception error) when (attempt < 4 && error is IOException or UnauthorizedAccessException)
                {
                    // A program may hold a brief read lock on the file, and dropping
                    // the rotated token would sign the user out. Replacing a file
                    // held without share-delete throws UnauthorizedAccessException,
                    // not IOException.
                    System.Threading.Thread.Sleep(40 * (attempt + 1));
                    continue;
                }

                // The swap is done (File.Replace keeps the old file's timestamp, so
                // the backup is stamped as new). What it displaced should be what was
                // checked: if another program wrote in the gap between that check and
                // the swap, put its file back.
                Touch(backup);
                var seen = LoadWithRetries(backup, out var displaced, out var displacedBytes);
                if (seen == Read.Unreadable)
                {
                    // Not read, so not known to be the checked file: keep it.
                    keepBackup = true;
                    return (Result.Written, applied);
                }
                if (seen == Read.Ok)
                {
                    if (!plan.Matches(displaced!))
                    {
                        if (!Restore(backup, path)) keepBackup = true;
                        return (keepBackup ? Result.Failed : Result.Superseded, null);
                    }
                    if (Convert.ToHexString(SHA256.HashData(displacedBytes!)) != tmpBase)
                    {
                        // Same login, other fields changed: go again from its content.
                        if (!Restore(backup, path))
                        {
                            keepBackup = true;
                            return (Result.Failed, null);
                        }
                        if (attempt >= 4) return (Result.Failed, null);
                        TryDelete(tmp);
                        tmp = null;
                        continue;
                    }
                }
                return (Result.Written, applied);
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"AgentIsland: failed to replace credential file: {error.GetType().Name}");
            return (Result.Failed, null);
        }
        finally
        {
            // The temp copy holds a live token pair and nothing else cleans it up,
            // unless it is all that is left of the file. The backup likewise is
            // the only copy of the old file while the target is missing, and the
            // only copy of another login when it could not be put back.
            if (tmp is not null && (!keepTmp || File.Exists(path))) TryDelete(tmp);
            if (File.Exists(path) && !keepBackup) TryDelete(backup);
        }
    }

    private static Read LoadWithRetries(string file, out JsonObject? root, out byte[]? bytes)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = Load(file, out root, out bytes);
            if (state != Read.Unreadable || attempt >= 4) return state;
            System.Threading.Thread.Sleep(25 * (attempt + 1));
        }
    }

    private static void Touch(string file)
    {
        try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); }
        catch { }
    }

    private static Read Load(string file, out JsonObject? root, out byte[]? bytes)
    {
        root = null;
        bytes = null;
        try
        {
            using var stream = SharedFile.Open(file);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
            root = JsonNode.Parse(bytes) as JsonObject;
            return root is null ? Read.Unreadable : Read.Ok;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Read.Missing;
        }
        catch (Exception)
        {
            return Read.Unreadable;
        }
    }

    /// Puts the displaced file back over ours, trying a few times: whoever holds
    /// the file now may let go in a moment.
    private static bool Restore(string backup, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(backup, path, overwrite: true);
                return true;
            }
            catch (Exception error) when (attempt < 4 && error is IOException or UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(40 * (attempt + 1));
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"AgentIsland: failed to restore credential file: {error.GetType().Name}");
                return false;
            }
        }
    }

    /// The half failure: the new file is the one meant to stand, the old one is
    /// the fallback. Whichever move works, nothing is deleted.
    private static bool Recover(string tmp, string path, string backup)
    {
        try
        {
            File.Move(tmp, path);
            return true;
        }
        catch
        {
        }
        try
        {
            File.Move(backup, path);
        }
        catch
        {
        }
        return false;
    }

    private static readonly object InterruptedGate = new();
    // The copies a swap of this process left when it failed half way, by credential file.
    private static readonly Dictionary<string, (string Tmp, string Backup)> Interrupted = new(StringComparer.OrdinalIgnoreCase);

    internal static void ResetForTests()
    {
        lock (InterruptedGate) Interrupted.Clear();
    }

    /// The file is there, so whatever an earlier cut-short swap left is of no use,
    /// and must not be taken for the file if it is removed later: forget the
    /// record and delete the copies.
    internal static void DiscardIfPresent(string credentialFile)
    {
        try
        {
            if (!File.Exists(credentialFile)) return;
            (string Tmp, string Backup) copies;
            lock (InterruptedGate)
            {
                if (!Interrupted.Remove(credentialFile, out copies)) return;
            }
            TryDelete(copies.Tmp);
            TryDelete(copies.Backup);
        }
        catch
        {
        }
    }

    internal enum Restoration
    {
        /// Nothing to put back: the file is there, or nothing was recorded, or what was left is not what is expected.
        None,
        Restored,
        /// There is a recorded scene, but it could not be read or moved just now. It is kept, and the file is not to be taken for signed out.
        Unsure,
    }

    /// A swap of this process failed half way and the credential file is missing:
    /// the copies it left, and only those, say so. The new copy is put back if it
    /// is the pair that was being written (`isNew`), else the old one if it is the
    /// login that was being replaced (`isReplaced`). A scene that does not hold
    /// what is expected is forgotten and its copies deleted, and the missing file
    /// stays a sign-out; one that cannot be read or moved right now is kept for
    /// the next try. Anything else lying next to the file is never taken for it.
    internal static Restoration RestoreInterrupted(string credentialFile, Func<JsonObject, bool> isNew, Func<JsonObject, bool> isReplaced)
    {
        try
        {
            if (File.Exists(credentialFile))
            {
                DiscardIfPresent(credentialFile);
                return Restoration.None;
            }
            (string Tmp, string Backup) copies;
            lock (InterruptedGate)
            {
                if (!Interrupted.TryGetValue(credentialFile, out copies)) return Restoration.None;
            }
            var old = Load(copies.Backup, out var oldRoot, out _);
            if (old == Read.Unreadable) return Restoration.Unsure;
            if (old != Read.Ok || !isReplaced(oldRoot!)) return Forget(credentialFile, copies);

            JsonObject? freshRoot = null;
            var fresh = File.Exists(copies.Tmp) ? Load(copies.Tmp, out freshRoot, out _) : Read.Missing;
            if (fresh == Read.Unreadable) return Restoration.Unsure;
            if (fresh == Read.Ok && isNew(freshRoot!))
            {
                File.Move(copies.Tmp, credentialFile);
                TryDelete(copies.Backup);
            }
            else
            {
                File.Move(copies.Backup, credentialFile);
                TryDelete(copies.Tmp);
            }
            lock (InterruptedGate) Interrupted.Remove(credentialFile);
            return Restoration.Restored;
        }
        catch
        {
            return Restoration.Unsure;
        }
    }

    private static Restoration Forget(string credentialFile, (string Tmp, string Backup) copies)
    {
        lock (InterruptedGate) Interrupted.Remove(credentialFile);
        TryDelete(copies.Tmp);
        TryDelete(copies.Backup);
        return Restoration.None;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    // MARK: - Leftovers

    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    /// Clears what a crash or a kill left next to a credential file: this app's
    /// own `*.tmp-<hex>` and `*.bak-<hex>` copies, once they are old enough that
    /// no write can still be using them. They are only ever deleted: at start-up
    /// nothing is known about what they hold, and putting one back could undo a
    /// sign-out.
    internal static void CleanUpLeftovers(string credentialFile, DateTimeOffset? now = null)
    {
        try
        {
            if (Path.GetDirectoryName(credentialFile) is not { Length: > 0 } directory || !Directory.Exists(directory)) return;
            var name = Path.GetFileName(credentialFile);
            var cutoff = (now ?? DateTimeOffset.UtcNow) - LeftoverAge;
            var copies = new List<(string Path, bool Backup, DateTime Written)>();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var leaf = Path.GetFileName(file);
                var backup = IsCopy(leaf, name + ".bak-");
                var temp = IsCopy(leaf, name + ".tmp-") || IsCopy(leaf, "." + name + ".tmp-");
                if (!backup && !temp) continue;
                var written = File.GetLastWriteTimeUtc(file);
                if (written > cutoff.UtcDateTime) continue;
                copies.Add((file, backup, written));
            }
            if (copies.Count == 0) return;
            foreach (var copy in copies) TryDelete(copy.Path);
        }
        catch
        {
        }
    }

    private static bool IsCopy(string leaf, string prefix) =>
        leaf.Length == prefix.Length + 32 && leaf.StartsWith(prefix, StringComparison.Ordinal)
        && leaf.AsSpan(prefix.Length).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
