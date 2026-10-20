using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    private readonly Shell.Subscriptions _subscriptions = new();

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
            // A picked look changes an agent's face and colour on its card.
            if (e.PropertyName == nameof(DrawerStore.LookId)) _dirty = true;
        };
        _subscriptions.Watch(DrawerStore.Shared, look);
        // Hiding, showing or reordering an agent rearranges the Agents page.
        PropertyChangedEventHandler regroup = (_, _) => DispatcherQueue.TryEnqueue(Rearrange);
        _subscriptions.Watch(ProviderVisibilityStore.Shared, regroup);

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
            Drawer.LookPreviews.Release();
        });
        _subscriptions.Add(() => FollowDrawerStore(null));
        _subscriptions.Add(EndCostPage);
        Closed += (_, _) =>
        {
            _tick.Stop();
            _subscriptions.Dispose();
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

    private void Watch(INotifyPropertyChanged source) => _subscriptions.Watch(source, (_, _) => _dirty = true);

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
}
