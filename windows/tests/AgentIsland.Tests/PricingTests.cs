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
