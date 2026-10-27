using AgentIsland.Core;
using AgentIsland.Cost;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The price table: Kimi Code's plan ids price like Moonshot's API ids.
public static class PricingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("Kimi K3.1 Pro prices the plan id and the API id alike", KimiK31Pro),
            ("Kimi highspeed ids price at their own rates", KimiHighspeed),
            ("Kimi K2.8 Preview prices at the TokenHub rates the user chose", KimiK28Preview),
            ("DeepSeek prices at half outside its peak hours, by the event's time", DeepSeekOffPeak),
            ("DeepSeek's peak windows have exact edges", DeepSeekPeakEdges),
            ("DeepSeek weekends are off-peak and other models never halve", DeepSeekWeekendAndOthers),
            ("DeepSeek Harness events price through the same rule", DeepSeekHarness),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS pricing: {name}");
        }
        Console.WriteLine("PricingTests GREEN");
    }

    // One million tokens of each kind, so the dollars read as the sum of the four rates.
    private static double Million(string model) =>
        Pricing.Cost(new TokenEvent(TriggerTool.Kimi, At, model, 1_000_000, 1_000_000, 1_000_000, 1_000_000));

    // 2026-10-27 is a Tuesday.
    private static DateTimeOffset Utc(int day, int hour, int minute = 0) =>
        new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static double DeepSeekCost(string model, DateTimeOffset at) =>
        Pricing.Cost(new TokenEvent(TriggerTool.Kimi, at, model, 1_000_000, 1_000_000, 1_000_000, 1_000_000));

    private static void DeepSeekOffPeak()
    {
        // Flash: 0.3 / 1.2 / 0.3 / 0.006 at peak.
        Expect(Math.Abs(DeepSeekCost("deepseek-flash", Utc(27, 7)) - 1.806) < 1e-9, "flash at peak");
        Expect(Math.Abs(DeepSeekCost("deepseek-flash", Utc(27, 12)) - 0.903) < 1e-9, "flash off-peak is half");
        Expect(Math.Abs(DeepSeekCost("deepseek-v4-flash", Utc(27, 12)) - 0.903) < 1e-9, "legacy flash name");
        // Pro: 1.32 / 3.96 / 1.32 / 0.044 at peak.
        Expect(Math.Abs(DeepSeekCost("deepseek-v4-pro", Utc(27, 7)) - 6.644) < 1e-9, "pro at peak");
        Expect(Math.Abs(DeepSeekCost("deepseek-v4-pro", Utc(27, 12)) - 3.322) < 1e-9, "pro off-peak is half");
    }

    private static void DeepSeekPeakEdges()
    {
        const double peak = 1.806, half = 0.903;
        void At(DateTimeOffset when, double expected) =>
            Expect(Math.Abs(DeepSeekCost("deepseek-flash", when) - expected) < 1e-9, $"{when:HH:mm}: expected {expected}");
        At(Utc(27, 0, 59), half);
        At(Utc(27, 1), peak);
        At(Utc(27, 3, 59), peak);
        At(Utc(27, 4), half);
        At(Utc(27, 5, 59), half);
        At(Utc(27, 6), peak);
        At(Utc(27, 9, 59), peak);
        At(Utc(27, 10), half);
        // The event's own offset does not matter, only the instant.
        At(new DateTimeOffset(2026, 10, 27, 3, 0, 0, TimeSpan.FromHours(-4)), peak);
    }

    private static void DeepSeekWeekendAndOthers()
    {
        Expect(Math.Abs(DeepSeekCost("deepseek-flash", Utc(24, 2)) - 0.903) < 1e-9, "Saturday 02:00 UTC is off-peak");
        Expect(Math.Abs(DeepSeekCost("deepseek-flash", Utc(25, 2)) - 0.903) < 1e-9, "Sunday 02:00 UTC is off-peak");
        Expect(Math.Abs(DeepSeekCost("deepseek-flash", Utc(23, 2)) - 1.806) < 1e-9, "Friday 02:00 UTC is peak");
        Expect(Math.Abs(DeepSeekCost("kimi-code/k3", Utc(27, 12)) - 21.3) < 1e-9, "a non-DeepSeek model is unchanged off-peak");
        Expect(Math.Abs(DeepSeekCost("kimi-code/k3", Utc(24, 2)) - 21.3) < 1e-9, "a non-DeepSeek model is unchanged on a weekend");
    }

    private static void DeepSeekHarness()
    {
        var offPeak = new TokenEvent(TriggerTool.DeepSeek, Utc(27, 12), "deepseek-flash", 1_000_000, 1_000_000, 0, 1_000_000);
        var peak = offPeak with { Timestamp = Utc(27, 7) };
        Expect(Math.Abs(Pricing.Cost(offPeak) - 0.753) < 1e-9, "off-peak harness event");
        Expect(Math.Abs(Pricing.Cost(peak) - 1.506) < 1e-9, "peak harness event");
    }

    private static void Rate(string model, double input, double output, double cacheCreation, double cacheRead) =>
        Expect(Math.Abs(Million(model) - (input + output + cacheCreation + cacheRead)) < 1e-9,
            $"{model}: expected {input + output + cacheCreation + cacheRead}, got {Million(model)}");

    private static void KimiK31Pro()
    {
        Rate("kimi-k3.1-pro", 2, 10, 2, 0.2);
        Rate("kimi-code/k3.1-pro", 2, 10, 2, 0.2);
        Expect(Pricing.IsKnown("kimi-code/k3.1-pro"), "the plan id is priced");
    }

    private static void KimiHighspeed()
    {
        Rate("kimi-k3.1-pro-highspeed", 4, 20, 4, 0.2);
        Rate("kimi-code/k3.1-pro-highspeed", 4, 20, 4, 0.2);
        Rate("kimi-k2.7-code-highspeed", 1.9, 8, 1.9, 0.38);
        Rate("kimi-code/kimi-for-coding-highspeed", 1.9, 8, 1.9, 0.38);
    }

    private static void KimiK28Preview()
    {
        Expect(Pricing.IsKnown("kimi-code/kimi-for-coding"), "K2.8 Preview is priced");
        Rate("kimi-code/kimi-for-coding", 1, 4, 1, 0.25);
        Rate("kimi-code/k3", 3, 15, 3, 0.3);
    }
}
