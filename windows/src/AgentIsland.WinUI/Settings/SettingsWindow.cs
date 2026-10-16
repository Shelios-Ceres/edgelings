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
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Settings;

/// Settings in the Windows 11 idiom: Mica, a navigation pane and setting
/// cards. Agents leads, since that is what the app is about; the drawer,
/// usage and general preferences follow.
internal sealed partial class SettingsWindow : Window
{
    private enum Page
    {
        Agents,
        Drawer,
        Usage,
        General,
    }

    private const string PageKey = "Settings.page";
    // This edition, and the original Agent Island it builds on.
    private const string Edition = "Shelios V1.0.0";
    private const string OriginalUrl = "https://github.com/tristan666666/agent-island";

    private static SettingsWindow? _shared;

    // Built for the look the window wears (Build): the pages are a list in
    // the kit's style.
    private bool _pixel;
    private ListView _pages = null!;
    private List<IconElement> _pageIcons = new();
    private ScrollViewer _scroll = null!;
    private readonly Dictionary<DisplayProvider, AgentSection> _agents = new();
    private readonly List<Action> _teardown = new();
    private readonly DispatcherQueueTimer _tick;
    private Page _page;
    private bool _dirty = true;
    private int _ticks;

    public static void Open(DisplayProvider? focus = null) => Open(focus, null);

    /// On the Usage & cost page, where the drawer's and the tray's menus go.
    public static void OpenUsage() => Open(null, Page.Usage);

    private static void Open(DisplayProvider? focus, Page? page)
    {
        // A window already on screen moves to the agent (M19) or the page
        // (M8); one that is appearing opens with them ready.
        var onScreen = _shared is { AppWindow.IsVisible: true };
        // A closed window was only hidden (Chrome.HideOnClose): it comes back
        // in the current look and language, on its last page.
        if (_shared is { AppWindow.IsVisible: false } hidden)
        {
            if (page is { } chosen) hidden._page = chosen;
            hidden.Build();
            hidden._tick.Start();
        }
        _shared ??= new SettingsWindow(page);
        // Cursor's settings are part of Grok's.
        if (focus is { } provider) _shared.Focus(provider.Character(), animate: onScreen);
        else if (page is { } next) _shared.SelectPage(next);
        Shell.Chrome.Show(_shared);
    }

    /// AGENTISLAND_DEBUG_SNAPSHOT: every page, rendered by the app itself.
    public static async Task SnapshotPagesAsync(string directory, DisplayProvider? focus)
    {
        Open(focus);
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        foreach (var page in Enum.GetValues<Page>())
        {
            if (page != Page.Agents || focus is null) window.SelectPage(page);
            if (page == Page.Agents && focus is { } provider) window.Focus(provider, animate: false);
            await Task.Delay(1500);
            if (window.Content is { } content) await Shell.Snapshot.SaveAsync(content, Path.Combine(directory, $"settings-{page}.png"));
            if (window._scroll.Content is UIElement body) await Shell.Snapshot.SaveAsync(body, Path.Combine(directory, $"settings-{page}-full.png"));
        }
    }

    /// AGENTISLAND_DEBUG_OPEN_COST with AGENTISLAND_DEBUG_SNAPSHOT: the Usage &
    /// cost page in each period, rendered by the app itself.
    public static async Task SnapshotCostAsync(string directory)
    {
        OpenUsage();
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        var periods = new[] { "Today", "Week", "Month" };
        for (var i = 0; i < periods.Length; i++)
        {
            window._costPage?.DebugPeriod(i);
            await Task.Delay(1200);
            if (window.Content is { } content) await Shell.Snapshot.SaveAsync(content, Path.Combine(directory, $"cost-{periods[i]}.png"));
            if (window._scroll.Content is UIElement body) await Shell.Snapshot.SaveAsync(body, Path.Combine(directory, $"cost-{periods[i]}-full.png"));
        }
    }

    /// AGENTISLAND_DEBUG_STATES: the kit's controls at rest, in hover, pressed
    /// and keyboard focus, and an agent's card in hand, rendered by the app
    /// itself; each with its card, so a focus ring outside the control shows.
    public static async Task SnapshotStatesAsync(string directory)
    {
        Open();
        if (_shared is not { } window) return;
        Directory.CreateDirectory(directory);
        async Task Shoot(UIElement frame, string name) =>
            await Shell.Snapshot.SaveAsync(frame, Path.Combine(directory, name + ".png"));
        async Task States(Control control, string name, UIElement? around = null)
        {
            var frame = around ?? (UIElement?)Above<SettingsCard>(control) ?? control;
            await Shoot(frame, $"{name}-rest");
            foreach (var state in new[] { "PointerOver", "Pressed" })
            {
                VisualStateManager.GoToState(control, state, false);
                await Task.Delay(200);
                await Shoot(frame, $"{name}-{state.ToLowerInvariant()}");
            }
            VisualStateManager.GoToState(control, "Normal", false);
            control.Focus(FocusState.Keyboard);
            await Task.Delay(400);
            await Shoot(frame, $"{name}-focus");
        }

        window.SelectPage(Page.Drawer);
        await Task.Delay(1500);
        var page = (DependencyObject)window._scroll.Content;
        if (Below<ToggleSwitch>(page) is { } toggle) await States(toggle, "toggle");
        if (Below<ComboBox>(page) is { } combo) await States(combo, "combo");

        // An agent card's arrow, and the card in hand (M11).
        window.SelectPage(Page.Agents);
        await Task.Delay(1500);
        if (window._agents.Values.FirstOrDefault(agent => agent.Down.IsEnabled) is { } first && window._agentList is { } list)
        {
            await States(first.Down, "icon-button", first.Expander);
            first.Hold(1);
            await Task.Delay(200);
            await Shoot(list, "card-in-hand");
            first.Hold(0);
        }

        window.SelectPage(Page.Usage);
        await Task.Delay(1500);
        page = (DependencyObject)window._scroll.Content;
        if (Below<Button>(page) is { } button)
        {
            await States(button, "button");
            if (Below<Button>(page, other => other != button) is { } accent) await States(accent, "accent-button");
        }
        // The page list: another page's item in hover and pressed, the open
        // page's item in keyboard focus.
        if (window._pages.ContainerFromIndex(0) is ListViewItem item)
        {
            foreach (var state in new[] { "PointerOver", "Pressed" })
            {
                VisualStateManager.GoToState(item, state, false);
                await Task.Delay(200);
                await Shoot(window._pages, $"nav-{state.ToLowerInvariant()}");
            }
            VisualStateManager.GoToState(item, "Normal", false);
        }
        if (window._pages.ContainerFromIndex((int)Page.Usage) is ListViewItem open)
        {
            open.Focus(FocusState.Keyboard);
            await Task.Delay(400);
            await Shoot(window._pages, "nav-focus");
        }
    }

    private static T? Below<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && (match is null || match(found))) return found;
            if (Below(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    private static T? Above<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T found) return found;
        }
        return null;
    }

    /// Rebuilds the open window in the current language and look, on the
    /// same page. In place: closing it for a new one would leak GDI objects
    /// (Chrome.Restyle). A hidden window is rebuilt when it opens.
    public static void Relocalize()
    {
        if (_shared is { AppWindow.IsVisible: true } window) window.Build();
    }

    private SettingsWindow(Page? page = null)
    {
        _page = page ?? (Enum.TryParse<Page>(Preferences.Get<string?>(PageKey), out var saved) ? saved : Page.Agents);
        Build();
        // The other character style brings the other look.
        PropertyChangedEventHandler look = (_, e) =>
        {
            if (e.PropertyName == nameof(DrawerStore.Theme) && Shell.Looks.Pixel != _pixel) DispatcherQueue.TryEnqueue(Relocalize);
        };
        DrawerStore.Shared.PropertyChanged += look;
        _teardown.Add(() => DrawerStore.Shared.PropertyChanged -= look);
        // Hiding, showing or reordering an agent rearranges the Agents page.
        PropertyChangedEventHandler regroup = (_, _) => DispatcherQueue.TryEnqueue(Rearrange);
        ProviderVisibilityStore.Shared.PropertyChanged += regroup;
        _teardown.Add(() => ProviderVisibilityStore.Shared.PropertyChanged -= regroup);

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

        // Closing hides the window; its pages and their updates go until it
        // opens again.
        Shell.Chrome.HideOnClose(this, () =>
        {
            _tick.Stop();
            FollowDrawerStore(null);
            _agents.Clear();
            EndCostPage();
            _scroll.Content = null;
        });
        _teardown.Add(() => FollowDrawerStore(null));
        _teardown.Add(EndCostPage);
        Closed += (_, _) =>
        {
            _tick.Stop();
            foreach (var teardown in _teardown) teardown();
            _shared = null;
        };
    }

    /// The frame and the pages in the current look and language, on the
    /// open page: when the window opens, and again on every switch.
    private void Build()
    {
        using var english = Shell.Looks.English();
        var title = "Agent Island — " + L10n.Tr("Settings");
        var root = Content is null
            ? Shell.Chrome.Apply(this, title, 980, 720, 720, 520)
            : Shell.Chrome.Restyle(this, title);
        _pixel = Shell.Looks.Pixel;
        _pages = new ListView { SelectionMode = ListViewSelectionMode.Single, Margin = _pixel ? new Thickness(4) : new Thickness(6, 4, 6, 4) };
        _pageIcons = new List<IconElement>();
        _scroll = PageScroller();
        var pages = new[]
        {
            (Page.Agents, "Agents", "\uE99A"),
            (Page.Drawer, "Drawer", "\uE7F4"),
            (Page.Usage, "Usage & cost", "\uE9D2"),
            (Page.General, "General", "\uE713"),
        };
        Shell.Chrome.SetBody(root, Pages(pages));
        SelectPage(_page);
    }

    /// The page list: a 200-DIP pane, the page on a layer panel beside it;
    /// the open page's icon turns to the accent.
    private FrameworkElement Pages(IEnumerable<(Page Page, string Label, string Glyph)> pages)
    {
        _pages.ItemContainerStyle = Shell.Looks.Kit<Style>("NavItemStyle");
        foreach (var (_, label, glyph) in pages)
        {
            var icon = Icon(glyph);
            icon.VerticalAlignment = VerticalAlignment.Center;
            _pageIcons.Add(icon);
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = _pixel ? 16 : 12,
                Children = { icon, new TextBlock { Text = L10n.Tr(label), VerticalAlignment = VerticalAlignment.Center } },
            };
            AutomationProperties.SetName(item, L10n.Tr(label));
            _pages.Items.Add(item);
        }
        var list = _pages;
        var icons = _pageIcons;
        var chosen = _pixel ? "PixelAccent" : "ClassicAccentText";
        var rest = Shell.Looks.Prefix + "Text";
        list.SelectionChanged += (_, e) =>
        {
            if (list.SelectedIndex < 0) return;
            for (var i = 0; i < icons.Count; i++) icons[i].Foreground = Res<Brush>(i == list.SelectedIndex ? chosen : rest);
            var before = e.RemovedItems.Count > 0 ? list.Items.IndexOf(e.RemovedItems[0]) : -1;
            Show((Page)list.SelectedIndex);
            if (before >= 0 && AppWindow.IsVisible) PlaySwitch(before, list.SelectedIndex);
            else
            {
                var next = _switched;
                _switched = null;
                next?.Invoke();
            }
        };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _nav = new Grid();
        _slider = NavSlider();
        _nav.Children.Add(_slider);
        _nav.Children.Add(_pages);
        body.Children.Add(_nav);
        var page = new Border { Style = Shell.Looks.Kit<Style>("LayerStyle"), Child = _scroll };
        Grid.SetColumn(page, 1);
        body.Children.Add(page);
        return body;
    }

    private void SelectPage(Page page) => _pages.SelectedIndex = (int)page;

    // The Drawer page's live controls follow the store through one handler,
    // replaced whenever the page is rebuilt and removed with the pages.
    private PropertyChangedEventHandler? _drawerPage;

    private void FollowDrawerStore(Action? update)
    {
        DrawerStore.Shared.PropertyChanged -= _drawerPage;
        _drawerPage = update is null ? null : (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            using var english = Shell.Looks.English();
            update();
        });
        DrawerStore.Shared.PropertyChanged += _drawerPage;
    }

    private void Watch(INotifyPropertyChanged source)
    {
        PropertyChangedEventHandler handler = (_, _) => _dirty = true;
        source.PropertyChanged += handler;
        _teardown.Add(() => source.PropertyChanged -= handler);
    }

    private void Tick()
    {
        if (_page != Page.Agents) return;
        using var english = Shell.Looks.English();
        // Reports read a few small files (Codex's account); the clock-driven
        // "synced" captions refresh every fifth second.
        if (_dirty || ++_ticks % 5 == 0)
        {
            _dirty = false;
            foreach (var (provider, section) in _agents) section.Update(AgentReports.For(provider));
        }
        foreach (var section in _agents.Values) section.UpdateActivity();
    }

    private void Show(Page page, bool keepOffset = false)
    {
        using var english = Shell.Looks.English();
        CloseToolTips();
        _page = page;
        Preferences.Set(PageKey, page.ToString());
        _agents.Clear();
        _cards.Clear();
        _agentList = null;
        _lineup = Array.Empty<(DisplayProvider, bool)>();
        _lineupKey = "";
        _drag = null;
        EndCostPage();
        var body = new StackPanel { Spacing = CardGap, MaxWidth = 1000, Padding = new Thickness(40, 28, 40, 40) };
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
        _body = body;
        SetPage(_scroll, body);
        if (!keepOffset) _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// Tooltips open in popups of their own and outlive their controls: a
    /// page rebuilt under the pointer (a switch that regroups the Agents
    /// page) would leave the old one on screen under the next.
    private void CloseToolTips()
    {
        if (Content?.XamlRoot is not { } root) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is ToolTip) popup.IsOpen = false;
        }
    }

    // MARK: - Agents

    private void BuildAgents(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("Agents")));
        body.Children.Add(PageIntro(L10n.Tr("Quota, tokens and live activity for every coding agent on this computer, in the drawer's order. Drag a card by its grip or use its arrows to move it; switch one off to hide it everywhere.")));
        // The shown in the drawer's order, then the rest at the bottom.
        var lineup = _lineup = AgentLineup();
        _lineupKey = LineupKey(lineup);
        var list = _agentList = StartsSection(new StackPanel { Spacing = CardGap });
        var shownCount = lineup.Count(entry => entry.Shown);
        for (var i = 0; i < lineup.Count; i++)
        {
            var (provider, shown) = lineup[i];
            var section = new AgentSection(provider, () => Content?.XamlRoot, step => MoveAgent(provider, step));
            _agents[provider] = section;
            section.Update(AgentReports.For(provider));
            section.UpdateActivity();
            section.Place(i, shownCount, shown);
            var row = AgentRow(section);
            HookDrag(section, provider);
            _cards[provider] = row;
            list.Children.Add(row);
        }
        body.Children.Add(list);
    }

    /// A card with its grip hanging in the page's margin to its left, and
    /// its look in hand: the shadow under it, Classic's outline over it. The
    /// grip's column is on page ground, so a card passing over another covers
    /// that one's grip too.
    private static Grid AgentRow(AgentSection section)
    {
        var row = new Grid { Margin = new Thickness(-AgentSection.GripWidth, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AgentSection.GripWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border { Background = Shell.Looks.Kit<Brush>("Layer"), IsHitTestVisible = false });
        row.Children.Add(section.Grip);
        foreach (var layer in new[] { section.Shadow, section.Expander, section.Outline })
        {
            if (layer is null) continue;
            Grid.SetColumn(layer, 1);
            row.Children.Add(layer);
        }
        return row;
    }

    private static IReadOnlyList<(DisplayProvider Provider, bool Shown)> AgentLineup() =>
        DrawerVisibility.Lineup(DrawerStore.Shared.Order, ProviderVisibilityStore.Shared.IsShown);

    private static string LineupKey(IEnumerable<(DisplayProvider Provider, bool Shown)> lineup) =>
        string.Join(",", lineup.Select(entry => $"{entry.Provider}:{entry.Shown}"));

    // The list the Agents page shows; hiding, showing or moving an agent
    // rearranges it (Rearrange), and a change in which agents are here
    // rebuilds it, keeping its scroll and open cards.
    private StackPanel? _agentList;
    private IReadOnlyList<(DisplayProvider Provider, bool Shown)> _lineup = Array.Empty<(DisplayProvider, bool)>();
    private string _lineupKey = "";

    /// A shown agent's arrow: a step up or down among the shown.
    private void MoveAgent(DisplayProvider provider, int step)
    {
        var order = _lineup.Where(entry => entry.Shown).Select(entry => entry.Provider).ToList();
        var from = order.IndexOf(provider);
        var to = from + step;
        if (from < 0 || to < 0 || to >= order.Count) return;
        (order[from], order[to]) = (order[to], order[from]);
        _moving = provider;
        DrawerStore.Shared.SetOrder(order);
    }

    // MARK: - Drawer

    private void BuildDrawer(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("Drawer")));
        body.Children.Add(PageIntro(L10n.Tr("The column of characters on the edge of your screen. Hover one for its status, click it for its settings, drag it to move the drawer, right-click it for options.")));
        var store = DrawerStore.Shared;
        body.Children.Add(Section(L10n.Tr("Characters")));
        body.Children.Add(Card(L10n.Tr("Character style"),
            L10n.Tr("Pixel bots drawn for each agent, or the classic shapes you can recolour"), "\uE771",
            Combo(new[] { L10n.Tr("Pixel"), L10n.Tr("Classic") }, store.Theme == CharacterTheme.Pixel ? 0 : 1,
                index => store.Theme = index == 0 ? CharacterTheme.Pixel : CharacterTheme.Classic)));
        body.Children.Add(Card(L10n.Tr("Character lines"),
            L10n.Tr("A character speaks up in a bubble when its agent is done, needs your approval or hits an error"),
            "\uE8BD", Toggle(store.SpeechBubbles, on => store.SpeechBubbles = on)));

        body.Children.Add(Section(L10n.Tr("Placement")));
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
        // Where the drawer sits on its edge, and whether it stays there.
        var center = Button(L10n.Tr("Center on the edge"), () => store.Offset = 0.5);
        var lockToggle = Toggle(store.Locked, on => store.Locked = on);
        lockToggle.OnContent = L10n.Tr("Locked");
        lockToggle.OffContent = L10n.Tr("Unlocked");
        AutomationProperties.SetAutomationId(lockToggle, "Settings.Drawer.Locked");
        AutomationProperties.SetName(lockToggle, L10n.Tr("Lock position"));
        var position = Card(L10n.Tr("Position"), null, "\uE7C2", SideBySide(center, lockToggle));
        void ShowLock()
        {
            lockToggle.IsOn = store.Locked;
            center.IsEnabled = !store.Locked;
            SetDescription(position, L10n.Tr(store.Locked
                ? "Characters stay put: dragging won't move the drawer, and a character appearing or leaving won't shift the ones above it"
                : "Drag any character up or down to move the drawer"));
        }
        ShowLock();
        body.Children.Add(position);
        FollowDrawerStore(ShowLock);


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
            L10n.Tr("With Windows animation effects off, characters slow down and stop following the pointer"),
            "\uE768", motion));
    }

    // MARK: - Usage & cost

    private void BuildUsage(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("Usage & cost")));
        body.Children.Add(PageIntro(L10n.Tr("Estimated at API list prices from this computer's session logs — not a bill.")));
        _costPage = new Cost.CostPage(DispatcherQueue, () => AppWindow.IsVisible && _page == Page.Usage, () => SelectPage(Page.Agents));
        body.Children.Add(_costPage.View);
        body.Children.Add(Section(L10n.Tr("Quota")));
        var presets = RefreshIntervalStore.Presets;
        body.Children.Add(Card(L10n.Tr("Refresh interval"),
            L10n.Tr("Providers rate-limit their usage endpoints, so nothing shorter than five minutes is offered"), "\uE895",
            Combo(presets.Select(p => L10n.TrFormat("Every {0} minutes", p / 60)).ToList(),
                Array.IndexOf(presets, RefreshIntervalStore.Shared.Seconds),
                index => RefreshIntervalStore.Shared.Seconds = presets[index])));
        var quota = QuotaDisplayModeStore.Shared;
        body.Children.Add(Card(L10n.Tr("Quota shows"), L10n.Tr("How the bars and percentages read on the drawer's cards and in Settings"), "\uE9D2",
            Combo(new[] { L10n.Tr("Used"), L10n.Tr("Remaining") }, quota.ShowsRemaining ? 1 : 0,
                index => quota.ShowsRemaining = index == 1)));

        body.Children.Add(Section(L10n.Tr("Tokens")));
        var counting = TokenCountModeStore.Shared;
        body.Children.Add(Card(L10n.Tr("Token counting"), L10n.Tr("Excludes cache reads; new input written to cache still counts. History is kept."), "\uE8EF",
            Combo(new[] { TokenDisplayText.ModeLabel(TokenCountMode.All), TokenDisplayText.ModeLabel(TokenCountMode.Billable) }, counting.Mode == TokenCountMode.All ? 0 : 1,
                index => counting.Mode = index == 0 ? TokenCountMode.All : TokenCountMode.Billable)));
    }

    // The usage numbers on the Usage & cost page, while it shows.
    private Cost.CostPage? _costPage;

    private void EndCostPage()
    {
        _costPage?.Dispose();
        _costPage = null;
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
        // Under Pixel these windows read in English whatever this says.
        var languageNote = Shell.Looks.Pixel ? L10n.Tr("The pixel style shows Settings in English") : null;
        body.Children.Add(Card(L10n.Tr("App language"), languageNote, "\uF2B7",
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
        var about = new SettingsExpander
        {
            Header = "Agent Island",
            Description = Edition,
            HeaderIcon = new ImageIcon
            {
                Source = Shell.Looks.Pixel
                    ? Shell.PixelIcons.Logo()
                    : new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "agentisland_logo_small.png"))),
            },
        };
        about.Items.Add(new SettingsCard
        {
            Header = L10n.Tr("Thanks"),
            Description = Credits(),
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

    /// The Thanks row: each credit's name on a line of its own, what it is
    /// below. Names stand out in bold, or under Pixel, whose face has one
    /// weight, in full ink against the grey.
    private static StackPanel Credits()
    {
        var credits = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Credit(string name, string detail, double gap)
        {
            credits.Children.Add(new TextBlock
            {
                Text = name,
                Margin = new Thickness(0, gap, 0, 0),
                Foreground = Res<Brush>("TextFillColorPrimaryBrush"),
                FontWeight = Shell.Looks.Pixel ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
            });
            credits.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap });
        }
        Credit("Agent Island", L10n.TrFormat("The original app, by {0} · MIT License", "Tristan Tang"), 0);
        credits.Children.Add(new HyperlinkButton
        {
            Content = "github.com/tristan666666/agent-island",
            NavigateUri = new Uri(OriginalUrl),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        Credit("Ark Pixel", L10n.TrFormat("The pixel font, by {0} · SIL Open Font License 1.1", "TakWolf"), 12);
        return credits;
    }
}
