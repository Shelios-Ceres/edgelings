using System.Collections.ObjectModel;
using System.ComponentModel;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using static AgentIsland.WinUI.Settings.SettingsUi;

namespace AgentIsland.WinUI.Settings;

/// The characters' order: a list to drag into place, with arrows for the
/// keyboard, and a switch to hide a character everywhere; it applies to
/// the drawer as soon as it changes.
///
/// The items are data and every container draws its own row. A reorder
/// removes an item and inserts it again, and a control can't join the new
/// container while the old one still holds it: XAML fails fast.
internal static class OrderList
{
    public static ListView Create(DrawerStore store)
    {
        // One row per character: Grok's stands for Cursor too, and Cursor
        // keeps its saved slot (DrawerStore.SetOrder).
        var items = new ObservableCollection<Entry>(store.Order
            .Where(provider => provider.HasCharacter())
            .Select(provider => new Entry(provider)));
        var list = new ListView
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid/></DataTemplate>"),
            // Rows are for dragging; Tab walks the arrows.
            ItemContainerStyle = Looks.Kit<Style>("OrderRowStyle"),
            CanReorderItems = true,
            CanDragItems = true,
            AllowDrop = true,
            SelectionMode = ListViewSelectionMode.None,
            TabNavigation = KeyboardNavigationMode.Local,
        };
        // Pixel never slides: rows make room for a drag in one frame (M11's
        // fallback, since ListView's own reorder can't be stepped).
        if (Looks.Pixel) list.ItemContainerTransitions = new TransitionCollection();
        AutomationProperties.SetAutomationId(list, "Settings.Drawer.Order");
        AutomationProperties.SetName(list, L10n.Tr("Order"));

        void Save() => store.SetOrder(items.Select(entry => entry.Provider));

        var visibility = ProviderVisibilityStore.Shared;
        bool Shown(DisplayProvider provider) => visibility.IsShown(provider);
        void SetShown(Row row, bool shown)
        {
            if (row.Entry is { } entry) visibility.SetShown(entry.Provider, shown);
        }

        // Rows whose item stayed put keep stale ends; refill every row shown.
        void Refresh()
        {
            using var english = Looks.English();
            for (var i = 0; i < items.Count; i++)
            {
                if (list.ContainerFromIndex(i) is not ListViewItem { ContentTemplateRoot: Grid { Tag: Row row } } container) continue;
                var name = ProviderIdentity.DisplayName(items[i].Provider);
                row.Show(items[i], i, items.Count, Shown(items[i].Provider), name);
                AutomationProperties.SetName(container, name);
            }
        }

        void Move(Row row, int step, FocusState focus)
        {
            if (row.Entry is not { } entry) return;
            var from = items.IndexOf(entry);
            var to = from + step;
            if (from < 0 || to < 0 || to >= items.Count) return;
            var other = items[to];
            // M11: where the two rows stand before they swap; the list's own
            // reorder animation would fight the swap's.
            var before = (Top(list, entry), Top(list, other));
            var panel = list.ItemsPanelRoot;
            var transitions = panel?.ChildrenTransitions;
            if (panel is not null) panel.ChildrenTransitions = new TransitionCollection();
            // The same removal and insertion a drop makes.
            items.RemoveAt(from);
            items.Insert(to, entry);
            Save();
            list.DispatcherQueue.TryEnqueue(() =>
            {
                list.UpdateLayout();
                if (panel is not null) panel.ChildrenTransitions = transitions;
                Refresh();
                Swap(list, entry, other, before);
                // The moved row has a new container; the keyboard follows it.
                if (focus == FocusState.Unfocused
                    || list.ContainerFromItem(entry) is not ListViewItem { ContentTemplateRoot: Grid { Tag: Row moved } }) return;
                var arrow = step < 0 ? moved.Up : moved.Down;
                (arrow.IsEnabled ? arrow : step < 0 ? moved.Down : moved.Up).Focus(focus);
            });
        }

        list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.Item is not Entry entry || args.ItemContainer.ContentTemplateRoot is not Grid root) return;
            using var english = Looks.English();
            var row = root.Tag as Row ?? new Row(root, Move, SetShown);
            var name = ProviderIdentity.DisplayName(entry.Provider);
            row.Show(entry, items.IndexOf(entry), items.Count, Shown(entry.Provider), name);
            AutomationProperties.SetName(args.ItemContainer, name);
            args.Handled = true;
        };
        list.DragItemsCompleted += (_, _) =>
        {
            Save();
            Refresh();
        };
        // The drawer menu and the Agents page can hide a character too.
        PropertyChangedEventHandler follow = (_, _) => list.DispatcherQueue.TryEnqueue(Refresh);
        list.Loaded += (_, _) =>
        {
            visibility.PropertyChanged -= follow;
            visibility.PropertyChanged += follow;
        };
        list.Unloaded += (_, _) => visibility.PropertyChanged -= follow;
        // A list a pixel wider can leave its floored rows as they were.
        list.SizeChanged += (_, _) =>
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (list.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid root }) Row.Reach(root);
            }
        };
        return list;
    }

    /// An entry's row's top in the list, or null while it has no row.
    private static double? Top(ListView list, object entry) =>
        list.ContainerFromItem(entry) is ListViewItem container ? container.TransformToVisual(list).TransformPoint(default).Y : null;

    /// M11 arrow move: the two rows glide from where they stood into their
    /// new places. Pixel steps in3 (half, then 15 % of the way left), the
    /// moving row riding 2 DIP up on the paper and shadow it wears when
    /// dragged; Classic glides both over 333 ms on inOut. Reduced: at once.
    private static void Swap(ListView list, object entry, object other, (double? Entry, double? Other) before)
    {
        if (Motion.ReducedMotion
            || list.ContainerFromItem(entry) is not ListViewItem moving || list.ContainerFromItem(other) is not ListViewItem neighbour
            || before.Entry is not { } entryTop || before.Other is not { } otherTop
            || Top(list, entry) is not { } entryNow || Top(list, other) is not { } neighbourNow) return;
        var rise = entryTop - entryNow;
        var fall = otherTop - neighbourNow;
        Canvas.SetZIndex(moving, 1);
        var lifted = Looks.Pixel ? ControlMotion.Find<FrameworkElement>(moving, "Lifted") : null;
        void Place(double movingY, double neighbourY, bool lift)
        {
            moving.Translation = new System.Numerics.Vector3(0, (float)movingY, 0);
            neighbour.Translation = new System.Numerics.Vector3(0, (float)neighbourY, 0);
            if (lifted is not null) lifted.Visibility = lift ? Visibility.Visible : Visibility.Collapsed;
        }
        void Land()
        {
            Place(0, 0, lift: false);
            Canvas.SetZIndex(moving, 0);
        }
        if (Looks.Pixel)
        {
            var risen = Motion.Remaining(rise, 0, Motion.In3);
            var fallen = Motion.Remaining(fall, 0, Motion.In3);
            UiMoment.Play(3 * Motion.TickMs, t =>
            {
                var k = Motion.Tick(t);
                if (k == 0) Place(rise, fall, lift: false);
                else if (k < 3) Place(risen[k - 1] - 2, fallen[k - 1], lift: true);
                else Land();
            }, Land);
            return;
        }
        UiMoment.Play(333, t =>
        {
            var left = 1 - Motion.InOut(t / 333);
            Place(rise * left, fall * left, lift: false);
        }, Land);
    }

    private sealed class Entry(DisplayProvider provider)
    {
        public DisplayProvider Provider { get; } = provider;

        public override string ToString() => ProviderIdentity.DisplayName(Provider);
    }

    /// One container's row, built once and refilled whenever the container
    /// shows another character or the character changes place.
    private sealed class Row
    {
        private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center };
        // Classic rows carry the character's face before its name.
        private readonly FaceChip? _face = Looks.Pixel ? null : new FaceChip(16, Microsoft.UI.Colors.Transparent) { VerticalAlignment = VerticalAlignment.Center };
        private readonly ToggleSwitch _shown = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        private bool _filling;

        public Row(Grid root, Action<Row, int, FocusState> move, Action<Row, bool> setShown)
        {
            root.Tag = this;
            root.ColumnSpacing = 10;
            root.Padding = new Thickness(0, 2, 0, 2);
            root.SizeChanged += (_, _) => Reach(root);
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var grip = Icon("\uE76F");
            if (grip is FontIcon glyph) glyph.FontSize = Looks.Pixel ? 14 : 16;
            grip.Foreground = Res<Brush>("TextFillColorTertiaryBrush");
            grip.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(grip);
            FrameworkElement label = _face is null
                ? _name
                : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _face, _name } };
            Grid.SetColumn(label, 1);
            root.Children.Add(label);
            Button Arrow(string glyph, int step)
            {
                var icon = Icon(glyph);
                if (icon is FontIcon font) font.FontSize = 14;
                var button = new Button { Content = icon, Style = Looks.Kit<Style>("IconButtonStyle") };
                button.Click += (_, _) => move(this, step, button.FocusState);
                return button;
            }
            Up = Arrow("\uE70E", -1);
            Down = Arrow("\uE70D", 1);
            UseKit(_shown);
            _shown.Toggled += (_, _) =>
            {
                if (!_filling) setShown(this, _shown.IsOn);
            };
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            arrows.Children.Add(_shown);
            arrows.Children.Add(Up);
            arrows.Children.Add(Down);
            Grid.SetColumn(arrows, 2);
            root.Children.Add(arrows);
        }

        /// ItemsStackPanel floors the width it hands each row, so at 150 % a
        /// 923-px list (615.333 DIP, 922.99997 px in floats) gets 922-px rows
        /// whose arrows end a pixel short of other cards' controls. The row
        /// reaches back out to the panel's edge.
        public static void Reach(Grid root)
        {
            DependencyObject? node = root;
            while (node is not null && node is not ListViewItem) node = VisualTreeHelper.GetParent(node);
            if (node is not ListViewItem item || VisualTreeHelper.GetParent(item) is not FrameworkElement panel) return;
            var shortfall = panel.ActualWidth - item.ActualWidth;
            var reach = shortfall is > 0 and < 1 ? shortfall : 0;
            if (Math.Abs(root.Margin.Right + reach) > 0.001) root.Margin = new Thickness(0, 0, -reach, 0);
        }

        public Entry? Entry { get; private set; }

        public Button Up { get; }

        public Button Down { get; }

        public void Show(Entry entry, int index, int count, bool shown, string name)
        {
            Entry = entry;
            _name.Text = name;
            if (_face is not null) _face.Color = ClassicTokens.Identity(entry.Provider, Brand.IsDark());
            // A hidden character keeps its place, faded.
            _name.Foreground = Res<Brush>(shown ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            _filling = true;
            _shown.IsOn = shown;
            _filling = false;
            NameShownSwitch(_shown, name);
            AutomationProperties.SetAutomationId(_shown, $"Settings.Drawer.Order.{entry.Provider.RawValue()}.Shown");
            Label(Up, "Up", L10n.Tr("Move up"), index > 0);
            Label(Down, "Down", L10n.Tr("Move down"), index >= 0 && index < count - 1);

            void Label(Button button, string id, string label, bool enabled)
            {
                AutomationProperties.SetName(button, $"{name} · {label}");
                AutomationProperties.SetAutomationId(button, $"Settings.Drawer.Order.{entry.Provider.RawValue()}.{id}");
                button.IsEnabled = enabled;
            }
        }
    }
}
