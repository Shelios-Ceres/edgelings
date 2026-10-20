namespace AgentIsland.Tests;

/// The assertions every suite uses, brought in with `using static`.
public static class Check
{
    public static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Expect<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}, got {actual}");
    }
}
