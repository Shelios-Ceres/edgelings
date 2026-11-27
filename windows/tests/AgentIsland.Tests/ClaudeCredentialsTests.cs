using System.Text.Json.Nodes;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// ClaudeCredentials 现在只读：认出“需要重新登录”的文案、描述登录在哪里、
/// 给终端登录的轮询一个变化信号。这里全部对着临时凭证文件，真实文件从不读写。
/// 刷新和写回的测试随那套代码一起删除了。
public static class ClaudeCredentialsTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("Describe reports the file's plan and no token, and none when there is no login", DescribeFromFile),
            ("the modification stamp is null without a file and changes when the file does", StampFollowsFile),
            ("on a Mac the not-enabled caption keeps the last numbers and is no failure", MacNotEnabledKeepsSnapshot),
        };
        var directory = Path.Combine(Path.GetTempPath(), "agentisland-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousFile = ClaudeCredentials.CredentialsFileOverride;
        try
        {
            ClaudeCredentials.CredentialsFileOverride = Path.Combine(directory, ".credentials.json");
            foreach (var (name, test) in tests)
            {
                if (File.Exists(CredentialsPath)) File.Delete(CredentialsPath);
                test();
                Console.WriteLine($"PASS claude credentials: {name}");
            }
            Console.WriteLine("ClaudeCredentialsTests GREEN");
        }
        finally
        {
            ClaudeCredentials.CredentialsFileOverride = previousFile;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CredentialsPath => ClaudeCredentials.CredentialsFileOverride!;

    private static void DescribeFromFile()
    {
        Expect(ClaudeCredentials.Describe().Source, "none");
        File.WriteAllText(CredentialsPath, new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = "a-token",
                ["refreshToken"] = "r-token",
                ["subscriptionType"] = "max",
                ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(90).ToUnixTimeMilliseconds(),
            },
        }.ToJsonString());
        var login = ClaudeCredentials.Describe();
        Expect(login.Source, "file");
        Expect(login.Plan, "max");
        Expect(login.MinutesLeft is > 80 and <= 90, "minutes left is read from expiresAt");
        Expect(!login.ToString().Contains("token"), "the description carries no token");
        File.WriteAllText(CredentialsPath, "{\"mcpOAuth\":{}}");
        Expect(ClaudeCredentials.Describe().Source, "none");
    }

    private static void StampFollowsFile()
    {
        Expect(ClaudeCredentials.CredentialsModificationStamp() is null, "no file, no stamp");
        File.WriteAllText(CredentialsPath, "{}");
        File.SetLastWriteTimeUtc(CredentialsPath, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        var first = ClaudeCredentials.CredentialsModificationStamp();
        Expect(first is not null, "a file gives a stamp");
        File.SetLastWriteTimeUtc(CredentialsPath, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        Expect(ClaudeCredentials.CredentialsModificationStamp() != first, "a newer file gives another stamp");
    }

    private static void MacNotEnabledKeepsSnapshot()
    {
        var reply = new AppUsage(new WindowUsage(0.4, null, null) { Observed = true }, new WindowUsage(0.2, null, null) { Observed = true });
        var store = new UsageStore(restore: false)
        {
            ClaudeShown = () => true,
            CodexShown = () => false,
            ClaudeFetcher = _ => Task.FromResult(reply),
        };
        store.Refresh();
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        Expect(Math.Abs(store.Claude.FiveHour.UsedPercent - 0.4) < 1e-9, "the first reading is shown");
        reply = AppUsage.ErrorPair(ClaudeUsageProbe.MacNotEnabledMessage);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (store.Loading && DateTime.UtcNow < until) Thread.Sleep(10);
        store.Refresh();
        store.RefreshTask!.Wait(TimeSpan.FromSeconds(5));
        until = DateTime.UtcNow.AddSeconds(5);
        while (store.Loading && DateTime.UtcNow < until) Thread.Sleep(10);
        Expect(Math.Abs(store.Claude.FiveHour.UsedPercent - 0.4) < 1e-9, "the old snapshot is kept");
        Expect(store.RefreshWarning is null, "not a failed fetch, so no footer warning");
        Expect(store.Claude.FiveHour.Error, ClaudeUsageProbe.MacNotEnabledMessage);
    }
}
