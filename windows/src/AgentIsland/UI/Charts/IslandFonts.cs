using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI.Charts;

public static class IslandFonts
{
    // Explicit CJK fallback: without it WPF walks its composite font for
    // missing glyphs and Chinese lands on a serif face that clashes with
    // the Latin sans (the macOS build gets PingFang for free from SF Pro).
    public static readonly FontFamily Ui = new(
        "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

    public static readonly FontFamily Mono = new(
        "Cascadia Mono, Consolas, Microsoft YaHei UI, Microsoft YaHei");
}
