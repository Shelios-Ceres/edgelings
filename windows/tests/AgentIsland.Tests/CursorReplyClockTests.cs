using AgentIsland.Core;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Cursor rewrites a bubble's row while the reply is written, but its
/// `createdAt` stays at the start. The quiet gap is measured from the later
/// of `createdAt` and the moment the bubble's content was seen to change.
public static class CursorReplyClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        FirstReadIsNotBusy();
        GrowingReplyStaysWorking();
        NewBubbleWithOldTimestamp();
        LeavingConversationsAreForgotten();
        BusyStoreIsNotAnEmptyRead();
        Console.WriteLine("CursorReplyClockTests GREEN");
    }

    /// A read that hits Cursor holding its store must not pass for a list of
    /// every conversation: the scan would forget their reply clocks.
    private static void BusyStoreIsNotAnEmptyRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentisland-cursor-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.vscdb");
        try
        {
            using (var writer = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                writer.Open();
                Sql(writer, "CREATE TABLE cursorDiskKV(key TEXT PRIMARY KEY, value TEXT); "
                    + """INSERT INTO cursorDiskKV VALUES ('bubbleId:c1:b1', '{"type":2,"text":"hi"}')""");
                var read = CursorConversations.NewestBubblePerConversation(path);
                Expect(read is { Count: 1 } && read[0].ComposerId == "c1", "a free store is read");
                Sql(writer, "BEGIN EXCLUSIVE");
                Expect(CursorConversations.NewestBubblePerConversation(path) is null, "a busy store is no read at all");
                Sql(writer, "COMMIT");
            }
            Console.WriteLine("PASS a busy Cursor store reads as nothing new, not as no conversations");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }

        static void Sql(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static ActivityState Status(DateTimeOffset stamp, DateTimeOffset now) =>
        SessionScanner.CursorStatus(new SessionTurnStatus(true, "cursor:b1", stamp), stamp, now);

    private static void FirstReadIsNotBusy()
    {
        var clock = new CursorBubbleClock();
        var createdAt = Start.AddMinutes(-10);
        var stamp = clock.Observe("c1", "cursor:b1", """{"text":"hello"}""", createdAt, Start);
        Expect(stamp, createdAt);
        Expect(Status(stamp, Start), ActivityState.NeedsYou);
        Console.WriteLine("PASS nothing reads as busy on the first read");
    }

    private static void GrowingReplyStaysWorking()
    {
        var clock = new CursorBubbleClock();
        var createdAt = Start;
        clock.Observe("c1", "cursor:b1", """{"text":"a"}""", createdAt, Start.AddSeconds(5));

        var stamp = clock.Observe("c1", "cursor:b1", """{"text":"ab"}""", createdAt, Start.AddSeconds(40));
        Expect(stamp, Start.AddSeconds(40));
        Expect(Status(stamp, Start.AddSeconds(50)), ActivityState.Working);

        // Unchanged since: the change time is kept, not moved to this read.
        stamp = clock.Observe("c1", "cursor:b1", """{"text":"ab"}""", createdAt, Start.AddSeconds(55));
        Expect(stamp, Start.AddSeconds(40));
        Expect(Status(stamp, Start.AddSeconds(65)), ActivityState.Working);
        Expect(Status(stamp, Start.AddSeconds(66)), ActivityState.NeedsYou);
        Console.WriteLine("PASS a bubble that keeps changing stays working, then turns to your turn 25 s after the last change");
    }

    private static void NewBubbleWithOldTimestamp()
    {
        var clock = new CursorBubbleClock();
        clock.Observe("c1", "cursor:b1", """{"text":"a"}""", Start.AddMinutes(-5), Start);
        var createdAt = Start.AddSeconds(-60);
        var stamp = clock.Observe("c1", "cursor:b2", """{"text":"b"}""", createdAt, Start.AddSeconds(10));
        Expect(stamp, createdAt);
        Expect(Status(stamp, Start.AddSeconds(10)), ActivityState.NeedsYou);
        Console.WriteLine("PASS a new bubble is dated by createdAt alone");
    }

    private static void LeavingConversationsAreForgotten()
    {
        var clock = new CursorBubbleClock();
        clock.Observe("c1", "cursor:b1", """{"text":"a"}""", Start, Start);
        clock.Keep(new List<string>());
        // Back again with changed content: it is a first sight, not a change.
        var stamp = clock.Observe("c1", "cursor:b1", """{"text":"ab"}""", Start, Start.AddSeconds(100));
        Expect(stamp, Start);
        Console.WriteLine("PASS a conversation that left the read starts over");
    }
}
