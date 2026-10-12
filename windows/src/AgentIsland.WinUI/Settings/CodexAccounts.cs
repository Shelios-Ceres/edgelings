using AgentIsland.Localization;
using AgentIsland.Usage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentIsland.WinUI.Settings;

/// One-click Codex account switching: park the live login under a name,
/// swap between parked logins, and opt into rotating when the active
/// account runs dry.
internal static class CodexAccounts
{
    public static DropDownButton Button(Func<XamlRoot?> root)
    {
        var menu = new MenuFlyout();
        var button = new DropDownButton { Content = L10n.Tr("Switch"), Flyout = menu };
        menu.Opening += (_, _) => Fill(menu, root);
        return button;
    }

    private static void Fill(MenuFlyout menu, Func<XamlRoot?> root)
    {
        menu.Items.Clear();
        var accounts = CodexAccountSwitcher.Accounts();
        var active = CodexAccountSwitcher.ActiveLabel();
        if (accounts.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = L10n.Tr("No saved accounts yet"), IsEnabled = false });
        }
        foreach (var account in accounts)
        {
            // A radio item would tick itself on click, claiming the login is
            // live before the swap has succeeded; the tick follows the file.
            var item = new RadioMenuFlyoutItem
            {
                Text = account.Email is { } email && email != account.Label ? $"{account.Label}  ({email})" : account.Label,
                GroupName = "codex-account",
                IsChecked = string.Equals(account.Label, active, StringComparison.Ordinal),
            };
            item.Click += (_, _) =>
            {
                if (!CodexAccountSwitcher.Activate(account)) return;
                // A manual pick supersedes the last automatic rotation.
                UsageStore.Shared.CodexAutoSwitched = null;
                UsageStore.Shared.Refresh();
            };
            menu.Items.Add(item);
        }
        if (accounts.Count > 0)
        {
            var remove = new MenuFlyoutSubItem { Text = L10n.Tr("Remove saved account") };
            foreach (var account in accounts)
            {
                var child = new MenuFlyoutItem { Text = account.Label };
                child.Click += (_, _) => CodexAccountSwitcher.Forget(account);
                remove.Items.Add(child);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(remove);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var save = new MenuFlyoutItem { Text = L10n.Tr("Save current account…") };
        save.Click += async (_, _) =>
        {
            if (root() is { } xamlRoot) await SaveCurrent(xamlRoot);
        };
        menu.Items.Add(save);
        var auto = new ToggleMenuFlyoutItem { Text = L10n.Tr("Auto-switch when exhausted"), IsChecked = CodexAccountSwitcher.AutoSwitchEnabled };
        auto.Click += (_, _) => CodexAccountSwitcher.AutoSwitchEnabled = auto.IsChecked;
        menu.Items.Add(auto);
    }

    private static async Task SaveCurrent(XamlRoot root)
    {
        var name = new TextBox { PlaceholderText = L10n.Tr("work / personal"), MaxLength = 40 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L10n.Tr("Save current account"),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = L10n.Tr("Give this login a name so you can switch back to it later"), TextWrapping = TextWrapping.Wrap },
                    name,
                },
            },
            PrimaryButtonText = L10n.Tr("Save"),
            CloseButtonText = L10n.Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        CodexAccountSwitcher.ParkCurrent(string.IsNullOrWhiteSpace(name.Text) ? null : name.Text);
    }
}
