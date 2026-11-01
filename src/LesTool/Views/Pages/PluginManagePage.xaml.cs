using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LesTool.Services.Plugins;

namespace LesTool.Views.Pages;

/// <summary>
/// 插件管理页：列出本次启动扫描到的全部插件（已装载/已禁用/装载失败）。
/// 开关写宿主侧状态（PluginState），重启生效；行内状态是本次启动的运行态，
/// 不随开关即时变化（进程内程序集常驻，运行中装卸已拍板不做）。
/// 插件行由代码后置生成（清单字段图标/描述因插件而异，无固定表单）。
/// </summary>
public partial class PluginManagePage : UserControl
{
    public PluginManagePage()
    {
        InitializeComponent();
        // 图标覆盖即时生效（页头选择器换图标 → 这里同步换脸）
        PluginState.IconOverridesChanged += Rebuild;
        Unloaded += (_, _) => PluginState.IconOverridesChanged -= Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        PluginList.Children.Clear();
        if (PluginState.IsProtected)
            PluginList.Children.Add(new SettingsRecoveryNotice("插件启用配置需要恢复", PluginState.ResetProtected));
        foreach (var item in PluginHost.ProtectedSettings.Where(pair => pair.Value.IsProtected))
            PluginList.Children.Add(new SettingsRecoveryNotice($"{PluginHost.InfoOf(item.Key)?.Name ?? item.Key} 的配置需要恢复", item.Value.ResetProtected));

        if (PluginHost.Discovered.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "未发现插件。将插件文件夹放入程序目录的 plugins\\ 下，重启后自动装载。",
                Margin = new Thickness(0, 12, 0, 0),
            };
            BindTheme(empty, TextBlock.FontSizeProperty, "FontSizeBody");
            BindTheme(empty, TextBlock.ForegroundProperty, "TextSecondary");
            PluginList.Children.Add(empty);
            return;
        }

        foreach (PluginInfo plugin in PluginHost.Discovered)
            PluginList.Children.Add(BuildRow(plugin));
    }

    private Border BuildRow(PluginInfo plugin)
    {
        var icon = new TextBlock
        {
            // 生效图标：用户覆盖 > 清单（与插件页头同一解析，PLUGIN_UI_SPEC F）
            Text = PluginState.GetEffectiveIcon(plugin.Id, plugin.Icon),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        BindTheme(icon, TextBlock.FontSizeProperty, "FontSizeTitle");
        BindTheme(icon, TextBlock.ForegroundProperty, "TextPrimary");

        var name = new TextBlock { Text = plugin.Name, FontWeight = FontWeights.SemiBold };
        BindTheme(name, TextBlock.FontSizeProperty, "FontSizeBody");
        BindTheme(name, TextBlock.ForegroundProperty, "TextPrimary");

        // 信息列：名称行（名称 + 版本）+ 描述/失败原因行
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(name);
        if (!string.IsNullOrEmpty(plugin.Version))
        {
            var version = new TextBlock { Text = $"  v{plugin.Version}", VerticalAlignment = VerticalAlignment.Bottom };
            BindTheme(version, TextBlock.FontSizeProperty, "FontSizeCaption");
            BindTheme(version, TextBlock.ForegroundProperty, "TextHint");
            nameRow.Children.Add(version);
        }
        info.Children.Add(nameRow);

        string detail = plugin.Status == PluginStatus.Failed && !string.IsNullOrEmpty(plugin.Reason)
            ? plugin.Reason
            : plugin.Description;
        if (!string.IsNullOrEmpty(detail))
        {
            var desc = new TextBlock { Text = detail, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            BindTheme(desc, TextBlock.FontSizeProperty, "FontSizeCaption");
            BindTheme(desc, TextBlock.ForegroundProperty,
                plugin.Status == PluginStatus.Failed ? "DangerBrush" : "TextSecondary");
            info.Children.Add(desc);
        }

        // 状态列 + 开关列：状态是本次启动的运行态；开关表达"下次启动要不要装"
        var status = new TextBlock
        {
            Text = plugin.Status switch
            {
                PluginStatus.Loaded => "已装载",
                PluginStatus.Disabled => "已禁用",
                _ => "装载失败",
            },
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        BindTheme(status, TextBlock.FontSizeProperty, "FontSizeCaption");
        BindTheme(status, TextBlock.ForegroundProperty, plugin.Status switch
        {
            PluginStatus.Loaded => "SuccessBrush",
            PluginStatus.Disabled => "TextHint",
            _ => "DangerBrush",
        });

        var toggle = new CheckBox
        {
            Content = "启用",
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = !PluginState.IsDisabled(plugin.Id),
            Tag = plugin.Id,
        };
        if (plugin.Status == PluginStatus.Failed || PluginState.IsProtected)
        {
            // 装载失败的插件不可开关：先解决失败原因（或删目录），开关才有意义
            toggle.IsEnabled = false;
            toggle.ToolTip = "装载失败的插件需先解决失败原因（或删除其目录）";
        }
        else
        {
            toggle.Checked += (_, _) => PluginState.SetEnabled(plugin.Id, true);
            toggle.Unchecked += (_, _) => PluginState.SetEnabled(plugin.Id, false);
        }

        // 右列竖排（状态 + 开关）：状态是本次启动的运行态，开关表达"下次启动要不要装"
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(status);
        right.Children.Add(toggle);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        icon.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(info, 1);
        right.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(icon);
        grid.Children.Add(info);
        grid.Children.Add(right);

        var card = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("CornerRadiusOverlay"),
            Child = grid,
        };
        BindTheme(card, Border.BackgroundProperty, "CardBackground");
        BindTheme(card, Border.BorderBrushProperty, "CardBorderColor");
        return card;
    }

    /// <summary>皮肤热重载安全版取资源（等价 XAML DynamicResource；数值 Token 亦走此路，行为一致）。</summary>
    private static void BindTheme(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);
}
