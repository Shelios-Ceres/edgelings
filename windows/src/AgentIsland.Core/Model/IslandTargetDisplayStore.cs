using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

/// Which screen hosts the drawer: Auto (primary) or pinned to a specific
/// display by device name, falling back to Auto when it's unplugged. Each
/// UI resolves the choice against its own screen API.
public sealed class IslandTargetDisplayStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.targetDisplay";

    public static IslandTargetDisplayStore Shared { get; } = new();

    private string _choice;

    public event PropertyChangedEventHandler? PropertyChanged;

    private IslandTargetDisplayStore()
    {
        _choice = Preferences.Get<string?>(Key) ?? "auto";
    }

    /// "auto" or a display device name such as \\.\DISPLAY1.
    public string Choice
    {
        get => _choice;
        set
        {
            if (_choice == value) return;
            _choice = value;
            Preferences.Set(Key, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Choice)));
        }
    }
}
