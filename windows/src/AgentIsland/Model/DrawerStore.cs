using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

public enum DrawerSide
{
    Right,
    Left,
}

/// Persisted side-drawer layout: which edge, where along it, which providers
/// it shows and how each provider's character looks. Visibility defaults to
/// detection, so a provider with no footprint on this machine stays out
/// until the user adds it from the drawer's menu.
public sealed class DrawerStore : INotifyPropertyChanged
{
    private const string SideKey = "AgentIsland.drawer.side";
    private const string OffsetKey = "AgentIsland.drawer.offset";
    private const string ShownKey = "AgentIsland.drawer.shown";
    private const string HiddenKey = "AgentIsland.drawer.hidden";
    private const string LookKeyPrefix = "AgentIsland.drawer.look.";

    public static DrawerStore Shared { get; } = new();

    private DrawerSide _side;
    private double _offset;
    private HashSet<string> _shown;
    private HashSet<string> _hidden;

    public event PropertyChangedEventHandler? PropertyChanged;

    private DrawerStore()
    {
        _side = Enum.TryParse<DrawerSide>(Preferences.Get<string?>(SideKey), out var side) ? side : DrawerSide.Right;
        _offset = Math.Clamp(Preferences.Get<double?>(OffsetKey) ?? 0.5, 0, 1);
        _shown = new HashSet<string>(Preferences.Get<List<string>?>(ShownKey) ?? new List<string>());
        _hidden = new HashSet<string>(Preferences.Get<List<string>?>(HiddenKey) ?? new List<string>());
    }

    public DrawerSide Side
    {
        get => _side;
        set
        {
            if (_side == value) return;
            _side = value;
            Preferences.Set(SideKey, value.ToString());
            Raise(nameof(Side));
        }
    }

    /// Vertical position of the drawer's centre along the work area, 0..1.
    public double Offset
    {
        get => _offset;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_offset - clamped) < 0.0001) return;
            _offset = clamped;
            Preferences.Set(OffsetKey, clamped);
            Raise(nameof(Offset));
        }
    }

    public bool IsShown(DisplayProvider provider, bool detected)
    {
        var raw = provider.RawValue();
        if (_hidden.Contains(raw)) return false;
        return _shown.Contains(raw) || detected;
    }

    public void SetShown(DisplayProvider provider, bool shown)
    {
        var raw = provider.RawValue();
        if (shown)
        {
            _hidden.Remove(raw);
            _shown.Add(raw);
        }
        else
        {
            _shown.Remove(raw);
            _hidden.Add(raw);
        }
        Preferences.Set(ShownKey, _shown.OrderBy(v => v).ToList());
        Preferences.Set(HiddenKey, _hidden.OrderBy(v => v).ToList());
        Raise(nameof(IsShown));
    }

    /// "shape/color" engine ids, or null to use the provider's default.
    public (string Shape, string Color)? Look(DisplayProvider provider)
    {
        if (Preferences.Get<string?>(LookKeyPrefix + provider.RawValue()) is not { } raw) return null;
        var parts = raw.Split('/');
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    public void SetLook(DisplayProvider provider, string shape, string color)
    {
        Preferences.Set(LookKeyPrefix + provider.RawValue(), $"{shape}/{color}");
        Raise(nameof(Look));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
