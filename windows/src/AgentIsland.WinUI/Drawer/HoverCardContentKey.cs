using System.Globalization;
using System.Text;
using AgentIsland.Model;

namespace AgentIsland.WinUI.Drawer;

/// 全员卡的面板信息：钉在面板头右边的那家额度，和底部的合计 token。
internal sealed record HoverCardPanel(StatusQuota? Pinned, string PinnedName, StatusTokens? Total);

/// 卡上看得见的内容：变了才重建。不含时间，也不含额度条的比例和“N 分钟前更新”这类卡上已不显示的东西。
internal static class HoverCardContentKey
{
    public static string Create(IReadOnlyList<(StatusCard Card, uint Swatch)> sections, bool dark, bool pixel, double scale, HoverCardPanel? panel = null)
    {
        var key = new StringBuilder();
        void Add(object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            key.Append(text.Length).Append(':').Append(text);
        }
        void Quota(StatusQuota? quota)
        {
            Add(quota is not null);
            if (quota is null) return;
            Add(quota.Stale);
            Add(quota.Windows.Count);
            foreach (var window in quota.Windows) { Add(window.Label); Add(window.Value); Add(window.Used is null); Add(window.Warn); }
        }
        void Tokens(StatusTokens? tokens)
        {
            Add(tokens is not null);
            if (tokens is null) return;
            Add(tokens.Label); Add(tokens.Today); Add(tokens.TodayNumber); Add(tokens.Month); Add(tokens.MonthNumber);
        }
        Add(dark);
        Add(pixel);
        // Pixel font sizing changes with physical scale even if every value is unchanged.
        if (pixel) Add(scale);
        Add(sections.Count);
        foreach (var (card, swatch) in sections)
        {
            Add(card.Provider); Add(swatch); Add(card.Name); Add(card.Plan);
            Add(card.Away); Add(card.Loading);
            Add(StatusTaskRows.Key(card.TaskRows));
            Quota(card.Quota);
            Add(card.Warnings.Count);
            foreach (var warning in card.Warnings) Add(warning);
            Tokens(card.Tokens);
        }
        Add(panel is not null);
        if (panel is not null)
        {
            Add(panel.PinnedName);
            Quota(panel.Pinned);
            Tokens(panel.Total);
        }
        return key.ToString();
    }
}
