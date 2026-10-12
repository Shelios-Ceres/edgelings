using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using AgentIsland.WinUI.Platform;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Settings;

/// Settings in the Windows 11 idiom: Mica, a navigation pane and setting
/// cards. Agents leads, since that is what the app is about; the drawer,
/// usage and general preferences follow.
internal sealed class SettingsWindow : Window
{
    private enum Page
    {
        Agents,
        Drawer,
        Usage,
        General,
    }

    private const string PageKey = "Settings.page";
    private const string RepositoryUrl = "https://github.com/tristan666666/agent-island";

    private static SettingsWindow? _shared;

    private readonly NavigationView _nav = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        IsPaneToggleButtonVisible = false,
        OpenPaneLength = 260,
    };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollMode = ScrollMode.Disabled };
    private readonly Dictionary<DisplayProvider, AgentSection> _agents = new();
    private readonly List<Action> _teardown = new();
    private readonly DispatcherQueueTimer _tick;
    private Page _page;
    private bool _dirty = true;
    private int _ticks;

    public static void Open(DisplayProvider? focus = null)
    {
        _shared ??= new SettingsWindow();
        if (focus is { } provider) _shared.Focus(provider);
        _shared.Activate();
    }

    /// AGENTISLAND_DEBUG_SNAPSHOT: every page, rendered by the app itself.
    public static async Task SnapshotPagesAsync(string directory, DisplayProvider? focus)
    {
        Open(focus);
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        foreach (var page in Enum.GetValues<Page>())
        {
            if (page != Page.Agents || focus is null) window._nav.SelectedItem = window._nav.MenuItems[(int)page];
            if (page == Page.Agents && focus is { } provider) window.Focus(provider);
            await Task.Delay(1500);
            if (window.Content is { } content) await Shell.Snapshot.SaveAsync(content, Path.Combine(directory, $"settings-{page}.png"));
            if (window._scroll.Content is UIElement body) await Shell.Snapshot.SaveAsync(body, Path.Combine(directory, $"settings-{page}-full.png"));
        }
    }

    /// Rebuilds the open window in the current language, on the same page.
    public static void Relocalize()
    {
        if (_shared is not { } open) return;
        open.Close();
        Open();
    }

    private SettingsWindow()
    {
        var root = Shell.Chrome.Apply(this, "Agent Island — " + L10n.Tr("Settings"), 980, 720, 720, 520);
        Shell.Chrome.SetBody(root, _nav);

        foreach (var (page, label, glyph) in new[]
        {
            (Page.Agents, "Agents", "\uE99A"),
            (Page.Drawer, "Drawer", "\uE7F4"),
            (Page.Usage, "Usage & cost", "\uE9D2"),
            (Page.General, "General", "\uE713"),
        })
        {
            _nav.MenuItems.Add(new NavigationViewItem { Content = L10n.Tr(label), Icon = Icon(glyph), Tag = page });
        }
        _nav.Content = _scroll;
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: Page page }) Show(page);
        };
        _page = Enum.TryParse<Page>(Preferences.Get<string?>(PageKey), out var saved) ? saved : Page.Agents;
        _nav.SelectedItem = _nav.MenuItems[(int)_page];

        Watch(UsageStore.Shared);
        Watch(AntigravityUsageStore.Shared);
        Watch(GrokUsageStore.Shared);
        Watch(CursorUsageStore.Shared);
        Watch(KimiUsageStore.Shared);
        Watch(CostStore.Shared);
        Watch(ProviderVisibilityStore.Shared);
        Watch(QuotaDisplayModeStore.Shared);
        Watch(TokenCountModeStore.Shared);
        Watch(ActivityMonitor.Shared);
        // The live parts — phases, "synced 2m ago" — move every second;
        // store changes rebuild the reports on the next tick.
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        Closed += (_, _) =>
        {
            _tick.Stop();
            foreach (var teardown in _teardown) teardown();
            _shared = null;
        };
    }

    private void Watch(INotifyPropertyChanged source)
    {
        PropertyChangedEventHandler handler = (_, _) => _dirty = true;
        source.PropertyChanged += handler;
        _teardown.Add(() => source.PropertyChanged -= handler);
    }

    private void Focus(DisplayProvider provider)
    {
        _nav.SelectedItem = _nav.MenuItems[(int)Page.Agents];
        Show(Page.Agents);
        if (!_agents.TryGetValue(provider, out var section)) return;
        section.Expander.IsExpanded = true;
        // After the expanded items have laid out, or the scroll falls short.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            section.Expander.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = true }));
    }

    private void Tick()
    {
        if (_page != Page.Agents) return;
        // Reports read a few small files (Codex's account); the clock-driven
        // "synced" captions refresh every fifth second.
        if (_dirty || ++_ticks % 5 == 0)
        {
            _dirty = false;
            foreach (var (provider, section) in _agents) section.Update(AgentReports.For(provider));
        }
        foreach (var section in _agents.Values) section.UpdateActivity();
    }

    private void Show(Page page)
    {
        _page = page;
        Preferences.Set(PageKey, page.ToString());
        _agents.Clear();
        var body = new StackPanel { Spacing = 4, MaxWidth = 1000, Padding = new Thickness(40, 28, 40, 40) };
        switch (page)
        {
            case Page.Agents:
                BuildAgents(body);
                break;
            case Page.Drawer:
                BuildDrawer(body);
                break;
            case Page.Usage:
                BuildUsage(body);
                break;
            default:
                BuildGeneral(body);
                break;
        }
        _scroll.Content = body;
        _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    // MARK: - Agents

    private void BuildAgents(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("Agents")));
        body.Children.Add(PageIntro(L10n.Tr("Quota, tokens and live activity for every coding agent on this computer. Each one shown in the drawer gets a character.")));
        var detected = DisplayProviders.All.Where(ProviderVisibilityStore.Shared.IsDetected).ToList();
        var missing = DisplayProviders.All.Except(detected).ToList();
        void Add(DisplayProvider provider)
        {
            var section = new AgentSection(provider, () => Content?.XamlRoot);
            _agents[provider] = section;
            section.Update(AgentReports.For(provider));
            section.UpdateActivity();
            body.Children.Add(section.Expander);
        }
        if (detected.Count > 0) body.Children.Add(Section(L10n.Tr("On this computer")));
        foreach (var provider in detected) Add(provider);
        if (missing.Count > 0) body.Children.Add(Section(L10n.Tr("Not detected")));
        foreach (var provider in missing) Add(provider);
    }

    // MARK: - Drawer

    private void BuildDrawer(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("Drawer")));
        body.Children.Add(PageIntro(L10n.Tr("The column of characters on the edge of your screen. Hover one for its status, drag it to move the drawer, right-click it for options.")));
        body.Children.Add(Section(L10n.Tr("Placement")));
        var store = DrawerStore.Shared;
        body.Children.Add(Card(L10n.Tr("Edge"), L10n.Tr("Which side of the screen the drawer docks to"), "\uE8AB",
            Combo(new[] { L10n.Tr("Right edge"), L10n.Tr("Left edge") }, store.Side == DrawerSide.Right ? 0 : 1,
                index => store.Side = index == 0 ? DrawerSide.Right : DrawerSide.Left)));

        var screens = Win32.Screens();
        var labels = new List<string> { L10n.Tr("Automatic (primary display)") };
        for (var i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            labels.Add(L10n.TrFormat("Display {0}", i + 1)
                + $"  {s.Bounds.Right - s.Bounds.Left}×{s.Bounds.Bottom - s.Bounds.Top}"
                + (s.Primary ? "  · " + L10n.Tr("primary") : ""));
        }
        var choice = IslandTargetDisplayStore.Shared.Choice;
        var selected = choice == "auto" ? 0 : screens.FindIndex(s => s.Device == choice) + 1;
        body.Children.Add(Card(L10n.Tr("Display"), L10n.Tr("A pinned display falls back to automatic while it is unplugged"), "\uE7F4",
            Combo(labels, Math.Max(0, selected), index =>
                IslandTargetDisplayStore.Shared.Choice = index == 0 ? "auto" : screens[index - 1].Device)));
        body.Children.Add(Card(L10n.Tr("Position"), L10n.Tr("Drag any character up or down to move the drawer"), "\uE7C2",
            Button(L10n.Tr("Center on the edge"), () => store.Offset = 0.5)));

        body.Children.Add(Section(L10n.Tr("Cards")));
        var quota = QuotaDisplayModeStore.Shared;
        body.Children.Add(Card(L10n.Tr("Quota shows"), L10n.Tr("How the bars and percentages read on cards and in Settings"), "\uE9D2",
            Combo(new[] { L10n.Tr("Used"), L10n.Tr("Remaining") }, quota.ShowsRemaining ? 1 : 0,
                index => quota.ShowsRemaining = index == 1)));

        body.Children.Add(Section(L10n.Tr("Motion")));
        var rates = new[] { 0, 120, 60 };
        body.Children.Add(Card(L10n.Tr("Frame rate while an agent works"), L10n.Tr("Calm characters always breathe at 30 fps"), "\uE916",
            Combo(new[] { L10n.Tr("Match the display"), L10n.TrFormat("{0} fps", 120), L10n.TrFormat("{0} fps", 60) },
                Math.Max(0, Array.IndexOf(rates, store.FrameRate)),
                index => store.FrameRate = rates[index])));
        body.Children.Add(Card(L10n.Tr("Save battery"), L10n.Tr("On battery, characters run at 60 fps at most"), "\uE83F",
            Toggle(store.SaveBattery, on => store.SaveBattery = on)));
        var motion = new HyperlinkButton { Content = L10n.Tr("Windows animation settings"), NavigateUri = new Uri("ms-settings:easeofaccess-visualeffects") };
        body.Children.Add(Card(L10n.Tr("Animation"),
            L10n.Tr("With Windows animation effects off, characters move slowly and stop following the pointer."),
            "\uE768", motion));
    }

    // MARK: - Usage & cost

    private void BuildUsage(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("Usage & cost")));
        body.Children.Add(PageIntro(L10n.Tr("How often quotas are fetched, how tokens are counted, and what your usage would cost at API prices.")));
        body.Children.Add(Section(L10n.Tr("Quota")));
        var presets = RefreshIntervalStore.Presets;
        body.Children.Add(Card(L10n.Tr("Refresh interval"),
            L10n.Tr("Providers rate-limit their usage endpoints, so nothing shorter than five minutes is offered"), "\uE895",
            Combo(presets.Select(p => L10n.TrFormat("Every {0} minutes", p / 60)).ToList(),
                Array.IndexOf(presets, RefreshIntervalStore.Shared.Seconds),
                index => RefreshIntervalStore.Shared.Seconds = presets[index])));

        body.Children.Add(Section(L10n.Tr("Tokens")));
        var counting = TokenCountModeStore.Shared;
        body.Children.Add(Card(L10n.Tr("Token counting"), L10n.Tr("Cache reads dwarf everything else in agent sessions; counting only input and output shows the work done"), "\uE8EF",
            Combo(new[] { L10n.Tr("All tokens (including cache)"), L10n.Tr("Input + output") }, counting.Mode == TokenCountMode.All ? 0 : 1,
                index => counting.Mode = index == 0 ? TokenCountMode.All : TokenCountMode.Billable)));

        body.Children.Add(Section(L10n.Tr("Cost")));
        var scanned = new TextBlock();
        void Caption() => scanned.Text = CostStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("Last scanned {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("Not scanned yet");
        Caption();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button(L10n.Tr("Scan now"), () =>
        {
            CostStore.Shared.Refresh();
            Caption();
        }));
        actions.Children.Add(Button(L10n.Tr("Open cost"), () => Cost.CostWindow.Open(), accent: true));
        var card = Card(L10n.Tr("Local logs"), null, "\uE8C7", actions);
        card.Description = scanned;
        body.Children.Add(card);
    }

    // MARK: - General

    private void BuildGeneral(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("General")));
        body.Children.Add(Section(L10n.Tr("Startup")));
        body.Children.Add(Card(L10n.Tr("Launch at login"), L10n.Tr("Start Agent Island when you sign in to Windows"), "\uE7E8",
            Toggle(LaunchAtLogin.IsEnabled, LaunchAtLogin.SetEnabled)));

        body.Children.Add(Section(L10n.Tr("Language")));
        var language = AppLanguageStore.Load();
        body.Children.Add(Card(L10n.Tr("App language"), null, "\uF2B7",
            Combo(new[] { L10n.Tr("Same as Windows"), "English", "简体中文" },
                language switch { L10n.Language.English => 1, L10n.Language.SimplifiedChinese => 2, _ => 0 },
                index =>
                {
                    var chosen = index switch { 1 => L10n.Language.English, 2 => L10n.Language.SimplifiedChinese, _ => L10n.Language.Auto };
                    if (chosen == AppLanguageStore.Load()) return;
                    AppLanguageStore.Save(chosen);
                    L10n.Current = chosen;
                    // Let the combo finish closing before the window rebuilds.
                    DispatcherQueue.TryEnqueue(() => App.Current.Relocalize());
                })));

        body.Children.Add(Section(L10n.Tr("About")));
        var version = typeof(SettingsWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        var about = new SettingsExpander
        {
            Header = "Agent Island",
            Description = L10n.TrFormat("Version {0}", version),
            HeaderIcon = new ImageIcon { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "agentisland_logo_small.png"))) },
        };
        about.Items.Add(new SettingsCard
        {
            Header = L10n.Tr("Source code"),
            Content = new HyperlinkButton { Content = "github.com/tristan666666/agent-island", NavigateUri = new Uri(RepositoryUrl) },
        });
        about.Items.Add(new SettingsCard
        {
            Header = L10n.Tr("Crash log"),
            Description = IslandPaths.AppSupportDir,
            Content = Button(L10n.Tr("Open folder"), () =>
            {
                Directory.CreateDirectory(IslandPaths.AppSupportDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(IslandPaths.AppSupportDir) { UseShellExecute = true });
            }),
        });
        about.Items.Add(new SettingsCard
        {
            Header = L10n.Tr("Quit Agent Island"),
            Description = L10n.Tr("Closes the drawer and the tray icon"),
            Content = Button(L10n.Tr("Quit"), () => App.Current.Quit()),
        });
        body.Children.Add(about);
    }
}
