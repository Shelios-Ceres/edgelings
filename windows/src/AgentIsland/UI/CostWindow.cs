using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The local cost ledger in its own window: one block per provider the
/// drawer shows. It replaced the island panel's cost page, whose two
/// columns were bound to the island's two provider slots.
public sealed class CostWindow : Window
{
    private static CostWindow? _open;

    private readonly UniformGrid _grid = new();
    private readonly Dictionary<DisplayProvider, CostBlock> _blocks = new();
    private readonly List<Action> _teardown = new();

    /// Opens the window, or brings the open one forward.
    public static void Open()
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        _open = new CostWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private CostWindow()
    {
        Title = L10n.Tr("Cost");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var closeGlyph = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Border
        {
            Child = closeGlyph,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        close.MouseEnter += (_, _) => close.Background = GrokTheme.InkBrush(GrokTheme.Tertiary);
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, _) => Close();
        var header = new Grid { Margin = new Thickness(4, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = L10n.Tr("Cost"),
            FontFamily = IslandFonts.Ui,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_grid);
        Content = new Border
        {
            Background = GrokTheme.Brush(GrokTheme.Elevated),
            BorderBrush = GrokTheme.InkBrush(GrokTheme.StrokeSecondary),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 8, 8),
            Margin = new Thickness(18),
            Effect = GrokTheme.Float(0.16),
            Child = body,
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        void Watch(System.ComponentModel.INotifyPropertyChanged source, Action handler)
        {
            System.ComponentModel.PropertyChangedEventHandler h = (_, _) => Dispatcher.BeginInvoke(handler);
            source.PropertyChanged += h;
            _teardown.Add(() => source.PropertyChanged -= h);
        }
        Watch(CostStore.Shared, Update);
        Watch(CostStylePreferenceStore.Shared, Update);
        Watch(ProviderVisibilityStore.Shared, Rebuild);
        Watch(DrawerStore.Shared, Rebuild);
        Closed += (_, _) => { foreach (var teardown in _teardown) teardown(); };
        Rebuild();
    }

    /// One Grok card per provider: brand mark and name, then the block.
    /// Antigravity keeps no local token ledger, so it gets a note instead of
    /// a fabricated $0; token-only providers (Cursor, Kimi Code, DeepSeek)
    /// show no dollars.
    private void Rebuild()
    {
        _grid.Children.Clear();
        _blocks.Clear();
        var shown = DisplayProviders.All.Where(ProviderVisibilityStore.Shared.IsShown).ToList();
        _grid.Columns = Math.Clamp(shown.Count, 1, 3);
        foreach (var provider in shown)
        {
            var section = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
            title.Children.Add(new Grid
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 7, 0),
                Children = { ProviderMarks.Mark(provider, 15, tintOpacity: 1, tint: GrokTheme.Brand(provider)) },
            });
            title.Children.Add(new TextBlock
            {
                Text = ProviderIdentity.DisplayName(provider),
                FontFamily = IslandFonts.Ui,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GrokTheme.InkBrush(GrokTheme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            });
            section.Children.Add(title);
            if (provider == DisplayProvider.Antigravity)
            {
                section.Children.Add(new TextBlock
                {
                    Text = L10n.Tr("No local usage log"),
                    FontFamily = IslandFonts.Ui,
                    FontSize = 11,
                    Foreground = GrokTheme.InkBrush(GrokTheme.TextTertiary),
                    Margin = new Thickness(12, 0, 12, 0),
                });
            }
            else
            {
                var block = new CostBlock(GrokTheme.Brand(provider), showsDollars: Report.ReportFormat.ProvidesDollars(provider));
                _blocks[provider] = block;
                section.Children.Add(block);
            }
            _grid.Children.Add(new Border
            {
                Child = section,
                Width = 236,
                Background = GrokTheme.InkBrush(GrokTheme.Field),
                BorderBrush = GrokTheme.InkBrush(GrokTheme.StrokeTertiary),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0, 12, 0, 12),
                Margin = new Thickness(0, 0, 6, 6),
            });
        }
        Update();
    }

    private void Update()
    {
        foreach (var (provider, block) in _blocks) block.Update(CostStore.Shared.Summary(provider));
    }
}
