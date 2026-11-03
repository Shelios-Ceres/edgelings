using AgentIsland.Localization;
using AgentIsland.Model;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// The Chinese copy the Pixel style now shows: it follows the app language
/// like Classic does, so every look name, the look row's description and the
/// locked bubble need their zh entries.
public static class PixelChineseTests
{
    public static void RunAll()
    {
        var language = L10n.Current;
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            var kept = new HashSet<string> { "Clawd", "Aeri" };
            foreach (var theme in new[] { CharacterTheme.Pixel, CharacterTheme.Classic })
            {
                foreach (var provider in DisplayProviders.All)
                {
                    foreach (var look in LookCatalog.For(theme, provider))
                    {
                        Expect(kept.Contains(look.Name) || L10n.Tr(look.Name) != look.Name, $"{theme} look '{look.Name}' has no Chinese name");
                    }
                }
            }
            Expect(L10n.Tr("Owl") == "猫头鹰" && L10n.Tr("Sea otter") == "海獭", "the owl and the sea otter are named");
            Expect(L10n.TrFormat("{0}. What {1} wears in the drawer, on its card and in menus", "猫头鹰", "Codex") == "猫头鹰 · Codex 在抽屉、卡片和菜单里的样子",
                "the look row's description");
            Expect(L10n.TrFormat("{0}. What {1} wear in the drawer, on its card and in menus", "黑猫", "Grok 和 Cursor") == "黑猫 · Grok 和 Cursor 在抽屉、卡片和菜单里的样子",
                "the shared look row's description");
            Expect(L10n.Tr("Display") == "显示器" && L10n.Tr("Display") != L10n.Tr("Shown"), "the Drawer page's Display row is not named like the Shown switch");
            Expect(L10n.Tr("Locked in place.\nRight-click to unlock.") == "已经固定住啦。\n右键可以解锁。", "the locked bubble breaks after its first sentence");
            Expect(L10n.Tr("The pixel style shows Settings in English") == "The pixel style shows Settings in English", "the Settings-in-English note is gone from the table");
            Expect(L10n.Tr("The pixel font for Chinese, by {0} · SIL Open Font License 1.1") != "The pixel font for Chinese, by {0} · SIL Open Font License 1.1", "the Fusion Pixel credit");
            // A number and its Han unit are set apart ("5 小时", "13 秒前"): no digit touches a Han character in the zh
            // table, nor in what the time formats print. A placeholder next to Han is allowed where it holds a whole
            // duration ("{0}重置" is "1 小时后重置"); the percent sign is not a digit's unit here.
            var touching = new System.Text.RegularExpressions.Regex(@"\d[㐀-鿿]|[㐀-鿿]\d");
            var allow = new HashSet<string>();
            foreach (var (key, value) in L10n.ChineseEntries)
            {
                Expect(allow.Contains(key) || !touching.IsMatch(value), $"zh '{key}' sets a digit against a Han character: {value}");
            }
            foreach (var seconds in new[] { 3, 5, 59, 60, 119, 3599, 3600, 7300, 86399, 86400, 200000, 400000 })
            {
                var span = TimeSpan.FromSeconds(seconds);
                Expect(!touching.IsMatch(AgentIsland.Core.Formatting.RelativeAgo(span, true)), $"RelativeAgo {seconds}: {AgentIsland.Core.Formatting.RelativeAgo(span, true)}");
                Expect(!touching.IsMatch(AgentIsland.Core.Formatting.LongCountdown(span, true)), $"LongCountdown {seconds}: {AgentIsland.Core.Formatting.LongCountdown(span, true)}");
            }
            Expect(AgentIsland.Core.Formatting.LongCountdown(TimeSpan.FromHours(1.4), true) == "1 小时后" && AgentIsland.Core.Formatting.RelativeAgo(TimeSpan.FromSeconds(13), true) == "13 秒前", "the countdown and the age");
            Console.WriteLine("PASS Pixel in Chinese: look names, the look row, the locked bubble and the font credit");
        }
        finally
        {
            L10n.Current = language;
        }
    }
}
