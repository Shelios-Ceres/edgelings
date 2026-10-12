namespace AgentIsland.UI;

/// The WPF app's reading of the screen choice, through WinForms' Screen.
internal static class ScreenChoice
{
    public static System.Windows.Forms.Screen Resolve(this Model.IslandTargetDisplayStore store)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (store.Choice != "auto")
        {
            var pinned = screens.FirstOrDefault(s => s.DeviceName == store.Choice);
            if (pinned is not null) return pinned;
        }
        return System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }
}
