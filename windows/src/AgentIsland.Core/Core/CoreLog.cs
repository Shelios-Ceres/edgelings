namespace AgentIsland.Core;

/// Where Core reports an error it had to swallow so the app could carry on.
/// Core has no log of its own: the app points Sink at its crash log, and
/// until it does (the tests, for one) errors go nowhere. A failure that
/// repeats on every scan is written once per launch.
public static class CoreLog
{
    private const int MostKept = 500;

    private static readonly object Gate = new();
    private static readonly HashSet<(string Context, string Type, string Message)> Seen = new();

    public static Action<Exception, string>? Sink { get; set; }

    public static void Error(Exception error, string context)
    {
        if (Sink is not { } sink) return;
        lock (Gate)
        {
            if (Seen.Count >= MostKept) Seen.Clear();
            if (!Seen.Add((context, error.GetType().FullName ?? "", error.Message))) return;
        }
        try
        {
            sink(error, context);
        }
        catch
        {
            // Logging must never be the thing that fails.
        }
    }

    /// Test seam: forget what was already written.
    internal static void Reset()
    {
        lock (Gate) Seen.Clear();
    }
}
