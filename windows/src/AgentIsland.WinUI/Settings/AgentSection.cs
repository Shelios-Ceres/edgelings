using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
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
    private const int MaxMeters = 2;

    private readonly DisplayProvider _provider;
    private readonly TextBlock _plan = new();
    private readonly Border _planBadge;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch _shown;
    private readonly SettingsCard _activity;
    private readonly TextBlock _activityText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(SettingsCard Card, QuotaBar Bar, TextBlock Value)> _meters = new();
    private readonly SettingsCard _tokens;
    private readonly TextBlock _tokensText = new();
    private readonly SettingsCard _note;
    private readonly SettingsCard _account;
    private readonly TextBlock _accountText = new() { IsTextSelectionEnabled = true };
    private readonly SettingsCard _signIn;
    private readonly Button _signInButton = new();
    private readonly ProgressRing _signInRing = new() { Width = 16, Height = 16, IsActive = false, Margin = new Thickness(0, 0, 10, 0) };
    private bool _updating;

    public AgentSection(DisplayProvider provider, Func<XamlRoot?> root)
    {
        _provider = provider;
        _planBadge = Badge(_plan);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = ProviderIdentity.DisplayName(provider), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(_planBadge);

        _shown = Toggle(ProviderVisibilityStore.Shared.IsShown(provider), on =>
        {
            if (_updating) return;
            ProviderVisibilityStore.Shared.SetShown(provider, on);
            if (on) AgentReports.RefreshNow(provider);
        });
        ToolTipService.SetToolTip(_shown, L10n.Tr("Show in drawer"));

        Expander = new SettingsExpander
        {
            Header = header,
            Description = _status,
            HeaderIcon = new ImageIcon(),
            Content = _shown,
        };
        _ = LoadMarkAsync();

        _activity = new SettingsCard { Header = L10n.Tr("Right now"), Description = _activityText, HeaderIcon = Icon("\uE9D9") };
        Expander.Items.Add(_activity);
        for (var i = 0; i < MaxMeters; i++)
        {
            var bar = new QuotaBar(8) { Width = 180, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { MinWidth = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { bar, value } };
            var card = new SettingsCard { HeaderIcon = Icon("\uE9D2"), Content = content };
            _meters.Add((card, bar, value));
            Expander.Items.Add(card);
        }
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
                if (AgentReports.StartSignIn(provider) || root() is not { } xamlRoot) return;
                await Alert(xamlRoot, L10n.Tr("Sign in"), L10n.Tr(provider == DisplayProvider.Claude
                    ? "Claude Code CLI not found. Log in from a terminal with: claude /login"
                    : "Codex CLI not found. Log in from a terminal with: codex login"));
            };
            _signIn.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _signInRing, _signInButton } };
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
        SetStatus(_status, report.Status, report.Warning);
        _shown.IsOn = ProviderVisibilityStore.Shared.IsShown(_provider);

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
            bar.Set(meter.Shown, Accent(), meter.Used >= 0.9, Brand.IsDark());
            value.Text = meter.Value;
        }
        _tokens.Visibility = report.Tokens is null ? Visibility.Collapsed : Visibility.Visible;
        if (report.Tokens is { } tokens)
        {
            _tokens.Description = L10n.Tr(TokenCountModeStore.Shared.Mode == TokenCountMode.Billable
                ? "Input and output, from this computer's logs"
                : "Input, output and cache, from this computer's logs");
            _tokensText.Text = $"{tokens.Today}  ·  {tokens.Month}";
        }
        _note.Visibility = report.Note is null ? Visibility.Collapsed : Visibility.Visible;
        _note.Header = report.Note ?? "";
        _account.Visibility = report.Account is null && _provider != DisplayProvider.Codex ? Visibility.Collapsed : Visibility.Visible;
        _accountText.Text = report.Account ?? L10n.Tr("Not saved yet");

        _signIn.Description = report.SignInHint;
        _signInRing.IsActive = report.SignIn == SignInOffer.Waiting;
        _signInRing.Visibility = report.SignIn == SignInOffer.Waiting ? Visibility.Visible : Visibility.Collapsed;
        _signInButton.IsEnabled = report.SignIn != SignInOffer.Waiting;
        _signInButton.Content = L10n.Tr(report.SignIn switch
        {
            SignInOffer.Waiting => "Waiting for login…",
            SignInOffer.Available => "Sign in",
            _ => "Sign in again",
        });
        _signInButton.Style = report.SignIn == SignInOffer.Available ? Res<Style>("AccentButtonStyle") : null;

        _activity.Visibility = report.Detected ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
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

    /// Bars wear the agent's character colour, as in the drawer.
    private Windows.UI.Color Accent()
    {
        var look = App.Current.Drawer?.LookOf(_provider) ?? DrawerLooks.For(_provider);
        return Brand.Rgb(Brand.Readable(DrawerLooks.Swatch(look.Color), Brand.IsDark()));
    }

    private static string MeterTitle(string label) => label switch
    {
        _ when label == L10n.Tr("5h") => L10n.Tr("5-hour window"),
        _ when label == L10n.Tr("week") => L10n.Tr("Weekly window"),
        _ when label == L10n.Tr("30d") => L10n.Tr("Billing cycle"),
        _ when label == L10n.Tr("month") => L10n.Tr("Monthly window"),
        _ => label,
    };

    /// Opt-in: the Notification hook that lets the drawer show an open
    /// permission prompt instead of a long-running tool.
    private static SettingsCard PromptHookCard(Func<XamlRoot?> root)
    {
        var toggle = new ToggleSwitch { IsOn = ClaudeHooks.IsInstalled(), OnContent = L10n.Tr("On"), OffContent = L10n.Tr("Off") };
        var settling = false;
        toggle.Toggled += async (_, _) =>
        {
            if (settling) return;
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
        foreach (var option in shapes) shapeBox.Items.Add(DrawerLooks.ShapeLabel(option));
        shapeBox.SelectedIndex = Math.Max(0, shapes.ToList().IndexOf(shape));
        var colorBox = new ComboBox { MinWidth = 150 };
        var dark = Brand.IsDark();
        foreach (var option in colors)
        {
            colorBox.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Fill = Brand.Brush(Brand.Readable(DrawerLooks.Swatch(option), dark)),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock { Text = DrawerLooks.ColorLabel(option), VerticalAlignment = VerticalAlignment.Center },
                },
            });
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

    private async Task LoadMarkAsync()
    {
        if (await Brand.MarkAsync(_provider, Brand.IsDark()) is { } mark) Expander.HeaderIcon = new ImageIcon { Source = mark };
    }
}
