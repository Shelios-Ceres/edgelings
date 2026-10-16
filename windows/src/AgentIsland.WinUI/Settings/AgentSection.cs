using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Settings;

/// One agent on the Agents page: an expander whose header says who it is,
/// its plan and how fresh its numbers are, with the drawer toggle; opened,
/// it shows what the agent is doing, each quota window with its reset, the
/// tokens counted from local logs, the account, sign-in and the character.
internal sealed class AgentSection
{
    private readonly DisplayProvider _provider;
    private readonly TextBlock _plan = new();
    private readonly Border _planBadge;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    // Under Pixel the status is rich text, to carry the kit's warning sign.
    private readonly bool _pixel = Looks.Pixel;
    private readonly RichTextBlock? _pixelStatus;
    private readonly ToggleSwitch _shown;
    private readonly SettingsCard _activity;
    private readonly TextBlock _activityText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(SettingsCard Card, IQuotaBar Bar, TextBlock Value)> _meters = new();
    private readonly SettingsCard _tokens;
    private readonly TextBlock _tokensText = new();
    private readonly SettingsCard _note;
    private readonly SettingsCard _account;
    private readonly TextBlock _accountText = new() { IsTextSelectionEnabled = true };
    private readonly SettingsCard _signIn;
    private readonly Button _signInButton = new();
    private readonly FaceSpinner _signInRing = new() { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly PixelSpinner _signInSpinner = new() { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private bool _updating;

    public AgentSection(DisplayProvider provider, Func<XamlRoot?> root)
    {
        _provider = provider;
        if (_pixel) _pixelStatus = PixelStatusText();
        _planBadge = Badge(_plan, provider);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(header, ProviderIdentity.DisplayName(provider));
        header.Children.Add(new TextBlock { Text = ProviderIdentity.DisplayName(provider), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(_planBadge);

        _shown = Toggle(ProviderVisibilityStore.Shared.IsShown(provider), on =>
        {
            if (_updating) return;
            ProviderVisibilityStore.Shared.SetShown(provider, on);
            if (on) AgentReports.RefreshNow(provider);
        });
        // Off hides the agent everywhere, not only from the drawer.
        _shown.OnContent = L10n.Tr("Shown");
        _shown.OffContent = L10n.Tr("Hidden");
        AutomationProperties.SetAutomationId(_shown, $"Agent.{provider.RawValue()}.ShowInDrawer");
        NameShownSwitch(_shown, ProviderIdentity.DisplayName(provider));

        Expander = new SettingsExpander
        {
            Header = header,
            Description = _pixelStatus is null ? _status : _pixelStatus,
            HeaderIcon = new ImageIcon(),
            Content = _shown,
        };
        Name(Expander, "Details", L10n.Tr("Settings"));
        _ = LoadMarkAsync();

        _activity = new SettingsCard { Header = L10n.Tr("Right now"), Description = _activityText, HeaderIcon = Icon("\uE9D9") };
        Name(_activity, "Activity", L10n.Tr("Right now"));
        Expander.Items.Add(_activity);
        _tokens = new SettingsCard { Header = L10n.Tr("Tokens"), HeaderIcon = Icon("\uE8EF"), Content = _tokensText };
        Expander.Items.Add(_tokens);
        _note = new SettingsCard { HeaderIcon = Icon("\uE946") };
        Expander.Items.Add(_note);
        _account = new SettingsCard { Header = L10n.Tr("Account"), HeaderIcon = Icon("\uE77B") };
        _account.Content = provider == DisplayProvider.Codex
            ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _accountText, CodexAccounts.Button(root) } }
            : _accountText;
        _accountText.VerticalAlignment = VerticalAlignment.Center;
        Expander.Items.Add(_account);

        _signIn = new SettingsCard { Header = L10n.Tr("Sign in"), HeaderIcon = Icon("\uE8D7") };
        if (provider is DisplayProvider.Claude or DisplayProvider.Codex)
        {
            _signInButton.Click += async (_, _) =>
            {
                using var english = Looks.English();
                if (AgentReports.StartSignIn(provider) || root() is not { } xamlRoot) return;
                await Alert(xamlRoot, L10n.Tr("Sign in"), L10n.Tr(provider == DisplayProvider.Claude
                    ? "Claude Code CLI not found. Log in from a terminal with: claude /login"
                    : "Codex CLI not found. Log in from a terminal with: codex login"));
            };
            _signIn.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Looks.Pixel ? _signInSpinner : _signInRing, _signInButton },
            };
        }
        Expander.Items.Add(_signIn);
        if (provider == DisplayProvider.Claude) Expander.Items.Add(PromptHookCard(root));

        if (App.Current.Drawer is { Shapes: { } shapes, Colors: { } colors } drawer)
        {
            Expander.Items.Add(LookCard(drawer, shapes, colors));
        }
    }

    public SettingsExpander Expander { get; }

    public void Update(AgentReport report)
    {
        _updating = true;
        _plan.Text = report.Plan ?? "";
        _planBadge.Visibility = string.IsNullOrEmpty(report.Plan) ? Visibility.Collapsed : Visibility.Visible;
        if (_pixelStatus is not null) SetStatus(_pixelStatus, report.Status, report.Warning);
        else SetStatus(_status, report.Status, report.Warning);
        _shown.IsOn = ProviderVisibilityStore.Shared.IsShown(_provider);

        while (_meters.Count < report.Meters.Count)
        {
            FrameworkElement bar = Looks.Pixel
                ? new PixelBar(30) { VerticalAlignment = VerticalAlignment.Center }
                : new QuotaBar(8) { Width = 180, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { MinWidth = Looks.Pixel ? 64 : 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { bar, value } };
            var card = new SettingsCard { HeaderIcon = Icon("\uE9D2"), Content = content };
            _meters.Add((card, (IQuotaBar)bar, value));
            Expander.Items.Insert(_meters.Count, card);
        }
        for (var i = 0; i < _meters.Count; i++)
        {
            var (card, bar, value) = _meters[i];
            if (i >= report.Meters.Count)
            {
                card.Visibility = Visibility.Collapsed;
                continue;
            }
            var meter = report.Meters[i];
            card.Visibility = Visibility.Visible;
            card.Header = MeterTitle(meter.Label);
            SetDescription(card, meter.Resets);
            // A new share counts in (M16), its percentage and warning with it.
            var text = meter.Value;
            var final = meter.Shown ?? 0;
            bar.Set(meter.Shown, Accent(), meter.Used >= 0.9, Brand.IsDark(), (share, warn) =>
            {
                value.Text = QuotaCount.Percent(share, final, text);
                if (_pixel) return;
                value.Foreground = Res<Brush>(warn ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
                value.FontWeight = warn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            });
        }
        _tokens.Visibility = report.Tokens is null ? Visibility.Collapsed : Visibility.Visible;
        if (report.Tokens is { } tokens)
        {
            _tokens.Description = TokenDisplayText.ModeLabel(TokenCountModeStore.Shared.Mode);
            _tokensText.Text = $"{tokens.Today}  ·  {tokens.Month}";
        }
        _note.Visibility = report.Note is null ? Visibility.Collapsed : Visibility.Visible;
        _note.Header = report.Note ?? "";
        _account.Visibility = report.Account is null && _provider != DisplayProvider.Codex ? Visibility.Collapsed : Visibility.Visible;
        _accountText.Text = report.Account ?? L10n.Tr("Not saved yet");

        _signIn.Description = report.SignInHint;
        var waiting = report.SignIn == SignInOffer.Waiting;
        _signInRing.IsActive = _signInSpinner.IsActive = waiting;
        _signInRing.Visibility = _signInSpinner.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        _signInButton.IsEnabled = report.SignIn != SignInOffer.Waiting;
        _signInButton.Content = L10n.Tr(report.SignIn switch
        {
            SignInOffer.Waiting => "Waiting for login…",
            SignInOffer.Available => "Sign in",
            _ => "Sign in again",
        });
        Name(_signInButton, "SignIn", _signInButton.Content.ToString()!);
        // Cleared rather than null, so the window's own button style applies.
        if (report.SignIn == SignInOffer.Available) _signInButton.Style = Res<Style>("AccentButtonStyle");
        else _signInButton.ClearValue(FrameworkElement.StyleProperty);

        _activity.Visibility = report.Detected ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
    }

    /// AGENTISLAND_DEBUG_SETTINGS_FILM: the first meter counts to a share, as
    /// a quota refresh would (M16); false without a meter.
    internal bool DebugCount(double share)
    {
        if (_meters.FirstOrDefault(meter => meter.Card.Visibility == Visibility.Visible) is not { Bar: { } bar, Value: { } value }) return false;
        bar.Set(share, Accent(), share >= 0.9, Brand.IsDark(), (shown, warn) =>
        {
            value.Text = $"{(int)Math.Round(shown * 100)}%";
            if (_pixel) return;
            value.Foreground = Res<Brush>(warn ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            value.FontWeight = warn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        });
        return true;
    }

    /// The live line: the character's phase and detail, and the project of
    /// the session it follows.
    public void UpdateActivity()
    {
        var monitor = ActivityMonitor.Shared;
        var tool = _provider.ToTriggerTool();
        var (phase, detail) = App.Current.Drawer?.PhaseOf(_provider) ?? (AgentPhase.Idle, null);
        var parts = new List<string> { DrawerLooks.PhaseLabel(phase) };
        if (detail is { Length: > 0 }) parts.Add(detail);
        // The model doing most of the last five hours' work.
        if (AgentIsland.Cost.CostStore.Shared.Summary(_provider).RecentModels.FirstOrDefault() is { } model) parts.Add(model.Model);
        if (monitor.ThreadFor(tool) is { } thread)
        {
            if (Project(thread.Cwd) is { } project) parts.Add(project);
            parts.Add(L10n.TrFormat("active {0}", Formatting.RelativeAgo(DateTimeOffset.Now - thread.Modified, L10n.IsChinese)));
        }
        _activityText.Text = string.Join("  ·  ", parts);
    }

    private static string? Project(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private void Name(DependencyObject control, string id, string label)
    {
        AutomationProperties.SetName(control, $"{ProviderIdentity.DisplayName(_provider)} · {label}");
        AutomationProperties.SetAutomationId(control, $"Agent.{_provider.RawValue()}.{id}");
    }

    /// Bars wear the agent's character colour, as in the drawer.
    private Windows.UI.Color Accent()
    {
        if (Looks.Pixel) return PixelTokens.Agent(_provider, Brand.IsDark());
        var swatch = App.Current.Drawer?.SwatchOf(_provider)
            ?? DrawerLooks.Swatch(_provider, DrawerLooks.For(_provider).Color, DrawerStore.Shared.Theme);
        return Brand.Rgb(Brand.Readable(swatch, Brand.IsDark()));
    }

    private static string MeterTitle(string label) => label switch
    {
        _ when label == L10n.Tr("5h") => L10n.Tr("5-hour window"),
        _ when label == L10n.Tr("Week") => L10n.Tr("Weekly window"),
        _ when label == L10n.Tr("30d") => L10n.Tr("Billing cycle"),
        _ when label == L10n.Tr("Month") => L10n.Tr("Monthly window"),
        _ => label,
    };

    /// Opt-in: the Notification hook that lets the drawer show an open
    /// permission prompt instead of a long-running tool.
    private static SettingsCard PromptHookCard(Func<XamlRoot?> root)
    {
        var toggle = new ToggleSwitch { IsOn = ClaudeHooks.IsInstalled(), OnContent = L10n.Tr("On"), OffContent = L10n.Tr("Off") };
        UseKit(toggle);
        AutomationProperties.SetName(toggle, $"Claude · {L10n.Tr("Permission prompts")}");
        AutomationProperties.SetAutomationId(toggle, "Agent.claude.PermissionPrompts");
        var settling = false;
        toggle.Toggled += async (_, _) =>
        {
            if (settling) return;
            using var english = Looks.English();
            var wanted = toggle.IsOn;
            var error = await Task.Run(() => ClaudeHooks.SetInstalled(wanted));
            if (error is null) return;
            settling = true;
            toggle.IsOn = !wanted;
            settling = false;
            if (root() is { } xamlRoot) await Alert(xamlRoot, L10n.Tr("Couldn't change Claude Code's settings"), error);
        };
        return new SettingsCard
        {
            Header = L10n.Tr("Permission prompts"),
            Description = new TextBlock
            {
                Text = L10n.Tr("Shows when Claude Code waits for you to approve a tool. Adds a small hook to ~/.claude/settings.json that records only the session and the prompt type; sessions started afterwards pick it up."),
                TextWrapping = TextWrapping.Wrap,
            },
            HeaderIcon = Icon("\uE8D7"),
            Content = toggle,
        };
    }

    private SettingsCard LookCard(Drawer.DrawerWindow drawer, IReadOnlyList<string> shapes, IReadOnlyList<string> colors)
    {
        var (shape, color) = drawer.LookOf(_provider);
        var shapeBox = new ComboBox { MinWidth = 130 };
        UseKit(shapeBox);
        Name(shapeBox, "Shape", L10n.Tr("Shape"));
        foreach (var option in shapes) shapeBox.Items.Add(DrawerLooks.ShapeLabel(option));
        shapeBox.SelectedIndex = Math.Max(0, shapes.ToList().IndexOf(shape));
        var colorBox = new ComboBox { MinWidth = 150 };
        UseKit(colorBox);
        Name(colorBox, "Color", L10n.Tr("Color"));
        var dark = Brand.IsDark();
        foreach (var option in colors)
        {
            var optionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FaceChip(14, Brand.Rgb(Brand.Readable(DrawerLooks.Swatch(option), dark))) { VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = DrawerLooks.ColorLabel(option), VerticalAlignment = VerticalAlignment.Center },
                },
            };
            AutomationProperties.SetName(optionPanel, DrawerLooks.ColorLabel(option));
            colorBox.Items.Add(optionPanel);
        }
        colorBox.SelectedIndex = Math.Max(0, colors.ToList().IndexOf(color));
        void Apply()
        {
            if (shapeBox.SelectedIndex < 0 || colorBox.SelectedIndex < 0) return;
            drawer.SetLook(_provider, shapes[shapeBox.SelectedIndex], colors[colorBox.SelectedIndex]);
        }
        shapeBox.SelectionChanged += (_, _) => Apply();
        colorBox.SelectionChanged += (_, _) => Apply();
        return new SettingsCard
        {
            Header = L10n.Tr("Character"),
            Description = L10n.Tr("How this agent looks in the drawer"),
            HeaderIcon = Icon("\uE790"),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { shapeBox, colorBox } },
        };
    }

    /// The brand's mark in the agent's puck, or under Pixel the agent's
    /// pixel bot face.
    private async Task LoadMarkAsync()
    {
        if (Looks.Pixel)
        {
            Expander.HeaderIcon = new ImageIcon { Source = PixelIcons.Face(_provider) };
            return;
        }
        var dark = Brand.IsDark();
        Puck.SetTint(Expander, new SolidColorBrush(ClassicTokens.Tint(ClassicTokens.Identity(_provider, dark), dark)));
        if (await Brand.MarkAsync(_provider, dark) is { } mark) Expander.HeaderIcon = new ImageIcon { Source = mark, Width = 18, Height = 18 };
    }
}
