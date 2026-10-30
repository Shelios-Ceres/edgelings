using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentIsland.Core;

/// Reads Cursor's conversation store — the same globalStorage/state.vscdb the
/// cost reader opens, through the SQLite that Windows itself ships
/// (winsqlite3.dll, present since Windows 10 1607). No NuGet dependency, and
/// real B-tree semantics rather than a byte scan that would surface rows from
/// freed pages.
///
/// Every entry point is wrapped: a missing DLL, a renamed export, or a
/// marshalling fault degrades to "nothing read", which reads as a Cursor
/// user with nothing running. It can never take the app down.
internal static class CursorConversations
{
    private const int SqliteOk = 0;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadonly = 0x00000001;
    private const int SqliteOpenUri = 0x00000040;
    private const int BusyWaitMilliseconds = 200;

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2")]
    private static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout")]
    private static extern int BusyTimeout(IntPtr db, int milliseconds);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close")]
    private static extern int Close(IntPtr db);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2")]
    private static extern int Prepare(IntPtr db, byte[] sql, int byteLength, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    private static extern int Step(IntPtr stmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr ColumnText(IntPtr stmt, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")]
    private static extern int ColumnBytes(IntPtr stmt, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    private static extern int FinalizeStatement(IntPtr stmt);

    /// The newest message of every conversation active lately, as
    /// (composerId, bubble JSON). A running Cursor appends to the -wal
    /// journal and the main file only catches up at a checkpoint, so the
    /// journal is read too: a plain read-only open, which needs the -shm file
    /// a running Cursor keeps. Without one (Cursor closed and checkpointed)
    /// the immutable open reads the file as it stands. Null when the store
    /// could not be read whole: a store busy with Cursor's writes is not read
    /// immutable, which would skip the journal, but left for the next scan.
    internal static List<(string ComposerId, string Json)>? NewestBubblePerConversation(string dbPath)
    {
        // winsqlite3.dll is Windows' own; the macOS reader comes with its platform layer.
        if (!OperatingSystem.IsWindows()) return null;
        if (Read(dbPath, "?mode=ro", out var busy) is { } rows) return rows;
        return busy ? null : Read(dbPath, "?mode=ro&immutable=1", out _);
    }

    /// Bubbles are keyed bubbleId:<composerId>:<bubbleId>, and every write
    /// (a new message, a tool's status) replaces its row under a new rowid,
    /// so the newest rows hold every conversation that moved lately; the
    /// first one met per composer is its newest. Null when the open or any
    /// step fails; busy says the failure was Cursor holding the store.
    private static List<(string ComposerId, string Json)>? Read(string dbPath, string query, out bool busy)
    {
        busy = false;
        var db = IntPtr.Zero;
        var statement = IntPtr.Zero;
        try
        {
            var uri = "file:" + dbPath.Replace('\\', '/') + query;
            if (Open(NullTerminated(uri), out db, SqliteOpenReadonly | SqliteOpenUri, IntPtr.Zero) != SqliteOk) return null;
            BusyTimeout(db, BusyWaitMilliseconds);
            const string sql = "SELECT key, value FROM cursorDiskKV"
                + " WHERE rowid > (SELECT max(rowid) FROM cursorDiskKV) - 4000"
                + " AND key >= 'bubbleId:' AND key < 'bubbleId;' ORDER BY rowid DESC";
            var prepared = Prepare(db, NullTerminated(sql), -1, out statement, IntPtr.Zero);
            if (prepared != SqliteOk)
            {
                busy = prepared is SqliteBusy or SqliteLocked;
                return null;
            }
            var output = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int code;
            while ((code = Step(statement)) == SqliteRow)
            {
                var parts = ReadColumnText(statement, 0)?.Split(':');
                if (parts is not { Length: >= 3 } || parts[1].Length == 0 || !seen.Add(parts[1])) continue;
                if (ReadColumnText(statement, 1) is { } json) output.Add((parts[1], json));
            }
            // A read cut short is no list of every conversation: taken as
            // one, it would wipe the reply clock of the ones it missed.
            if (code != SqliteDone)
            {
                busy = code is SqliteBusy or SqliteLocked;
                return null;
            }
            return output;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (statement != IntPtr.Zero) { try { FinalizeStatement(statement); } catch (Exception) { } }
            if (db != IntPtr.Zero) { try { Close(db); } catch (Exception) { } }
        }
    }

    /// A bubble's own first line stands in for a conversation title — Cursor
    /// leaves composerData.name empty on most threads.
    internal static string? Title(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var raw = text.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var line = raw.Split('\n', 2)[0].Trim();
            if (line.Length == 0) return null;
            return line.Length > 48 ? line[..48] : line;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadColumnText(IntPtr statement, int column)
    {
        var pointer = ColumnText(statement, column);
        if (pointer == IntPtr.Zero) return null;
        var length = ColumnBytes(statement, column);
        if (length <= 0) return null;
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static byte[] NullTerminated(string value)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(value);
        var buffer = new byte[raw.Length + 1];
        Array.Copy(raw, buffer, raw.Length);
        return buffer;
    }
}
