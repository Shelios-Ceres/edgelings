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

        var close = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Padding = new Thickness(6),
        };
        close.MouseLeftButtonUp += (_, _) => Close();
        var header = new Grid { Margin = new Thickness(12, 0, 4, 14) };
        header.Children.Add(new TextBlock
        {
            Text = L10n.Tr("Cost"),
            FontFamily = IslandFonts.Ui,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_grid);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x12)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.08)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 14, 14, 18),
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

    /// Antigravity keeps no local token ledger, so it gets a note instead of
    /// a fabricated $0; Cursor logs tokens without a model, so no dollars.
    private void Rebuild()
    {
        _grid.Children.Clear();
        _blocks.Clear();
        var shown = DisplayProviders.All.Where(ProviderVisibilityStore.Shared.IsShown).ToList();
        _grid.Columns = Math.Clamp(shown.Count, 1, 3);
        foreach (var provider in shown)
        {
            var section = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 0, 16) };
            section.Children.Add(new TextBlock
            {
                Text = ProviderIdentity.DisplayName(provider),
                FontFamily = IslandFonts.Ui,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ProviderIdentity.AccentBrush(provider),
                Margin = new Thickness(12, 0, 12, 6),
            });
            if (provider == DisplayProvider.Antigravity)
            {
                section.Children.Add(new TextBlock
                {
                    Text = L10n.Tr("No local usage log"),
                    FontFamily = IslandFonts.Ui,
                    FontSize = 11,
                    Foreground = IslandColors.Brush(IslandColors.White(0.45)),
                    Margin = new Thickness(12, 0, 12, 0),
                });
            }
            else
            {
                var block = new CostBlock(ProviderIdentity.Accent(provider), showsDollars: provider != DisplayProvider.Cursor);
                _blocks[provider] = block;
                section.Children.Add(block);
            }
            _grid.Children.Add(section);
        }
        Update();
    }

    private void Update()
    {
        foreach (var (provider, block) in _blocks) block.Update(CostStore.Shared.Summary(provider));
    }
}
