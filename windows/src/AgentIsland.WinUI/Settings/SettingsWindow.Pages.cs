using AgentIsland.Core;
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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Settings;

// The pages' content: the Agents list, Drawer, Usage & cost and General.
internal sealed partial class SettingsWindow
{
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
            L10n.Tr("A character speaks up in a bubble when a session is done, asks you something, needs your approval or hits an error"),
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
