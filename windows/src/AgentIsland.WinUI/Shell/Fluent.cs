using Microsoft.UI.Xaml;

namespace AgentIsland.WinUI.Shell;

/// Theme resources for a given theme, not the one the app started in: the
/// drawer's surfaces follow the system theme live.
internal static class Fluent
{
    public static T Resource<T>(string key, bool dark)
    {
        if (Find(Application.Current.Resources, key, dark ? "Dark" : "Light") is T found) return found;
        if (dark && Find(Application.Current.Resources, key, "Default") is T fallback) return fallback;
        return (T)Application.Current.Resources[key];
    }

    private static object? Find(ResourceDictionary dictionary, string key, string theme)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed)
            && themed is ResourceDictionary resources
            && resources.TryGetValue(key, out var value))
        {
            return value;
        }
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Find(merged, key, theme) is { } found) return found;
        }
        return null;
    }
}
