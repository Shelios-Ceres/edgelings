using System.Diagnostics;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;
using AgentIsland.WinUI.Shell;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Settings;

/// The Look row on an agent's card: a tile for each look the agent can wear
/// in the current character style, the worn one marked, applied the moment
/// one is picked. Pixel's tiles are 48-DIP plates with the look's face and a
/// tooltip; Classic's are 80-DIP tiles with the look over its name. A tile
/// plays its idle animation while the pointer is over it (Classic's worn one
/// plays too); the rest hold one frame.
internal sealed class LookRow
{
    private const double Face = 44;

    private sealed class Tile(LookCatalog.Look look, RadioButton button, Image image, FrameworkElement tick, TextBlock? name)
    {
        public LookCatalog.Look Look { get; } = look;
        public RadioButton Button { get; } = button;
        public Image Image { get; } = image;
        public FrameworkElement Tick { get; } = tick;
        public TextBlock? Name { get; } = name;
        public CanvasImageSource? Target { get; set; }
        public bool Hovered { get; set; }
        public bool Playing { get; set; }
    }

    private readonly DisplayProvider _provider;
    private readonly CharacterTheme _theme = Looks.Pixel ? CharacterTheme.Pixel : CharacterTheme.Classic;
    private readonly Func<bool> _open;
    private readonly List<Tile> _tiles = new();
    private readonly string _group;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    // Held in a field so it is not collected before it ticks.
    private readonly DispatcherQueueTimer _timer;
    private double _last;
    private bool _syncing;

    /// open says whether the card is expanded, so a collapsed one stops animating.
    public LookRow(DisplayProvider provider, Func<bool> open)
    {
        _provider = provider;
        _open = open;
        _group = $"look.{_theme}.{provider.RawValue()}";
        var pixel = _theme == CharacterTheme.Pixel;
        var panel = new ItemsRepeater
        {
            Layout = new UniformGridLayout
            {
                MinItemWidth = pixel ? 48 : 80,
                MinItemHeight = pixel ? 48 : 88,
                MinColumnSpacing = 4,
                MinRowSpacing = pixel ? 4 : 2,
                ItemsStretch = UniformGridLayoutItemsStretch.None,
            },
            HorizontalAlignment = pixel ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var items = new List<UIElement>();
        foreach (var look in LookCatalog.For(_theme, provider))
        {
            var tile = MakeTile(look, pixel);
            _tiles.Add(tile);
            items.Add(tile.Button);
        }
        panel.ItemsSource = items;

        Element = Card(L10n.Tr("Look"), null, "", panel);
        // Classic's tiles sit under the header, wrapping; Pixel's are right-aligned beside it.
        if (!pixel) Element.ContentAlignment = ContentAlignment.Vertical;
        AutomationProperties.SetAutomationId(Element, $"Agent.{provider.RawValue()}.Look");
        AutomationProperties.SetName(Element, $"{ProviderIdentity.DisplayName(provider)} · {L10n.Tr("Look")}");
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(Motion.TickMs);
        _timer.Tick += (_, _) => Step();
        _hop = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hop.IsRepeating = false;
        _hop.Interval = TimeSpan.FromMilliseconds(2 * Motion.TickMs);
        _hop.Tick += (_, _) => _hopped?.Image.ClearValue(UIElement.RenderTransformProperty);
        Element.Loaded += (_, _) => Prepare();
        Element.Unloaded += (_, _) => _timer.Stop();
        Sync();
    }

    public SettingsCard Element { get; }

    private Tile MakeTile(LookCatalog.Look look, bool pixel)
    {
        var name = L10n.Tr(look.Name);
        // A fixed 44-DIP box, drawn to fit it whatever the source's own size.
        var image = new Image { Width = Face, Height = Face, Stretch = Stretch.Uniform };
        FrameworkElement tick = new Microsoft.UI.Xaml.Shapes.Rectangle { Visibility = Visibility.Collapsed };
        TextBlock? caption = null;
        FrameworkElement content;
        if (pixel)
        {
            // A 12-DIP accent square on the plate's bottom-right corner, 2 DIP over its edge,
            // with the kit's check at 8 DIP in onAccent.
            var check = PixelIcons.Icon("check");
            check.Foreground = Res<Brush>("PixelOnAccent");
            tick = new Grid
            {
                Width = 12,
                Height = 12,
                Background = Res<Brush>("PixelAccent"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -2, -2),
                Visibility = Visibility.Collapsed,
                Children = { new Viewbox { Width = 8, Height = 8, Child = check } },
            };
            content = new Grid { Width = 48, Height = 48, Children = { new Grid { Width = Face, Height = Face, Children = { image }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, tick } };
        }
        else
        {
            caption = new TextBlock
            {
                Text = name,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                FontSize = 12,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Res<Brush>("ClassicText"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            content = new StackPanel { Spacing = 2, Children = { image, caption } };
        }
        var button = new RadioButton
        {
            GroupName = _group,
            Content = content,
            Style = Looks.Kit<Style>("LookTileStyle"),
            Tag = look.Id,
        };
        // Top-aligned, each as tall as its own name needs.
        if (!pixel) button.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(button, pixel ? $"{L10n.Tr("Look")}: {name}" : name);
        AutomationProperties.SetAutomationId(button, $"Agent.{_provider.RawValue()}.Look.{look.Id}");
        if (pixel) SetTip(button, name);
        var tile = new Tile(look, button, image, tick, caption);
        button.Checked += (_, _) => Picked(tile);
        button.PointerEntered += (_, _) => { tile.Hovered = true; Wake(); };
        button.PointerExited += (_, _) => { tile.Hovered = false; Wake(); };
        button.GotFocus += (_, _) => Wake();
        return tile;
    }

    /// The images need the window's scale, so they are made once the row is on screen.
    private void Prepare()
    {
        var scale = Element.XamlRoot?.RasterizationScale ?? 1;
        foreach (var tile in _tiles)
        {
            if (tile.Target is not null && Math.Abs(tile.Target.Dpi - 96 * scale) < 0.5) continue;
            tile.Target = LookPreviews.NewTarget(Face, scale);
            tile.Image.Source = tile.Target;
            Show(tile, LookPreviews.Still(_theme, _provider, tile.Look.Id, Face));
        }
        Wake();
    }

    private void Show(Tile tile, string? svg)
    {
        if (svg is not null && tile.Target is { } target) LookPreviews.Draw(target, svg, _theme, Face);
    }

    /// Follows the pick saved for this agent: the worn tile, the description and
    /// the Classic names' weight.
    public void Sync()
    {
        var worn = LookCatalog.Of(_theme, _provider);
        _syncing = true;
        foreach (var tile in _tiles)
        {
            var on = tile.Look.Id == worn.Id;
            tile.Button.IsChecked = on;
            tile.Tick.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (tile.Name is { } name)
            {
                name.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                name.Foreground = Res<Brush>(on ? "ClassicAccentText" : "ClassicText");
            }
        }
        _syncing = false;
        var label = L10n.Tr(worn.Name);
        var who = _provider == DisplayProvider.Grok && ProviderVisibilityStore.Shared.IsDetected(DisplayProvider.Cursor)
            ? L10n.Tr("Grok and Cursor")
            : ProviderIdentity.DisplayName(_provider);
        SetDescription(Element, _theme == CharacterTheme.Pixel
            ? L10n.TrFormat("{0}. What {1} wears in the drawer, on its card and in menus", label, who)
            : label);
        Wake();
    }

    private void Picked(Tile tile)
    {
        if (_syncing) return;
        foreach (var other in _tiles) other.Tick.Visibility = other == tile ? Visibility.Visible : Visibility.Collapsed;
        var changed = LookCatalog.Of(_theme, _provider).Id != tile.Look.Id;
        if (!changed) return;
        DrawerStore.Shared.SetLookId(_theme, _provider, tile.Look.Id);
        Sync();
        if (_theme == CharacterTheme.Pixel && !Motion.ReducedMotion) Hop(tile);
    }

    // The picked tile hops a cell, as the drawer's bot does, then lands.
    private readonly DispatcherQueueTimer _hop;
    private Tile? _hopped;

    private void Hop(Tile tile)
    {
        _hopped?.Image.ClearValue(UIElement.RenderTransformProperty);
        _hopped = tile;
        tile.Image.RenderTransform = new TranslateTransform { Y = -2 };
        _hop.Stop();
        _hop.Start();
    }

    private bool Plays(Tile tile) =>
        !Motion.ReducedMotion && (tile.Hovered || tile.Button.FocusState != FocusState.Unfocused && _theme == CharacterTheme.Pixel
            || _theme == CharacterTheme.Classic && tile.Button.IsChecked == true);

    /// Starts or stops the clock by whether any tile plays.
    private void Wake()
    {
        var any = Element.IsLoaded && _open() && _tiles.Any(Plays);
        if (any && !_timer.IsRunning)
        {
            _last = _clock.Elapsed.TotalSeconds;
            _timer.Start();
        }
        else if (!any)
        {
            _timer.Stop();
            foreach (var tile in _tiles.Where(t => t.Playing))
            {
                tile.Playing = false;
                Show(tile, LookPreviews.Still(_theme, _provider, tile.Look.Id, Face));
            }
        }
    }

    private void Step()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _last, 0.1);
        _last = now;
        if (!Element.IsLoaded || !_open())
        {
            Wake();
            return;
        }
        foreach (var tile in _tiles)
        {
            if (!Plays(tile))
            {
                if (tile.Playing)
                {
                    tile.Playing = false;
                    Show(tile, LookPreviews.Still(_theme, _provider, tile.Look.Id, Face));
                }
                continue;
            }
            tile.Playing = true;
            Show(tile, LookPreviews.Step(_theme, _provider, tile.Look.Id, dt, Face));
        }
        if (!_tiles.Any(Plays)) Wake();
    }
}
