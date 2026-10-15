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
        // While Grok and Cursor share a character, Grok's row stands for both;
        // Cursor keeps its slot for when they split (DrawerStore.SetOrder).
        var merged = SharedSubscription.GrokWithCursor();
        var items = new ObservableCollection<Entry>(store.Order
            .Where(provider => !(merged && provider == DisplayProvider.Cursor))
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
        AutomationProperties.SetAutomationId(list, "Settings.Drawer.Order");
        AutomationProperties.SetName(list, L10n.Tr("Order"));

        void Save() => store.SetOrder(items.Select(entry => entry.Provider));

        // Grok's switch stands for the shared character while merged: on if
        // either choice shows it, and it sets both (DrawerVisibility.SetBy).
        var visibility = ProviderVisibilityStore.Shared;
        bool Shown(DisplayProvider provider) => merged && provider == DisplayProvider.Grok
            ? visibility.IsShown(DisplayProvider.Grok) || visibility.IsShown(DisplayProvider.Cursor)
            : visibility.IsShown(provider);
        void SetShown(Row row, bool shown)
        {
            if (row.Entry is not { } entry) return;
            foreach (var choice in DrawerVisibility.SetBy(entry.Provider, merged)) store.SetShown(choice, shown);
        }

        // Rows whose item stayed put keep stale ends; refill every row shown.
        void Refresh()
        {
            using var english = Looks.English();
            for (var i = 0; i < items.Count; i++)
            {
                if (list.ContainerFromIndex(i) is not ListViewItem { ContentTemplateRoot: Grid { Tag: Row row } } container) continue;
                var name = SharedSubscription.CharacterName(items[i].Provider, merged);
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
            // The same removal and insertion a drop makes.
            items.RemoveAt(from);
            items.Insert(to, entry);
            Save();
            list.DispatcherQueue.TryEnqueue(() =>
            {
                list.UpdateLayout();
                Refresh();
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
            var name = SharedSubscription.CharacterName(entry.Provider, merged);
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
