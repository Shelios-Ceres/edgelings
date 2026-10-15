namespace AgentIsland.WinUI.Shell;

internal static class Pixels
{
    /// A popup's edge on a whole physical pixel: an offset between pixels (at
    /// 150 %) smears a hairline or a pixel outline across two.
    public static double Snap(double dip, double scale) => Math.Round(dip * scale) / scale;
}
