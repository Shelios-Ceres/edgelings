using System.Text;
using System.Text.Json;
using AgentIsland.Cost;
using AgentIsland.Usage;
using Microsoft.Data.Sqlite;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

public static class CursorDatabaseTests
{
    public static void RunAll()
    {
        Run("unreadable ledger faults instead of replacing totals with zero", ReadFailure);
        Run("empty ledger is a successful zero and WAL deletion is respected", EmptyLedger);
        Run("an unchanged ledger is not read again, a commit is", UnchangedLedger);
        Run("a WAL commit that keeps every length and write time still moves the stamp", WalCommitSameLength);
        Run("packaged SQLite engine includes the security fix", EngineVersion);
        Console.WriteLine("CursorDatabaseTests GREEN");
    }

    private static void ReadFailure()
    {
        using var fixture = new Fixture();
        fixture.Bubble();
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Single().WireTokens == 120, "control ledger must contain 120 tokens");
        fixture.Connection.Close();
        using (var locked = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            MustFail(() => CursorLogReader.ReadDatabase(fixture.Path, 1));
        }
        fixture.Connection.Open();
        fixture.Sql("DROP TABLE cursorDiskKV");
        MustFail(() => CursorLogReader.ReadDatabase(fixture.Path, 1));
        MustFail(() => CursorLogReader.ReadDatabase(fixture.Path + ".missing", 1));
    }

    private static void EmptyLedger()
    {
        using var fixture = new Fixture();
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 0, "a valid empty table is a successful scan");
        fixture.Bubble();
        fixture.Sql("PRAGMA wal_checkpoint(TRUNCATE)");
        fixture.Sql("DELETE FROM cursorDiskKV");
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 0, "committed deletion must not count stale pages");
    }

    private static void UnchangedLedger()
    {
        using var fixture = new Fixture();
        fixture.Bubble();
        // Out of WAL mode, so the ledger reads with no writer holding it open.
        fixture.Sql("PRAGMA journal_mode=DELETE");
        fixture.Connection.Close();
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 1, "control ledger must contain one bubble");
        // The range SQLite takes its shared locks in: a reader cannot open the
        // database while it is held, but the header bytes the stamp reads stay free.
        using (var holder = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            // Byte-range locks are Windows' (a Mac's FileStream refuses them).
            if (OperatingSystem.IsWindows()) holder.Lock(0x40000002, 510);
            Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 1, "an unchanged ledger must be served without opening it");
        }
        fixture.Connection.Open();
        using (var command = fixture.Connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO cursorDiskKV VALUES ('bubbleId:second', @value)";
            command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(new { createdAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(), tokenCount = new { inputTokens = 5, outputTokens = 1 } }));
            command.ExecuteNonQuery();
        }
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 2, "a commit must be read");
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 2, "a repeated read must return the same bubbles");
    }

    private static void WalCommitSameLength()
    {
        using var fixture = new Fixture();
        fixture.Bubble();
        fixture.Sql("PRAGMA wal_checkpoint(RESTART)");
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 1, "control ledger must contain one bubble");
        var files = new[] { fixture.Path, fixture.Path + "-wal", fixture.Path + "-shm" };
        var before = CursorLogReader.Stamp(fixture.Path);
        var lengths = files.Select(file => new FileInfo(file).Length).ToArray();
        var times = files.Select(File.GetLastWriteTimeUtc).ToArray();
        fixture.Bubble("second");
        // The WAL restarts from its first frame, so the commit lands inside the
        // old length; the write times are put back as a lazily stamped file shows them.
        Expect(files.Select(file => new FileInfo(file).Length).SequenceEqual(lengths), "the commit must keep every file's length");
        for (var i = 0; i < files.Length; i++) File.SetLastWriteTimeUtc(files[i], times[i]);
        var after = CursorLogReader.Stamp(fixture.Path);
        Expect(before is not null && after is not null && before != after, "the stamp must move on a commit that changes no length or write time");
        Expect(CursorLogReader.ReadDatabase(fixture.Path, 1).Count == 2, "a commit must be read");
    }

    private static void EngineVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = (string)command.ExecuteScalar()!;
        Expect(Version.Parse(version) >= new Version(3, 50, 2), $"SQLite {version} predates CVE-2025-6965 fix");
        Console.WriteLine($"SQLite runtime {version}");
    }

    private static void MustFail(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or SqliteException) { return; }
        throw new Exception("failed read must propagate to CostStore's successful-task gate");
    }

    private static void Run(string name, Action action) { action(); Console.WriteLine($"PASS Cursor database: {name}"); }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentisland-cursor-db-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public SqliteConnection Connection { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "state.vscdb");
            Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            Connection.Open();
            Sql("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA secure_delete=OFF; "
                + "CREATE TABLE ItemTable(key TEXT PRIMARY KEY, value TEXT); CREATE TABLE cursorDiskKV(key TEXT PRIMARY KEY, value TEXT)");
        }

        public void Sql(string sql) { using var command = Connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public void Identity(string subject, long expires, string email, string plan, SqliteTransaction? transaction = null)
        {
            static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var token = Base64("{\"alg\":\"none\"}") + "." + Base64(JsonSerializer.Serialize(new { sub = subject, exp = expires })) + ".fixture";
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO ItemTable VALUES ('cursorAuth/accessToken',@token),('cursorAuth/cachedEmail',@email),('cursorAuth/stripeMembershipType',@plan)";
            command.Parameters.AddWithValue("@token", token);
            command.Parameters.AddWithValue("@email", email);
            command.Parameters.AddWithValue("@plan", plan);
            command.ExecuteNonQuery();
        }
        public void Bubble(string key = "fixture")
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "INSERT INTO cursorDiskKV VALUES (@key, @value)";
            command.Parameters.AddWithValue("@key", "bubbleId:" + key);
            command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(new { createdAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(), tokenCount = new { inputTokens = 100, outputTokens = 20 } }));
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            Connection.Dispose();
            var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var root = System.IO.Path.GetFullPath(_root);
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(root).StartsWith("agentisland-cursor-db-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary directory");
            Directory.Delete(root, recursive: true);
        }
    }
}
