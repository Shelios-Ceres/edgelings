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
        var pixel = Looks.Pixel;
        var rowStyle = pixel ? Res<Style>("PixelOrderRowStyle") : new Style(typeof(ListViewItem));
        if (!pixel)
        {
            rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            // Rows are for dragging; Tab walks the arrows.
            rowStyle.Setters.Add(new Setter(Control.IsTabStopProperty, false));
        }
        var list = new ListView
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid/></DataTemplate>"),
            ItemContainerStyle = rowStyle,
            CanReorderItems = true,
            CanDragItems = true,
            AllowDrop = true,
            SelectionMode = ListViewSelectionMode.None,
            TabNavigation = KeyboardNavigationMode.Local,
            Margin = pixel ? new Thickness(0) : new Thickness(-12, 4, 0, 0),
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
                if (list.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid { Tag: Row row } }) row.Show(items[i], i, items.Count, Shown(items[i].Provider));
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
            row.Show(entry, items.IndexOf(entry), items.Count, Shown(entry.Provider));
            AutomationProperties.SetName(args.ItemContainer, ProviderIdentity.DisplayName(entry.Provider));
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
            if (grip is FontIcon glyph) glyph.FontSize = 14;
            grip.Foreground = Res<Brush>("TextFillColorTertiaryBrush");
            grip.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(grip);
            Grid.SetColumn(_name, 1);
            root.Children.Add(_name);
            Button Arrow(string glyph, int step)
            {
                var button = new Button { Content = Icon(glyph) };
                if (Looks.Pixel) button.Style = Res<Style>("PixelIconButtonStyle");
                else button.Padding = new Thickness(8, 4, 8, 4);
                button.Click += (_, _) => move(this, step, button.FocusState);
                return button;
            }
            Up = Arrow("\uE70E", -1);
            Down = Arrow("\uE70D", 1);
            Pixelate(_shown);
            ToolTipService.SetToolTip(_shown, Tip(L10n.Tr("Show in the drawer, its menus and the Cost window")));
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

        public void Show(Entry entry, int index, int count, bool shown)
        {
            Entry = entry;
            var name = ProviderIdentity.DisplayName(entry.Provider);
            _name.Text = name;
            // A hidden character keeps its place, faded.
            _name.Foreground = Res<Brush>(shown ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            _filling = true;
            _shown.IsOn = shown;
            _filling = false;
            AutomationProperties.SetName(_shown, $"{name} · {L10n.Tr("Show in drawer")}");
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
