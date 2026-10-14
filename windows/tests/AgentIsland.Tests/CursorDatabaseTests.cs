using System.IO;
using System.Text;
using System.Text.Json;
using AgentIsland.Cost;
using AgentIsland.Usage;
using Microsoft.Data.Sqlite;

namespace AgentIsland.Tests;

public static class CursorDatabaseTests
{
    public static void RunAll()
    {
        Run("current WAL identity replaces older token with later expiration", CurrentIdentity);
        Run("deleted and rolled back credentials cannot become current", DeletedIdentity);
        Run("unreadable ledger faults instead of replacing totals with zero", ReadFailure);
        Run("empty ledger is a successful zero and WAL deletion is respected", EmptyLedger);
        Run("packaged SQLite engine includes the security fix", EngineVersion);
        Console.WriteLine("CursorDatabaseTests GREEN");
    }

    private static void CurrentIdentity()
    {
        using var fixture = new Fixture();
        fixture.Identity("old-account", 2_000_000_000, "old@example.invalid", "pro");
        fixture.Sql("PRAGMA wal_checkpoint(TRUNCATE)");
        fixture.Identity("current-account", 1_900_000_000, "current@example.invalid", "ultra");
        var snapshot = CursorCredentials.ReadDatabase(fixture.Path);
        Expect(snapshot.Session?.UserId == "current-account", "must query the live row, not the token's expiration");
        Expect(snapshot.Email == "current@example.invalid" && snapshot.Plan == "ultra", "identity fields must share the committed snapshot");
        using var transaction = fixture.Connection.BeginTransaction();
        fixture.Identity("uncommitted", 2_100_000_000, "pending@example.invalid", "free", transaction);
        Expect(CursorCredentials.ReadDatabase(fixture.Path).Session?.UserId == "current-account", "uncommitted WAL must remain invisible");
        transaction.Rollback();
    }

    private static void DeletedIdentity()
    {
        using var fixture = new Fixture();
        fixture.Identity("removed", 2_000_000_000, "removed@example.invalid", "pro");
        fixture.Sql("PRAGMA wal_checkpoint(TRUNCATE)");
        fixture.Sql("DELETE FROM ItemTable");
        var snapshot = CursorCredentials.ReadDatabase(fixture.Path);
        Expect(snapshot.Session is null && snapshot.Email is null && snapshot.Plan is null, "deleted rows and their freed pages must not be recovered as live credentials");
        fixture.Sql("INSERT INTO ItemTable VALUES('cursorAuth/accessToken','not-a-jwt')");
        Expect(CursorCredentials.ReadDatabase(fixture.Path).Session is null, "malformed token must not create a session");
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
            MustFail(() => CursorCredentials.ReadDatabase(fixture.Path));
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
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }

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
        public void Bubble()
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "INSERT INTO cursorDiskKV VALUES ('bubbleId:fixture', @value)";
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
