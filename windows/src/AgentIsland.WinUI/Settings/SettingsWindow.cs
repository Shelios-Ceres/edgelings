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
    // This edition, and the original Agent Island it builds on.
    private const string Edition = "Shelios V1.0.0";
    private const string OriginalUrl = "https://github.com/tristan666666/agent-island";

    private static SettingsWindow? _shared;

    // Built for the look the window wears (Build); under Pixel the pages are
    // a pixel list instead of a NavigationView.
    private NavigationView _nav = null!;
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

    public static void Open(DisplayProvider? focus = null)
    {
        // A closed window was only hidden (Chrome.HideOnClose): it comes back
        // in the current look and language, on its last page.
        if (_shared is { AppWindow.IsVisible: false } hidden)
        {
            hidden.Build();
            hidden._tick.Start();
        }
        _shared ??= new SettingsWindow();
        if (focus is { } provider) _shared.Focus(provider);
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
            if (page == Page.Agents && focus is { } provider) window.Focus(provider);
            await Task.Delay(1500);
            if (window.Content is { } content) await Shell.Snapshot.SaveAsync(content, Path.Combine(directory, $"settings-{page}.png"));
            if (window._scroll.Content is UIElement body) await Shell.Snapshot.SaveAsync(body, Path.Combine(directory, $"settings-{page}-full.png"));
        }
    }

    /// AGENTISLAND_DEBUG_STATES: the kit's controls at rest, in hover, pressed
    /// and keyboard focus, and a dragged order row, rendered by the app
    /// itself; each with its card, so a focus ring outside the control shows.
    public static async Task SnapshotStatesAsync(string directory)
    {
        Open();
        if (_shared is not { } window || !window._pixel) return;
        Directory.CreateDirectory(directory);
        async Task Shoot(UIElement frame, string name) =>
            await Shell.Snapshot.SaveAsync(frame, Path.Combine(directory, name + ".png"));
        async Task States(Control control, string name)
        {
            var frame = (UIElement?)Above<SettingsCard>(control) ?? control;
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
        if (Below<ListView>(page, list => AutomationProperties.GetAutomationId(list) == "Settings.Drawer.Order") is { } order)
        {
            if (Below<Button>(order, arrow => arrow.IsEnabled) is { } arrow) await States(arrow, "icon-button");
            if (order.ContainerFromIndex(1) is ListViewItem row && Above<SettingsCard>(row) is { } card)
            {
                VisualStateManager.GoToState(row, "Dragging", false);
                await Task.Delay(200);
                await Shoot(card, "order-dragged");
                VisualStateManager.GoToState(row, "NotDragging", false);
            }
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

    private SettingsWindow()
    {
        _page = Enum.TryParse<Page>(Preferences.Get<string?>(PageKey), out var saved) ? saved : Page.Agents;
        Build();
        // The other character style brings the other look.
        PropertyChangedEventHandler look = (_, e) =>
        {
            if (e.PropertyName == nameof(DrawerStore.Theme) && Shell.Looks.Pixel != _pixel) DispatcherQueue.TryEnqueue(Relocalize);
        };
        DrawerStore.Shared.PropertyChanged += look;
        _teardown.Add(() => DrawerStore.Shared.PropertyChanged -= look);
        // Hiding, showing or reordering an agent regroups the Agents page.
        PropertyChangedEventHandler regroup = (_, _) => DispatcherQueue.TryEnqueue(Regroup);
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
            _scroll.Content = null;
        });
        _teardown.Add(() => FollowDrawerStore(null));
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
        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 200,
        };
        _pages = new ListView { SelectionMode = ListViewSelectionMode.Single, Margin = new Thickness(4) };
        _pageIcons = new List<IconElement>();
        _scroll = new ScrollViewer { HorizontalScrollMode = ScrollMode.Disabled };
        var pages = new[]
        {
            (Page.Agents, "Agents", "\uE99A"),
            (Page.Drawer, "Drawer", "\uE7F4"),
            (Page.Usage, "Usage & cost", "\uE9D2"),
            (Page.General, "General", "\uE713"),
        };
        if (_pixel)
        {
            Shell.Chrome.SetBody(root, PixelPages(pages));
        }
        else
        {
            Shell.Chrome.SetBody(root, _nav);
            foreach (var (page, label, glyph) in pages)
            {
                _nav.MenuItems.Add(new NavigationViewItem { Content = L10n.Tr(label), Icon = Icon(glyph), Tag = page });
            }
            _nav.Content = _scroll;
            _nav.SelectionChanged += (_, e) =>
            {
                if (e.SelectedItem is NavigationViewItem { Tag: Page page }) Show(page);
            };
        }
        SelectPage(_page);
        // Pixel type is crisp only on whole pixels. WinUI draws the scrolled
        // page snapped, but a drop-down's list takes the page's exact place,
        // so a scroll that settles between pixels blurs it: settle on one.
        if (_pixel)
        {
            var scroll = _scroll;
            scroll.ViewChanged += (_, e) =>
            {
                if (e.IsIntermediate) return;
                var scale = scroll.XamlRoot?.RasterizationScale ?? 1;
                var exact = scroll.VerticalOffset * scale;
                var whole = Math.Round(exact) / scale > scroll.ScrollableHeight ? Math.Floor(exact) : Math.Round(exact);
                if (Math.Abs(whole - exact) > 0.001) scroll.ChangeView(null, whole / scale, null, disableAnimation: true);
            };
        }
    }

    /// The pixel kit's page list: a 200-DIP pane on desk, the page on a layer
    /// panel beside it; the open page's icon turns to the accent.
    private FrameworkElement PixelPages(IEnumerable<(Page Page, string Label, string Glyph)> pages)
    {
        _pages.ItemContainerStyle = Res<Style>("PixelNavItemStyle");
        foreach (var (_, label, glyph) in pages)
        {
            var icon = Icon(glyph);
            icon.VerticalAlignment = VerticalAlignment.Center;
            _pageIcons.Add(icon);
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                Children = { icon, new TextBlock { Text = L10n.Tr(label), VerticalAlignment = VerticalAlignment.Center } },
            };
            AutomationProperties.SetName(item, L10n.Tr(label));
            _pages.Items.Add(item);
        }
        var list = _pages;
        var icons = _pageIcons;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            for (var i = 0; i < icons.Count; i++) icons[i].Foreground = Res<Brush>(i == list.SelectedIndex ? "PixelAccent" : "PixelText");
            Show((Page)list.SelectedIndex);
        };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_pages);
        var page = new Border { Style = Res<Style>("PixelLayerStyle"), Child = _scroll };
        Grid.SetColumn(page, 1);
        body.Children.Add(page);
        return body;
    }

    private void SelectPage(Page page)
    {
        if (_pixel) _pages.SelectedIndex = (int)page;
        else _nav.SelectedItem = _nav.MenuItems[(int)page];
    }

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

    private static SettingsCard OrderCard(DrawerStore store)
    {
        var card = Card(L10n.Tr("Order"), L10n.Tr("Drag or use the arrows; the first stands at the top. Switch one off to hide it everywhere."), "\uE8CB", null);
        if (Shell.Looks.Pixel)
        {
            card.Style = Res<Style>("PixelSettingsCardVerticalStyle");
        }
        else
        {
            card.ContentAlignment = CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical;
            card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }
        card.Content = OrderList.Create(store);
        return card;
    }

    private void Watch(INotifyPropertyChanged source)
    {
        PropertyChangedEventHandler handler = (_, _) => _dirty = true;
        source.PropertyChanged += handler;
        _teardown.Add(() => source.PropertyChanged -= handler);
    }

    private void Focus(DisplayProvider provider)
    {
        SelectPage(Page.Agents);
        Show(Page.Agents);
        if (!_agents.TryGetValue(provider, out var section)) return;
        section.Expander.IsExpanded = true;
        // After the expanded items have laid out, or the scroll falls short.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            section.Expander.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = true });
            if (FocusManager.FindFirstFocusableElement(section.Expander) is Control control) control.Focus(FocusState.Keyboard);
        });
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

    private void Show(Page page)
    {
        using var english = Shell.Looks.English();
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
        // In the drawer's order; agents the user hid wait at the bottom.
        var (detected, missing, hidden) = AgentGroups();
        _agentGroups = GroupsKey(detected, missing, hidden);
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
        if (hidden.Count > 0)
        {
            body.Children.Add(Section(L10n.Tr("Hidden agents")));
            body.Children.Add(HiddenAgents(hidden));
        }
    }

    private static (IReadOnlyList<DisplayProvider> Detected, IReadOnlyList<DisplayProvider> Missing, IReadOnlyList<DisplayProvider> Hidden) AgentGroups()
    {
        var visibility = ProviderVisibilityStore.Shared;
        return DrawerVisibility.Groups(DrawerStore.Shared.Order, visibility.IsHidden, visibility.IsDetected);
    }

    private static string GroupsKey(params IReadOnlyList<DisplayProvider>[] groups) =>
        string.Join("|", groups.Select(group => string.Join(",", group)));

    // The groups the Agents page was built with; hiding, showing or
    // reordering an agent rebuilds it, keeping its scroll and open sections.
    private string _agentGroups = "";

    private void Regroup()
    {
        if (_page != Page.Agents || !AppWindow.IsVisible) return;
        var (detected, missing, hidden) = AgentGroups();
        if (GroupsKey(detected, missing, hidden) == _agentGroups) return;
        var offset = _scroll.VerticalOffset;
        var open = _agents.Where(pair => pair.Value.Expander.IsExpanded).Select(pair => pair.Key).ToList();
        Show(Page.Agents);
        foreach (var provider in open)
        {
            if (_agents.TryGetValue(provider, out var section)) section.Expander.IsExpanded = true;
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, offset, null, disableAnimation: true));
    }

    /// Hidden agents, collapsed into one list where each can come back.
    private static SettingsExpander HiddenAgents(IReadOnlyList<DisplayProvider> hidden)
    {
        var list = new SettingsExpander
        {
            Header = L10n.TrFormat("{0} hidden", hidden.Count),
            Description = L10n.Tr("Out of the drawer, its menus and the Cost window"),
            HeaderIcon = Icon("\uE890"),
        };
        AutomationProperties.SetAutomationId(list, "Settings.Agents.Hidden");
        foreach (var provider in hidden)
        {
            var show = Button(L10n.Tr("Show"), () => ProviderVisibilityStore.Shared.SetShown(provider, true));
            AutomationProperties.SetAutomationId(show, $"Settings.Agents.Hidden.{provider.RawValue()}");
            AutomationProperties.SetName(show, $"{ProviderIdentity.DisplayName(provider)} · {L10n.Tr("Show")}");
            list.Items.Add(new SettingsCard { Header = ProviderIdentity.DisplayName(provider), HeaderIcon = Mark(provider), Content = show });
        }
        return list;
    }

    /// An agent's mark for a list row: its pixel face under Pixel, else the
    /// brand's.
    private static ImageIcon Mark(DisplayProvider provider)
    {
        var icon = new ImageIcon();
        if (Shell.Looks.Pixel) icon.Source = Shell.PixelIcons.Face(provider);
        else _ = SetMarkAsync(icon, provider);
        return icon;
    }

    private static async Task SetMarkAsync(ImageIcon icon, DisplayProvider provider)
    {
        if (await Shell.Brand.MarkAsync(provider, Shell.Brand.IsDark()) is { } mark) icon.Source = mark;
    }

    // MARK: - Drawer

    private void BuildDrawer(StackPanel body)
    {
        body.Children.Add(PageTitle(L10n.Tr("Drawer")));
        body.Children.Add(PageIntro(L10n.Tr("The column of characters on the edge of your screen. Hover one for its status, drag it to move the drawer, right-click it for options.")));
        var store = DrawerStore.Shared;
        body.Children.Add(Section(L10n.Tr("Characters")));
        body.Children.Add(Card(L10n.Tr("Character style"),
            L10n.Tr("Pixel bots drawn for each agent, or the classic shapes you can recolour"), "\uE771",
            Combo(new[] { L10n.Tr("Pixel"), L10n.Tr("Classic") }, store.Theme == CharacterTheme.Pixel ? 0 : 1,
                index => store.Theme = index == 0 ? CharacterTheme.Pixel : CharacterTheme.Classic)));
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
        var lockToggle = Toggle(store.Locked, on => store.Locked = on);
        AutomationProperties.SetAutomationId(lockToggle, "Settings.Drawer.Locked");
        AutomationProperties.SetName(lockToggle, L10n.Tr("Lock position"));
        body.Children.Add(Card(L10n.Tr("Lock position"),
            L10n.Tr("Characters stay put: dragging won't move the drawer, and a character appearing or leaving won't shift the ones above it"),
            "\uE72E", lockToggle));
        var center = Button(L10n.Tr("Center on the edge"), () => store.Offset = 0.5);
        var position = Card(L10n.Tr("Position"), null, "\uE7C2", center);
        void ShowLock()
        {
            lockToggle.IsOn = store.Locked;
            center.IsEnabled = !store.Locked;
            SetDescription(position, L10n.Tr(store.Locked ? "Unlock the drawer to move it" : "Drag any character up or down to move the drawer"));
        }
        ShowLock();
        body.Children.Add(position);
        body.Children.Add(OrderCard(store));
        FollowDrawerStore(ShowLock);

        body.Children.Add(Section(L10n.Tr("Cards")));
        var quota = QuotaDisplayModeStore.Shared;
        body.Children.Add(Card(L10n.Tr("Quota shows"), L10n.Tr("How the bars and percentages read on cards and in Settings"), "\uE9D2",
            Combo(new[] { L10n.Tr("Used"), L10n.Tr("Remaining") }, quota.ShowsRemaining ? 1 : 0,
                index => quota.ShowsRemaining = index == 1)));

        body.Children.Add(Card(L10n.Tr("Character lines"),
            L10n.Tr("When an agent needs you — done, waiting for approval, an error — its character says so in a bubble beside it"),
            "\uE8BD", Toggle(store.SpeechBubbles, on => store.SpeechBubbles = on)));

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
        body.Children.Add(Card(L10n.Tr("Token counting"), L10n.Tr("Excludes cache reads; new input written to cache still counts. History is kept."), "\uE8EF",
            Combo(new[] { TokenDisplayText.ModeLabel(TokenCountMode.All), TokenDisplayText.ModeLabel(TokenCountMode.Billable) }, counting.Mode == TokenCountMode.All ? 0 : 1,
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
            using var english = Shell.Looks.English();
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
