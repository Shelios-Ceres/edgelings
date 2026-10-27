using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Codex auto-switch fires on the same rule that puts the bot to sleep: any
/// readable window at 100%, not the 5-hour window alone.
public static class CodexAutoSwitchReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static WindowUsage Window(double used, DateTimeOffset? resetAt = null) =>
        new(used, resetAt ?? Now.AddHours(3), null) { Observed = true };

    private static WindowUsage Broken => new(0, null, "http 500");

    private static UsageStore.CodexQuotaReading Read(WindowUsage fiveHour, WindowUsage weekly) =>
        UsageStore.ReadCodexQuota(new AppUsage(fiveHour, weekly), Now);

    public static void RunAll()
    {
        Expect(Read(Window(0.2), Window(1.0)), UsageStore.CodexQuotaReading.Spent);
        Console.WriteLine("PASS 5-hour at 20% and weekly at 100% is spent");

        Expect(Read(Window(1.0), Broken), UsageStore.CodexQuotaReading.Spent);
        Expect(Read(Broken, Window(1.0)), UsageStore.CodexQuotaReading.Spent);
        Console.WriteLine("PASS a spent window counts while the other has an error");

        Expect(Read(Broken, Broken), UsageStore.CodexQuotaReading.Unreadable);
        Console.WriteLine("PASS two windows with errors are unreadable, so nothing switches");

        Expect(Read(Window(0.2), Window(0.5)), UsageStore.CodexQuotaReading.Clear);
        Expect(Read(Window(0.2), Broken), UsageStore.CodexQuotaReading.Clear);
        Console.WriteLine("PASS a reading with nothing spent is clear, which resets the tried set");

        Expect(Read(Window(0.2), Window(1.0, Now.AddMinutes(-1))), UsageStore.CodexQuotaReading.Clear);
        Expect(Read(Window(1.0, Now.AddSeconds(-1)), Window(0.5)), UsageStore.CodexQuotaReading.Clear);
        Console.WriteLine("PASS a spent window past its reset time is not spent");

        Console.WriteLine("CodexAutoSwitchReadingTests GREEN");
    }
}
