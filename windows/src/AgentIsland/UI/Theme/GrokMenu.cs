using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace AgentIsland.UI.Theme;

/// Light Grok menus. The WPF default ContextMenu is the Windows 7 Aero
/// chrome; this swaps in a white floating card with ink rows, an accent tick
/// for checked items and a chevron for submenus. Every right-click menu in
/// the app — the drawer's characters, the tray, Codex accounts — is one of
/// these.
public static class GrokMenu
{
    private const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style x:Key="Menu" TargetType="ContextMenu">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="HasDropShadow" Value="False"/>
    <Setter Property="SnapsToDevicePixels" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ContextMenu">
          <Border Margin="10" Background="#FFFFFFFF" BorderBrush="#1F141414" BorderThickness="1"
                  CornerRadius="8" Padding="4">
            <Border.Effect>
              <DropShadowEffect ShadowDepth="3" Direction="270" BlurRadius="14" Opacity="0.14" Color="#141414"/>
            </Border.Effect>
            <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Separator">
          <Border Height="1" Margin="8,4,8,4" Background="#14141414"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="MenuItem">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Foreground" Value="#EB141414"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <Grid>
            <Border x:Name="Row" CornerRadius="5" Padding="6,5,10,5" MinWidth="160" Background="Transparent">
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="20"/>
                  <ColumnDefinition Width="*"/>
                  <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <Path x:Name="Tick" Data="M 0 3.5 L 3 6.5 L 8.5 0.5" Stroke="#FF599CE7" StrokeThickness="1.7"
                      HorizontalAlignment="Left" VerticalAlignment="Center" Margin="2,0,0,0" Visibility="Hidden"/>
                <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True"
                                  VerticalAlignment="Center"/>
                <Path x:Name="Chevron" Grid.Column="2" Data="M 0 0 L 3.5 3.5 L 0 7" Stroke="#8A141414"
                      StrokeThickness="1.4" Margin="14,0,0,0" VerticalAlignment="Center" Visibility="Collapsed"/>
              </Grid>
            </Border>
            <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="-4" VerticalOffset="-15"
                   IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}"
                   AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
              <Border Margin="10" Background="#FFFFFFFF" BorderBrush="#1F141414" BorderThickness="1"
                      CornerRadius="8" Padding="4">
                <Border.Effect>
                  <DropShadowEffect ShadowDepth="3" Direction="270" BlurRadius="14" Opacity="0.14" Color="#141414"/>
                </Border.Effect>
                <ScrollViewer VerticalScrollBarVisibility="Auto" MaxHeight="560">
                  <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="Row" Property="Background" Value="#0F141414"/>
            </Trigger>
            <Trigger Property="IsSubmenuOpen" Value="True">
              <Setter TargetName="Row" Property="Background" Value="#0F141414"/>
            </Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Tick" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="HasItems" Value="True">
              <Setter TargetName="Chevron" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Foreground" Value="#8A141414"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";

    /// Per thread: the drawer runs its own UI thread, and styles and
    /// templates belong to the dispatcher that parsed them.
    [ThreadStatic]
    private static ResourceDictionary? _resources;

    public static ContextMenu Create()
    {
        _resources ??= (ResourceDictionary)XamlReader.Parse(Xaml);
        var menu = new ContextMenu
        {
            Style = (Style)_resources["Menu"],
            FontFamily = Charts.IslandFonts.Ui,
            FontSize = 12.5,
        };
        menu.Resources.MergedDictionaries.Add(_resources);
        return menu;
    }

    /// A menu's title row, styled like the hover card's: the character's
    /// colour dot and its name. Inert — no highlight, no keyboard stop.
    public static MenuItem Title(string text, System.Windows.Media.Color dot)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = GrokTheme.Brush(dot),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        header.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return new MenuItem { Header = header, IsHitTestVisible = false, Focusable = false };
    }

    /// The text-field menu (cut, copy, paste, select all) in the same dress,
    /// replacing WPF's built-in Aero one.
    public static ContextMenu TextEditing()
    {
        var menu = Create();
        foreach (var (label, command) in new (string, System.Windows.Input.RoutedUICommand)[]
        {
            ("Cut", System.Windows.Input.ApplicationCommands.Cut),
            ("Copy", System.Windows.Input.ApplicationCommands.Copy),
            ("Paste", System.Windows.Input.ApplicationCommands.Paste),
        })
        {
            menu.Items.Add(new MenuItem { Header = Localization.L10n.Tr(label), Command = command });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = Localization.L10n.Tr("Select all"),
            Command = System.Windows.Input.ApplicationCommands.SelectAll,
        });
        return menu;
    }
}
