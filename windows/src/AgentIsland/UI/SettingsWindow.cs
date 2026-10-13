using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Trigger;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// Settings window: a sidebar rail (Providers / Display / General), scrolling
/// row content, and a footer with the cost and report shortcuts.
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public static void Open()
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new SettingsWindow();
        _open = window;
        window.Closed += (_, _) => _open = null;
        window.Show();

        // Scripted verification: render the active tab's full content (past the
        // viewport) to a PNG — immune to the window occlusion a screen grab hits.
        var png = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SETTINGS_PNG");
        if (!string.IsNullOrEmpty(png)) window.SaveSnapshot(png);
    }

    /// Renders the current tab's content column at full height onto the panel
    /// background, so a verification screenshot shows every row even when the
    /// window is behind something else.
    public void SaveSnapshot(string path)
    {
        var settle = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            try
            {
                if (_scroll.Content is not FrameworkElement content) return;
                var w = (int)Math.Ceiling(content.ActualWidth);
                var h = (int)Math.Ceiling(content.ActualHeight);
                if (w <= 0 || h <= 0) return;
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(
                        GrokTheme.Brush(GrokTheme.Paper), null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(
                        new System.Windows.Media.VisualBrush(content), null, new Rect(0, 0, w, h));
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(path);
                encoder.Save(stream);
            }
            catch
            {
            }
        };
        settle.Start();
    }

    /// Declaration order IS the sidebar order (macOS 2.1.1 IA): the
    /// providers page leads because it is what the app is about; alerts
    /// stand alone instead of hiding at the bottom of General.
    private enum Tab
    {
        Providers,
        Display,
        General,
    }

    private static (string Label, string Glyph) TabFace(Tab tab) => tab switch
    {
        Tab.Providers => ("Providers", "\uE8A9"),
        Tab.Display => ("Display", "\uE7F4"),
        Tab.General => ("General", "\uE713"),
        _ => (tab.ToString(), "\uE713"),
    };

    private readonly List<(Tab Tab, Border Cell, TextBlock Glyph, TextBlock Label)> _navItems = new();
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private Tab _active = Tab.General;

    // Provider rows re-read their stores in place rather than rebuilding the
    // tab: a rebuild would lose the scroll position and yank the account menu
    // out from under the cursor. The list belongs to whichever tab built it;
    // the store subscriptions live for the window's lifetime and just walk it.
    private readonly List<Action> _providerRefreshers = new();

    private SettingsWindow()
    {
        Title = "Agent Island — " + L10n.Tr("Settings");
        Width = 680;
        Height = 560;
        MinWidth = 640;
        MinHeight = 460;
        Background = GrokTheme.Brush(GrokTheme.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Pixel-snapped glyphs: the 10-13px settings copy is blurry in WPF's
        // default Ideal mode.
        System.Windows.Media.TextOptions.SetTextFormattingMode(
            this, System.Windows.Media.TextFormattingMode.Display);

        // No system title bar: the brand header doubles as the drag strip
        // and the caption buttons live inside the page (top-right), matching
        // the macOS integrated-titlebar look.
        WindowStyle = WindowStyle.None;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 58,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // macOS 2.1.1 shell: full-height sidebar rail, hairline divider,
        // then the content column with the footer parked at its bottom.
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        main.Children.Add(sidebar);

        var divider = new Border { Background = GrokTheme.InkBrush(GrokTheme.Quaternary) };
        Grid.SetColumn(divider, 1);
        main.Children.Add(divider);

        var contentColumn = new DockPanel();
        var captionSpacer = new Border { Height = 24 };
        DockPanel.SetDock(captionSpacer, Dock.Top);
        contentColumn.Children.Add(captionSpacer);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        contentColumn.Children.Add(footer);

        var bottomHairline = Hairline();
        DockPanel.SetDock(bottomHairline, Dock.Bottom);
        contentColumn.Children.Add(bottomHairline);

        contentColumn.Children.Add(_scroll);
        Grid.SetColumn(contentColumn, 2);
        main.Children.Add(contentColumn);

        var shell = new Grid();
        shell.Children.Add(main);
        shell.Children.Add(CaptionButtons.Build(this));
        Content = shell;

        // Usage lands from background fetches and the guest stores publish on
        // their own schedule; the provider rows follow along instead of going
        // stale until the next tab switch.
        UsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        ProviderVisibilityStore.Shared.PropertyChanged += OnProviderStoreChanged;
        AntigravityUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        GrokUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        CursorUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        Closed += (_, _) =>
        {
            UsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            ProviderVisibilityStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            AntigravityUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            GrokUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            CursorUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
        };

        var savedTab = Preferences.Get<string?>("Settings.activeTab");
        if (Enum.TryParse<Tab>(savedTab, out var restored)) _active = restored;
        // Scripted-verification hook: jump straight to a tab.
        if (Enum.TryParse<Tab>(
                Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SETTINGS_TAB"),
                out var forced))
        {
            _active = forced;
        }
        Select(_active);
    }

    // MARK: - Chrome

    /// Left rail: compact brand up top, one item per page, the version
    /// pill + Quit at the bottom (app-level controls live on the rail, not
    /// in the page footer — macOS 2.1.1).
    private UIElement BuildSidebar()
    {
        var rail = new DockPanel { Background = GrokTheme.Brush(GrokTheme.Chrome) };

        var top = new StackPanel();
        top.Children.Add(new Border { Height = 30 });
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 14),
        };
        try
        {
            var mark = new Image
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = new BitmapImage(new Uri(
                    "pack://application:,,,/Assets/agentisland_logo_small.png")),
            };
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            brand.Children.Add(mark);
        }
        catch
        {
        }
        brand.Children.Add(new TextBlock
        {
            Text = "Agent Island",
            FontFamily = IslandFonts.Ui,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(brand);
        foreach (var tab in Enum.GetValues<Tab>())
        {
            top.Children.Add(NavItem(tab));
        }
        DockPanel.SetDock(top, Dock.Top);
        rail.Children.Add(top);

        var bottom = new Grid { Margin = new Thickness(14, 8, 14, 14) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var version = RailPill(
            "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0"),
            L10n.Tr("What's new in this version"),
            WhatsNewWindow.Open);
        Grid.SetColumn(version, 0);
        bottom.Children.Add(version);

        var quit = RailPill(L10n.Tr("Quit"), L10n.Tr("Quit AgentIsland"),
            () => System.Windows.Application.Current.Shutdown());
        Grid.SetColumn(quit, 2);
        bottom.Children.Add(quit);

        DockPanel.SetDock(bottom, Dock.Bottom);
        rail.Children.Add(bottom);
        rail.Children.Add(new Border());
        return rail;
    }

    private static Border RailPill(string text, string help, Action onClick)
    {
        var pill = new Border
        {
            Child = new TextBlock
            {
                Text = text,
                FontFamily = IslandFonts.Ui,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
            },
            CornerRadius = new CornerRadius(10),
            Background = GrokTheme.InkBrush(GrokTheme.Quaternary),
            Padding = new Thickness(9, 4, 9, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = help,
        };
        pill.MouseEnter += (_, _) => pill.Background = GrokTheme.InkBrush(GrokTheme.Secondary);
        pill.MouseLeave += (_, _) => pill.Background = GrokTheme.InkBrush(GrokTheme.Quaternary);
        pill.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            onClick();
        };
        return pill;
    }

    private UIElement NavItem(Tab tab)
    {
        var (label, glyph) = TabFace(tab);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 16,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        row.Children.Add(icon);
        var text = new TextBlock
        {
            Text = L10n.Tr(label),
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(text);

        var cell = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(8, 1, 8, 1),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var captured = tab;
        cell.MouseLeftButtonUp += (_, args) =>
        {
            Select(captured);
            args.Handled = true;
        };
        cell.MouseEnter += (_, _) =>
        {
            if (_active != captured) cell.Background = GrokTheme.InkBrush(GrokTheme.Quinary);
        };
        cell.MouseLeave += (_, _) =>
        {
            if (_active != captured) cell.Background = Brushes.Transparent;
        };
        _navItems.Add((tab, cell, icon, text));
        return cell;
    }

    private static UIElement Hairline() => new Border
    {
        Height = 1,
        Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(GrokTheme.InkA(GrokTheme.StrokeTertiary), 0.25),
                new GradientStop(GrokTheme.InkA(GrokTheme.StrokeTertiary), 0.75),
                new GradientStop(Colors.Transparent, 1),
            },
            new Point(0, 0),
            new Point(1, 0)),
    };

    private UIElement BuildFooter()
    {
        var grid = new Grid { Margin = new Thickness(24, 12, 24, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var share = new StackPanel { Orientation = Orientation.Horizontal };
        var cost = new PillButtonControl(L10n.Tr("Cost")) { ToolTip = L10n.Tr("Cost…") };
        cost.Clicked += CostWindow.Open;
        cost.Margin = new Thickness(0, 0, 8, 0);
        share.Children.Add(cost);
        var weekly = new PillButtonControl(L10n.Tr("Weekly")) { ToolTip = L10n.Tr("Share weekly report") };
        weekly.Clicked += () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly);
        weekly.Margin = new Thickness(0, 0, 8, 0);
        share.Children.Add(weekly);
        var monthly = new PillButtonControl(L10n.Tr("Monthly")) { ToolTip = L10n.Tr("Share monthly report") };
        monthly.Clicked += () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly);
        share.Children.Add(monthly);
        Grid.SetColumn(share, 3);
        grid.Children.Add(share);
        return grid;
    }

    /// The stores raise from background completions; hop to the dispatcher
    /// and let each row re-read whatever it shows.
    private void OnProviderStoreChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.BeginInvoke(RefreshProviderRows);

    private void RefreshProviderRows()
    {
        foreach (var refresh in _providerRefreshers)
        {
            refresh();
        }
    }

    private void Select(Tab tab)
    {
        _active = tab;
        // The refreshers belong to the tab that built them — anything still
        // in the list points at discarded visuals.
        _providerRefreshers.Clear();
        Preferences.Set("Settings.activeTab", tab.ToString());
        foreach (var (cellTab, cell, glyph, label) in _navItems)
        {
            var isOn = cellTab == tab;
            cell.Background = isOn
                ? GrokTheme.InkBrush(GrokTheme.Tertiary)
                : Brushes.Transparent;
            glyph.Foreground = GrokTheme.InkBrush(isOn ? GrokTheme.TextPrimary : GrokTheme.TextTertiary);
            label.Foreground = GrokTheme.InkBrush(isOn ? GrokTheme.TextPrimary : GrokTheme.TextSecondary);
        }
        var body = tab switch
        {
            Tab.Providers => BuildProviders(),
            Tab.Display => BuildDisplay(),
            Tab.General => BuildGeneral(),
            _ => (UIElement)new StackPanel(),
        };
        var pageColumn = new StackPanel();
        pageColumn.Children.Add(new TextBlock
        {
            Text = L10n.Tr(TabFace(tab).Label),
            FontFamily = IslandFonts.Ui,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            Margin = new Thickness(24, 6, 24, 2),
        });
        pageColumn.Children.Add(body);
        _scroll.Content = pageColumn;
    }

    // MARK: - Section helpers

    private static StackPanel TabStack() => new()
    {
        Orientation = Orientation.Vertical,
        Margin = new Thickness(14, 18, 14, 6),
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = L10n.Tr(text).ToUpperInvariant(),
        FontFamily = IslandFonts.Ui,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = GrokTheme.InkBrush(GrokTheme.TextQuaternary),
        Margin = new Thickness(10, 14, 10, 6),
    };

    // MARK: - General

    private UIElement BuildGeneral()
    {
        var stack = TabStack();

        var launch = new CobaltToggle(LaunchAtLogin.IsEnabled);
        launch.Toggled += enabled => LaunchAtLogin.SetEnabled(enabled);
        stack.Children.Add(new SettingsRowControl(
            "Launch at Login", null, launch));

        var language = GrokComboStyle.Apply(new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center });
        language.Items.Add(L10n.Tr("Auto (system)"));
        language.Items.Add("English");
        language.Items.Add("简体中文");
        language.SelectedIndex = AppLanguageStore.Load() switch
        {
            L10n.Language.English => 1,
            L10n.Language.SimplifiedChinese => 2,
            _ => 0,
        };
        language.SelectionChanged += (_, _) =>
        {
            var chosen = language.SelectedIndex switch
            {
                1 => L10n.Language.English,
                2 => L10n.Language.SimplifiedChinese,
                _ => L10n.Language.Auto,
            };
            AppLanguageStore.Save(chosen);
            L10n.Current = chosen;
            App.Instance.RebuildForLanguageChange();
            // Recreate this window outright: patching just the title, tab
            // bar, and rows left the brand header and footer in whatever
            // language the window was BORN in (the "inverted slogan" bug).
            // Close() raises Closed synchronously, clearing the singleton,
            // so Open() builds a fresh window on the persisted tab.
            Close();
            Open();
        };
        // Title + picker only — the picker already SHOWS the choice, an
        // echoing subtitle was noise (owner, repeatedly, on macOS).
        stack.Children.Add(new SettingsRowControl(
            "Language", null, language));

        return stack;
    }

    // MARK: - Display

    private UIElement BuildDisplay()
    {
        var stack = TabStack();

        // Quota percent as used or remaining (drawer card, tray tooltip).
        // macOS SegmentedControl order: items [false, true] → 已用 then 剩余.
        var quotaMode = Model.QuotaDisplayModeStore.Shared;
        var quotaSeg = new Segmented(
            new[] { Localization.L10n.Tr("Used"), Localization.L10n.Tr("Remaining") },
            quotaMode.ShowsRemaining ? 1 : 0);
        quotaSeg.SelectionChanged += index => quotaMode.ShowsRemaining = index == 1;
        stack.Children.Add(new SettingsRowControl(
            "Quota shows",
            null,
            quotaSeg));

        // 成本显示 — how the cost window's hero numbers read.
        stack.Children.Add(SectionLabel("Cost display"));
        var costPicker = new CostStylePickerControl(CostStylePreferenceStore.Shared.Style)
        {
            Margin = new Thickness(10, 2, 2, 8),
        };
        costPicker.StyleSelected += style => CostStylePreferenceStore.Shared.Style = style;
        stack.Children.Add(costPicker);

        // 屏幕 — which display the side drawer docks to.
        stack.Children.Add(SectionLabel("Screen"));
        var screens = System.Windows.Forms.Screen.AllScreens;
        var display = GrokComboStyle.Apply(new ComboBox { Width = 180, VerticalAlignment = VerticalAlignment.Center });
        display.Items.Add(L10n.Tr("Auto"));
        foreach (var screen in screens)
        {
            display.Items.Add(screen.DeviceName.TrimStart('\\', '.') + (screen.Primary ? " ★" : ""));
        }
        var choice = IslandTargetDisplayStore.Shared.Choice;
        display.SelectedIndex = choice == "auto"
            ? 0
            : Math.Max(0, Array.FindIndex(screens, s => s.DeviceName == choice) + 1);
        display.SelectionChanged += (_, _) =>
            IslandTargetDisplayStore.Shared.Choice = display.SelectedIndex <= 0
                ? "auto"
                : screens[display.SelectedIndex - 1].DeviceName;
        stack.Children.Add(new SettingsRowControl(
            "Show on",
            choice == "auto"
                ? L10n.Tr("Auto — picks the best available screen.")
                : L10n.Tr("Pinned to a specific display. Falls back to Auto if unplugged."),
            display));

        return stack;
    }

    // MARK: - Providers

    private UIElement BuildProviders()
    {
        var stack = TabStack();

        // The "Open threads via" pickers are retired (macOS 1.6.1): threads
        // always land back where the session lives — desktop sessions in the
        // desktop app, CLI sessions in a terminal.
        foreach (var provider in DisplayProviders.All)
        {
            stack.Children.Add(ProviderRow(provider));
        }

        var refreshPresets = RefreshIntervalStore.Presets;
        var refreshRow = new Segmented(new[] { "5m", "15m", "30m" },
            Math.Max(0, Array.IndexOf(refreshPresets, RefreshIntervalStore.Shared.Seconds)));
        refreshRow.SelectionChanged += index => RefreshIntervalStore.Shared.Seconds = refreshPresets[index];
        stack.Children.Add(new SettingsRowControl(
            "Refresh interval", null, refreshRow));

        stack.Children.Add(SectionLabel("Tokens"));
        var mode = new Segmented(
            new[] { L10n.Tr("All tokens"), L10n.Tr("Input + output") },
            TokenCountModeStore.Shared.Mode == TokenCountMode.All ? 0 : 1);
        mode.SelectionChanged += index =>
            TokenCountModeStore.Shared.Mode = index == 0 ? TokenCountMode.All : TokenCountMode.Billable;
        stack.Children.Add(new SettingsRowControl(
            "Token counting",
            L10n.Tr("Excludes cache reads; new input written to cache still counts. History is kept."),
            mode));

        // Cost freshness strip: section label + last-scan caption + Refresh.
        var costRow = new Grid { Margin = new Thickness(10, 14, 10, 14) };
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var costLabel = SectionLabel("Cost");
        costLabel.Margin = new Thickness(0);
        costLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(costLabel, 0);
        costRow.Children.Add(costLabel);
        var costCaption = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        costCaption.Text = Cost.CostStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("last scan {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : "—";
        Grid.SetColumn(costCaption, 1);
        costRow.Children.Add(costCaption);
        var costRefresh = new PillButtonControl(L10n.Tr("Refresh"));
        costRefresh.Clicked += () => Cost.CostStore.Shared.Refresh();
        var costView = new PillButtonControl(L10n.Tr("Cost…")) { Margin = new Thickness(0, 0, 8, 0) };
        costView.Clicked += CostWindow.Open;
        var costActions = new StackPanel { Orientation = Orientation.Horizontal };
        costActions.Children.Add(costView);
        costActions.Children.Add(costRefresh);
        Grid.SetColumn(costActions, 2);
        costRow.Children.Add(costActions);
        stack.Children.Add(costRow);

        return stack;
    }

    /// One provider row: a brand-tinted rule marks the leading edge, the name
    /// carries the plan/tier chip, the status line is live, and the drawer
    /// toggle sits on the trailing edge behind whatever actions the provider
    /// offers. Guests carry no action buttons — their only recovery is signing
    /// in with their own tool. No card chrome: separation is a hairline plus
    /// whitespace (macOS owner call, 2026-08-08: 不喜欢卡片质感).
    private UIElement ProviderRow(DisplayProvider provider)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The real brand mark leads the row (macOS providerCard: 20pt mark
        // in a 24pt slot, 12pt gap). The brand-tinted rule on the leading
        // edge STAYS — rule + mark together are the row's identity.
        var markHost = new Grid
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        markHost.Children.Add(ProviderMarks.Mark(provider, 20, tint: GrokTheme.Brand(provider)));
        Grid.SetColumn(markHost, 0);
        grid.Children.Add(markHost);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = provider.DisplayName(),
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var chipText = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextSecondary),
        };
        var chip = new Border
        {
            Child = chipText,
            CornerRadius = new CornerRadius(4),
            Background = GrokTheme.InkBrush(GrokTheme.Quaternary),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        titleRow.Children.Add(chip);

        var status = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleRow);
        text.Children.Add(status);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        PillButtonControl? reauth = null;
        PillButtonControl? pasteLogin = null;
        if (provider.HasFullMonitoring())
        {
            var tool = provider.ToTriggerTool();
            if (provider == DisplayProvider.Codex)
            {
                trailing.Children.Add(CodexAccountMenuButton());
            }
            if (provider == DisplayProvider.Claude)
            {
                // ONE sign-in button by default. The code fallback appears in
                // the same spot only after a browser round has actually
                // failed — progressive disclosure, not a toolbar (owner
                // review, 2026-08-08: 搞成这样子很奇怪).
                pasteLogin = new PillButtonControl(L10n.Tr("Sign in with a code"))
                {
                    Margin = new Thickness(0, 0, 8, 0),
                    Visibility = Visibility.Collapsed,
                };
                pasteLogin.Clicked += StartClaudePasteLogin;
                trailing.Children.Add(pasteLogin);
            }
            reauth = new PillButtonControl(L10n.Tr("Re-authenticate"))
            {
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };
            reauth.Clicked += () => ReauthFlow.Run(tool);
            trailing.Children.Add(reauth);
        }
        var toggle = new CobaltToggle(ProviderVisibilityStore.Shared.IsShown(provider))
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        trailing.Children.Add(toggle);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        // macOS providerCard chrome: no boxes — a brand-gradient rule on the
        // leading edge (Google's four hues for Antigravity), separation by a
        // bottom hairline + whitespace, and hover breathing a faint brand
        // wash across the row while the rule brightens and widens a hair.
        var content = new Border
        {
            Child = grid,
            Padding = new Thickness(14, 13, 4, 13),
            Background = Brushes.Transparent,
        };
        var washStops = new GradientStopCollection();
        var stops = GrokTheme.BrandStops(provider);
        for (var i = 0; i < stops.Count; i++)
        {
            washStops.Add(new GradientStop(
                IslandColors.Alpha(stops[i], 0.07), 0.7 * i / Math.Max(1, stops.Count - 1)));
        }
        washStops.Add(new GradientStop(Colors.Transparent, 1));
        var wash = new Border
        {
            Background = new LinearGradientBrush(washStops, new Point(0, 0), new Point(1, 0)),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var rule = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        var hairline = new Border
        {
            Height = 1,
            Background = GrokTheme.InkBrush(GrokTheme.Quaternary),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };
        var row = new Grid { Background = Brushes.Transparent };
        row.Children.Add(wash);
        row.Children.Add(content);
        row.Children.Add(rule);
        row.Children.Add(hairline);

        var hovered = false;
        void Paint()
        {
            var enabledNow = ProviderVisibilityStore.Shared.IsShown(provider);
            var ruleOpacity = enabledNow ? (hovered ? 1.0 : 0.85) : (hovered ? 0.45 : 0.22);
            rule.Background = GrokTheme.BrandGradient(provider, new Point(0, 0), new Point(0, 1));
            var ease = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            };
            var beat = new Duration(TimeSpan.FromMilliseconds(160));
            wash.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(hovered ? 1 : 0, beat) { EasingFunction = ease });
            rule.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(ruleOpacity, beat) { EasingFunction = ease });
            rule.BeginAnimation(WidthProperty,
                new System.Windows.Media.Animation.DoubleAnimation(hovered ? 3 : 2, beat) { EasingFunction = ease });
        }
        row.MouseEnter += (_, _) =>
        {
            hovered = true;
            Paint();
        };
        row.MouseLeave += (_, _) =>
        {
            hovered = false;
            Paint();
        };

        toggle.Toggled += enabled =>
        {
            ProviderVisibilityStore.Shared.SetShown(provider, enabled);
            if (enabled) KickGuestRefresh(provider);
            RefreshProviderRows();
        };

        void Refresh()
        {
            toggle.IsOn = ProviderVisibilityStore.Shared.IsShown(provider);
            status.Text = ProviderStatus(provider);
            var badge = ProviderChip(provider);
            chipText.Text = badge ?? string.Empty;
            chip.Visibility = string.IsNullOrEmpty(badge) ? Visibility.Collapsed : Visibility.Visible;
            if (reauth is not null)
            {
                var available = provider == DisplayProvider.Claude
                    ? ClaudeReauthAvailable()
                    : CodexReauthAvailable();
                var waiting = provider == DisplayProvider.Claude
                    ? UsageStore.Shared.ClaudeReauthInProgress
                    : UsageStore.Shared.CodexReauthInProgress;
                reauth.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                reauth.Label = waiting ? L10n.Tr("waiting for login…") : L10n.Tr("Re-authenticate");
            }
            if (pasteLogin is not null)
            {
                var store = UsageStore.Shared;
                pasteLogin.Visibility = store.ClaudeReauthFailureCaption is not null
                    && !store.ClaudeReauthInProgress
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            Paint();
        }

        _providerRefreshers.Add(Refresh);
        Refresh();
        return row;
    }

    /// A guest slot just turned on: fetch now instead of waiting out the next
    /// usage poll. The guest stores gate themselves on the selection, so an
    /// unselected provider had never fetched at all.
    private static void KickGuestRefresh(DisplayProvider provider)
    {
        switch (provider)
        {
            case DisplayProvider.Antigravity:
                AntigravityUsageStore.Shared.KickRefresh();
                break;
            case DisplayProvider.Grok:
                GrokUsageStore.Shared.KickRefresh();
                break;
            case DisplayProvider.Cursor:
                CursorUsageStore.Shared.KickRefresh();
                break;
            default:
                break;
        }
    }

    private static string ProviderStatus(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => ClaudeStatus(),
        DisplayProvider.Codex => ProviderSubtitle(UsageStore.Shared.Codex),
        DisplayProvider.Antigravity => AntigravityStatus(),
        DisplayProvider.Grok => GrokStatus(),
        DisplayProvider.Cursor => CursorStatus(),
        DisplayProvider.Kimi => LocalOnlyStatus(DisplayProvider.Kimi, "Not detected — open Kimi Code once"),
        DisplayProvider.DeepSeek => LocalOnlyStatus(DisplayProvider.DeepSeek, "Not detected — open DeepSeek Harness once"),
        _ => string.Empty,
    };

    private static string? ProviderChip(DisplayProvider provider)
    {
        var visibility = ProviderVisibilityStore.Shared;
        return provider switch
        {
            DisplayProvider.Claude => UsageStore.Shared.Claude.Plan?.ToUpperInvariant(),
            DisplayProvider.Codex => UsageStore.Shared.Codex.Plan?.ToUpperInvariant(),
            // A badge for a provider with no login on this machine would be a
            // leftover from a cached snapshot, not a fact about this machine.
            DisplayProvider.Antigravity =>
                visibility.AntigravityDetected ? AntigravityUsageStore.Shared.TierBadge : null,
            DisplayProvider.Grok => visibility.GrokDetected ? GrokUsageStore.Shared.AuthModeBadge : null,
            DisplayProvider.Cursor => visibility.CursorDetected ? CursorUsageStore.Shared.PlanBadge : null,
            _ => null,
        };
    }

    /// "synced 2m ago · 69% / 33%" — the most authoritative diagnostic
    /// surface; errors surface in place of the numbers. A single-window
    /// provider (Codex's one weekly quota) shows one percentage, not a
    /// phantom pair (macOS secondaryMissing).
    private static string ProviderSubtitle(AppUsage usage)
    {
        var synced = UsageStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("idle");
        var five = WindowCaption(usage.FiveHour);
        var week = WindowCaption(usage.Weekly);
        // Both windows failing the same way is ONE fact — "⚠ login required /
        // ⚠ login required" read as a stutter bug (macOS owner screenshot,
        // 2026-08-08).
        var caption = usage.SecondaryMissing || (five == week && five.StartsWith("⚠", StringComparison.Ordinal))
            ? five
            : $"{five} / {week}";
        return $"{synced} · {caption}";
    }

    private static string WindowCaption(WindowUsage window)
    {
        // Gate on the PRINTED number, not the raw fraction: Swift tests
        // percentInt, so a merged stale window sitting at 0.4% printed "0%"
        // on Windows while macOS surfaced its error caption.
        var percent = Formatting.PercentInt(window.UsedPercent);
        if (window.Error is { } error && percent == 0)
        {
            return "⚠ " + ErrorDisplay.Localize(error);
        }
        return $"{percent}%";
    }

    /// Guest rows read like the Claude/Codex rows — sync freshness first, then
    /// the quota numbers. The account email deliberately stays out of the line:
    /// a row leading with a bare email read as a glitch (macOS owner report,
    /// 2026-08-08); identity lives in the usage-strip hover instead.
    private static string AntigravityStatus()
    {
        var store = AntigravityUsageStore.Shared;
        if (!ProviderVisibilityStore.Shared.AntigravityDetected)
        {
            return L10n.Tr("Not detected — sign in with the antigravity CLI");
        }
        var parts = new List<string> { GuestSync(store.LastUpdated) };
        if (store.StatusCaption is { } caption)
        {
            parts.Add("⚠ " + ErrorDisplay.Localize(caption));
        }
        else if (store.Snapshot?.Primary is { } pool)
        {
            // Only the Gemini pool: Claude and GPT are other providers'
            // rows in this app, and showing their shared pool here read as
            // cross-wiring (owner call, 2026-08-09).
            parts.Add(L10n.TrFormat("{0} {1}%", pool.ShortLabel, Percent(pool.UsedPercent)));
        }
        return string.Join(" · ", parts);
    }

    /// A failed browser sign-in has to SAY what went wrong. The old path
    /// swallowed the reason and spawned a terminal running a retired CLI
    /// command, which surfaced as an "authentication failed" from nowhere
    /// (owner repro, 2026-08-08). The reason replaces the usage numbers only
    /// while it is fresh — a later successful round clears it.
    private static string ClaudeStatus()
    {
        var subtitle = ProviderSubtitle(UsageStore.Shared.Claude);
        var store = UsageStore.Shared;
        if (store.ClaudeReauthFailureCaption is not { } reason || store.ClaudeReauthInProgress)
        {
            return subtitle;
        }
        return $"{subtitle} · ⚠ {ErrorDisplay.Localize(reason)}";
    }

    private static string GrokStatus()
    {
        var store = GrokUsageStore.Shared;
        if (!ProviderVisibilityStore.Shared.GrokDetected)
        {
            return L10n.Tr("Not detected — sign in with the grok CLI");
        }
        var parts = new List<string> { GuestSync(store.LastUpdated) };
        if (store.ErrorCaption is { } caption)
        {
            parts.Add("⚠ " + ErrorDisplay.Localize(caption));
        }
        else if (store.Snapshot is { } snapshot)
        {
            parts.Add(L10n.TrFormat("week {0}%", Percent(snapshot.WeeklyUsedPercent)));
        }
        return string.Join(" · ", parts);
    }

    private static string CursorStatus()
    {
        var store = CursorUsageStore.Shared;
        if (!ProviderVisibilityStore.Shared.CursorDetected)
        {
            return L10n.Tr("Not detected — sign in inside Cursor");
        }
        var parts = new List<string> { GuestSync(store.LastUpdated) };
        if (store.ErrorCaption is { } caption)
        {
            parts.Add("⚠ " + ErrorDisplay.Localize(caption));
        }
        else if (store.Snapshot is { } snapshot)
        {
            parts.Add(L10n.TrFormat("cycle {0}%", Percent(snapshot.UsedPercent)));
        }
        return string.Join(" · ", parts);
    }

    /// Kimi Code and DeepSeek publish no quota: their row reports today's
    /// tokens from the local logs instead.
    private static string LocalOnlyStatus(DisplayProvider provider, string missing)
    {
        if (!ProviderVisibilityStore.Shared.IsDetected(provider)) return L10n.Tr(missing);
        var summary = Cost.CostStore.Shared.Summary(provider);
        var tokens = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable
            ? summary.TodayBillableTokens
            : summary.TodayTokens;
        return L10n.TrFormat("local sessions · {0} tokens today", Formatting.CompactTokens(tokens));
    }

    private static string GuestSync(DateTimeOffset? updated) => updated is { } stamp
        ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - stamp, L10n.IsChinese))
        : L10n.Tr("idle");

    private static int Percent(double fraction) => Formatting.PercentInt(fraction);

    /// #31: the button rides the CURRENT auth state, not the mere presence of
    /// a CLI on disk — the inherited gate parked a permanent Re-authenticate
    /// beside a perfectly healthy row (owner review, 2026-08-08:
    /// 已经登录了为什么会出现重新认证). Kept visible while a login is in
    /// flight so the "waiting" state doesn't vanish mid-flow. Claude's web
    /// login needs no CLI, so there is no binary check here at all.
    private static bool ClaudeReauthAvailable()
    {
        if (UsageStore.Shared.ClaudeReauthInProgress) return true;
        var usage = UsageStore.Shared.Claude;
        return ClaudeCredentials.IsAuthRecoverableError(usage.FiveHour.Error)
            || ClaudeCredentials.IsAuthRecoverableError(usage.Weekly.Error);
    }

    /// Codex has no in-app login, so a locatable CLI stays a precondition for
    /// the terminal flow — but it is not, on its own, a reason to offer
    /// re-auth. Mirrors macOS CodexCredentials.canPromptReauth(usage:), which
    /// the Windows CodexCredentials.CanPromptReauth() (CLI-only) does not.
    private static bool CodexReauthAvailable()
    {
        if (UsageStore.Shared.CodexReauthInProgress) return true;
        // Caption test FIRST. CanPromptReauth is a full disk probe (dozens of
        // stats plus a PATH scan), and this runs on the UI thread once per
        // PropertyChanged from five stores — roughly seven times per poll
        // while the window is open. A healthy Codex row never pays for it.
        var usage = UsageStore.Shared.Codex;
        if (!MentionsAuthFailure(usage.FiveHour.Error) && !MentionsAuthFailure(usage.Weekly.Error))
        {
            return false;
        }
        return CodexCredentials.CanPromptReauth();
    }

    private static bool MentionsAuthFailure(string? message) =>
        message is not null
        && (message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("401", StringComparison.Ordinal));

    /// One-click Codex account switching: park the live login under a name,
    /// swap between parked logins, and opt into auto-rotation when the active
    /// account runs dry (macOS codexAccountMenu). The label turns amber once
    /// the auto-switcher has rotated on its own — that is the only trace it
    /// leaves, and the row should say so.
    private UIElement CodexAccountMenuButton()
    {
        var label = new TextBlock
        {
            Text = L10n.Tr("Accounts"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        var button = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(6),
            Background = GrokTheme.InkBrush(GrokTheme.Tertiary),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.MouseEnter += (_, _) => button.Background = GrokTheme.InkBrush(GrokTheme.Secondary);
        button.MouseLeave += (_, _) => button.Background = GrokTheme.InkBrush(GrokTheme.Tertiary);
        button.MouseLeftButtonUp += (_, args) =>
        {
            OpenCodexAccountMenu(button);
            args.Handled = true;
        };

        void Refresh()
        {
            var switched = UsageStore.Shared.CodexAutoSwitched;
            label.Foreground = IslandColors.Brush(switched is null
                ? GrokTheme.InkA(GrokTheme.TextPrimary)
                : GrokTheme.Amber);
            button.ToolTip = switched is null
                ? L10n.Tr("Switch Codex account")
                : L10n.TrFormat("Auto-switched to {0}", switched);
        }

        _providerRefreshers.Add(Refresh);
        Refresh();
        return button;
    }

    private void OpenCodexAccountMenu(FrameworkElement anchor)
    {
        var menu = GrokMenu.Create();
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

        var accounts = CodexAccountSwitcher.Accounts();
        var active = CodexAccountSwitcher.ActiveLabel();
        if (accounts.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = L10n.Tr("No saved accounts yet"),
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var account in accounts)
            {
                var captured = account;
                // The tick lives in the header text, not IsChecked: a
                // checkable item toggles its own mark on click and would then
                // claim a login is live before the swap even succeeded.
                var item = new MenuItem
                {
                    Header = string.Equals(account.Label, active, StringComparison.Ordinal)
                        ? "✓ " + account.Label
                        : account.Label,
                };
                item.Click += (_, _) =>
                {
                    if (!CodexAccountSwitcher.Activate(captured)) return;
                    // The badge described the previous rotation; a manual pick
                    // supersedes it (macOS parity).
                    UsageStore.Shared.CodexAutoSwitched = null;
                    UsageStore.Shared.Refresh();
                    RefreshProviderRows();
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = L10n.Tr("Remove saved account") };
            foreach (var account in accounts)
            {
                var captured = account;
                var child = new MenuItem { Header = account.Label };
                child.Click += (_, _) =>
                {
                    CodexAccountSwitcher.Forget(captured);
                    RefreshProviderRows();
                };
                remove.Items.Add(child);
            }
            menu.Items.Add(remove);
            menu.Items.Add(new Separator());
        }

        var save = new MenuItem { Header = L10n.Tr("Save current account…") };
        save.Click += (_, _) => PromptParkCodexAccount();
        menu.Items.Add(save);
        menu.Items.Add(new Separator());

        var auto = new MenuItem
        {
            Header = L10n.Tr("Auto-switch when exhausted"),
            IsCheckable = true,
            IsChecked = CodexAccountSwitcher.AutoSwitchEnabled,
        };
        auto.Click += (_, _) => CodexAccountSwitcher.AutoSwitchEnabled = auto.IsChecked;
        menu.Items.Add(auto);

        menu.IsOpen = true;
    }

    /// Paste-code sign-in: open Anthropic's own code page, let it show a code,
    /// take that code back through a plain text field. Independent of which
    /// browser profile holds the claude.ai session, and of whether a loopback
    /// listener can bind at all — which is exactly why it is the fallback the
    /// failure caption unlocks.
    private void StartClaudePasteLogin()
    {
        var ticket = ClaudeCredentials.BeginPasteLogin();
        // The return value is deliberately ignored: if the default browser
        // won't open, the dialog's own "Copy link" is the remedy, and showing
        // an error instead would hide it.
        _ = ClaudeWebLogin.OpenInBrowser(ticket.Url);
        var pasted = NamePrompt.Ask(
            this,
            L10n.Tr("Sign in with a code"),
            L10n.Tr("Approve the page that just opened, then paste the code it shows here"),
            L10n.Tr("Paste the code"),
            confirmLabel: L10n.Tr("Sign in"),
            // An authorization code plus its state fragment runs well past the
            // 40-character label cap the account prompt uses.
            maxLength: 4096,
            extraLabel: L10n.Tr("Copy link"),
            extraAction: () =>
            {
                // Clipboard access can throw when another process holds it
                // open; a failed copy must not take down the dialog.
                try { System.Windows.Clipboard.SetText(ticket.Url); }
                catch { }
            });
        if (pasted is null) return;
        _ = CompleteClaudePasteLogin(pasted, ticket);
    }

    private async Task CompleteClaudePasteLogin(string pasted, ClaudeCredentials.PasteLoginTicket ticket)
    {
        bool ok;
        try
        {
            ok = await ClaudeCredentials.CompletePasteLogin(pasted, ticket.Verifier, ticket.State);
        }
        catch
        {
            ok = false;
        }
        if (ok)
        {
            UsageStore.Shared.ClearClaudeReauthFailure();
            UsageStore.Shared.Refresh();
            RefreshProviderRows();
            return;
        }
        IslandDialog.ShowApp(
            L10n.Tr("That code did not work"),
            L10n.Tr("Copy the whole code from the page and try once more"),
            secondaryLabel: L10n.Tr("OK"));
    }

    private void PromptParkCodexAccount()
    {
        var label = NamePrompt.Ask(
            this,
            L10n.Tr("Save current account"),
            L10n.Tr("Give this login a name so you can switch back to it later"),
            L10n.Tr("work / personal"));
        if (label is null) return;
        if (!CodexAccountSwitcher.ParkCurrent(label)) return;
        RefreshProviderRows();
    }

    /// Single-field name prompt for parking the live Codex login — the
    /// island-styled stand-in for macOS's NSAlert accessory text field.
    /// IslandDialog carries no input form and this is the only place in the
    /// app that needs one.
    private sealed class NamePrompt : Window
    {
        private readonly TextBox _field;
        private bool _accepted;

        private NamePrompt(
            string title,
            string message,
            string placeholder,
            string confirmLabel,
            int maxLength,
            string? extraLabel,
            Action? extraAction)
        {
            Width = 400;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Title = title;
            System.Windows.Media.TextOptions.SetTextFormattingMode(
                this, System.Windows.Media.TextFormattingMode.Display);

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = GrokTheme.Brush(GrokTheme.Elevated),
                BorderBrush = GrokTheme.InkBrush(GrokTheme.StrokeSecondary),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(16),
                Effect = GrokTheme.Float(0.18),
            };
            Content = card;

            var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
            card.Child = stack;
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = IslandFonts.Ui,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            });
            stack.Children.Add(new TextBlock
            {
                Text = message,
                FontFamily = IslandFonts.Ui,
                FontSize = 12,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextSecondary),
                Margin = new Thickness(0, 6, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            _field = new TextBox
            {
                FontFamily = IslandFonts.Ui,
                FontSize = 13,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
                CaretBrush = GrokTheme.InkBrush(GrokTheme.TextPrimary),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                // The switcher caps stored labels at 40 characters anyway;
                // stopping here means the name the user typed is the name
                // they get back in the menu. An OAuth code is far longer, so
                // the cap is per-prompt rather than a constant.
                MaxLength = maxLength,
                ContextMenu = GrokMenu.TextEditing(),
            };
            var hint = new TextBlock
            {
                Text = placeholder,
                FontFamily = IslandFonts.Ui,
                FontSize = 13,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextQuaternary),
                IsHitTestVisible = false,
            };
            _field.TextChanged += (_, _) =>
                hint.Visibility = _field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var fieldLayer = new Grid();
            fieldLayer.Children.Add(hint);
            fieldLayer.Children.Add(_field);
            stack.Children.Add(new Border
            {
                Child = fieldLayer,
                CornerRadius = new CornerRadius(6),
                Background = GrokTheme.InkBrush(GrokTheme.Field),
                BorderBrush = GrokTheme.InkBrush(GrokTheme.StrokeSecondary),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            if (extraLabel is not null && extraAction is { } onExtra)
            {
                // "Copy link" is a side action, not an exit: it puts the URL
                // on the clipboard and leaves the dialog up, because the user
                // still has to come back and paste the code.
                var extra = new PillButtonControl(extraLabel) { Margin = new Thickness(0, 0, 8, 0) };
                extra.Clicked += () => onExtra();
                buttons.Children.Add(extra);
            }
            var cancel = new PillButtonControl(L10n.Tr("Cancel")) { Margin = new Thickness(0, 0, 8, 0) };
            cancel.Clicked += Close;
            var confirm = new PillButtonControl(confirmLabel, primary: true);
            confirm.Clicked += Accept;
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            stack.Children.Add(buttons);

            KeyDown += (_, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter) Accept();
                else if (args.Key == System.Windows.Input.Key.Escape) Close();
            };
            Loaded += (_, _) => _field.Focus();
        }

        private void Accept()
        {
            _accepted = true;
            Close();
        }

        /// The typed text, or null when the prompt was dismissed or left blank.
        public static string? Ask(
            Window owner,
            string title,
            string message,
            string placeholder,
            string? confirmLabel = null,
            int maxLength = 40,
            string? extraLabel = null,
            Action? extraAction = null)
        {
            var prompt = new NamePrompt(
                title,
                message,
                placeholder,
                confirmLabel ?? L10n.Tr("Save"),
                maxLength,
                extraLabel,
                extraAction)
            {
                Owner = owner,
            };
            prompt.ShowDialog();
            if (!prompt._accepted) return null;
            var value = prompt._field.Text.Trim();
            return value.Length == 0 ? null : value;
        }
    }
}
