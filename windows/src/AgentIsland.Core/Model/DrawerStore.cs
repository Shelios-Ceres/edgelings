using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

public enum DrawerSide
{
    Right,
    Left,
}

/// The drawer's character set: the bundled pixel bots, one per agent, or the
/// classic shapes that can be reshaped and recoloured, drawn by the bundled
/// Classic engine.
public enum CharacterTheme
{
    Pixel,
    Classic,
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
    private const string FrameRateKey = "AgentIsland.drawer.fps";
    private const string SaveBatteryKey = "AgentIsland.drawer.saveBattery";
    private const string SpeechKey = "AgentIsland.drawer.speech";
    private const string ThemeKey = "AgentIsland.drawer.theme";
    private const string LockedKey = "AgentIsland.drawer.locked";
    private const string PinnedTopKey = "AgentIsland.drawer.pinnedTop";
    private const string OrderKey = "AgentIsland.drawer.order";

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

    /// Hidden by the user, as opposed to off for want of the agent on this
    /// computer.
    public bool IsHidden(DisplayProvider provider) => _hidden.Contains(provider.RawValue());

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

    /// Frame-rate ceiling for busy characters: 0 follows the display's
    /// refresh rate.
    public int FrameRate
    {
        get => Preferences.Get<int?>(FrameRateKey) ?? 0;
        set
        {
            if (FrameRate == value) return;
            Preferences.Set(FrameRateKey, value);
            Raise(nameof(FrameRate));
        }
    }

    /// On battery the ceiling drops to 60 fps unless the user turned this off.
    public bool SaveBattery
    {
        get => Preferences.Get<bool?>(SaveBatteryKey) ?? true;
        set
        {
            if (SaveBattery == value) return;
            Preferences.Set(SaveBatteryKey, value);
            Raise(nameof(SaveBattery));
        }
    }

    /// Characters say a line when their agent needs you.
    public bool SpeechBubbles
    {
        get => Preferences.Get<bool?>(SpeechKey) ?? true;
        set
        {
            if (SpeechBubbles == value) return;
            Preferences.Set(SpeechKey, value);
            Raise(nameof(SpeechBubbles));
        }
    }

    /// Which character set the drawer shows; the pixel bots unless chosen otherwise.
    public CharacterTheme Theme
    {
        get => Enum.TryParse<CharacterTheme>(Preferences.Get<string?>(ThemeKey), out var theme) ? theme : CharacterTheme.Pixel;
        set
        {
            if (Theme == value) return;
            Preferences.Set(ThemeKey, value.ToString());
            Raise(nameof(Theme));
        }
    }

    /// The characters stay put: dragging does not move the drawer, and a
    /// character appearing or leaving does not shift the ones above it.
    public bool Locked
    {
        get => Preferences.Get<bool?>(LockedKey) ?? false;
        set
        {
            if (Locked == value) return;
            Preferences.Set(LockedKey, value);
            Raise(nameof(Locked));
        }
    }

    /// While locked: the drawer's top edge, in DIPs below the work area's top.
    public double? PinnedTop
    {
        get => Preferences.Get<double?>(PinnedTopKey);
        set
        {
            if (PinnedTop == value) return;
            if (value is { } top) Preferences.Set(PinnedTopKey, top);
            else Preferences.Remove(PinnedTopKey);
            Raise(nameof(PinnedTop));
        }
    }

    /// The characters' order down the drawer, top first (DrawerPlacement.Order).
    public IReadOnlyList<DisplayProvider> Order => DrawerPlacement.Order(Preferences.Get<List<string>?>(OrderKey));

    /// Providers left out keep their slots (DrawerPlacement.Reordered).
    public void SetOrder(IEnumerable<DisplayProvider> order)
    {
        var current = Order;
        var next = DrawerPlacement.Reordered(current, order);
        if (next.SequenceEqual(current)) return;
        Preferences.Set(OrderKey, next.Select(provider => provider.RawValue()).ToList());
        Raise(nameof(Order));
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
