namespace AgentIsland.Core;

/// Whether the drawer steps aside for a full-screen app. On Windows the guard asks this every second:
/// an app in front that fills its display covers the drawer unless the user chose to keep it showing.
/// A full-screen overlay (the snipping layer) never covers it. On a Mac the same choice decides whether
/// the drawer's panels join full-screen Spaces.
public static class FullscreenRule
{
    public static bool Covers(bool foregroundIsFullscreen, bool foregroundIsOverlay, bool showOverFullscreen) =>
        foregroundIsFullscreen && !foregroundIsOverlay && !showOverFullscreen;
}
