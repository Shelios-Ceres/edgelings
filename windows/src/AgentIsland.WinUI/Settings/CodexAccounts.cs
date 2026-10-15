using AgentIsland.Localization;
using AgentIsland.Usage;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        button.Style = Looks.Kit<Style>("DropDownButtonStyle");
        AutomationProperties.SetName(button, L10n.Tr("Switch Codex account"));
        AutomationProperties.SetAutomationId(button, "Agent.codex.SwitchAccount");
        menu.Opening += (_, _) => Fill(menu, root);
        return button;
    }

    private static void Fill(MenuFlyout menu, Func<XamlRoot?> root)
    {
        using var english = Looks.English();
        menu.Items.Clear();
        var accounts = CodexAccountSwitcher.Accounts();
        var active = CodexAccountSwitcher.ActiveLabel();
        if (accounts.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = L10n.Tr("No saved accounts yet"), IsEnabled = false });
        }
        var choices = new List<(string Label, RadioMenuFlyoutItem Item)>();
        void RestoreChecks()
        {
            var current = CodexAccountSwitcher.ActiveLabel();
            foreach (var (label, choice) in choices) choice.IsChecked = string.Equals(label, current, StringComparison.Ordinal);
        }
        foreach (var account in accounts)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = account.Email is { } email && email != account.Label ? $"{account.Label}  ({email})" : account.Label,
                GroupName = "codex-account",
                IsChecked = string.Equals(account.Label, active, StringComparison.Ordinal),
            };
            choices.Add((account.Label, item));
            item.Click += async (_, _) =>
            {
                using var english = Looks.English();
                var activated = CodexAccountSwitcher.Activate(account);
                RestoreChecks();
                if (!activated)
                {
                    menu.Hide();
                    if (root() is { } xamlRoot)
                        await SettingsUi.Alert(xamlRoot, L10n.Tr("Couldn't switch account"),
                            L10n.Tr("Switch canceled. Credentials could not be read or safely saved. Check file access and try again."));
                    return;
                }
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
        MenuHost.UseKit(menu);
    }

    private static async Task SaveCurrent(XamlRoot root)
    {
        using var english = Looks.English();
        var name = new TextBox { PlaceholderText = L10n.Tr("work / personal"), MaxLength = 40 };
        SettingsUi.UseKit(name);
        AutomationProperties.SetName(name, L10n.Tr("Save current account"));
        AutomationProperties.SetAutomationId(name, "Agent.codex.AccountLabel");
        var dialog = SettingsUi.Dialog(root);
        dialog.Title = L10n.Tr("Save current account");
        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = L10n.Tr("Give this login a name so you can switch back to it later"), TextWrapping = TextWrapping.Wrap },
                name,
            },
        };
        dialog.PrimaryButtonText = L10n.Tr("Save");
        dialog.CloseButtonText = L10n.Tr("Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!CodexAccountSwitcher.ParkCurrent(string.IsNullOrWhiteSpace(name.Text) ? null : name.Text))
            await SettingsUi.Alert(root, L10n.Tr("Couldn't save account"),
                L10n.Tr("The current login could not be read or saved. Check file access and try again."));
    }
}
