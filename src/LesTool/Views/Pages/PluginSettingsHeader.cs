using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LesTool.Services.Plugins;

namespace LesTool.Views.Pages;

/// <summary>
/// 插件页标准页头（PLUGIN_UI_SPEC A，2026-10-31 二改）：图标（48×48 卡片按钮，点击
/// 开字形库选择器换图标）+ 清单简介（可选，作者账只读）+ 右缘键位小部件
/// （PluginHotkeyButton：单动作直改、多动作汇总+下拉）。名称与版本不在此重复——
/// 名字左侧导航就有，版本进图标 tooltip（插件管理页亦可查）。
/// 图标覆盖写用户账（PluginState），变更即时刷新本页头与插件管理页，重启仍生效——
/// 名实两本账：作者账（plugin.json）管名与简介，用户账（plugins.json）管启用与图标。
/// </summary>
public sealed class PluginSettingsHeader : UserControl
{
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    private readonly string _pluginId;
    private readonly string _manifestIcon;
    private readonly TextBlock _icon = new() { FontFamily = IconFont };

    public PluginSettingsHeader(string pluginId, string name, string version,
        string description, string manifestIcon)
    {
        _pluginId = pluginId;
        _manifestIcon = manifestIcon;

        var iconButton = new Button
        {
            Width = 48,
            Height = 48,
            Content = _icon,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = string.IsNullOrEmpty(version)
                ? $"{name}\n点击更换图标（内置字形库，即时生效）"
                : $"{name}  v{version}\n点击更换图标（内置字形库，即时生效）",
        };
        _icon.FontSize = 24;
        _icon.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        iconButton.Margin = new Thickness(0, 0, 16, 0);
        iconButton.Click += OnPickIcon;

        // 简介可选：清单没写就不占位（约束 ①"可选"）。页头通栏后限最大宽度，
        // 超长简介不至于横贯整个窗口拉成一行（可读性行长上限）
        TextBlock? desc = null;
        if (!string.IsNullOrEmpty(description))
        {
            desc = new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 720,
                // WPF 坑：默认 Stretch 对齐在设了 MaxWidth 后拉不满，会退化成居中摆放
                // （2026-11-01 简介浮到页头中间实锤）——想要左对齐必须显式声明
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }

        // 键位小部件：无可绑命令的插件自 collapse（约束 ②"必有"按可绑语义）
        var hotkey = new PluginHotkeyButton(pluginId)
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconButton, 0);
        if (desc is not null)
        {
            Grid.SetColumn(desc, 1);
            grid.Children.Add(desc);
        }
        Grid.SetColumn(hotkey, 2);
        grid.Children.Add(iconButton);
        grid.Children.Add(hotkey);
        Content = grid;

        RefreshIcon();
        PluginState.IconOverridesChanged += RefreshIcon;
        Unloaded += (_, _) => PluginState.IconOverridesChanged -= RefreshIcon;
    }

    private void RefreshIcon() =>
        _icon.Text = PluginState.GetEffectiveIcon(_pluginId, _manifestIcon);

    /// <summary>换图标：选择器返回 null=取消、""=恢复默认、其余=字形。</summary>
    private void OnPickIcon(object sender, RoutedEventArgs e)
    {
        string? result = Views.IconPickerWindow.Pick(Window.GetWindow(this),
            PluginState.GetEffectiveIcon(_pluginId, _manifestIcon));
        if (result is null) return;
        PluginState.SetIcon(_pluginId, result.Length == 0 ? null : result);
    }
}
