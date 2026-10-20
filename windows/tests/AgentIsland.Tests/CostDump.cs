using System.Globalization;
using AgentIsland.Cost;

namespace AgentIsland.Tests;

/// `AgentIsland.Tests.exe costdump`: what every cost reader finds in this
/// machine's real logs, as sorted plain text, so two builds can be compared
/// line for line. Events from the last ten minutes are left out, since a
/// session that is running writes them while two builds are being compared.
/// A diagnostic, not a test.
public static class CostDump
{
    public static int Run()
    {
        var readers = new (string Name, Func<int, List<TokenEvent>> Scan)[]
        {
            ("claude", ClaudeLogReader.Scan),
            ("codex", CodexLogReader.Scan),
            ("grok", GrokLogReader.Scan),
            ("cursor", CursorLogReader.Scan),
            ("kimi", KimiLogReader.Scan),
            ("deepseek", DeepSeekLogReader.Scan),
            ("antigravity", AntigravityLogReader.Scan),
        };
        var settled = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var (name, scan) in readers)
        {
            var events = scan(400).Where(e => e.Timestamp < settled).ToList();
            Console.WriteLine($"{name}: {events.Count} events, {events.Sum(e => e.WireTokens)} wire tokens, {events.Sum(e => e.Dollars).ToString("F4", CultureInfo.InvariantCulture)} dollars");
            foreach (var model in events.GroupBy(e => e.Model).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {model.Key}: n={model.Count()} in={model.Sum(e => e.InputTokens)} out={model.Sum(e => e.OutputTokens)} cc={model.Sum(e => e.CacheCreationTokens)} cr={model.Sum(e => e.CacheReadTokens)}");
            }
            foreach (var day in events.GroupBy(e => e.Timestamp.UtcDateTime.Date).OrderBy(g => g.Key))
            {
                Console.WriteLine($"  {day.Key:yyyy-MM-dd}: n={day.Count()} wire={day.Sum(e => e.WireTokens)}");
            }
        }
        return 0;
    }
}
