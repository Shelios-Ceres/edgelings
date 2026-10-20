using System.Text.RegularExpressions;

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
            if (!Seen.Add((context, error.GetType().FullName ?? "", SameFailure(error.Message)))) return;
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

    /// A message with the ids that change on every attempt (a per-write temp
    /// file's name) taken out, so a failure that repeats reads as one.
    private static string SameFailure(string message) => TempId.Replace(message, "#");

    private static readonly Regex TempId = new("[0-9a-fA-F]{32}", RegexOptions.Compiled);

    /// Test seam: forget what was already written.
    internal static void Reset()
    {
        lock (Gate) Seen.Clear();
    }
}
