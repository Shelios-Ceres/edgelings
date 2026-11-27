using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AgentIsland.WinUI.Settings;

/// AGENTISLAND_DEBUG_USAGE_SHOTS=<dir>：Usage 页在假数据上的样子（第 9 批），
/// 默认宽度的今天和本月、最窄宽度的今天各存一张 PNG 后退出。外观用 AGENTISLAND_DEBUG_CARD_LOOK，语言用 AGENTISLAND_LANG。
/// 必须同时设 LIMEN_ROOT（Program 里检查）：设置、检测用的目录都在那下面。
/// 不建抽屉和托盘，不启动任何 store 的刷新，不扫描会话，不读任何账号；
/// 也不抢单实例锁，所以不会惊动正在运行的 Limen。
internal sealed partial class SettingsWindow
{
    public static async Task UsageShotsAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // 外观和语言在开窗前定好（AGENTISLAND_DEBUG_CARD_LOOK、AGENTISLAND_LANG），每种组合跑一次进程。
            var pixel = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_CARD_LOOK") == "pixel";
            DrawerStore.Shared.Theme = pixel ? CharacterTheme.Pixel : CharacterTheme.Classic;
            SeedUsage(DateTimeOffset.Now);
            OpenUsage();
            await Task.Delay(1500);
            if (_shared is not { } window) return;
            var name = $"{(pixel ? "pixel" : "classic")}-{(L10n.IsChinese ? "zh" : "en")}";
            foreach (var (width, wide) in new[] { (980, "wide"), (720, "narrow") })
            {
                var scale = window.Content.XamlRoot?.RasterizationScale ?? 1;
                window.AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(900 * scale)));
                await Task.Delay(1200);
                var periods = wide == "wide" ? new[] { (0, "today"), (2, "month") } : new[] { (0, "today") };
                foreach (var (index, period) in periods)
                {
                    window._costPage?.DebugPeriod(index);
                    await Task.Delay(1500);
                    if (window._scroll.Content is UIElement body)
                        await Shell.Snapshot.SaveAsync(body, Path.Combine(directory, $"usage-{name}-{wide}-{period}.png"));
                }
            }
            DebugTrace.Write("usage shots saved in " + directory);
        }
        catch (Exception error)
        {
            DebugTrace.Write("usage shots failed: " + error);
        }
        Application.Current.Exit();
    }

    /// 假数据：检测用的目录、额度快照（经 Preferences 恢复，和真实启动走同一条路）
    /// 和各家的 token 事件（经 CostSummarizer 汇总，和真实扫描走同一条路）。
    private static void SeedUsage(DateTimeOffset now)
    {
        if (IslandPaths.RootOverride is null) throw new InvalidOperationException("LIMEN_ROOT is not set");
        foreach (var folder in new[]
        {
            Path.Combine(IslandPaths.Home, ".claude"), Path.Combine(IslandPaths.Home, ".codex"),
            IslandPaths.KimiSessionsRoot, IslandPaths.GrokSessionsRoot, IslandPaths.DeepSeekSessionsRoot,
        }) Directory.CreateDirectory(folder);

        WindowUsage Window(double used, TimeSpan resetsIn, double period) =>
            new(used, now + resetsIn, null, period) { Observed = true };
        Preferences.Set(UsageStore.CacheKey, new UsageCacheSnapshot
        {
            Claude = new AppUsage(Window(0.38, TimeSpan.FromMinutes(134), 5 * 3600), Window(0.61, TimeSpan.FromHours(75), 7 * 86400), "max"),
            Codex = new AppUsage(Window(0.12, TimeSpan.FromMinutes(200), 5 * 3600), Window(0.93, TimeSpan.FromHours(30), 7 * 86400), "plus"),
            UpdatedAt = now.AddMinutes(-3),
            ClaudeUpdatedAt = now.AddMinutes(-3),
            CodexUpdatedAt = now.AddMinutes(-26),
        });
        Preferences.Set(KimiUsageStore.CacheKey, new KimiCachedSnapshot
        {
            Snapshot = new KimiUsageSnapshot(
                new KimiQuotaWindow(0.22, now.AddMinutes(90)),
                new KimiQuotaWindow(0.47, now.AddDays(4)),
                new KimiQuotaWindow(0.35, new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(1)),
                new KimiQuotaWindow(0.21, null),
                "Moderato", 1250, "CNY"),
            UpdatedAt = now.AddMinutes(-1),
        });

        // 当天的几点和本月前几天，各家各模型都有；每条的四类 token 不一样，拆分才看得出来。
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        var events = new Dictionary<DisplayProvider, List<TokenEvent>>();
        void Add(DisplayProvider provider, TriggerTool tool, string model, long input, long output, long write, long read, double? dollars = null, string? pricedAs = null)
        {
            if (!events.TryGetValue(provider, out var list)) events[provider] = list = new List<TokenEvent>();
            foreach (var hour in new[] { 1, Math.Max(2, now.Hour - 1) })
            {
                var at = midnight.AddHours(Math.Min(hour, now.Hour)).AddMinutes(5);
                list.Add(new TokenEvent(tool, at, model, input, output, write, read, dollars) { PricedAs = pricedAs });
            }
            for (var day = 1; day < Math.Min(now.Day, 9); day++)
            {
                list.Add(new TokenEvent(tool, midnight.AddDays(-day).AddHours(15), model, input * 3, output * 3, write * 3, read * 3, dollars * 3) { PricedAs = pricedAs });
            }
        }
        Add(DisplayProvider.Claude, TriggerTool.Claude, "claude-opus-5-5", 42_000, 18_000, 120_000, 2_400_000);
        Add(DisplayProvider.Claude, TriggerTool.Claude, "claude-sonnet-5-5", 18_000, 6_000, 40_000, 900_000);
        // 外接 API：经 ANTHROPIC_BASE_URL 接到别家的模型，留在 Claude 名下，价格表里没有它。
        Add(DisplayProvider.Claude, TriggerTool.Claude, "glm-5.1", 30_000, 9_000, 0, 260_000);
        Add(DisplayProvider.Codex, TriggerTool.Codex, "gpt-6.2-sol", 64_000, 21_000, 0, 1_800_000);
        Add(DisplayProvider.Codex, TriggerTool.Codex, "gpt-5.3-codex-spark", 12_000, 4_000, 0, 300_000);
        Add(DisplayProvider.Codex, TriggerTool.Codex, "codex-auto-review", 8_000, 1_000, 0, 90_000);
        Add(DisplayProvider.Kimi, TriggerTool.Kimi, "kimi-code/k3.1-pro", 36_000, 12_000, 0, 1_100_000);
        // 外接 API：Kimi Code 里配的 DeepSeek，按 DeepSeek 自己的价格计。
        Add(DisplayProvider.Kimi, TriggerTool.Kimi, "deepseek/deepseek-flash", 50_000, 15_000, 0, 700_000, pricedAs: "deepseek-flash");
        Add(DisplayProvider.Kimi, TriggerTool.Kimi, "kimi-code/kimi-for-coding", 9_000, 3_000, 0, 120_000);
        Add(DisplayProvider.Grok, TriggerTool.Grok, "grok-4.7-build", 20_000, 7_000, 0, 400_000, dollars: 0.42);
        // 一家全部没有价格：卡上的大数字写“未定价”。
        Add(DisplayProvider.DeepSeek, TriggerTool.DeepSeek, "deepseek-v5-preview", 25_000, 8_000, 0, 300_000);

        var names = new Dictionary<string, string> { ["kimi-code/k3.1-pro"] = "K3.1 Pro", ["kimi-code/kimi-for-coding"] = "K2.8 Preview" };
        CostStore.Shared.DebugCommit(events.ToDictionary(
            pair => pair.Key,
            pair => CostSummarizer.Summarize(pair.Value, now) with { ModelNames = pair.Key == DisplayProvider.Kimi ? names : new Dictionary<string, string>() }));
    }
}
