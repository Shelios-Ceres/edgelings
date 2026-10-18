using System.ComponentModel;
using System.IO;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Model;

/// Which providers are on screen. The side drawer is the only surface: a
/// provider shows when this machine has its footprint, unless the user
/// overrides that from the drawer menu or Settings (DrawerStore). Quota
/// refreshes and usage attention follow the same answer.
///
/// Detection: Claude/Codex are probed once at launch; the zero-intrusion
/// guests re-probe on the refresh cadence, so a sign-in made while the app
/// runs shows up without a relaunch.
public sealed class ProviderVisibilityStore : INotifyPropertyChanged
{
    public static ProviderVisibilityStore Shared { get; } = new();

    private readonly Dictionary<DisplayProvider, bool> _detected = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private ProviderVisibilityStore()
    {
        // The recording rig has no footprint to find: AGENTISLAND_DEMO_PROVIDERS
        // (default Claude + Codex) stands in for detection.
        HashSet<DisplayProvider>? demo = null;
        if (AppEnvironment.IsDemo)
        {
            demo = DemoProviders().Select(DisplayProviders.Parse).OfType<DisplayProvider>().ToHashSet();
            if (demo.Count == 0) demo = new HashSet<DisplayProvider> { DisplayProvider.Claude, DisplayProvider.Codex };
        }
        foreach (var provider in DisplayProviders.All)
        {
            _detected[provider] = demo?.Contains(provider) ?? Probe(provider);
        }
        DrawerStore.Shared.PropertyChanged += (_, _) => Raise();
    }

    /// CLI or login footprint on this machine.
    public bool IsDetected(DisplayProvider provider) =>
        _detected.TryGetValue(provider, out var detected) && detected;

    /// The provider's character has something here: Grok's while either
    /// Grok or Cursor is (DisplayProviderExtensions.Character).
    public bool IsCharacterDetected(DisplayProvider provider) => provider.Character() == DisplayProvider.Grok
        ? IsDetected(DisplayProvider.Grok) || IsDetected(DisplayProvider.Cursor)
        : IsDetected(provider);

    /// One choice per character: Cursor follows Grok's.
    public bool IsShown(DisplayProvider provider) =>
        DrawerStore.Shared.IsShown(provider.Character(), IsCharacterDetected(provider));

    /// Single accessor for call sites holding a TriggerTool.
    public bool IsVisible(TriggerTool tool) => IsShown(tool.ToDisplayProvider());

    public void SetShown(DisplayProvider provider, bool shown) => DrawerStore.Shared.SetShown(provider.Character(), shown);

    /// Hidden by the user: out of the drawer, its menus, Usage & cost and
    /// the Agents page's own groups.
    public bool IsHidden(DisplayProvider provider) => DrawerStore.Shared.IsHidden(provider.Character());

    public bool ClaudeDetected => IsDetected(DisplayProvider.Claude);
    public bool CodexDetected => IsDetected(DisplayProvider.Codex);
    public bool AntigravityDetected => IsDetected(DisplayProvider.Antigravity);
    public bool GrokDetected => IsDetected(DisplayProvider.Grok);
    public bool CursorDetected => IsDetected(DisplayProvider.Cursor);
    public bool KimiDetected => IsDetected(DisplayProvider.Kimi);
    public bool DeepSeekDetected => IsDetected(DisplayProvider.DeepSeek);

    /// Re-probe the zero-intrusion guests (macOS redetectGuests).
    public void RedetectGuests()
    {
        if (AppEnvironment.IsDemo) return;
        var changed = false;
        foreach (var provider in DisplayProviders.Guests)
        {
            var detected = Probe(provider);
            if (_detected.TryGetValue(provider, out var known) && known == detected) continue;
            _detected[provider] = detected;
            changed = true;
        }
        if (changed) Raise();
    }

    private static bool Probe(DisplayProvider provider)
    {
        var home = IslandPaths.Home;
        return provider switch
        {
            DisplayProvider.Claude => HasDir(home, ".claude") || HasDir(home, ".config", "claude"),
            DisplayProvider.Codex => HasDir(home, ".codex"),
            // Antigravity is probed by its data roots: running agy (or the
            // IDE) once creates them, and sign-in state lives in the
            // platform credential store where a presence probe proves
            // nothing about the local quota server being reachable anyway.
            DisplayProvider.Antigravity => AntigravityCredentials.Detected,
            DisplayProvider.Grok => GrokAuthFile.Exists(),
            // Cursor's editor state db is also where its session token lives.
            DisplayProvider.Cursor => CursorCredentials.Exists(),
            // Both write their session trees on first use. Kimi Code's plan
            // quota comes through its own local server (KimiUsageFetcher).
            DisplayProvider.Kimi => Directory.Exists(IslandPaths.KimiSessionsRoot),
            DisplayProvider.DeepSeek => Directory.Exists(IslandPaths.DeepSeekSessionsRoot),
            _ => false,
        };
    }

    private static bool HasDir(params string[] parts) => Directory.Exists(Path.Combine(parts));

    private static IEnumerable<string> DemoProviders()
    {
        var raw = Environment.GetEnvironmentVariable("AGENTISLAND_DEMO_PROVIDERS");
        return string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// Everything here derives from detection plus DrawerStore, so one
    /// "all properties" notification covers every reader.
    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
